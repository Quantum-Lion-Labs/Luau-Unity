[CmdletBinding()]
param(
    [string] $OutputPath = "",

    [switch] $Check,

    [string] $Tag = "",

    [switch] $SkipUnityConsumer,

    [string] $UnityPath = "",

    [string] $ConsumerOutputRoot = "",

    [ValidateRange(1, 180)]
    [int] $ConsumerTimeoutMinutes = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$packageRoot = Join-Path $repositoryRoot "Luau.Unity"
$policyPath = Join-Path $PSScriptRoot "UnityPackageReleasePolicy.json"
$repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $repositoryCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Unable to resolve the package source commit: $repositoryCommit"
}
$repositoryCommit = $repositoryCommit.ToLowerInvariant()

function Get-RelativePackagePath([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $relativePath = $fullPath.Substring($packageRoot.Length)
    return $relativePath.TrimStart([char[]]@('\', '/')).Replace('\', '/')
}

function Get-Sha256Bytes([byte[]] $Bytes) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($Bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-Sha256File([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Crc32([byte[]] $Bytes) {
    $table = [uint32[]]::new(256)
    for ($tableIndex = 0; $tableIndex -lt $table.Length; $tableIndex++) {
        [uint32]$entry = $tableIndex
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($entry -band 1) -ne 0) {
                $entry = [uint32](($entry -shr 1) -bxor 0xedb88320L)
            }
            else {
                $entry = [uint32]($entry -shr 1)
            }
        }
        $table[$tableIndex] = $entry
    }

    [uint32]$crc = 0xffffffffL
    foreach ($value in $Bytes) {
        $lookup = [int](($crc -bxor [uint32]$value) -band 0xff)
        $crc = [uint32](($crc -shr 8) -bxor $table[$lookup])
    }
    return [uint32]($crc -bxor 0xffffffffL)
}

function Get-LittleEndianUInt32([uint32] $Value) {
    $bytes = [BitConverter]::GetBytes($Value)
    if (![BitConverter]::IsLittleEndian) {
        [Array]::Reverse($bytes)
    }
    return $bytes
}

function New-DeterministicStoredGzip([byte[]] $Bytes) {
    $stream = [IO.MemoryStream]::new()
    try {
        # Fixed gzip header: deflate, zero mtime, no flags, neutral OS marker.
        $header = [byte[]]@(0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff)
        $stream.Write($header, 0, $header.Length)

        # Emit only stored DEFLATE blocks. This is intentionally uncompressed:
        # it makes the archive bytes independent of runtime/zlib versions.
        $offset = 0
        do {
            $remaining = $Bytes.Length - $offset
            $length = [Math]::Min(65535, $remaining)
            $isFinal = $offset + $length -eq $Bytes.Length
            [byte]$finalBlockHeader = if ($isFinal) { 1 } else { 0 }
            $stream.WriteByte($finalBlockHeader)
            [uint16]$blockLength = $length
            [uint16]$inverseLength = 0xffff - $length
            $lengthBytes = [BitConverter]::GetBytes($blockLength)
            $inverseBytes = [BitConverter]::GetBytes($inverseLength)
            if (![BitConverter]::IsLittleEndian) {
                [Array]::Reverse($lengthBytes)
                [Array]::Reverse($inverseBytes)
            }
            $stream.Write($lengthBytes, 0, $lengthBytes.Length)
            $stream.Write($inverseBytes, 0, $inverseBytes.Length)
            if ($length -ne 0) {
                $stream.Write($Bytes, $offset, $length)
            }
            $offset += $length
        } while ($offset -lt $Bytes.Length)

        $crcBytes = Get-LittleEndianUInt32 (Get-Crc32 $Bytes)
        $sizeBytes = Get-LittleEndianUInt32 ([uint32]($Bytes.Length -band 0xffffffffL))
        $stream.Write($crcBytes, 0, $crcBytes.Length)
        $stream.Write($sizeBytes, 0, $sizeBytes.Length)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-Sequence([string] $Description, [object[]] $Actual, [object[]] $Expected) {
    $actualValues = @($Actual)
    $expectedValues = @($Expected)
    if ($actualValues.Count -ne $expectedValues.Count) {
        throw "$Description mismatch. Expected $($expectedValues.Count), found $($actualValues.Count)."
    }
    for ($index = 0; $index -lt $expectedValues.Count; $index++) {
        if ($actualValues[$index] -cne $expectedValues[$index]) {
            throw "$Description mismatch at $index. Expected '$($expectedValues[$index])', found '$($actualValues[$index])'."
        }
    }
}

function Get-OrdinalSortedStrings([string[]] $Values) {
    $sorted = [string[]]@($Values)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return $sorted
}

function Set-AsciiField([byte[]] $Header, [int] $Offset, [int] $Length, [string] $Value) {
    $bytes = [Text.Encoding]::ASCII.GetBytes($Value)
    if ($bytes.Length -gt $Length) {
        throw "Tar header value is too long: $Value"
    }
    [Array]::Copy($bytes, 0, $Header, $Offset, $bytes.Length)
}

function Set-OctalField([byte[]] $Header, [int] $Offset, [int] $Length, [long] $Value) {
    if ($Value -lt 0) {
        throw "Tar header values cannot be negative."
    }
    $octal = [Convert]::ToString($Value, 8)
    if ($octal.Length -gt $Length - 1) {
        throw "Tar header value does not fit: $Value"
    }
    Set-AsciiField $Header $Offset $Length ($octal.PadLeft($Length - 1, '0') + [char]0)
}

function Write-TarEntry(
    [IO.Stream] $Stream,
    [string] $ArchivePath,
    [byte[]] $Content) {
    if ($ArchivePath.Length -gt 100 -or $ArchivePath -notmatch '^[\x20-\x7e]+$') {
        throw "The deterministic package archive requires an ASCII path of at most 100 bytes: $ArchivePath"
    }

    $header = [byte[]]::new(512)
    Set-AsciiField $header 0 100 $ArchivePath
    Set-OctalField $header 100 8 420
    Set-OctalField $header 108 8 0
    Set-OctalField $header 116 8 0
    Set-OctalField $header 124 12 $Content.Length
    Set-OctalField $header 136 12 0
    for ($index = 148; $index -lt 156; $index++) {
        $header[$index] = 32
    }
    $header[156] = [byte][char]'0'
    Set-AsciiField $header 257 6 ("ustar" + [char]0)
    Set-AsciiField $header 263 2 "00"
    Set-AsciiField $header 265 32 "root"
    Set-AsciiField $header 297 32 "root"

    $sum = 0
    foreach ($value in $header) {
        $sum += $value
    }
    $checksum = [Convert]::ToString($sum, 8).PadLeft(6, '0') + [char]0 + ' '
    Set-AsciiField $header 148 8 $checksum

    $Stream.Write($header, 0, $header.Length)
    $Stream.Write($Content, 0, $Content.Length)
    $padding = (512 - ($Content.Length % 512)) % 512
    if ($padding -ne 0) {
        $Stream.Write([byte[]]::new($padding), 0, $padding)
    }
}

function New-DeterministicPackageArchive([string[]] $RelativeFiles) {
    $tar = [IO.MemoryStream]::new()
    try {
        foreach ($relativePath in $RelativeFiles) {
            $content = [IO.File]::ReadAllBytes((Join-Path $packageRoot $relativePath))
            Write-TarEntry $tar ("package/" + $relativePath) $content
        }
        $tar.Write([byte[]]::new(1024), 0, 1024)
        return New-DeterministicStoredGzip $tar.ToArray()
    }
    finally {
        $tar.Dispose()
    }
}

if (!(Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "Package root is missing: $packageRoot"
}
$crcProbe = Get-Crc32 ([Text.Encoding]::ASCII.GetBytes("123456789"))
if ($crcProbe -ne [uint32]0xcbf43926L) {
    throw "The deterministic gzip CRC32 implementation failed its regression probe."
}
if (!(Test-Path -LiteralPath $policyPath -PathType Leaf)) {
    throw "Release policy is missing: $policyPath"
}
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$packageJsonPath = Join-Path $packageRoot "package.json"
$package = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json
if ([int]$policy.schemaVersion -ne 1 -or
    $policy.releaseTag -cne "v0.2.0" -or
    $policy.archiveFormat -cne "ustar+gzip-stored-v1" -or
    [long]$policy.maximumArchiveBytes -ne 8388608L -or
    $policy.androidNdkRevision -cne "27.2.12479018") {
    throw "Release policy identity, archive format, budget, or toolchain revision is not approved."
}
if ($package.name -cne $policy.packageId -or $package.version -cne $policy.packageVersion) {
    throw "Package identity/version does not match the reviewed release policy."
}

$expectedTopLevel = @(
    "CHANGELOG.md",
    "CHANGELOG.md.meta",
    "Documentation~",
    "Editor",
    "Editor.meta",
    "LICENSE.md",
    "LICENSE.md.meta",
    "package.json",
    "package.json.meta",
    "README.md",
    "README.md.meta",
    "Runtime",
    "Runtime.meta",
    "Samples~",
    "Tests",
    "Tests.meta",
    "Third Party Notices.md",
    "Third Party Notices.md.meta"
)
$actualTopLevel = @(
    Get-ChildItem -LiteralPath $packageRoot -Force | ForEach-Object Name
)
$actualTopLevel = @(Get-OrdinalSortedStrings $actualTopLevel)
$expectedTopLevel = @(Get-OrdinalSortedStrings $expectedTopLevel)
Assert-Sequence "Package top-level allowlist" $actualTopLevel $expectedTopLevel

$forbiddenDirectories = @(
    "Assets", "Packages", "ProjectSettings", "Library", "Temp", "Logs",
    "Builds", "UserSettings", "bin", "obj", "Verification", "Sandbox", "URP")
foreach ($directory in Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -Directory) {
    if ($forbiddenDirectories -ccontains $directory.Name) {
        throw "Project-only or generated directory ships in the package: $(Get-RelativePackagePath $directory.FullName)"
    }
}
$expectedDirectories = @(
    "Documentation~",
    "Editor",
    "Runtime",
    "Runtime/Interop",
    "Runtime/Plugins",
    "Runtime/Plugins/android-arm64",
    "Runtime/Plugins/android-x64",
    "Runtime/Plugins/win-x64",
    "Samples~",
    "Samples~/Getting Started",
    "Samples~/Full Luau Scripting Demo",
    "Samples~/Full Luau Scripting Demo/Core",
    "Samples~/Full Luau Scripting Demo/Demo Game",
    "Samples~/Full Luau Scripting Demo/Demo Game/Art",
    "Samples~/Full Luau Scripting Demo/Demo Game/Audio",
    "Samples~/Full Luau Scripting Demo/Demo Game/Prefabs",
    "Samples~/Full Luau Scripting Demo/Demo Game/Scenes",
    "Samples~/Full Luau Scripting Demo/Demo Game/Scripts",
    "Tests",
    "Tests/EditMode"
)
$actualDirectories = @(
    Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -Directory |
        ForEach-Object { Get-RelativePackagePath $_.FullName }
)
$actualDirectories = @(Get-OrdinalSortedStrings $actualDirectories)
$expectedDirectories = @(Get-OrdinalSortedStrings $expectedDirectories)
Assert-Sequence "Package directory allowlist" $actualDirectories $expectedDirectories

$allItems = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -Force)
$guidOwners = @{}
foreach ($item in $allItems) {
    $relativePath = Get-RelativePackagePath $item.FullName
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Symbolic links/reparse points cannot ship in the package: $relativePath"
    }
    if (!$item.PSIsContainer) {
        $allowedExtensions = @(
            ".asmdef", ".cs", ".dll", ".json", ".luau", ".md",
            ".meta", ".png", ".prefab", ".so", ".unity", ".xml")
        if ($allowedExtensions -cnotcontains $item.Extension.ToLowerInvariant()) {
            throw "Unexpected package file type: $relativePath"
        }
        if ($relativePath -match '(?i)(PlayerSmoke|IntegrationSmoke|ConsumerProbe|Verification)') {
            throw "Integration, consumer-probe, or smoke content ships in the package: $relativePath"
        }
    }
    if ($item.Name.EndsWith(".meta", [StringComparison]::OrdinalIgnoreCase)) {
        $assetPath = $item.FullName.Substring(0, $item.FullName.Length - 5)
        if (!(Test-Path -LiteralPath $assetPath)) {
            throw "Orphan Unity metadata: $relativePath"
        }
        $metaText = [IO.File]::ReadAllText($item.FullName)
        $matches = [Regex]::Matches($metaText, '(?m)^guid:\s*([0-9a-fA-F]{32})\s*$')
        if ($matches.Count -ne 1) {
            throw "Unity metadata must contain one GUID: $relativePath"
        }
        $guid = $matches[0].Groups[1].Value.ToLowerInvariant()
        if ($guidOwners.ContainsKey($guid)) {
            throw "Duplicate Unity GUID '$guid' in '$relativePath' and '$($guidOwners[$guid])'."
        }
        $guidOwners[$guid] = $relativePath
        continue
    }
    if (!$item.PSIsContainer -and $relativePath -ceq "package.json") {
        continue
    }
    if ($item.PSIsContainer -and $relativePath -in @("Documentation~", "Samples~")) {
        continue
    }
    if (!(Test-Path -LiteralPath ($item.FullName + ".meta") -PathType Leaf)) {
        throw "Package asset is missing Unity metadata: $relativePath"
    }
}

$expectedArtifactPolicy = @(
    @{ Path = "Runtime/Luau.dll"; MaximumBytes = 1048576L },
    @{ Path = "Runtime/Luau.xml"; MaximumBytes = 1048576L },
    @{ Path = "Runtime/Luau.SourceGenerator.dll"; MaximumBytes = 524288L },
    @{ Path = "Runtime/Plugins/win-x64/luau_host.dll"; MaximumBytes = 2097152L },
    @{ Path = "Runtime/Plugins/android-arm64/libluau_host.so"; MaximumBytes = 3145728L },
    @{ Path = "Runtime/Plugins/android-x64/libluau_host.so"; MaximumBytes = 3145728L }
)
$policyArtifactPaths = @($policy.artifacts | ForEach-Object path)
Assert-Sequence `
    "Release artifact policy allowlist" `
    $policyArtifactPaths `
    @($expectedArtifactPolicy | ForEach-Object Path)
for ($index = 0; $index -lt $expectedArtifactPolicy.Count; $index++) {
    if ([long]$policy.artifacts[$index].maximumBytes -ne
        [long]$expectedArtifactPolicy[$index].MaximumBytes) {
        throw "Release artifact budget mismatch for '$($expectedArtifactPolicy[$index].Path)'."
    }
}
$allowedArtifacts = @(Get-OrdinalSortedStrings $policyArtifactPaths)
$artifactExtensions = @(".dll", ".so", ".xml", ".pdb", ".mdb", ".a", ".aar", ".lib", ".exe", ".jar")
$actualArtifacts = @(
    Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -File |
        Where-Object { $artifactExtensions -ccontains $_.Extension.ToLowerInvariant() } |
        ForEach-Object { Get-RelativePackagePath $_.FullName }
)
$actualArtifacts = @(Get-OrdinalSortedStrings $actualArtifacts)
Assert-Sequence "Managed/native/XML artifact allowlist" $actualArtifacts $allowedArtifacts

foreach ($artifact in @($policy.artifacts)) {
    $artifactPath = Join-Path $packageRoot $artifact.path
    if (!(Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Required package artifact is missing: $($artifact.path)"
    }
    $length = (Get-Item -LiteralPath $artifactPath).Length
    if ($length -gt [long]$artifact.maximumBytes) {
        throw "Package artifact '$($artifact.path)' is $length bytes; budget is $($artifact.maximumBytes)."
    }
}

$sampleNames = @($package.samples | ForEach-Object displayName)
$samplePaths = @($package.samples | ForEach-Object path)
Assert-Sequence `
    "Declared sample display names" `
    $sampleNames `
    @("Getting Started", "Full Luau Scripting Demo")
Assert-Sequence `
    "Declared sample paths" `
    $samplePaths `
    @("Samples~/Getting Started", "Samples~/Full Luau Scripting Demo")
$requiredSampleFiles = @(
    "Samples~/Getting Started/GettingStartedSample.cs",
    "Samples~/Getting Started/GettingStartedTarget.cs",
    "Samples~/Getting Started/GettingStarted.luau",
    "Samples~/Getting Started/README.md",
    "Samples~/Full Luau Scripting Demo/README.md",
    "Samples~/Full Luau Scripting Demo/Core/FullLuauScriptingDemo.Core.asmdef",
    "Samples~/Full Luau Scripting Demo/Core/LuauBehaviourRuntime.cs",
    "Samples~/Full Luau Scripting Demo/Core/LuauBehaviour.cs",
    "Samples~/Full Luau Scripting Demo/Core/LuauUnityCapabilities.cs",
    "Samples~/Full Luau Scripting Demo/Core/LuauUnityTableValues.cs",
    "Samples~/Full Luau Scripting Demo/Core/LuauQuaternionLibrary.cs",
    "Samples~/Full Luau Scripting Demo/Core/LuauInputLibrary.cs",
    "Samples~/Full Luau Scripting Demo/Core/README.md",
    "Samples~/Full Luau Scripting Demo/Demo Game/Prefabs/Bird.prefab",
    "Samples~/Full Luau Scripting Demo/Demo Game/Prefabs/PipePair.prefab",
    "Samples~/Full Luau Scripting Demo/Demo Game/Scenes/FlappyBird.unity",
    "Samples~/Full Luau Scripting Demo/Demo Game/Scripts/GameController.luau",
    "Samples~/Full Luau Scripting Demo/Demo Game/Scripts/PlayerController.luau",
    "Samples~/Full Luau Scripting Demo/Demo Game/Scripts/PipeController.luau",
    "Samples~/Full Luau Scripting Demo/Demo Game/Art/Bird.png",
    "Samples~/Full Luau Scripting Demo/Demo Game/Art/Pipe.png",
    "Samples~/Full Luau Scripting Demo/Demo Game/Art/Ground.png",
    "Samples~/Full Luau Scripting Demo/Demo Game/Audio/README.md",
    "Samples~/Full Luau Scripting Demo/Demo Game/README.md"
)
foreach ($sampleFile in $requiredSampleFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $packageRoot $sampleFile) -PathType Leaf)) {
        throw "Declared sample content is missing: $sampleFile"
    }
}
$demoGameRoot = Join-Path $packageRoot "Samples~/Full Luau Scripting Demo/Demo Game"
$demoGameCSharpFiles = @(
    Get-ChildItem -LiteralPath $demoGameRoot -Recurse -File -Filter "*.cs")
if ($demoGameCSharpFiles.Count -ne 0) {
    throw (
        "Full Luau Scripting Demo gameplay must remain C#-free; found: " +
        (($demoGameCSharpFiles | ForEach-Object {
            Get-RelativePackagePath $_.FullName
        }) -join ", "))
}

$xmlPath = Join-Path $packageRoot "Runtime/Luau.xml"
try {
    [xml]$xml = Get-Content -LiteralPath $xmlPath -Raw
}
catch {
    throw "Runtime/Luau.xml is not valid XML: $_"
}
if ($xml.doc.assembly.name -cne "Luau") {
    throw "Runtime/Luau.xml does not describe the Luau assembly."
}

$tagCommit = ""
if (![string]::IsNullOrWhiteSpace($Tag)) {
    if ($Tag -cne $policy.releaseTag) {
        throw "Requested tag '$Tag' does not match reviewed release tag '$($policy.releaseTag)'."
    }
    $tagCommit = (& git -C $repositoryRoot rev-parse --verify "refs/tags/$Tag^{commit}" 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $tagCommit -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Release tag does not exist or does not resolve to a commit: $Tag"
    }
    if (!$tagCommit.Equals($repositoryCommit, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The current commit '$repositoryCommit' is not exact release tag $Tag ('$tagCommit')."
    }
    $repositoryStatus = (& git -C $repositoryRoot status --porcelain --untracked-files=all 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $repositoryStatus.Length -ne 0) {
        throw "The repository working tree must be clean for exact-tag validation: $repositoryStatus"
    }

    # The release wrapper must include every frozen API/ABI/importer/artifact
    # hash gate, rather than merely validating archive shape and budgets.
    & (Join-Path $PSScriptRoot "Test-UnityPackageStatic.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "Static semantic-freeze validation failed for exact release tag $Tag."
    }

    $taggedPackageJson = (& git -C $repositoryRoot show "$Tag`:Luau.Unity/package.json" 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "The exact tag does not contain Luau.Unity/package.json."
    }
    $taggedPackage = $taggedPackageJson | ConvertFrom-Json
    if ($taggedPackage.name -cne $package.name -or $taggedPackage.version -cne $package.version) {
        throw "The exact tag has a different package identity or version."
    }
    $taggedInstallUrl = "https://github.com/Quantum-Lion-Labs/Luau-Unity.git?path=Luau.Unity#$Tag"
    $packageReadme = Get-Content -LiteralPath (Join-Path $packageRoot "README.md") -Raw
    if ($packageReadme.IndexOf($taggedInstallUrl, [StringComparison]::Ordinal) -lt 0) {
        throw "Package README does not contain the exact tagged install URL: $taggedInstallUrl"
    }
}

$relativeFiles = @(
    Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -File |
        ForEach-Object { Get-RelativePackagePath $_.FullName }
)
$relativeFiles = @(Get-OrdinalSortedStrings $relativeFiles)

if (![string]::IsNullOrWhiteSpace($Tag)) {
    $taggedRelativeFiles = @(
        & git -C $repositoryRoot ls-tree -r --name-only $Tag -- Luau.Unity 2>&1 |
            ForEach-Object {
                $path = [string]$_
                if ($path.StartsWith("Luau.Unity/", [StringComparison]::Ordinal)) {
                    $path.Substring("Luau.Unity/".Length)
                }
            }
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enumerate exact package files from release tag $Tag."
    }
    $taggedRelativeFiles = @(Get-OrdinalSortedStrings $taggedRelativeFiles)
    Assert-Sequence "Exact-tag package file inventory" $relativeFiles $taggedRelativeFiles

    foreach ($relativePath in $relativeFiles) {
        $tagObject = (& git -C $repositoryRoot rev-parse "$Tag`:Luau.Unity/$relativePath" 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $tagObject -notmatch '^[0-9a-fA-F]{40}$') {
            throw "Unable to resolve exact-tag blob for Luau.Unity/$relativePath."
        }
        $workingObject = (& git -C $repositoryRoot hash-object --no-filters -- (Join-Path $packageRoot $relativePath) 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $workingObject -notmatch '^[0-9a-fA-F]{40}$') {
            throw "Unable to hash working package file: $relativePath"
        }
        if (!$tagObject.Equals($workingObject, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Working package content differs from exact release tag $Tag`: $relativePath"
        }
    }
}

$archiveBytes = New-DeterministicPackageArchive $relativeFiles
$secondArchiveBytes = New-DeterministicPackageArchive $relativeFiles
$archiveHash = Get-Sha256Bytes $archiveBytes
if ($archiveHash -cne (Get-Sha256Bytes $secondArchiveBytes)) {
    throw "The package archive is not deterministic across identical inputs."
}
if ($archiveBytes.Length -gt [long]$policy.maximumArchiveBytes) {
    throw "Package archive is $($archiveBytes.Length) bytes; budget is $($policy.maximumArchiveBytes)."
}

$fileRecords = @(
    foreach ($relativePath in $relativeFiles) {
        $file = Get-Item -LiteralPath (Join-Path $packageRoot $relativePath)
        [ordered]@{
            path = $relativePath
            bytes = $file.Length
            sha256 = Get-Sha256File $file.FullName
        }
    }
)
$manifest = [ordered]@{
    schemaVersion = 1
    packageId = $package.name
    packageVersion = $package.version
    sourceCommit = $repositoryCommit
    releasePolicySha256 = Get-Sha256File $policyPath
    archive = [ordered]@{
        name = "$($package.name)-$($package.version).tgz"
        format = $policy.archiveFormat
        bytes = $archiveBytes.Length
        sha256 = $archiveHash
    }
    files = $fileRecords
}
$manifestText = ($manifest | ConvertTo-Json -Depth 8 -Compress) + "`n"
$utf8 = [Text.UTF8Encoding]::new($false)

if ($SkipUnityConsumer -and [string]::IsNullOrWhiteSpace($Tag)) {
    throw "-SkipUnityConsumer requires -Tag so exact-tag validation cannot be bypassed."
}

if (![string]::IsNullOrWhiteSpace($Tag) -and !$SkipUnityConsumer) {
    $consumerScript = Join-Path $PSScriptRoot "Test-UnityPackageConsumer.ps1"
    $consumerArguments = @{
        PackageReference = $taggedInstallUrl
        ExpectedGitCommit = $tagCommit
        UnityTimeoutMinutes = $ConsumerTimeoutMinutes
    }
    if (![string]::IsNullOrWhiteSpace($UnityPath)) {
        $consumerArguments["UnityPath"] = $UnityPath
    }
    if (![string]::IsNullOrWhiteSpace($ConsumerOutputRoot)) {
        $consumerArguments["OutputRoot"] = $ConsumerOutputRoot
    }
    & $consumerScript @consumerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The generated consumer failed against exact release tag $Tag."
    }
}

if (![string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
    if ($resolvedOutput.StartsWith(
        $packageRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "The release archive must be outside the package tree."
    }
    $outputDirectory = Split-Path -Parent $resolvedOutput
    if (![string]::IsNullOrEmpty($outputDirectory)) {
        [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    }
    $manifestPath = $resolvedOutput + ".manifest.json"
    if ($Check) {
        if (!(Test-Path -LiteralPath $resolvedOutput -PathType Leaf) -or
            !(Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "Release archive or manifest is missing. Refresh without -Check first."
        }
        if ((Get-Sha256File $resolvedOutput) -cne $archiveHash) {
            throw "Release archive is stale: $resolvedOutput"
        }
        $actualManifest = [IO.File]::ReadAllText($manifestPath)
        if ($actualManifest -cne $manifestText) {
            throw "Release package manifest is stale: $manifestPath"
        }
    }
    else {
        [IO.File]::WriteAllBytes($resolvedOutput, $archiveBytes)
        [IO.File]::WriteAllText($manifestPath, $manifestText, $utf8)
    }
}
elseif ($Check) {
    throw "-Check requires -OutputPath."
}

Write-Host "Unity package release validation passed."
Write-Host "  Files: $($relativeFiles.Count)"
Write-Host "  Archive: $($archiveBytes.Length) bytes"
Write-Host "  SHA256: $archiveHash"
