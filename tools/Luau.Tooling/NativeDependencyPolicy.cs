using System.Buffers.Binary;
using System.Text;

namespace Luau.Tooling;

// The shared libraries each ELF plugin may load. Every other runtime, the C++
// runtime included, must be linked statically and hidden, because a Unity
// project cannot be relied on to provide it.
internal static class NativeDependencyPolicy
{
    // A shipping Linux host may depend only on the C runtime.
    internal static readonly string[] LinuxAllowedNeeded = ["ld-linux-x86-64.so.2", "libc.so.6", "libm.so.6"];

    // Android system libraries present on every device at the plugin's API
    // level. A Unity player contains libc++_shared.so only when something else
    // ships it (in practice the GameActivity entry point), so a plugin that
    // needs it fails to load under the classic Activity entry point.
    internal static readonly string[] AndroidAllowedNeeded = ["libc.so", "libdl.so", "libm.so"];

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
        _ => throw new ToolingException($"No dynamic dependency policy exists for platform {platform}."),
    };

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
    // PT_DYNAMIC and the PT_LOAD mapping of DT_STRTAB. Section headers are
    // optional for loading and are not consulted. Any malformed or truncated
    // structure fails the audit rather than hiding a dependency.
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
        (ulong Offset, ulong Size)? dynamicSegment = null;
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
                dynamicSegment = (offset, size);
            }
        }
        Require(dynamicSegment is not null, $"{description} has no dynamic segment.");

        var neededOffsets = new List<ulong>();
        ulong? tableAddress = null;
        ulong? tableSize = null;
        var terminated = false;
        var entries = bytes.Slice((int)dynamicSegment!.Value.Offset, (int)dynamicSegment.Value.Size);
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
        var segment = loads.FindIndex(load =>
            tableStart >= load.Address && tableStart - load.Address <= load.Size &&
            tableLength <= load.Size - (tableStart - load.Address));
        Require(segment >= 0, $"{description} has a dynamic string table outside its loaded segments.");
        var table = bytes.Slice((int)(loads[segment].Offset + (tableStart - loads[segment].Address)), (int)tableLength);

        var names = new string[neededOffsets.Count];
        for (var index = 0; index < names.Length; index++)
        {
            Require(neededOffsets[index] < (ulong)table.Length,
                $"{description} names a dependency outside its dynamic string table.");
            var name = table[(int)neededOffsets[index]..];
            var end = name.IndexOf((byte)0);
            Require(end > 0, $"{description} names an empty or unterminated dependency.");
            names[index] = Encoding.ASCII.GetString(name[..end]);
        }
        return names;
    }

    private static void Require(bool condition, string message) => PackageStaticCommand.Require(condition, message);
}
