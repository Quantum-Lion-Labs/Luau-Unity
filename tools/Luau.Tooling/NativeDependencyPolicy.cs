using System.Buffers.Binary;
using System.Text;

namespace Luau.Tooling;

// The libraries each plugin may load. Every other runtime, the C and C++
// runtimes included, must be linked statically and hidden, because a Unity
// project cannot be relied on to provide it.
internal static class NativeDependencyPolicy
{
    // A Unity Windows player does not ship the Visual C++ runtime DLLs
    // (MSVCP140.dll, VCRUNTIME140.dll, VCRUNTIME140_1.dll) or the Universal CRT
    // forwarders (api-ms-win-crt-*.dll), so the Windows plugin links both
    // runtimes statically and may import only from the operating system.
    // Windows compares DLL names without regard to case.
    internal static readonly string[] WindowsAllowedImports = ["KERNEL32.dll"];

    // The MSVC runtime selection that makes that hold: the static C and C++
    // runtimes (/MT, /MTd for Debug). The windows-x64 preset sets it, and the
    // CMake build refuses any other.
    internal const string WindowsMsvcRuntimeLibrary = "MultiThreaded$<$<CONFIG:Debug>:Debug>";

    // A shipping Linux host may depend only on the C runtime.
    internal static readonly string[] LinuxAllowedNeeded = ["ld-linux-x86-64.so.2", "libc.so.6", "libm.so.6"];

    // Android system libraries present on every device at the plugin's API
    // level. A Unity player contains libc++_shared.so only when something else
    // ships it (in practice the GameActivity entry point), so a plugin that
    // needs it fails to load under the classic Activity entry point.
    internal static readonly string[] AndroidAllowedNeeded = ["libc.so", "libdl.so", "libm.so"];

    // Far above anything a host plugin needs, for the ELF and PE readers
    // alike. The limits keep a crafted file from making the audit allocate
    // without bound.
    private const int MaximumNeededLibraries = 64;
    private const int MaximumLibraryNameBytes = 255;

    private const uint ProgramHeaderLoad = 1;
    private const uint ProgramHeaderDynamic = 2;
    private const long DynamicNull = 0;
    private const long DynamicNeeded = 1;
    private const long DynamicStringTable = 5;
    private const long DynamicStringTableSize = 10;

    internal static IReadOnlyList<string> AllowedNeeded(string platform) => platform switch
    {
        "linux-x64" => LinuxAllowedNeeded,
        "android-arm64" or "android-x64" => AndroidAllowedNeeded,
        "win-x64" => throw new ToolingException("The win-x64 plugin is a PE image; its imports are checked by ValidatePe."),
        _ => throw new ToolingException($"No dynamic dependency policy exists for platform {platform}."),
    };

    // Checks a committed or freshly built plugin with the reader for its format.
    internal static void ValidatePlugin(string platform, string description, byte[] binary)
    {
        if (platform == "win-x64")
        {
            ValidatePe(platform, description, binary);
        }
        else
        {
            ValidateElf(platform, description, binary);
        }
    }

    internal static void Validate(string platform, string description, IReadOnlyList<string> needed)
    {
        var allowed = AllowedNeeded(platform);
        var unexpected = needed.Where(name => !allowed.Contains(name, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        Require(unexpected.Length == 0,
            $"{description} needs {string.Join(", ", unexpected)}, which {(unexpected.Length == 1 ? "is" : "are")} not allowed for " +
            $"{platform} (allowed: {string.Join(", ", allowed)}). Link every other runtime, including the C++ runtime, statically.");
        Require(needed.Contains(allowed.Single(static name => name is "libc.so" or "libc.so.6"), StringComparer.Ordinal),
            $"{description} does not declare its C runtime dependency, so it is not a {platform} shared library.");
    }

    internal static void ValidateElf(string platform, string description, byte[] elf) =>
        Validate(platform, description, ReadNeededLibraries(elf, description));

    // Reads DT_NEEDED entries the way the dynamic loader finds them: through
    // the loaded address of PT_DYNAMIC and the PT_LOAD mapping of DT_STRTAB.
    // Section headers are optional for loading and are not consulted. Any
    // malformed, truncated, or ambiguous structure fails the audit rather than
    // hiding a dependency.
    internal static string[] ReadNeededLibraries(byte[] elf, string description = "ELF plugin")
    {
        var bytes = elf.AsSpan();
        Require(bytes.Length >= 64 && bytes[..4].SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }),
            $"{description} is not an ELF file.");
        Require(bytes[4] == 2 && bytes[5] == 1, $"{description} is not a little-endian ELF64 file.");

        var headerOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x20..]);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x36..]);
        var headerCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x38..]);
        Require(headerSize >= 56 && headerCount > 0 && headerOffset <= (ulong)bytes.Length &&
                (ulong)headerSize * headerCount <= (ulong)bytes.Length - headerOffset,
            $"{description} has an invalid program header table.");

        var loads = new List<(ulong Address, ulong Offset, ulong Size)>();
        (ulong Address, ulong Offset, ulong Size)? dynamicSegment = null;
        for (var index = 0; index < headerCount; index++)
        {
            var header = bytes.Slice((int)(headerOffset + (ulong)index * headerSize), headerSize);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
            var address = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
            if (type is not (ProgramHeaderLoad or ProgramHeaderDynamic))
            {
                continue;
            }
            Require(offset <= (ulong)bytes.Length && size <= (ulong)bytes.Length - offset,
                $"{description} has a segment outside the file.");
            if (type == ProgramHeaderLoad)
            {
                loads.Add((address, offset, size));
            }
            else
            {
                Require(dynamicSegment is null, $"{description} has more than one dynamic segment.");
                dynamicSegment = (address, offset, size);
            }
        }
        Require(dynamicSegment is not null, $"{description} has no dynamic segment.");

        // The loader maps each address through exactly one PT_LOAD. Overlapping
        // segments would let the same address name different file bytes.
        for (var first = 0; first < loads.Count; first++)
        {
            for (var second = first + 1; second < loads.Count; second++)
            {
                Require(!Overlaps(loads[first], loads[second]), $"{description} has overlapping loaded segments.");
            }
        }

        // The loader reads the dynamic table at its loaded address, not at
        // PT_DYNAMIC's file offset. Require both to name the same bytes, so a
        // file cannot show the audit one table and the loader another.
        var dynamic = dynamicSegment!.Value;
        var dynamicLoad = loads.FindIndex(load => Contains(load, dynamic.Address, dynamic.Size));
        Require(dynamicLoad >= 0 && dynamic.Offset == loads[dynamicLoad].Offset + (dynamic.Address - loads[dynamicLoad].Address),
            $"{description} has a dynamic segment whose file offset is not the image of its loaded address.");

        var neededOffsets = new List<ulong>();
        ulong? tableAddress = null;
        ulong? tableSize = null;
        var terminated = false;
        var entries = bytes.Slice((int)dynamic.Offset, (int)dynamic.Size);
        for (var entry = 0; entry + 16 <= entries.Length && !terminated; entry += 16)
        {
            var tag = BinaryPrimitives.ReadInt64LittleEndian(entries[entry..]);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(entries[(entry + 8)..]);
            switch (tag)
            {
                case DynamicNull:
                    terminated = true;
                    break;
                case DynamicNeeded:
                    Require(neededOffsets.Count < MaximumNeededLibraries,
                        $"{description} lists more than {MaximumNeededLibraries} dependencies.");
                    neededOffsets.Add(value);
                    break;
                case DynamicStringTable:
                    tableAddress = value;
                    break;
                case DynamicStringTableSize:
                    tableSize = value;
                    break;
            }
        }
        Require(terminated, $"{description} has an unterminated dynamic section.");
        if (neededOffsets.Count == 0)
        {
            return [];
        }
        Require(tableAddress is not null && tableSize is not null, $"{description} has no dynamic string table.");

        var tableStart = tableAddress!.Value;
        var tableLength = tableSize!.Value;
        var segment = loads.FindIndex(load => Contains(load, tableStart, tableLength));
        Require(segment >= 0, $"{description} has a dynamic string table outside its loaded segments.");
        var table = bytes.Slice((int)(loads[segment].Offset + (tableStart - loads[segment].Address)), (int)tableLength);

        var names = new string[neededOffsets.Count];
        for (var index = 0; index < names.Length; index++)
        {
            Require(neededOffsets[index] < (ulong)table.Length,
                $"{description} names a dependency outside its dynamic string table.");
            var name = table[(int)neededOffsets[index]..];
            var end = name[..Math.Min(name.Length, MaximumLibraryNameBytes + 1)].IndexOf((byte)0);
            Require(end > 0,
                $"{description} names an empty or unterminated dependency, or one longer than {MaximumLibraryNameBytes} bytes.");
            names[index] = Encoding.ASCII.GetString(name[..end]);
        }
        return names;
    }

    // What a Windows DLL makes the loader load: the DLLs in its import
    // directory and its delay-load import directory, in table order, and the
    // targets of its forwarded exports ("OTHER.Function").
    internal sealed record PeDependencies(string[] Imports, string[] DelayImports, string[] Forwarders);

    // The loader's own limit, and the 16-bit ordinal space of an export table.
    private const int MaximumSections = 96;
    private const uint MaximumExportedFunctions = 65536;
    private const int MaximumForwarders = 64;
    private const int MaximumForwarderBytes = 1024;

    private const ushort MachineAmd64 = 0x8664;
    private const ushort OptionalHeaderPe32Plus = 0x20b;
    private const ushort CharacteristicExecutableImage = 0x0002;
    private const ushort CharacteristicDll = 0x2000;
    private const int DataDirectoryCount = 16;
    private const int DirectoryExport = 0;
    private const int DirectoryImport = 1;
    private const int DirectoryBoundImport = 11;
    private const int DirectoryDelayImport = 13;
    private const int SectionHeaderSize = 40;
    private const int ExportDirectorySize = 40;
    private const int ImportDescriptorSize = 20;
    private const int DelayImportDescriptorSize = 32;
    private const uint DelayAttributesRvaBased = 1;

    private readonly record struct PeSection(uint Address, uint MappedSize, uint FileOffset, uint FileSize);

    internal static void ValidatePe(string platform, string description, byte[] image) =>
        ValidateWindows(platform, description, ReadPeDependencies(image, description));

    internal static void ValidateWindows(string platform, string description, PeDependencies dependencies)
    {
        Require(platform == "win-x64", $"No PE import policy exists for platform {platform}.");
        var allowed = $"allowed: {string.Join(", ", WindowsAllowedImports)}";
        RejectUnexpected(dependencies.Imports, "imports");
        RejectUnexpected(dependencies.DelayImports, "delay-loads");
        Require(dependencies.Forwarders.Length == 0,
            $"{description} forwards exports to {string.Join(", ", dependencies.Forwarders)}. The loader loads the DLL a " +
            $"forwarded export names, so it is a dependency {platform} does not allow ({allowed}).");
        Require(dependencies.Imports.Contains(WindowsAllowedImports[0], StringComparer.OrdinalIgnoreCase),
            $"{description} does not import {WindowsAllowedImports[0]}, so it is not a {platform} DLL.");

        void RejectUnexpected(string[] names, string verb)
        {
            var unexpected = names
                .Where(static name => !WindowsAllowedImports.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Require(unexpected.Length == 0,
                $"{description} {verb} {string.Join(", ", unexpected)}, which {(unexpected.Length == 1 ? "is" : "are")} not allowed for " +
                $"{platform} ({allowed}). Link the C and C++ runtimes statically (/MT) so the plugin needs only the operating system.");
        }
    }

    // Reads the DLLs a PE32+ image makes the Windows loader load, the way the
    // loader finds them: through the optional header's data directories, with
    // every address mapped through the one section that contains it. Any
    // malformed, truncated, oversized, or ambiguous structure fails the audit
    // rather than hiding a dependency.
    internal static PeDependencies ReadPeDependencies(byte[] image, string description = "PE plugin")
    {
        var bytes = image.AsSpan();
        Require(bytes.Length >= 64 && bytes[0] == 'M' && bytes[1] == 'Z', $"{description} is not a PE image.");
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x3c..]);
        Require(signature >= 64 && signature <= (uint)bytes.Length - 26 && bytes[(int)signature..].StartsWith("PE\0\0"u8),
            $"{description} has no PE signature at its header offset.");

        var coff = (int)signature + 4;
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes[coff..]);
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(coff + 2)..]);
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(coff + 16)..]);
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(coff + 18)..]);
        var optional = coff + 20;
        Require(machine == MachineAmd64 && BinaryPrimitives.ReadUInt16LittleEndian(bytes[optional..]) == OptionalHeaderPe32Plus,
            $"{description} is not an x86-64 PE32+ image.");
        Require((characteristics & (CharacteristicExecutableImage | CharacteristicDll)) == (CharacteristicExecutableImage | CharacteristicDll),
            $"{description} is not a DLL.");
        Require(optionalSize >= 112 + DataDirectoryCount * 8 && (long)optional + optionalSize <= bytes.Length,
            $"{description} has a truncated optional header.");

        var sectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(optional + 32)..]);
        var fileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(optional + 36)..]);
        var imageSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(optional + 56)..]);
        var headersSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(optional + 60)..]);
        var directoryCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(optional + 108)..]);
        Require(directoryCount == DataDirectoryCount,
            $"{description} declares {directoryCount} data directories instead of {DataDirectoryCount}.");
        // The loader rounds a section's file offset down to 512 bytes, so an
        // unaligned offset would show the audit other bytes than it maps.
        Require(uint.IsPow2(fileAlignment) && fileAlignment is >= 512 and <= 65536 &&
                uint.IsPow2(sectionAlignment) && sectionAlignment >= fileAlignment,
            $"{description} has an invalid section or file alignment.");

        Require(sectionCount is > 0 and <= MaximumSections,
            $"{description} declares {sectionCount} sections; a DLL has between 1 and {MaximumSections}.");
        var sectionTable = (long)optional + optionalSize;
        Require(sectionTable + (long)sectionCount * SectionHeaderSize <= headersSize && headersSize <= (uint)bytes.Length,
            $"{description} has a section table outside its headers.");

        var sections = new PeSection[sectionCount];
        for (var index = 0; index < sectionCount; index++)
        {
            var header = bytes.Slice((int)(sectionTable + (long)index * SectionHeaderSize), SectionHeaderSize);
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            var address = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            Require(rawSize % fileAlignment == 0 && (rawSize == 0 || rawOffset % fileAlignment == 0),
                $"{description} has a section whose file data is not aligned to its file alignment.");
            Require(rawSize == 0 || rawOffset >= headersSize && (ulong)rawOffset + rawSize <= (ulong)bytes.Length,
                $"{description} has a section outside the file.");
            var mappedSize = virtualSize == 0 ? rawSize : virtualSize;
            Require(mappedSize > 0 && address % sectionAlignment == 0 && address >= headersSize && (ulong)address + mappedSize <= imageSize,
                $"{description} has a section outside its image.");
            sections[index] = new PeSection(address, mappedSize, rawOffset, Math.Min(mappedSize, rawSize));
        }

        // Each address must name exactly one section's bytes.
        for (var first = 0; first < sections.Length; first++)
        {
            for (var second = first + 1; second < sections.Length; second++)
            {
                var (low, high) = sections[first].Address <= sections[second].Address
                    ? (sections[first], sections[second])
                    : (sections[second], sections[first]);
                Require(high.Address - low.Address >= low.MappedSize, $"{description} has overlapping sections.");
            }
        }

        (uint Address, uint Size) Directory(int index) => (
            BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 112 + index * 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 112 + index * 8 + 4)));

        // link.exe never writes bound imports; the audit does not interpret
        // them, so it refuses a file that has them.
        Require(Directory(DirectoryBoundImport) == (0, 0),
            $"{description} has a bound import directory, which the audit does not read.");

        return new PeDependencies(
            ReadLibraryTable(image, sections, Directory(DirectoryImport), delay: false, description),
            ReadLibraryTable(image, sections, Directory(DirectoryDelayImport), delay: true, description),
            ReadForwarders(image, sections, Directory(DirectoryExport), description));
    }

    // Walks a descriptor table to its all-zero terminator, as the loader does,
    // and requires the directory's declared size to end exactly there.
    private static string[] ReadLibraryTable(
        byte[] image, PeSection[] sections, (uint Address, uint Size) directory, bool delay, string description)
    {
        var kind = delay ? "delay-load import" : "import";
        var descriptorSize = delay ? DelayImportDescriptorSize : ImportDescriptorSize;
        if (directory == (0, 0))
        {
            return [];
        }
        Require(directory.Address != 0 && directory.Size != 0,
            $"{description} has {WithArticle($"{kind} directory")} with {(directory.Address == 0 ? "a size but no address" : "an address but no size")}.");

        var table = MapToSectionEnd(image, sections, directory.Address, $"its {kind} directory", description);
        var names = new List<string>();
        for (var offset = 0; ; offset += descriptorSize)
        {
            Require(offset + descriptorSize <= table.Length, $"{description} has an unterminated {kind} directory.");
            var descriptor = table.Slice(offset, descriptorSize);
            if (!descriptor.ContainsAnyExcept((byte)0))
            {
                break;
            }
            Require(names.Count < MaximumNeededLibraries,
                $"{description} {(delay ? "delay-loads" : "imports")} more than {MaximumNeededLibraries} libraries.");

            uint name;
            if (delay)
            {
                // Attributes 0 is the obsolete format whose fields are
                // addresses, not RVAs.
                Require(BinaryPrimitives.ReadUInt32LittleEndian(descriptor) == DelayAttributesRvaBased,
                    $"{description} has a delay-load import descriptor that is not RVA-based.");
                name = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
                Require(name != 0 && BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]) != 0,
                    $"{description} has a delay-load import descriptor without a name or import address table.");
            }
            else
            {
                name = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]);
                Require(name != 0 && BinaryPrimitives.ReadUInt32LittleEndian(descriptor[16..]) != 0,
                    $"{description} has an import descriptor without a name or import address table.");
            }
            names.Add(ReadText(image, sections, name, MaximumLibraryNameBytes, $"{kind} library name", description));
        }
        Require((ulong)(names.Count + 1) * (ulong)descriptorSize == directory.Size,
            $"{description} declares {WithArticle($"{kind} directory")} of {directory.Size} bytes, but its descriptors " +
            $"and terminator take {(names.Count + 1) * descriptorSize}.");
        return names.ToArray();
    }

    // An export whose address lies inside the export directory is a forwarder:
    // the loader resolves it by loading the DLL its text names.
    private static string[] ReadForwarders(byte[] image, PeSection[] sections, (uint Address, uint Size) directory, string description)
    {
        if (directory == (0, 0))
        {
            return [];
        }
        Require(directory.Address != 0 && directory.Size >= ExportDirectorySize,
            $"{description} has an export directory smaller than its table.");
        var table = Map(image, sections, directory.Address, directory.Size, "its export directory", description);
        var functionCount = BinaryPrimitives.ReadUInt32LittleEndian(table[20..]);
        var functionsAddress = BinaryPrimitives.ReadUInt32LittleEndian(table[28..]);
        Require(functionCount <= MaximumExportedFunctions,
            $"{description} exports more than {MaximumExportedFunctions} functions.");
        if (functionCount == 0)
        {
            return [];
        }

        var functions = Map(image, sections, functionsAddress, functionCount * 4, "its export address table", description);
        var forwarders = new List<string>();
        for (var index = 0; index < functionCount; index++)
        {
            var address = BinaryPrimitives.ReadUInt32LittleEndian(functions[(index * 4)..]);
            if (address < directory.Address || address - directory.Address >= directory.Size)
            {
                continue;
            }
            Require(forwarders.Count < MaximumForwarders, $"{description} forwards more than {MaximumForwarders} exports.");
            forwarders.Add(ReadText(image, sections, address, MaximumForwarderBytes, "forwarded export", description));
        }
        return forwarders.ToArray();
    }

    // A non-empty, NUL-terminated, printable ASCII string of at most
    // maximumBytes, read from the file data of the section that contains it.
    private static string ReadText(byte[] image, PeSection[] sections, uint address, int maximumBytes, string what, string description)
    {
        var text = MapToSectionEnd(image, sections, address, WithArticle(what), description);
        var end = text[..Math.Min(text.Length, maximumBytes + 1)].IndexOf((byte)0);
        Require(end > 0, $"{description} has an empty or unterminated {what}, or one longer than {maximumBytes} bytes.");
        Require(!text[..end].ContainsAnyExceptInRange((byte)0x20, (byte)0x7e),
            $"{description} has {WithArticle(what)} that is not printable ASCII.");
        return Encoding.ASCII.GetString(text[..end]);
    }

    private static string WithArticle(string noun) => ("aeiou".Contains(noun[0]) ? "an " : "a ") + noun;

    private static ReadOnlySpan<byte> Map(byte[] image, PeSection[] sections, uint address, uint size, string what, string description)
    {
        var data = MapToSectionEnd(image, sections, address, what, description);
        Require(size <= (uint)data.Length, $"{description} has {what} that extends past the file data of its section.");
        return data[..(int)size];
    }

    // The file bytes from an address to the end of its section's file data.
    // Sections do not overlap, so at most one contains the address; bytes the
    // loader would zero-fill rather than read from the file are refused.
    private static ReadOnlySpan<byte> MapToSectionEnd(byte[] image, PeSection[] sections, uint address, string what, string description)
    {
        foreach (var section in sections)
        {
            if (address >= section.Address && address - section.Address < section.MappedSize)
            {
                var delta = address - section.Address;
                Require(delta < section.FileSize, $"{description} has {what} outside the file data of its section.");
                return image.AsSpan((int)(section.FileOffset + delta), (int)(section.FileSize - delta));
            }
        }
        throw new ToolingException($"{description} has {what} outside its sections.");
    }

    // Whether [address, address + size) lies inside the file-backed part of a
    // loaded segment. Written to avoid overflow on hostile values.
    private static bool Contains((ulong Address, ulong Offset, ulong Size) load, ulong address, ulong size) =>
        address >= load.Address && address - load.Address <= load.Size && size <= load.Size - (address - load.Address);

    private static bool Overlaps((ulong Address, ulong Offset, ulong Size) first, (ulong Address, ulong Offset, ulong Size) second) =>
        first.Size != 0 && second.Size != 0 &&
        (first.Address <= second.Address
            ? second.Address - first.Address < first.Size
            : first.Address - second.Address < second.Size);

    private static void Require(bool condition, string message) => PackageStaticCommand.Require(condition, message);
}
