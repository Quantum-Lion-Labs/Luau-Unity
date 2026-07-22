Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$packageRoot = Join-Path $repositoryRoot "Luau.Unity"
$integrationProjectRoot = Join-Path $repositoryRoot "tests/Luau.Unity.Integration"
$upstreamRoot = Join-Path $repositoryRoot "native/luau"
$hostRoot = Join-Path $repositoryRoot "native/luau-host"

function Get-RequiredFile {
    param(
        [string] $Root,
        [string] $RelativePath,
        [string] $Description
    )

    $path = Join-Path $Root $RelativePath
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$Description is missing: $path"
    }

    return $path
}

function Get-RepositoryFile {
    param([string] $RelativePath)

    return Get-RequiredFile $repositoryRoot $RelativePath "Required repository file"
}

function Get-PackageFile {
    param([string] $RelativePath)

    return Get-RequiredFile $packageRoot $RelativePath "Required package file"
}

function Get-IntegrationFile {
    param([string] $RelativePath)

    return Get-RequiredFile $integrationProjectRoot $RelativePath "Required integration-project file"
}

function Read-RepositoryText {
    param([string] $RelativePath)

    return [IO.File]::ReadAllText((Get-RepositoryFile $RelativePath))
}

function Read-RepositoryJson {
    param([string] $RelativePath)

    return Read-RepositoryText $RelativePath | ConvertFrom-Json
}

function Read-PackageText {
    param([string] $RelativePath)

    return [IO.File]::ReadAllText((Get-PackageFile $RelativePath))
}

function Read-PackageJson {
    param([string] $RelativePath)

    return Read-PackageText $RelativePath | ConvertFrom-Json
}

function Read-IntegrationText {
    param([string] $RelativePath)

    return [IO.File]::ReadAllText((Get-IntegrationFile $RelativePath))
}

function Read-IntegrationJson {
    param([string] $RelativePath)

    return Read-IntegrationText $RelativePath | ConvertFrom-Json
}

function Assert-Equal {
    param(
        [string] $Description,
        $Actual,
        $Expected
    )

    if ($Actual -ne $Expected) {
        throw "$Description mismatch. Expected '$Expected', found '$Actual'."
    }
}

function Assert-SequenceEqual {
    param(
        [string] $Description,
        [object[]] $Actual,
        [object[]] $Expected
    )

    $actualValues = @($Actual)
    $expectedValues = @($Expected)
    if ($actualValues.Count -ne $expectedValues.Count) {
        throw "$Description mismatch. Expected $($expectedValues.Count) value(s), found $($actualValues.Count)."
    }

    for ($index = 0; $index -lt $expectedValues.Count; $index++) {
        if ($actualValues[$index] -ne $expectedValues[$index]) {
            throw "$Description mismatch at index $index. Expected '$($expectedValues[$index])', found '$($actualValues[$index])'."
        }
    }
}

function Assert-ContainsLiteral {
    param(
        [string] $Description,
        [string] $Text,
        [string] $Literal
    )

    if ($Text.IndexOf($Literal, [StringComparison]::Ordinal) -lt 0) {
        throw "$Description is missing required text: $Literal"
    }
}

function Assert-NotContainsLiteral {
    param(
        [string] $Description,
        [string] $Text,
        [string] $Literal
    )

    if ($Text.IndexOf($Literal, [StringComparison]::Ordinal) -ge 0) {
        throw "$Description contains forbidden text: $Literal"
    }
}

function Assert-LiteralCount {
    param(
        [string] $Description,
        [string] $Text,
        [string] $Literal,
        [int] $Expected
    )

    $count = 0
    $offset = 0
    while (($offset = $Text.IndexOf($Literal, $offset, [StringComparison]::Ordinal)) -ge 0) {
        $count++
        $offset += $Literal.Length
    }

    if ($count -ne $Expected) {
        throw "$Description mismatch. Expected '$Literal' $Expected time(s), found $count."
    }
}

function Assert-LiteralOrder {
    param(
        [string] $Description,
        [string] $Text,
        [string[]] $Literals
    )

    $offset = 0
    foreach ($literal in $Literals) {
        $index = $Text.IndexOf($literal, $offset, [StringComparison]::Ordinal)
        if ($index -lt 0) {
            throw "$Description is missing or misorders required text: $literal"
        }
        $offset = $index + $literal.Length
    }
}

function Assert-PathAbsent {
    param([string] $RelativePath)

    if (Test-Path -LiteralPath (Join-Path $repositoryRoot $RelativePath)) {
        throw "Retired package surface must remain absent: $RelativePath"
    }
}

function Assert-PackagePathAbsent {
    param([string] $RelativePath)

    if (Test-Path -LiteralPath (Join-Path $packageRoot $RelativePath)) {
        throw "Retired or non-package surface must remain absent from the package: $RelativePath"
    }
}

function Assert-FileHash {
    param(
        [string] $Description,
        [string] $Path,
        [string] $ExpectedSha256
    )

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (!$actual.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description SHA256 mismatch. Expected '$ExpectedSha256', found '$actual'."
    }
}

function Get-CanonicalUtf8Sha256 {
    param([byte[]] $Bytes)

    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $canonicalUtf8 = [Text.UTF8Encoding]::new($false)
    $text = $strictUtf8.GetString($Bytes)
    $canonicalText = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha256.ComputeHash($canonicalUtf8.GetBytes($canonicalText)))).Replace("-", "")
    }
    finally {
        $sha256.Dispose()
    }
}

function Assert-CanonicalTextFileHash {
    param(
        [string] $Description,
        [string] $Path,
        [string] $ExpectedSha256
    )

    $actual = Get-CanonicalUtf8Sha256 ([IO.File]::ReadAllBytes($Path))
    if (!$actual.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description canonical UTF-8 SHA256 mismatch. Expected '$ExpectedSha256', found '$actual'."
    }
}

function Assert-FileLength {
    param(
        [string] $Description,
        [string] $Path,
        [long] $ExpectedLength
    )

    $actual = (Get-Item -LiteralPath $Path).Length
    if ($actual -ne $ExpectedLength) {
        throw "$Description length mismatch. Expected '$ExpectedLength', found '$actual'."
    }
}

function Get-MetaGuid {
    param(
        [string] $Description,
        [string] $Path
    )

    $text = [IO.File]::ReadAllText($Path)
    $matches = [Regex]::Matches($text, '(?m)^guid:\s*([0-9a-fA-F]{32})\s*$')
    if ($matches.Count -ne 1) {
        throw "$Description must contain exactly one 32-digit Unity GUID; found $($matches.Count)."
    }

    return $matches[0].Groups[1].Value.ToLowerInvariant()
}

function Assert-MetaGuid {
    param(
        [string] $Description,
        [string] $Path,
        [string] $ExpectedGuid
    )

    Assert-Equal $Description (Get-MetaGuid $Description $Path) $ExpectedGuid
}

function Get-PackageRelativePath {
    param([string] $Path)

    $relative = [IO.Path]::GetFullPath($Path).Substring($packageRoot.Length)
    return $relative.TrimStart([char[]]@('\', '/')).Replace('\', '/')
}

function Get-ImporterPlatformSection {
    param(
        [string] $Description,
        [string] $ImporterText,
        [string] $Platform
    )

    $pattern = '(?ms)^    ' + [Regex]::Escape($Platform) +
        ':\r?\n(?<section>.*?)(?=^    [A-Za-z0-9]+:\r?$|^  userData:)'
    $match = [Regex]::Match($ImporterText, $pattern)
    if (!$match.Success) {
        throw "$Description is missing the $Platform platform importer block."
    }

    return $match.Groups['section'].Value
}

function Assert-ImporterPlatform {
    param(
        [string] $Description,
        [string] $ImporterText,
        [string] $Platform,
        [bool] $Enabled,
        [string[]] $RequiredSettings = @()
    )

    $section = Get-ImporterPlatformSection $Description $ImporterText $Platform
    $expectedEnabled = if ($Enabled) { '1' } else { '0' }
    Assert-ContainsLiteral "$Description $Platform state" $section "enabled: $expectedEnabled"
    foreach ($setting in $RequiredSettings) {
        Assert-ContainsLiteral "$Description $Platform settings" $section $setting
    }
}

if (!(Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "The standalone UPM package root is missing: $packageRoot"
}
if (!(Test-Path -LiteralPath $integrationProjectRoot -PathType Container)) {
    throw "The Unity integration project is missing: $integrationProjectRoot"
}

$canonicalHashProbeUtf8 = [Text.UTF8Encoding]::new($false)
$canonicalHashLf = Get-CanonicalUtf8Sha256 (
    $canonicalHashProbeUtf8.GetBytes("stage-6`npackage-metadata`n"))
$canonicalHashCrLf = Get-CanonicalUtf8Sha256 (
    $canonicalHashProbeUtf8.GetBytes("stage-6`r`npackage-metadata`r`n"))
Assert-Equal "Canonical text hash line-ending regression probe" $canonicalHashCrLf $canonicalHashLf

$gitAttributes = Read-RepositoryText ".gitattributes"
foreach ($requiredAttribute in @(
    "*.meta text eol=lf",
    "*.asmdef text eol=lf",
    "*.json text eol=lf",
    "*.luau text eol=lf",
    "*.md text eol=lf",
    "*.xml text eol=lf",
    "tests/Luau.Tests/PublicApi.approved.txt text eol=lf",
    "native/luau-host/include/luau_host.h text eol=lf",
    "native/luau-host/src/luau_host.cpp text eol=lf",
    "native/luau-host/src/reference_tokens.h text eol=lf",
    "native/luau-host/src/tracked_allocation.h text eol=lf",
    "native/luau-host/exports/luau_host.exports text eol=lf"
)) {
    Assert-ContainsLiteral "Hash-pinned text checkout policy" $gitAttributes $requiredAttribute
}
Write-Host "PASS: hash-pinned text contracts use canonical UTF-8 hashes and LF checkout policy."

$packageTopLevel = @(
    Get-ChildItem -LiteralPath $packageRoot -Force |
        ForEach-Object Name |
        Sort-Object
)
Assert-SequenceEqual `
    "Stage 6 package top-level content" `
    $packageTopLevel `
    @(
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
        "Third Party Notices.md.meta")
Write-Host "PASS: Luau.Unity has the exact Stage 6 release-content top-level allowlist."

$package = Read-PackageJson "package.json"
Assert-Equal "Unity package name" $package.name "com.qll.luau.unity"
Assert-Equal "Unity package version" $package.version "0.2.0"
Assert-Equal "Unity package display name" $package.displayName "Luau.Unity"
Assert-Equal `
    "Unity package description" `
    $package.description `
    "A bounded, Unity-first managed host for the official Luau VM, with source-generated APIs and maintained Windows and Android native plugins."
Assert-Equal "Unity package minimum Unity version" $package.unity "6000.3"
Assert-Equal "Unity package minimum Unity release" $package.unityRelease "19f1"
Assert-Equal "Unity package license" $package.license "MIT"
Assert-Equal "Unity package author" $package.author.name "Quantum Lion Labs"
Assert-Equal "Unity package repository" $package.repository.url "https://github.com/Quantum-Lion-Labs/Luau-Unity.git"
Assert-Equal "Unity package repository directory" $package.repository.directory "Luau.Unity"
Assert-Equal "Unity package issues" $package.bugs.url "https://github.com/Quantum-Lion-Labs/Luau-Unity/issues"
foreach ($urlProperty in @("licensesUrl", "documentationUrl", "changelogUrl")) {
    $url = [string]$package.$urlProperty
    if (!$url.StartsWith("https://github.com/Quantum-Lion-Labs/Luau-Unity/", [StringComparison]::Ordinal) -or
        $url.IndexOf("v0.2.0", [StringComparison]::Ordinal) -lt 0) {
        throw "Unity package $urlProperty is not pinned to the canonical v0.2.0 tree: $url"
    }
}
$sampleNames = @($package.samples | ForEach-Object displayName)
$samplePaths = @($package.samples | ForEach-Object path)
Assert-SequenceEqual "Unity package sample names" $sampleNames @("Getting Started", "Capability Binding", "Luau Behaviour")
Assert-SequenceEqual `
    "Unity package sample paths" `
    $samplePaths `
    @("Samples~/Getting Started", "Samples~/Capability Binding", "Samples~/Luau Behaviour")
$dependencyProperty = $package.PSObject.Properties["dependencies"]
if ($null -ne $dependencyProperty -and @($dependencyProperty.Value.PSObject.Properties).Count -ne 0) {
    throw "The Unity package must not rely on development-project dependencies."
}
Write-Host "PASS: final package identity, preview version, origin, legal links, and samples are declared without dependencies."

$requiredReleaseFiles = @(
    "README.md",
    "CHANGELOG.md",
    "LICENSE.md",
    "Third Party Notices.md",
    "Documentation~/getting-started.md",
    "Documentation~/execution-and-trust.md",
    "Documentation~/capability-bindings.md",
    "Documentation~/resource-limits.md",
    "Documentation~/modules.md",
    "Documentation~/script-instances.md",
    "Documentation~/artifacts.md",
    "Documentation~/compiler-security.md",
    "Samples~/Getting Started/GettingStartedSample.cs",
    "Samples~/Getting Started/GettingStarted.luau",
    "Samples~/Capability Binding/CapabilityBindingSample.cs",
    "Samples~/Capability Binding/CapabilityBinding.luau",
    "Samples~/Luau Behaviour/LuauBehaviourRuntimeSample.cs",
    "Samples~/Luau Behaviour/LuauBehaviourSample.cs",
    "Samples~/Luau Behaviour/LuauBehaviour.luau"
)
foreach ($releaseFile in $requiredReleaseFiles) {
    Get-PackageFile $releaseFile | Out-Null
}
$packageReadme = Read-PackageText "README.md"
Assert-ContainsLiteral `
    "Tagged package installation" `
    $packageReadme `
    "https://github.com/Quantum-Lion-Labs/Luau-Unity.git?path=Luau.Unity#v0.2.0"
foreach ($contract in @(
    "LuauResultScope",
    "source-only by default",
    "runtime mod-source limits",
    "Windows x64 and Android ARM64/x86_64"
)) {
    Assert-ContainsLiteral "Package README product contract" $packageReadme $contract
}
$trackedDocumentation = @("README.md") + @(
    Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter "*.md" |
        ForEach-Object { $_.FullName })
foreach ($documentationPath in $trackedDocumentation) {
    $documentationText = if ([IO.Path]::IsPathRooted($documentationPath)) {
        [IO.File]::ReadAllText($documentationPath)
    } else {
        Read-RepositoryText $documentationPath
    }
    foreach ($forbiddenLink in @("](docs/plans/", "](../docs/plans/")) {
        Assert-NotContainsLiteral "Tracked release documentation $documentationPath" $documentationText $forbiddenLink
    }
}
$gettingStartedSample = Read-PackageText "Samples~/Getting Started/GettingStartedSample.cs"
foreach ($required in @(
    '[LuauLibrary("sample")]',
    "ConfigureHostApis = state =>",
    "using var root = LuauUnity.CreateState(",
    "using var results = await root.ExecuteAsync(",
    "destroyCancellationToken"
)) {
    Assert-ContainsLiteral "Getting Started sample" $gettingStartedSample $required
}
$capabilitySample = Read-PackageText "Samples~/Capability Binding/CapabilityBindingSample.cs"
foreach ($required in @(
    "GameObject target;",
    "using var sandbox = root.CreateSandboxedThread();",
    "using var targetHandle = root.CreateHandle(target);",
    'sandbox["target"] = targetHandle;',
    "using var results = await sandbox.ExecuteAsync("
)) {
    Assert-ContainsLiteral "Capability Binding sample" $capabilitySample $required
}
foreach ($forbidden in @("GameObject.Find", "FindObjectOfType", "FindFirstObjectByType", "Resources.Load")) {
    Assert-NotContainsLiteral "Capability Binding sample" $capabilitySample $forbidden
}
$luauBehaviourRuntimeSample = Read-PackageText "Samples~/Luau Behaviour/LuauBehaviourRuntimeSample.cs"
$luauBehaviourSample = Read-PackageText "Samples~/Luau Behaviour/LuauBehaviourSample.cs"
foreach ($required in @(
    "new LuauScriptScheduler(root)",
    '"Update"',
    "WallClockLimit = TimeSpan.FromMilliseconds(2)",
    "InterruptCountLimit = 10_000",
    "AggregateWallClockBudget = TimeSpan.FromMilliseconds(4)",
    "LuauScriptPhaseFailureMode.DisableAndContinue"
)) {
    Assert-ContainsLiteral "Luau Behaviour runtime sample" $luauBehaviourRuntimeSample $required
}
foreach ($required in @(
    "LuauBehaviourRuntimeSample runtimeHost;",
    "LuauAsset script;",
    "CreateScriptInstanceAsync(",
    'thread["self"] = self;',
    'GetRequiredEntrypoint("update")',
    "runtimeHost.Register(this, update, updateOrder)",
    'TryGetEntrypoint("destroy"',
    "instance?.Dispose();"
)) {
    Assert-ContainsLiteral "Luau Behaviour component sample" $luauBehaviourSample $required
}
foreach ($forbidden in @("GameObject.Find", "FindObjectOfType", "FindFirstObjectByType", "Resources.Load", "static LuauBehaviourRuntimeSample")) {
    Assert-NotContainsLiteral "Luau Behaviour sample" ($luauBehaviourRuntimeSample + $luauBehaviourSample) $forbidden
}
Write-Host "PASS: tracked package docs, legal notices, trust guidance, and all three importable public-contract samples are complete."

$forbiddenDirectoryNames = @(
    "Assets",
    "Packages",
    "ProjectSettings",
    "Library",
    "Temp",
    "Logs",
    "Builds",
    "UserSettings",
    "bin",
    "obj",
    "Sandbox",
    "URP",
    "Verification"
)
foreach ($directory in Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -Directory) {
    if ($forbiddenDirectoryNames -contains $directory.Name) {
        throw "Development, generated, or project-only directory ships in the package: $(Get-PackageRelativePath $directory.FullName)"
    }
}

foreach ($item in Get-ChildItem -LiteralPath $packageRoot -Recurse -Force) {
    if ($item.Name.EndsWith(".meta", [StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    $relativePath = Get-PackageRelativePath $item.FullName
    if (!$item.PSIsContainer -and $relativePath -eq "package.json") {
        continue
    }
    if ($item.PSIsContainer -and $relativePath -in @("Documentation~", "Samples~")) {
        continue
    }

    if (!(Test-Path -LiteralPath ($item.FullName + ".meta") -PathType Leaf)) {
        throw "Package asset is missing its Unity metadata: $relativePath"
    }
}

$guidOwners = @{}
foreach ($metaFile in Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -File -Filter "*.meta") {
    $assetPath = $metaFile.FullName.Substring(0, $metaFile.FullName.Length - ".meta".Length)
    $relativeMetaPath = Get-PackageRelativePath $metaFile.FullName
    if (!(Test-Path -LiteralPath $assetPath)) {
        throw "Orphan Unity metadata has no corresponding package asset: $relativeMetaPath"
    }

    $guid = Get-MetaGuid "Package metadata $relativeMetaPath" $metaFile.FullName
    if ($guidOwners.ContainsKey($guid)) {
        throw "Duplicate Unity GUID '$guid' appears in '$($guidOwners[$guid])' and '$relativeMetaPath'."
    }
    $guidOwners[$guid] = $relativeMetaPath
}
Write-Host "PASS: every imported package asset has metadata, hidden UPM roots are exempt, and all package GUIDs are unique."

$allowedBinaryPaths = @(
    "Runtime/Luau.dll",
    "Runtime/Luau.SourceGenerator.dll",
    "Runtime/Luau.xml",
    "Runtime/Plugins/android-arm64/libluau_host.so",
    "Runtime/Plugins/android-x64/libluau_host.so",
    "Runtime/Plugins/win-x64/luau_host.dll"
)
$binaryExtensions = @(".a", ".aar", ".bundle", ".dll", ".dylib", ".exe", ".jar", ".lib", ".mdb", ".pdb", ".so", ".xml")
$actualBinaryPaths = @(
    Get-ChildItem -LiteralPath $packageRoot -Recurse -Force -File |
        Where-Object { $binaryExtensions -contains $_.Extension.ToLowerInvariant() } |
        ForEach-Object { Get-PackageRelativePath $_.FullName } |
        Sort-Object
)
Assert-SequenceEqual "Stage 6 managed/native/XML artifact inventory" $actualBinaryPaths $allowedBinaryPaths
Write-Host "PASS: the package contains only the six approved managed/native/XML artifacts."

$managedProject = Read-RepositoryText "src/Luau/Luau.csproj"
Assert-ContainsLiteral `
    "Managed XML documentation build" `
    $managedProject `
    "<GenerateDocumentationFile>true</GenerateDocumentationFile>"
Assert-ContainsLiteral `
    "Managed XML documentation completeness gate" `
    $managedProject `
    '<WarningsAsErrors>$(WarningsAsErrors);CS1591</WarningsAsErrors>'
$managedXmlPath = Get-PackageFile "Runtime/Luau.xml"
try {
    [xml]$managedXml = Get-Content -LiteralPath $managedXmlPath -Raw
}
catch {
    throw "Runtime/Luau.xml is not well-formed XML: $_"
}
Assert-Equal "Managed XML assembly identity" $managedXml.doc.assembly.name "Luau"
$documentedMemberNames = @($managedXml.doc.members.member | ForEach-Object name)
foreach ($requiredMember in @(
    "T:Luau.LuauState",
    "T:Luau.LuauResultScope",
    "T:Luau.LuauBytecodeArtifactCodec",
    "T:Luau.LuauModuleMap",
    "T:Luau.LuauRequirer",
    "T:Luau.LuauStateOptions",
    'M:Luau.LuauCallContext.Read``1(System.Int32)',
    "M:Luau.LuauValue.Retain",
    "M:Luau.LuauState.CreateSandboxedThread",
    "M:Luau.LuauState.ExecuteCompilerOutput(Luau.LuauCompilerOutput,System.ReadOnlySpan{System.Char},Luau.LuauExecutionOptions)"
)) {
    if ($documentedMemberNames -cnotcontains $requiredMember) {
        throw "Runtime/Luau.xml is missing required IntelliSense member: $requiredMember"
    }
}
$semanticDocumentation = @(
    @{ Name = "T:Luau.LuauState"; Text = "Root disposal cancels active operations" },
    @{ Name = "T:Luau.LuauResultScope"; Text = "Dispose the scope deterministically" },
    @{ Name = "T:Luau.LuauBytecodeArtifactCodec"; Text = "never grants compiler-output trust" },
    @{ Name = "T:Luau.LuauModuleMap"; Text = "never performs filesystem or network resolution" },
    @{ Name = "T:Luau.LuauRequirer"; Text = "explicitly authorized modules" },
    @{ Name = "M:Luau.LuauValue.Retain"; Text = "independently disposable owner" },
    @{ Name = "M:Luau.LuauState.CreateSandboxedThread"; Text = "isolated writable global proxy" },
    @{ Name = "M:Luau.LuauState.ExecuteCompilerOutput(Luau.LuauCompilerOutput,System.ReadOnlySpan{System.Char},Luau.LuauExecutionOptions)"; Text = "Dispose the returned scope" }
)
foreach ($requirement in $semanticDocumentation) {
    $member = @($managedXml.doc.members.member | Where-Object { $_.name -ceq $requirement.Name })
    if ($member.Count -ne 1) {
        throw "Runtime/Luau.xml must contain exactly one semantic documentation member '$($requirement.Name)'."
    }
    $normalizedMemberText = [regex]::Replace([string]$member[0].InnerText, "\s+", " ").Trim()
    if ($normalizedMemberText -cnotlike "*$($requirement.Text)*") {
        throw "Runtime/Luau.xml member '$($requirement.Name)' is missing required ownership/trust guidance '$($requirement.Text)'."
    }
}
$copyManagedArtifacts = Read-RepositoryText "tools/Copy-DotNetArtifactsToUnity.ps1"
foreach ($required in @(
    'Source = "src/Luau/bin/$Configuration/netstandard2.1/Luau.xml"',
    'FileName = "Luau.xml"',
    'CanonicalText = $true',
    "function Get-CanonicalUtf8Bytes",
    '$destinationMeta = "$destination.meta"'
)) {
    Assert-ContainsLiteral "Managed XML artifact copy/check" $copyManagedArtifacts $required
}
Write-Host "PASS: deterministic Luau XML IntelliSense is built, copied, documented, and metadata-protected."

$artifactCodec = Read-RepositoryText "src/Luau/LuauBytecodeArtifactCodec.cs"
foreach ($required in @(
    "public const int CurrentFormatVersion = 1;",
    "IBufferWriter<byte> destination",
    "Stream destination",
    "ReadOnlySpan<byte> encoded",
    'CheckLimit("envelope", encoded.Length, effectiveLimits.MaxEnvelopeBytes);',
    'CheckLimit("sourceIdentity", sourceIdentityLength, effectiveLimits.MaxSourceIdentityBytes);',
    'CheckLimit("provenanceId", provenanceIdLength, effectiveLimits.MaxProvenanceIdBytes);',
    'CheckLimit("provenanceData", provenanceLength, effectiveLimits.MaxProvenanceBytes);',
    'CheckLimit("bytecode", bytecodeLength, effectiveLimits.MaxBytecodeBytes);',
    "CryptographicOperations.FixedTimeEquals",
    "LuauArtifactFailureKind.IntegrityMismatch",
    "LuauArtifactFailureKind.RuntimeIdentityMismatch",
    "The artifact envelope contains trailing bytes.",
    "Artifact field lengths overflow the managed envelope size."
)) {
    Assert-ContainsLiteral "Managed artifact codec" $artifactCodec $required
}

$artifactLimits = Read-RepositoryText "src/Luau/LuauArtifactLimits.cs"
foreach ($required in @(
    "int? maxEnvelopeBytes = 8 * 1024 * 1024;",
    "int? maxBytecodeBytes = 4 * 1024 * 1024;",
    "int? maxProvenanceBytes = 64 * 1024;",
    "int? maxProvenanceIdBytes = 256;",
    "int? maxSourceIdentityBytes = 1024;",
    "public static LuauArtifactLimits UnsafeUnbounded"
)) {
    Assert-ContainsLiteral "Managed artifact limits" $artifactLimits $required
}

$artifactCodecTests = Read-RepositoryText "tests/Luau.Tests/ArtifactCodecTests.cs"
foreach ($required in @(
    "SpanStreamAndBufferWriterRoundTripsPreserveArtifactMetadata",
    "TruncationAndTrailingBytesAreTypedMalformedFailures",
    "DeclaredLengthOverflowIsRejectedBeforePayloadAllocation",
    "EveryEnvelopeFieldLimitIsCheckedAtExactAndOneOverBoundaries",
    "IdentityCorruptionAndInvalidUtf8HaveTypedDiagnostics",
    "BoundedRandomMutationCorpusOnlyProducesArtifactsOrTypedRejections",
    "BoundedStreamRejectsOneByteOverWithoutReturningAnArtifact"
)) {
    Assert-ContainsLiteral "Managed artifact codec tests" $artifactCodecTests $required
}
Write-Host "PASS: the managed artifact envelope is versioned, bounded before allocation, integrity checked, typed on rejection, and independently mutation tested."

$artifactBaselines = @(
    @{ Path = "Runtime/Luau.dll"; Length = 276992L; Sha256 = "2EA5972C5205268275CA52CC3B87367E8FECC49F71CFCF534EBD36E2735A489D" },
    @{ Path = "Runtime/Luau.xml"; Length = 175918L; Sha256 = "AFEF6ABD26CAA0167DD5FD2CABEE5AAFB64CF066D8446AAE20336AC78760C714" },
    @{ Path = "Runtime/Luau.SourceGenerator.dll"; Length = 63488L; Sha256 = "E6EC90D4A4152CEE1D5EC5F5F48D6ACCB4DAFFEB2560183B08D0CD233101D419" },
    @{ Path = "Runtime/Plugins/win-x64/luau_host.dll"; Length = 995328L; Sha256 = "429671AE55387F2783C70526D18CAFB68253B04EEB278B638D57ED13C724F0D0" },
    @{ Path = "Runtime/Plugins/android-arm64/libluau_host.so"; Length = 866704L; Sha256 = "382907D397A5B3AEED0E7F74B8E917B027A9B563B3988EE080B147B7D4CC6266" },
    @{ Path = "Runtime/Plugins/android-x64/libluau_host.so"; Length = 900512L; Sha256 = "E21051C53411AE4B4E332D8A81BB2D96CCAC09D8F0A3AEB02835B4D4090F675D" }
)
foreach ($artifact in $artifactBaselines) {
    $artifactPath = Get-PackageFile $artifact.Path
    Assert-FileLength "Checked-in artifact $($artifact.Path)" $artifactPath $artifact.Length
    Assert-FileHash "Checked-in artifact $($artifact.Path)" $artifactPath $artifact.Sha256
}

$importerMetaBaselines = @(
    @{ Path = "Runtime/Luau.dll.meta"; Sha256 = "15053472E460343CF4695A7233307CFC03962D1C3CD1D3E73A29179A33FB11D8" },
    @{ Path = "Runtime/Luau.xml.meta"; Sha256 = "55112CC099D1C5DC2B976BF62876FE88B18F43C49BC3BA63032993D3CC118FC9" },
    @{ Path = "Runtime/Luau.SourceGenerator.dll.meta"; Sha256 = "AAB0C496B4CE44B75F1D5259F73995B3A686FEDA1626EF8D658FA489EA1AE199" },
    @{ Path = "Runtime/Plugins/win-x64/luau_host.dll.meta"; Sha256 = "699FB21B9A924DB6EFE360450AD8CBD620CB980B8001F5F4D026FAA383F3E24D" },
    @{ Path = "Runtime/Plugins/android-arm64/libluau_host.so.meta"; Sha256 = "C13B52911F78424D6F9B2C6FD86BF29F8B7680AFDE5F04B51664A06F117B1D01" },
    @{ Path = "Runtime/Plugins/android-x64/libluau_host.so.meta"; Sha256 = "618EF509865F2810495805919BF0611968B0AFF751AEE052AC2C05E3F8681C92" }
)
foreach ($meta in $importerMetaBaselines) {
    Assert-CanonicalTextFileHash `
        "Checked-in importer metadata $($meta.Path)" `
        (Get-PackageFile $meta.Path) `
        $meta.Sha256
}
Write-Host "PASS: managed/XML/analyzer/native artifacts and importer metadata match the Stage 6 semantic-freeze hashes."

$managedImporter = Read-PackageText "Runtime/Luau.dll.meta"
Assert-ContainsLiteral "Managed runtime importer" $managedImporter "isExplicitlyReferenced: 0"
Assert-ContainsLiteral "Managed runtime importer" $managedImporter "validateReferences: 1"
Assert-ImporterPlatform "Managed runtime importer" $managedImporter "Any" $true @("Exclude Android: 0", "Exclude Editor: 0", "Exclude Win64: 0")

$analyzerImporter = Read-PackageText "Runtime/Luau.SourceGenerator.dll.meta"
Assert-ContainsLiteral "Source generator importer" $analyzerImporter "labels:`n- RoslynAnalyzer"
Assert-ContainsLiteral "Source generator importer" $analyzerImporter "isExplicitlyReferenced: 1"
Assert-ContainsLiteral "Source generator importer" $analyzerImporter "validateReferences: 0"
foreach ($platform in @("Android", "Any", "Editor", "Linux64", "OSXUniversal", "Win", "Win64", "WindowsStoreApps", "iOS")) {
    Assert-ImporterPlatform "Source generator importer" $analyzerImporter $platform $false
}
Assert-ContainsLiteral "Source generator importer Any exclusions" (Get-ImporterPlatformSection "Source generator importer" $analyzerImporter "Any") "Exclude WebGL: 1"

$windowsImporter = Read-PackageText "Runtime/Plugins/win-x64/luau_host.dll.meta"
Assert-ImporterPlatform "Windows native importer" $windowsImporter "Any" $false @("Exclude Android: 1", "Exclude Editor: 0", "Exclude Win64: 0")
Assert-ImporterPlatform "Windows native importer" $windowsImporter "Editor" $true @("CPU: x86_64", "OS: Windows")
Assert-ImporterPlatform "Windows native importer" $windowsImporter "Win64" $true @("CPU: x86_64")
foreach ($platform in @("Android", "Linux64", "OSXUniversal", "Win", "WindowsStoreApps", "iOS")) {
    Assert-ImporterPlatform "Windows native importer" $windowsImporter $platform $false
}

$androidArm64Importer = Read-PackageText "Runtime/Plugins/android-arm64/libluau_host.so.meta"
Assert-ImporterPlatform "Android ARM64 native importer" $androidArm64Importer "Android" $true @("CPU: ARM64")
Assert-ImporterPlatform "Android ARM64 native importer" $androidArm64Importer "Any" $false @("Exclude Android: 0", "Exclude Editor: 1", "Exclude Win64: 1")
foreach ($platform in @("Editor", "Linux64", "OSXUniversal", "Win", "Win64", "iOS")) {
    Assert-ImporterPlatform "Android ARM64 native importer" $androidArm64Importer $platform $false
}

$androidX64Importer = Read-PackageText "Runtime/Plugins/android-x64/libluau_host.so.meta"
Assert-ImporterPlatform "Android x86_64 native importer" $androidX64Importer "Android" $true @("CPU: X86_64")
Assert-ImporterPlatform "Android x86_64 native importer" $androidX64Importer "Any" $false @("Exclude Android: 0", "Exclude Editor: 1", "Exclude Win64: 1")
foreach ($platform in @("Editor", "Linux64", "OSXUniversal", "Win", "Win64", "iOS")) {
    Assert-ImporterPlatform "Android x86_64 native importer" $androidX64Importer $platform $false
}
Write-Host "PASS: analyzer and native importer matrices exactly select analyzer-only, Windows x86_64, Android ARM64, and Android x86_64 targets."


$runtimeAsmdef = Read-PackageJson "Runtime/Luau.Unity.asmdef"
Assert-Equal "Runtime assembly name" $runtimeAsmdef.name "Luau.Unity"
Assert-Equal "Runtime root namespace" $runtimeAsmdef.rootNamespace ""
Assert-SequenceEqual `
    "Runtime assembly references" `
    @($runtimeAsmdef.references) `
    @("GUID:d61c9d398cb91443ea08b5afda6f6d75")
Assert-Equal "Runtime unsafe-code policy" ([bool]$runtimeAsmdef.allowUnsafeCode) $false
Assert-Equal "Runtime override-references policy" ([bool]$runtimeAsmdef.overrideReferences) $false
Assert-Equal "Runtime auto-reference policy" ([bool]$runtimeAsmdef.autoReferenced) $true
Assert-Equal "Runtime engine-reference policy" ([bool]$runtimeAsmdef.noEngineReferences) $false
Assert-SequenceEqual "Runtime included platforms" @($runtimeAsmdef.includePlatforms) @()
Assert-SequenceEqual "Runtime excluded platforms" @($runtimeAsmdef.excludePlatforms) @()
Assert-SequenceEqual "Runtime precompiled references" @($runtimeAsmdef.precompiledReferences) @()
Assert-SequenceEqual "Runtime define constraints" @($runtimeAsmdef.defineConstraints) @()
Assert-SequenceEqual "Runtime version defines" @($runtimeAsmdef.versionDefines) @()
Assert-MetaGuid `
    "Runtime assembly GUID" `
    (Get-PackageFile "Runtime/Luau.Unity.asmdef.meta") `
    "c727d2ef8dd2e4846ab81fbe6ca1f508"
Write-Host "PASS: runtime assembly references only package-owned interop and enables no unsafe code."

$interopAsmdef = Read-PackageJson "Runtime/Interop/Luau.Interop.asmdef"
Assert-Equal "Interop assembly name" $interopAsmdef.name "Luau.Interop"
Assert-Equal "Interop root namespace" $interopAsmdef.rootNamespace ""
Assert-SequenceEqual "Interop assembly references" @($interopAsmdef.references) @()
Assert-Equal "Interop unsafe-code policy" ([bool]$interopAsmdef.allowUnsafeCode) $true
Assert-Equal "Interop override-references policy" ([bool]$interopAsmdef.overrideReferences) $false
Assert-Equal "Interop auto-reference policy" ([bool]$interopAsmdef.autoReferenced) $false
Assert-Equal "Interop engine-reference policy" ([bool]$interopAsmdef.noEngineReferences) $true
Assert-SequenceEqual "Interop included platforms" @($interopAsmdef.includePlatforms) @()
Assert-SequenceEqual "Interop excluded platforms" @($interopAsmdef.excludePlatforms) @()
Assert-SequenceEqual "Interop precompiled references" @($interopAsmdef.precompiledReferences) @()
Assert-SequenceEqual "Interop define constraints" @($interopAsmdef.defineConstraints) @()
Assert-SequenceEqual "Interop version defines" @($interopAsmdef.versionDefines) @()
Assert-MetaGuid `
    "Interop assembly GUID" `
    (Get-PackageFile "Runtime/Interop/Luau.Interop.asmdef.meta") `
    "d61c9d398cb91443ea08b5afda6f6d75"
$interopSourceInventory = @(
    Get-ChildItem -LiteralPath (Join-Path $packageRoot "Runtime/Interop") -File -Filter "*.cs" |
        ForEach-Object Name |
        Sort-Object
)
Assert-SequenceEqual `
    "Package-owned interop source inventory" `
    $interopSourceInventory `
    @("AssemblyInfo.cs", "NativeMethods.cs", "NativeTypes.cs")
Write-Host "PASS: package-owned interop remains isolated and is the sole unsafe source assembly."

$editorAsmdef = Read-PackageJson "Editor/Luau.Unity.Editor.asmdef"
Assert-Equal "Editor assembly name" $editorAsmdef.name "Luau.Unity.Editor"
Assert-Equal "Editor root namespace" $editorAsmdef.rootNamespace ""
Assert-SequenceEqual `
    "Editor assembly references" `
    @($editorAsmdef.references) `
    @("GUID:c727d2ef8dd2e4846ab81fbe6ca1f508")
Assert-SequenceEqual "Editor platforms" @($editorAsmdef.includePlatforms) @("Editor")
Assert-Equal "Editor unsafe-code policy" ([bool]$editorAsmdef.allowUnsafeCode) $false
Assert-Equal "Editor override-references policy" ([bool]$editorAsmdef.overrideReferences) $true
Assert-Equal "Editor auto-reference policy" ([bool]$editorAsmdef.autoReferenced) $true
Assert-Equal "Editor engine-reference policy" ([bool]$editorAsmdef.noEngineReferences) $false
Assert-SequenceEqual "Editor excluded platforms" @($editorAsmdef.excludePlatforms) @()
Assert-SequenceEqual "Editor precompiled references" @($editorAsmdef.precompiledReferences) @("Luau.dll")
Assert-SequenceEqual "Editor define constraints" @($editorAsmdef.defineConstraints) @()
Assert-SequenceEqual "Editor version defines" @($editorAsmdef.versionDefines) @()
Assert-MetaGuid `
    "Editor assembly GUID" `
    (Get-PackageFile "Editor/Luau.Unity.Editor.asmdef.meta") `
    "827e3872b9ef548b0b8e2ab0ad7f3ff4"
Write-Host "PASS: editor code references only the package runtime and shipped managed artifact."

$testAsmdef = Read-PackageJson "Tests/EditMode/Luau.Unity.EditModeTests.asmdef"
Assert-Equal "EditMode test assembly name" $testAsmdef.name "Luau.Unity.EditModeTests"
Assert-Equal "EditMode test root namespace" $testAsmdef.rootNamespace "Luau.Unity.Tests"
Assert-SequenceEqual `
    "EditMode test assembly references" `
    @($testAsmdef.references) `
    @(
        "GUID:c727d2ef8dd2e4846ab81fbe6ca1f508",
        "GUID:827e3872b9ef548b0b8e2ab0ad7f3ff4"
    )
Assert-SequenceEqual "EditMode test platforms" @($testAsmdef.includePlatforms) @("Editor")
Assert-Equal "EditMode test unsafe-code policy" ([bool]$testAsmdef.allowUnsafeCode) $false
Assert-Equal "EditMode test override-references policy" ([bool]$testAsmdef.overrideReferences) $true
Assert-Equal "EditMode test auto-reference policy" ([bool]$testAsmdef.autoReferenced) $false
Assert-Equal "EditMode test engine-reference policy" ([bool]$testAsmdef.noEngineReferences) $false
Assert-SequenceEqual "EditMode test excluded platforms" @($testAsmdef.excludePlatforms) @()
Assert-SequenceEqual "EditMode test precompiled references" @($testAsmdef.precompiledReferences) @("Luau.dll")
Assert-SequenceEqual "EditMode test define constraints" @($testAsmdef.defineConstraints) @()
Assert-SequenceEqual "EditMode test version defines" @($testAsmdef.versionDefines) @()
Assert-SequenceEqual "EditMode test optional references" @($testAsmdef.optionalUnityReferences) @("TestAssemblies")
Assert-MetaGuid `
    "EditMode test assembly GUID" `
    (Get-PackageFile "Tests/EditMode/Luau.Unity.EditModeTests.asmdef.meta") `
    "272c0a43a4974766a1266dfbf3844615"

$assemblyNames = @(
    Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter "*.asmdef" |
        ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).name } |
        Sort-Object
)
Assert-SequenceEqual `
    "Package assembly inventory" `
    $assemblyNames `
    @("Luau.Interop", "Luau.Unity", "Luau.Unity.EditModeTests", "Luau.Unity.Editor")
Write-Host "PASS: all package assemblies have explicit package-local dependency boundaries."

$verificationRuntimeAsmdef = Read-IntegrationJson "Assets/Verification/Runtime/Luau.Unity.Verification.asmdef"
Assert-Equal "Verification Runtime assembly name" $verificationRuntimeAsmdef.name "Luau.Unity.Verification"
Assert-Equal "Verification Runtime root namespace" $verificationRuntimeAsmdef.rootNamespace ""
Assert-SequenceEqual `
    "Verification Runtime references" `
    @($verificationRuntimeAsmdef.references) `
    @("GUID:c727d2ef8dd2e4846ab81fbe6ca1f508")
Assert-SequenceEqual "Verification Runtime included platforms" @($verificationRuntimeAsmdef.includePlatforms) @()
Assert-SequenceEqual "Verification Runtime excluded platforms" @($verificationRuntimeAsmdef.excludePlatforms) @()
Assert-Equal "Verification Runtime unsafe-code policy" ([bool]$verificationRuntimeAsmdef.allowUnsafeCode) $false
Assert-Equal "Verification Runtime override-references policy" ([bool]$verificationRuntimeAsmdef.overrideReferences) $false
Assert-Equal "Verification Runtime auto-reference policy" ([bool]$verificationRuntimeAsmdef.autoReferenced) $true
Assert-Equal "Verification Runtime engine-reference policy" ([bool]$verificationRuntimeAsmdef.noEngineReferences) $false
Assert-SequenceEqual "Verification Runtime precompiled references" @($verificationRuntimeAsmdef.precompiledReferences) @()
Assert-SequenceEqual "Verification Runtime define constraints" @($verificationRuntimeAsmdef.defineConstraints) @()
Assert-SequenceEqual "Verification Runtime version defines" @($verificationRuntimeAsmdef.versionDefines) @()
Assert-MetaGuid `
    "Verification Runtime assembly GUID" `
    (Get-IntegrationFile "Assets/Verification/Runtime/Luau.Unity.Verification.asmdef.meta") `
    "ae747f3b7ca5411e97eb0bc61ae3f38e"

$verificationEditorAsmdef = Read-IntegrationJson "Assets/Verification/Editor/Luau.Unity.Verification.Editor.asmdef"
Assert-Equal "Verification Editor assembly name" $verificationEditorAsmdef.name "Luau.Unity.Verification.Editor"
Assert-Equal "Verification Editor root namespace" $verificationEditorAsmdef.rootNamespace ""
Assert-SequenceEqual `
    "Verification Editor references" `
    @($verificationEditorAsmdef.references) `
    @(
        "GUID:ae747f3b7ca5411e97eb0bc61ae3f38e",
        "GUID:c727d2ef8dd2e4846ab81fbe6ca1f508"
    )
Assert-SequenceEqual "Verification Editor included platforms" @($verificationEditorAsmdef.includePlatforms) @("Editor")
Assert-SequenceEqual "Verification Editor excluded platforms" @($verificationEditorAsmdef.excludePlatforms) @()
Assert-Equal "Verification Editor unsafe-code policy" ([bool]$verificationEditorAsmdef.allowUnsafeCode) $false
Assert-Equal "Verification Editor override-references policy" ([bool]$verificationEditorAsmdef.overrideReferences) $false
Assert-Equal "Verification Editor auto-reference policy" ([bool]$verificationEditorAsmdef.autoReferenced) $true
Assert-Equal "Verification Editor engine-reference policy" ([bool]$verificationEditorAsmdef.noEngineReferences) $false
Assert-SequenceEqual "Verification Editor precompiled references" @($verificationEditorAsmdef.precompiledReferences) @()
Assert-SequenceEqual "Verification Editor define constraints" @($verificationEditorAsmdef.defineConstraints) @()
Assert-SequenceEqual "Verification Editor version defines" @($verificationEditorAsmdef.versionDefines) @()
Assert-MetaGuid `
    "Verification Editor assembly GUID" `
    (Get-IntegrationFile "Assets/Verification/Editor/Luau.Unity.Verification.Editor.asmdef.meta") `
    "4159a2e1300c42ac934cc6c5c933451f"

Assert-MetaGuid `
    "Player smoke script GUID" `
    (Get-IntegrationFile "Assets/Verification/Runtime/LuauPlayerSmoke.cs.meta") `
    "36da335a91e048ebb78746c6113e4ef6"
Assert-MetaGuid `
    "Player smoke build script GUID" `
    (Get-IntegrationFile "Assets/Verification/Editor/LuauPlayerSmokeBuild.cs.meta") `
    "c170ae794b1f4e88a75d2674259b80c8"

$packageProductText = [string]::Join(
    "`n",
    @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
        Where-Object { @(".asmdef", ".cs", ".json") -contains $_.Extension.ToLowerInvariant() } |
        Sort-Object FullName |
        ForEach-Object { [IO.File]::ReadAllText($_.FullName) }))
foreach ($forbiddenVerificationReference in @(
    "Luau.Unity.Verification",
    "ae747f3b7ca5411e97eb0bc61ae3f38e",
    "4159a2e1300c42ac934cc6c5c933451f",
    "LuauPlayerSmoke"
)) {
    Assert-NotContainsLiteral `
        "Standalone package product assemblies" `
        $packageProductText `
        $forbiddenVerificationReference
}
Write-Host "PASS: player smoke verification lives only in explicit integration-project assemblies with preserved GUIDs and no product coupling."

foreach ($retiredPath in @(
    "Runtime/AddressablesLuauRequirer.cs",
    "Runtime/AddressablesLuauRequirer.cs.meta",
    "Runtime/ResourcesLuauRequirer.cs",
    "Runtime/ResourcesLuauRequirer.cs.meta"
)) {
    Assert-PackagePathAbsent $retiredPath
}
Write-Host "PASS: retired global Resources and Addressables resolvers remain absent."

$importer = Read-PackageText "Editor/LuauImporter.cs"
Assert-ContainsLiteral "Luau importer" $importer '[ScriptedImporter(3, "luau")]'
foreach ($required in @(
    "source = ReadSourceBytes(",
    "LuauAssetImportSettings.MaxImportedSourceBytes",
    "text = DecodeSource(source);",
    "compileResult = LuauUnity",
    ".CompileAssetSourceAsync(",
    "if (compileResult.Kind != LuauCompileResultKind.Success)",
    "var compilerOutput = compileResult.Output;",
    "ImportErrorObserverForTests",
    "ctx.LogImportError(message);",
    "static readonly UTF8Encoding StrictUtf8",
    "throwOnInvalidBytes: true",
    "var declaredLength = stream.Length;",
    "if (declaredLength > maxSourceBytes)",
    "catch (System.Exception exception)",
    "asset.SetSource(text, source);",
    "LuauAssetImportSettings.ImportPolicy ==",
    "LuauAssetImportPolicy.AllowFirstPartyPrecompile &&",
    "precompile;"
)) {
    Assert-ContainsLiteral "Luau importer" $importer $required
}
foreach ($forbidden in @(
    "File.ReadAllText",
    "File.ReadAllBytes",
    "Encoding.UTF8.GetBytes(text)",
    "Encoding.UTF8.GetBytes(sourceText)"
)) {
    Assert-NotContainsLiteral "Strict bounded Luau importer" $importer $forbidden
}
Assert-LiteralOrder `
    "Luau importer compiler-identity dependency ordering" `
    $importer `
    @(
        "LuauCompilerIdentityDependency.DependsOn(ctx);",
        "compileResult = LuauUnity",
        ".CompileAssetSourceAsync(",
        "var compilerOutput = compileResult.Output;",
        "LuauCompilerIdentityDependency.ScheduleRegistration(compilerOutput);"
    )

$compilerIdentityDependency = Read-PackageText "Editor/LuauCompilerIdentityDependency.cs"
Get-PackageFile "Editor/LuauCompilerIdentityDependency.cs.meta" |
    Out-Null
foreach ($required in @(
    'internal const string Name = "Luau.Unity.CompilerIdentity";',
    "context.DependsOnCustomDependency(Name);",
    "var identity = string.Join(",
    "LuauBytecodeArtifact.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)",
    "options.OptimizationLevel.ToString(CultureInfo.InvariantCulture)",
    "options.DebugLevel.ToString(CultureInfo.InvariantCulture)",
    "options.TypeInfoLevel.ToString(CultureInfo.InvariantCulture)",
    "options.CoverageLevel.ToString(CultureInfo.InvariantCulture)",
    "internal static Hash128 ComputeHash(LuauCompilerOutput output)",
    'output.UpstreamRevisionHash.ToString("x16", CultureInfo.InvariantCulture)',
    'output.HostBuildFingerprint.ToString("x16", CultureInfo.InvariantCulture)',
    "return Hash128.Compute(identity);",
    "EditorApplication.delayCall += RegisterPending;",
    "AssetDatabase.RegisterCustomDependency(Name, hash);",
    "[InitializeOnLoadMethod]",
    "EditorApplication.delayCall += RegisterAtEditorReady;",
    "ComputeHash(LuauCompiler.Compile(ReadOnlySpan<byte>.Empty))"
)) {
    Assert-ContainsLiteral "Luau compiler identity dependency" $compilerIdentityDependency $required
}
$importerPolicyTests = Read-PackageText "Tests/EditMode/LuauImporterPolicyTests.cs"
foreach ($required in @(
    "CompilerIdentityTracksCoverageInstrumentation",
    "SourceAdmissionAcceptsExactLimitAndRejectsOneOverBeforeReading",
    "StrictUtf8AcceptsBomAndEmptySourceAndRejectsInvalidBytes",
    "ImporterRejectsOneByteOverTheConfiguredLimit",
    "ImporterPreservesBomAndEmptySourceBytes",
    "ImporterRejectsInvalidUtf8WithoutPersistingReplacementText",
    "CompileFailurePreservesTheExactAdmittedSource",
    "LegacyPrecompiledAssetWithoutSourceIdentityRequiresReimport",
    "ImporterUsesBoundedSharedLaneAndPreservesSourceOnOutputLimit",
    'FindProperty("sourceIdentity")',
    "CoverageLevel = 2",
    "coverageOutput.BytecodeSha256",
    "LuauCompilerIdentityDependency.ComputeHash(coverageOutput)",
    "LuauCompilerIdentityDependency.ComputeHash(defaultOutput)"
)) {
    Assert-ContainsLiteral "Luau compiler coverage identity test" $importerPolicyTests $required
}
Assert-ContainsLiteral `
    "Bounded importer compilation lane" `
    $importer `
    ".CompileAssetSourceAsync("
Assert-NotContainsLiteral `
    "Bounded importer compilation lane" `
    $importer `
    "LuauCompiler.Compile(source)"
$compilationServiceTests = Read-RepositoryText "tests/Luau.Tests/LuauThreadedCompilationServiceTests.cs"
foreach ($required in @(
    "OversizedCompilerOutputIsAnInfrastructureLimitFailure",
    "MaxBytecodeBytesPerResult = 3",
    "bytecodeLength: 3",
    "AssertResultShape(exact, LuauCompileResultKind.Success)",
    "bytecodeLength: 4",
    "LuauCompilationLimitKind.BytecodeBytesPerResult"
)) {
    Assert-ContainsLiteral "Bounded compiler-output exact/over tests" $compilationServiceTests $required
}
Write-Host "PASS: importer invalidation tracks compiler schema, options, and exact native identity before persistent artifact creation."
Write-Host "PASS: SourceOnly imports compile transiently for diagnostics but persist source."

$projectSettings = Read-PackageText "Editor/LuauUnityProjectSettings.cs"
foreach ($required in @(
    "SourceOnly = 0",
    "AllowFirstPartyPrecompile = 1",
    "LuauAssetImportPolicy importPolicy = LuauAssetImportPolicy.SourceOnly;",
    "DefaultMaxImportedSourceBytes = 1024 * 1024",
    "int maxImportedSourceBytes = LuauAssetImportSettings.DefaultMaxImportedSourceBytes;",
    "public static int MaxImportedSourceBytes",
    "public static void SetMaxImportedSourceBytes(int maxSourceBytes)"
)) {
    Assert-ContainsLiteral "Luau project import policy" $projectSettings $required
}

$checkedInSettings = Read-IntegrationText "ProjectSettings/LuauUnitySettings.asset"
Assert-ContainsLiteral "Checked-in Luau project settings" $checkedInSettings "importPolicy: 0"
Assert-ContainsLiteral "Checked-in Luau project settings" $checkedInSettings "maxImportedSourceBytes: 1048576"
Write-Host "PASS: the project-wide import policy is SourceOnly with one first-party opt-in and a finite 1 MiB importer cap."

foreach ($required in @(
    "LuauAssetImportSettings.FirstPartyProvenanceId",
    "AssetDatabase.AssetPathToGUID(ctx.assetPath)",
    "var artifact = LuauBytecodeArtifact.Create(",
    '"unity-asset-guid:" + assetGuid',
    "Encoding.UTF8.GetBytes(assetGuid)",
    "asset.SetVerifiedBytecode(sourceText, artifact);"
)) {
    Assert-ContainsLiteral "First-party Luau precompile path" $importer $required
}
Write-Host "PASS: first-party precompile stores an artifact with configured provenance and the stable asset GUID."

$asset = Read-PackageText "Runtime/LuauAsset.cs"
foreach ($required in @(
    "Source = 0",
    "VerifiedBytecode = 1",
    "internal bool IsSource => contentKind == LuauAssetContentKind.Source;",
    "public ReadOnlySpan<byte> AsSpan()",
    "public ReadOnlyMemory<byte> AsMemory()",
    "throw InvalidContentKind();",
    "internal LuauBytecodeArtifact GetVerifiedBytecode()",
    "[SerializeField] internal string sourceIdentity;",
    "sourceIdentity = artifact.SourceIdentity;",
    '"The serialized bytecode artifact has no source identity.',
    "sourceIdentity,"
)) {
    Assert-ContainsLiteral "Luau asset" $asset $required
}
Assert-LiteralCount "Luau source access fail-closed guards" $asset "if (!IsSource)" 2
Write-Host "PASS: public source access fails closed for bytecode and unknown serialized content kinds."

$stateExtensions = Read-PackageText "Runtime/LuauStateExtensions.cs"
Assert-ContainsLiteral "Luau asset execution extensions" $stateExtensions "state.DoString("
Assert-ContainsLiteral "Luau asset execution extensions" $stateExtensions "state.DoStringInto("
Assert-ContainsLiteral "Luau asset execution extensions" $stateExtensions "state.ExecuteVerifiedBytecode("
Assert-ContainsLiteral "Luau asset execution extensions" $stateExtensions "state.ExecuteVerifiedBytecodeInto("
Assert-ContainsLiteral "Luau asset execution extensions" $stateExtensions "public static ValueTask<LuauResultScope> ExecuteAsync("
Assert-ContainsLiteral "Luau asset execution extensions" $stateExtensions "public static ValueTask<int> ExecuteIntoAsync("
Assert-ContainsLiteral "Luau asset execution extensions" $stateExtensions "LuauUnity.CompileAssetSourceAsync"
Assert-NotContainsLiteral "Luau asset execution extensions" $stateExtensions "state.DoStringAsync("
foreach ($forbidden in @(
    "ExecuteTrusted",
    "LoadTrusted",
    "AllowUnvalidated",
    "state.Load(",
    "state.LoadVerifiedBytecode(",
    "state.Execute("
)) {
    Assert-NotContainsLiteral "Luau asset execution extensions" $stateExtensions $forbidden
}

$stateCompilationExtensions = Read-PackageText "Runtime/LuauStateCompilationExtensions.cs"
foreach ($required in @(
    "public static ValueTask<LuauResultScope> ExecuteWithCompilationServiceAsync(",
    "public static ValueTask<int> ExecuteIntoWithCompilationServiceAsync(",
    "SnapshotAssetForAsync(state, asset, cancellationToken)",
    "ValidateSourceSize(state, source.Length, assetName)",
    "state.ExecuteCompilerOutputOnOwnerAsync(",
    "state.ExecuteCompilerOutputIntoOnOwnerAsync(",
    "state.ExecuteVerifiedBytecodeAsync(",
    "state.ExecuteVerifiedBytecodeIntoAsync(",
    "result.CompilationDiagnostic",
    "throw new LuauExecutionCanceledException("
)) {
    Assert-ContainsLiteral "Luau asset compilation execution" $stateCompilationExtensions $required
}
Assert-NotContainsLiteral `
    "Luau asset compilation execution" `
    $stateCompilationExtensions `
    "ExecuteCompilerOutputOnOwnerThreadAsync"

$unityCompilation = Read-PackageText "Runtime/LuauUnityCompilation.cs"
foreach ($required in @(
    "static LuauAssetCompilationProvider assetCompilationProvider = CompileAsync;",
    "internal static ValueTask<LuauCompileResult> CompileAssetSourceAsync(",
    "public static ValueTask<LuauModuleBundle> CompileModuleBundleAsync(",
    "SharedModuleCompilationService.Instance",
    "moduleMap.CompileModuleBundleAsync(",
    "internal static IDisposable OverrideAssetCompilationProviderForTests(",
    "new LuauThreadedCompilationService(",
    "MaxQueuedRequestCount = windows ? 32 : 16",
    "MaxQueuedSourceBytes = windows ? 8L * 1024 * 1024 : 4L * 1024 * 1024",
    "internal static async Task DrainCompilationServiceAsync(",
    "compilationServiceStopping = true;",
    "await service.DisposeAsync().ConfigureAwait(false);",
    "compilationService = null;",
    "internal static void ResetCompilationServiceAfterDrainForTests()"
)) {
    Assert-ContainsLiteral "Unity package-owned compilation lane" $unityCompilation $required
}
$editorCompilationLifetime = Read-PackageText "Editor/LuauCompilationServiceEditorLifetime.cs"
foreach ($required in @(
    "AssemblyReloadEvents.beforeAssemblyReload += DrainForAssemblyReload;",
    "internal static void DrainForAssemblyReload()",
    "LuauUnity.DrainCompilationServiceAsync("
)) {
    Assert-ContainsLiteral "Unity Editor compilation lane lifetime" $editorCompilationLifetime $required
}
$unityCompilationTests = Read-PackageText "Tests/EditMode/LuauCompilationServiceTests.cs"
foreach ($required in @(
    "EditorReloadHookDrainsSharedLaneAndRejectsAdmissionUntilReset",
    "SharedFacadeCompilesModuleBundleThroughThePackageOwnedLane",
    "LuauCompilationServiceEditorLifetime.DrainForAssemblyReload();",
    "LuauUnity.ResetCompilationServiceAfterDrainForTests();",
    'Does.Contain("shutting down")'
)) {
    Assert-ContainsLiteral "Unity shared compilation lane lifecycle tests" $unityCompilationTests $required
}
$playerSmoke = Read-IntegrationText "Assets/Verification/Runtime/LuauPlayerSmoke.cs"
foreach ($required in @(
    "compiledResult = await first.ExecuteAsync(backgroundAsset);",
    '[LuauLibrary("PlayerSmokeCapability", Exposure = LuauLibraryExposure.Capability)]',
    "using var capabilityHandle = root.CreateHandle(capability);",
    "capability.Position = vector.create(1, 2, 3)",
    "capability.Hidden == nil",
    "exception.InnerException is MissingReferenceException"
)) {
    Assert-ContainsLiteral "Luau player ordinary asset and capability smoke" $playerSmoke $required
}
Assert-NotContainsLiteral `
    "Luau player ordinary asset smoke" `
    $playerSmoke `
    "ExecuteCompilerOutputOnOwnerThreadAsync"
Write-Host "PASS: ordinary async asset execution uses the injectable package-owned bounded lane and shared owner dispatch."

$unityPublicApiTests = Read-PackageText "Tests/EditMode/LuauUnityPublicApiTests.cs"
foreach ($required in @(
    "RuntimePublicAndProtectedApiMatchesApprovedInventory",
    "BindingFlags.NonPublic",
    "IsPublicOrProtected",
    "ExecuteExtensionSurfaceContainsOnlyApprovedShapes",
    '"ExecuteInto(Luau.LuauState,Luau.Unity.LuauAsset,System.Span<Luau.LuauValue>)->System.Int32"',
    '"ExecuteAsync(Luau.LuauState,Luau.Unity.LuauAsset,System.Threading.CancellationToken)->System.Threading.Tasks.ValueTask<Luau.LuauResultScope>"',
    'const string ApprovedApiSha256 ='
)) {
    Assert-ContainsLiteral "Unity runtime public API inventory" $unityPublicApiTests $required
}
Assert-NotContainsLiteral "Unity runtime public API inventory" $unityPublicApiTests "REPLACE_AFTER_PROBE"
Write-Host "PASS: Unity public/protected API and exact Execute extension shapes have deterministic approval gates."

$managedLoadSurface = Read-RepositoryText "src/Luau/LuauState.Load.cs"
$managedExecuteSurface = Read-RepositoryText "src/Luau/LuauState.Execute.cs"
$managedBytecodePolicy = Read-RepositoryText "src/Luau/LuauBytecodePolicy.cs"
foreach ($forbidden in @(
    "public LuauFunction Load(",
    "LoadTrustedBytecode"
)) {
    Assert-NotContainsLiteral "Managed bytecode load surface" $managedLoadSurface $forbidden
}
Assert-NotContainsLiteral "Managed bytecode execution surface" $managedExecuteSurface "ExecuteTrustedBytecode"
Assert-NotContainsLiteral "Managed bytecode policy" $managedBytecodePolicy "AllowUnvalidated"
Write-Host "PASS: runtime assets use source compilation or validator-gated ExecuteVerifiedBytecode APIs only."

$sourceOnlyValidator = Read-PackageText "Editor/LuauSourceOnlyAssetValidator.cs"
foreach ($required in @(
    "public static class LuauSourceOnlyAssetValidator",
    "public static IReadOnlyList<string> FindNonSourceAssets(",
    "AssetDatabase.LoadAllAssetsAtPath(path)",
    ".OfType<LuauAsset>()",
    ".Any(asset => !asset.IsSource)",
    "public static void ValidateSourceOnly(",
    "internal sealed class LuauSourceOnlyBuildPreprocessor : IPreprocessBuildWithReport",
    "LuauAssetImportSettings.ImportPolicy ==",
    "LuauAssetImportPolicy.AllowFirstPartyPrecompile)",
    "LuauSourceOnlyAssetValidator.ValidateProject();"
)) {
    Assert-ContainsLiteral "Source-only package validator" $sourceOnlyValidator $required
}
Assert-NotContainsLiteral `
    "Source-only package validator" `
    $sourceOnlyValidator `
    "LuauAssetImportSettings.ImportPolicy != LuauAssetImportPolicy.SourceOnly"

$importerEditor = Read-PackageText "Editor/LuauImporterEditor.cs"
Assert-LiteralCount "Importer precompile field" $importerEditor 'serializedObject.FindProperty("precompile")' 1
Assert-LiteralOrder `
    "Importer policy-specific UI" `
    $importerEditor `
    @(
        "if (policy == LuauAssetImportPolicy.AllowFirstPartyPrecompile)",
        "EditorGUILayout.PropertyField(",
        'serializedObject.FindProperty("precompile")',
        "else",
        "This project stores .luau assets as UTF-8 source."
    )
Write-Host "PASS: SourceOnly hides precompile controls and unknown policy values still receive fail-closed build validation."
Write-Host "PASS: reusable validation and the build preprocessor inspect imported asset content, not importer flags."

Assert-PackagePathAbsent "Runtime/LuauModuleMap.cs"
Assert-PackagePathAbsent "Runtime/LuauModuleMap.cs.meta"
$moduleMap = Read-RepositoryText "src/Luau/LuauModuleMap.cs"
foreach ($required in @(
    "public sealed class LuauModuleMap : LuauRequirer",
    "CanonicalizeModuleId",
    "protected override string GetCacheKey",
    "ExecuteModuleSource",
    "(byte[])source.Clone()",
    "public async ValueTask<LuauModuleBundle> CompileModuleBundleAsync(",
    "public sealed class LuauModuleBundle : LuauRequirer",
    "LuauModuleLimits"
)) {
    Assert-ContainsLiteral "Managed-core Luau module policy" $moduleMap $required
}
foreach ($forbidden in @(
    "using System.IO;",
    "UnityEngine.Resources",
    "Addressables.",
    "ExecuteModuleBytecode",
    "ExecuteTrustedModuleBytecode"
)) {
    Assert-NotContainsLiteral "Managed-core Luau module policy" $moduleMap $forbidden
}

$unityFacade = Read-PackageText "Runtime/LuauUnity.cs"
foreach ($required in @(
    "public LuauStateOptions StateOptions { get; set; } = LuauStateOptions.Default;",
    "public LuauModuleMap ModuleMap { get; set; }",
    "state.OpenRequireLibrary(options.ModuleMap);",
    "return stateOptions with",
    "DefaultExecutionOptions = executionOptions with",
    "DefaultMaxPrintArguments = 32",
    "DefaultMaxPrintUtf8Bytes = 4 * 1024",
    "DefaultMaxPrintMessagesPerSecond = 20"
)) {
    Assert-ContainsLiteral "Unity Luau facade" $unityFacade $required
}
foreach ($forbidden in @("EnableRequire", "public LuauRequirer Requirer")) {
    Assert-NotContainsLiteral "Unity Luau facade" $unityFacade $forbidden
}

$runtimeSource = [string]::Join(
    "`n",
    @(Get-ChildItem -LiteralPath (Join-Path $packageRoot "Runtime") -File -Filter "*.cs" |
        Sort-Object FullName |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }))
foreach ($forbidden in @(
    "UnityEngine.AddressableAssets",
    "UnityEngine.Resources",
    "Resources.Load",
    "ExecuteTrustedBytecode",
    "LoadTrustedBytecode",
    "LuauBytecodePolicy.AllowUnvalidated",
    "state.Load(",
    "state.LoadVerifiedBytecode(",
    "state.Execute("
)) {
    Assert-NotContainsLiteral "Unity package runtime" $runtimeSource $forbidden
}
Write-Host "PASS: module loading is immutable, source-only, canonicalized, and rooted in finite Unity defaults."

$managedPublicApiPath = Get-RepositoryFile "tests/Luau.Tests/PublicApi.approved.txt"
Assert-CanonicalTextFileHash `
    "Managed public API approval inventory" `
    $managedPublicApiPath `
    "DD449ACF8A4F8B92F16C3484285B5A9D75E37F6AB58F6DDB3ED2A7CBB5057FBB"
$managedPublicApiProject = Read-RepositoryText "tests/Luau.Tests/Luau.Tests.csproj"
Assert-ContainsLiteral `
    "Managed public API test project" `
    $managedPublicApiProject `
    '<EmbeddedResource Include="PublicApi.approved.txt" LogicalName="Luau.Tests.PublicApi.approved.txt" />'
Assert-ContainsLiteral `
    "Unity public API approval inventory" `
    $unityPublicApiTests `
    '"ecb2664344f10c6bea023321a0733eeba2b3e9222d64e9339d7273972b63750d"'
Assert-NotContainsLiteral `
    "Unity public API approval inventory" `
    $unityPublicApiTests `
    '"56c52c'
Write-Host "PASS: managed and Unity public API approval inventories match the Stage 6 ownership surface."

$headerPath = Get-RequiredFile $hostRoot "include/luau_host.h" "Native host ABI header"
$exportsPath = Get-RequiredFile $hostRoot "exports/luau_host.exports" "Native host export allowlist"
Assert-CanonicalTextFileHash `
    "Native host ABI header" `
    $headerPath `
    "F3C276BDCD88254503E63767598F6071089760835A57CC79673C7094DBF84459"
Assert-CanonicalTextFileHash `
    "Native host export allowlist" `
    $exportsPath `
    "DC6351C83986F1A1B8EDAA246F97701C4CD0CAFD34D605C91F0BFF910F4558A0"
$exports = @(
    Get-Content -LiteralPath $exportsPath |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -ne "" -and !$_.StartsWith("#", [StringComparison]::Ordinal) }
)
Assert-Equal "Native host export count" $exports.Count 80
Assert-SequenceEqual "Native host sorted export allowlist" $exports @($exports | Sort-Object -CaseSensitive)
foreach ($export in $exports) {
    if (!$export.StartsWith("luau_host_", [StringComparison]::Ordinal)) {
        throw "Native export does not belong to the versioned host ABI: $export"
    }
}
if ($exports -cnotcontains "luau_host_callback_registration_id") {
    throw "Native ABI 2.0 is missing direct callback registration identity."
}
foreach ($removedExport in @(
    "luau_host_callback_userdata",
    "luau_host_is_thread_reset",
    "luau_host_raw_equal",
    "luau_host_to_function",
    "luau_host_open_all_libraries",
    "luau_host_memory_arm_quota_failure"
)) {
    if ($exports -ccontains $removedExport) {
        throw "Removed native ABI 2.0 export is still present: $removedExport"
    }
}
$nativeHeader = Get-Content -LiteralPath $headerPath -Raw
foreach ($required in @(
    "LUAU_HOST_ABI_MAJOR = 2",
    "LUAU_HOST_ABI_MINOR = 0",
    "LUAU_HOST_FEATURE_OPAQUE_REFERENCE_TOKENS = 1U << 9",
    "LUAU_HOST_FEATURE_DIRECT_CALLBACK_IDENTITY = 1U << 10",
    "LUAU_HOST_FEATURE_OBSERVATION_ONLY_GC_INTERRUPT = 1U << 11"
)) {
    Assert-ContainsLiteral "Native ABI 2.0 header" $nativeHeader $required
}
$managedNativeProtection = Read-RepositoryText "src/Luau/Internal/LuauNativeProtection.cs"
foreach ($required in @(
    "ExpectedAbiMajor = 2",
    "MinimumAbiMinor = 0",
    "ExpectedFeatureFlags = 0xfffU",
    "info.abi_minor != LuauNativeProtection.MinimumAbiMinor",
    'expected the exact ABI {LuauNativeProtection.ExpectedAbiMajor}.{LuauNativeProtection.MinimumAbiMinor}'
)) {
    Assert-ContainsLiteral "Managed ABI 2.0 verifier" $managedNativeProtection $required
}

$expectedUpstreamRevision = "6e9b580e2e24643214caf0f4bbbb3db911ca30f3"
$gitModules = Read-RepositoryText ".gitmodules"
Assert-ContainsLiteral "Luau submodule registration" $gitModules "path = native/luau"
Assert-ContainsLiteral "Luau submodule registration" $gitModules "url = https://github.com/luau-lang/luau.git"
Assert-NotContainsLiteral "Luau submodule registration" $gitModules "path = luau`n"

$gitLinkLine = (& git -C $repositoryRoot ls-files --stage -- "native/luau" 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect the native/luau gitlink: $gitLinkLine"
}
$gitLinkMatch = [Regex]::Match(
    $gitLinkLine,
    '^160000\s+([0-9a-fA-F]{40})\s+0\s+native/luau$')
if (!$gitLinkMatch.Success) {
    throw "native/luau is not an exact Git submodule entry: $gitLinkLine"
}
Assert-Equal `
    "Pinned upstream Luau gitlink" `
    $gitLinkMatch.Groups[1].Value.ToLowerInvariant() `
    $expectedUpstreamRevision

if (Test-Path -LiteralPath (Join-Path $upstreamRoot ".git")) {
    $actualUpstreamRevision = (& git -C $upstreamRoot rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect the initialized native/luau revision: $actualUpstreamRevision"
    }
    Assert-Equal "Initialized upstream Luau revision" $actualUpstreamRevision $expectedUpstreamRevision

    $upstreamStatus = (& git -C $upstreamRoot status --porcelain 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect the initialized native/luau worktree: $upstreamStatus"
    }
    Assert-Equal "Initialized upstream Luau status" $upstreamStatus ""
}

$cmake = Read-RepositoryText "native/luau-host/CMakeLists.txt"
Assert-ContainsLiteral "Native host upstream path" $cmake '"${CMAKE_CURRENT_SOURCE_DIR}/../luau"'
Assert-ContainsLiteral `
    "Native host pinned revision" `
    $cmake `
    "set(LUAU_HOST_UPSTREAM_REVISION `"$expectedUpstreamRevision`")"
Write-Host "PASS: native ABI 2.0 header, 80-symbol allowlist, stale-safe O(1) references, checked-in plugins, and pinned upstream revision are synchronized."

$integrationManifest = Read-IntegrationJson "Packages/manifest.json"
$integrationDependency = $integrationManifest.dependencies.PSObject.Properties["com.qll.luau.unity"]
if ($null -eq $integrationDependency) {
    throw "The integration project does not declare the standalone Luau.Unity package."
}
Assert-Equal `
    "Integration-project package reference" `
    $integrationDependency.Value `
    "file:../../../Luau.Unity"
Assert-SequenceEqual `
    "Integration-project testable package inventory" `
    @($integrationManifest.testables) `
    @("com.qll.luau.unity")

$integrationLock = Read-IntegrationJson "Packages/packages-lock.json"
$integrationLockEntry = $integrationLock.dependencies.PSObject.Properties["com.qll.luau.unity"]
if ($null -eq $integrationLockEntry) {
    throw "The integration lock file does not contain the standalone Luau.Unity package."
}
Assert-Equal "Integration lock package reference" $integrationLockEntry.Value.version "file:../../../Luau.Unity"
Assert-Equal "Integration lock package depth" ([int]$integrationLockEntry.Value.depth) 0
Assert-Equal "Integration lock package source" $integrationLockEntry.Value.source "local"
if (@($integrationLockEntry.Value.dependencies.PSObject.Properties).Count -ne 0) {
    throw "The integration lock file records unexpected Luau.Unity dependencies."
}

foreach ($consumerProbeFile in @(
    "tests/Luau.Unity.PackageConsumerProbe/ConsumerProbe.asmdef",
    "tests/Luau.Unity.PackageConsumerProbe/ConsumerApiProbe.cs",
    "tests/Luau.Unity.PackageConsumerProbe/ConsumerGeneratedLibrary.cs",
    "tests/Luau.Unity.PackageConsumerProbe/Editor/RunConsumerProbe.cs"
)) {
    Get-RepositoryFile $consumerProbeFile | Out-Null
}
$consumerApiProbe = Read-RepositoryText "tests/Luau.Unity.PackageConsumerProbe/ConsumerApiProbe.cs"
Assert-ContainsLiteral `
    "Package consumer module-bundle adapter" `
    $consumerApiProbe `
    "return LuauUnity.CompileModuleBundleAsync("
Write-Host "PASS: the integration project consumes the standalone package and the generated-consumer fixture remains a source-only probe."

$operationalFiles = @(
    "README.md",
    ".gitmodules",
    ".github/workflows/build-luau-host.android.yml",
    ".github/workflows/build-luau-host.windows.yml",
    ".github/workflows/build-luau-host.yml",
    ".github/workflows/validate-managed-package.yml",
    ".github/dependabot.yml",
    "native/luau-host/CMakeLists.txt",
    "native/luau-host/cmake/Write-ArtifactManifest.ps1",
    "tools/Copy-DotNetArtifactsToUnity.ps1",
    "tools/Copy-NativeArtifactsToUnity.ps1",
    "tools/Test-LuauHostSoak.ps1",
    "tools/Test-ManagedHarnessSelection.ps1",
    "tools/Test-UnityHost.ps1",
    "tools/Test-UnityPackageConsumer.ps1",
    "tools/Test-UnityPackageRelease.ps1",
    "tools/Test-UnityPackageStatic.ps1",
    "tools/UnityPackageReleasePolicy.json",
    "tools/harness/Luau.Interop.csproj",
    "tests/Luau.Unity.Integration/Packages/manifest.json",
    "tests/Luau.Unity.Integration/Packages/packages-lock.json"
)
$obsoleteProjectUnix = @("src", "Luau.Unity") -join "/"
$obsoleteProjectWindows = @("src", "Luau.Unity") -join "\"
$obsoleteEmbeddedPackageUnix = @("Assets", "Luau.Unity") -join "/"
$obsoleteEmbeddedPackageWindows = @("Assets", "Luau.Unity") -join "\"
$obsoleteUpstreamUnix = @("..", "..", "luau") -join "/"
$obsoleteUpstreamWindows = @("..", "..", "luau") -join "\"
foreach ($operationalFile in $operationalFiles) {
    $operationalText = Read-RepositoryText $operationalFile
    foreach ($obsoletePath in @(
        $obsoleteProjectUnix,
        $obsoleteProjectWindows,
        $obsoleteEmbeddedPackageUnix,
        $obsoleteEmbeddedPackageWindows,
        $obsoleteUpstreamUnix,
        $obsoleteUpstreamWindows
    )) {
        Assert-NotContainsLiteral "Operational file $operationalFile" $operationalText $obsoletePath
    }
}

$trackedObsoleteProduct = (& git -C $repositoryRoot ls-files -- $obsoleteProjectUnix ($obsoleteProjectUnix + "/**") 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect tracked obsolete Unity paths: $trackedObsoleteProduct"
}
Assert-Equal "Tracked obsolete Unity project/product paths" $trackedObsoleteProduct ""
$trackedObsoleteSubmodule = (& git -C $repositoryRoot ls-files --stage -- "luau" 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect the obsolete root Luau submodule path: $trackedObsoleteSubmodule"
}
Assert-Equal "Tracked obsolete root Luau submodule path" $trackedObsoleteSubmodule ""

$copyManaged = Read-RepositoryText "tools/Copy-DotNetArtifactsToUnity.ps1"
Assert-ContainsLiteral "Managed artifact copy path" $copyManaged '$packageRoot = Join-Path $root "Luau.Unity"'
$copyNative = Read-RepositoryText "tools/Copy-NativeArtifactsToUnity.ps1"
Assert-ContainsLiteral "Native artifact copy path" $copyNative '$pluginsRoot = Join-Path $packageRoot "Runtime/Plugins"'
foreach ($required in @(
    "--strip-unneeded",
    "--remove-section=.comment",
    '"shipping-check"',
    '"shipping"',
    '"symbols/$target"',
    "--only-keep-debug",
    "Assert-AndroidElfHardening",
    "Write-ShippingManifest",
    "Write-SymbolManifest",
    "source_commit",
    "source_tree_clean",
    "toolchain",
    "unstripped_input",
    "shipping_output",
    "audited_manifest_sha256",
    "maximum_bytes",
    "Write-ArtifactManifest.ps1",
    "UnityPackageReleasePolicy.json"
)) {
    Assert-ContainsLiteral "Native shipping artifact shaping" $copyNative $required
}
$nativeManifestWriter = Read-RepositoryText "native/luau-host/cmake/Write-ArtifactManifest.ps1"
foreach ($required in @(
    "schema_version = 3",
    "source_commit",
    "source_tree_clean",
    "Get-ToolchainMetadata",
    "CMAKE_CXX_COMPILER_ID",
    "CMAKE_CXX_COMPILER_VERSION",
    "CMAKE_CXX_COMPILER_LINKER_ID",
    "CMAKE_CXX_COMPILER_LINKER_VERSION",
    "CMAKE_MAKE_PROGRAM",
    "CMAKE_SYSTEM_VERSION",
    "windows_sdk",
    "windows_header_sha256",
    "runtime_header_sha256",
    "kernel_library_sha256",
    "executable_sha256",
    "ci_image_version"
)) {
    Assert-ContainsLiteral "Native artifact toolchain provenance" $nativeManifestWriter $required
}
$releasePolicy = Read-RepositoryJson "tools/UnityPackageReleasePolicy.json"
Assert-Equal "Release policy schema" ([int]$releasePolicy.schemaVersion) 1
Assert-Equal "Release policy package" $releasePolicy.packageId "com.qll.luau.unity"
Assert-Equal "Release policy version" $releasePolicy.packageVersion "0.2.0"
Assert-Equal "Release policy tag" $releasePolicy.releaseTag "v0.2.0"
Assert-Equal "Release policy archive format" $releasePolicy.archiveFormat "ustar+gzip-stored-v1"
Assert-Equal "Release policy NDK" $releasePolicy.androidNdkRevision "27.2.12479018"
Assert-Equal "Release policy archive budget" ([long]$releasePolicy.maximumArchiveBytes) 8388608L
$expectedReleaseArtifactPolicy = @(
    @{ Path = "Runtime/Luau.dll"; MaximumBytes = 1048576L },
    @{ Path = "Runtime/Luau.xml"; MaximumBytes = 1048576L },
    @{ Path = "Runtime/Luau.SourceGenerator.dll"; MaximumBytes = 524288L },
    @{ Path = "Runtime/Plugins/win-x64/luau_host.dll"; MaximumBytes = 2097152L },
    @{ Path = "Runtime/Plugins/android-arm64/libluau_host.so"; MaximumBytes = 3145728L },
    @{ Path = "Runtime/Plugins/android-x64/libluau_host.so"; MaximumBytes = 3145728L }
)
Assert-SequenceEqual `
    "Release artifact policy allowlist" `
    @($releasePolicy.artifacts | ForEach-Object path) `
    @($expectedReleaseArtifactPolicy | ForEach-Object Path)
for ($index = 0; $index -lt $expectedReleaseArtifactPolicy.Count; $index++) {
    Assert-Equal `
        "Release artifact budget $($expectedReleaseArtifactPolicy[$index].Path)" `
        ([long]$releasePolicy.artifacts[$index].maximumBytes) `
        ([long]$expectedReleaseArtifactPolicy[$index].MaximumBytes)
}
$releaseValidator = Read-RepositoryText "tools/Test-UnityPackageRelease.ps1"
foreach ($required in @(
    "Package top-level allowlist",
    "Managed/native/XML artifact allowlist",
    "Duplicate Unity GUID",
    "New-DeterministicPackageArchive",
    "New-DeterministicStoredGzip",
    "stored DEFLATE blocks",
    "0xcbf43926",
    "ConvertTo-Json -Depth 8 -Compress",
    "The package archive is not deterministic",
    'refs/tags/$Tag^{commit}',
    "The repository working tree must be clean for exact-tag validation",
    'Test-UnityPackageStatic.ps1',
    'ls-tree -r --name-only $Tag -- Luau.Unity',
    'hash-object --no-filters',
    'Exact-tag package file inventory',
    '?path=Luau.Unity#$Tag',
    "Test-UnityPackageConsumer.ps1",
    'PackageReference = $taggedInstallUrl',
    'ExpectedGitCommit = $tagCommit',
    'UnityTimeoutMinutes = $ConsumerTimeoutMinutes',
    '[switch] $SkipUnityConsumer',
    '-SkipUnityConsumer requires -Tag',
    '!$SkipUnityConsumer'
)) {
    Assert-ContainsLiteral "Package release validator" $releaseValidator $required
}
$consumerValidator = Read-RepositoryText "tools/Test-UnityPackageConsumer.ps1"
foreach ($required in @(
    "function Resolve-PackageContentRoot",
    'PackageSource',
    '$resolvedPackageJsonPath = Join-Path $packageContentRoot "package.json"',
    '$sampleSource = [System.IO.Path]::GetFullPath((Join-Path $packageContentRoot $sample.path))',
    '(Join-Path $packageContentRoot "Runtime/Luau.xml")',
    '[int] $UnityTimeoutMinutes = 20',
    '$timeoutMilliseconds = [int]([TimeSpan]::FromMinutes($UnityTimeoutMinutes).TotalMilliseconds)',
    '$unityProcess.WaitForExit($timeoutMilliseconds)',
    'System32/taskkill.exe',
    'packages-lock.json',
    '$lockEntry.source -cne "git"',
    '$lockEntry.version -cne $resolvedPackageReference',
    '$lockEntry.hash.Equals($ExpectedGitCommit'
)) {
    Assert-ContainsLiteral "Exact-reference package consumer" $consumerValidator $required
}
Assert-NotContainsLiteral `
    "Exact-reference package consumer samples" `
    $consumerValidator `
    'Join-Path $packageRoot $sample.path'
$hostSoak = Read-RepositoryText "tools/Test-LuauHostSoak.ps1"
Assert-ContainsLiteral "Host soak default plugin" $hostSoak '"Luau.Unity/Runtime/Plugins/win-x64/luau_host.dll"'
$harnessProject = Read-RepositoryText "tools/harness/Luau.Interop.csproj"
Assert-ContainsLiteral "Harness interop authority" $harnessProject '..\..\Luau.Unity\Runtime\Interop\NativeMethods.cs'
Assert-ContainsLiteral "Harness default plugin" $harnessProject '..\..\Luau.Unity\Runtime\Plugins\win-x64\luau_host.dll'
$artifactManifest = Read-RepositoryText "native/luau-host/cmake/Write-ArtifactManifest.ps1"
Assert-ContainsLiteral "Artifact manifest upstream root" $artifactManifest 'Join-Path $repositoryRoot "native/luau"'
Assert-ContainsLiteral "Artifact manifest interop authority" $artifactManifest 'Join-Path $packageRoot "Runtime/Interop/NativeTypes.cs"'
$disposableHost = Read-RepositoryText "tools/Test-UnityHost.ps1"
Assert-ContainsLiteral "Disposable host integration source" $disposableHost 'Join-Path $repositoryRoot "tests/Luau.Unity.Integration"'
Assert-ContainsLiteral "Disposable host package source" $disposableHost 'Join-Path $repositoryRoot "Luau.Unity"'
Assert-ContainsLiteral "Disposable host staged package" $disposableHost 'Join-Path $projectRoot "Packages/$luauPackageName"'
Write-Host "PASS: operational scripts use only final package, integration-project, interop, plugin, and upstream paths."

$managedWorkflow = Read-RepositoryText ".github/workflows/validate-managed-package.yml"
Assert-ContainsLiteral "Managed validation workflow" $managedWorkflow "pull_request:"
Assert-ContainsLiteral "Reusable managed validation workflow" $managedWorkflow "workflow_call:"
Assert-ContainsLiteral "Managed validation outer timeout" $managedWorkflow "timeout-minutes: 45"
if ([Regex]::IsMatch($managedWorkflow, '(?m)^\s+paths(?:-ignore)?:\s*$')) {
    throw "Managed package validation must remain unfiltered so every compatibility path triggers it."
}
foreach ($requiredCommand in @(
    "dotnet test Luau.slnx",
    "tools/Test-ManagedHarnessSelection.ps1",
    "tools/Copy-DotNetArtifactsToUnity.ps1",
    "tools/Test-UnityPackageStatic.ps1",
    "tools/Test-UnityPackageRelease.ps1"
)) {
    Assert-ContainsLiteral "Managed validation workflow" $managedWorkflow $requiredCommand
}
$compatibilityPathsCoveredByUnfilteredPullRequests = @(
    "Luau.Unity/Runtime/Interop/AssemblyInfo.cs",
    "Luau.Unity/Runtime/Interop/NativeMethods.cs",
    "Luau.Unity/Runtime/Interop/NativeTypes.cs",
    "Luau.Unity/Runtime/Plugins/win-x64/luau_host.dll",
    "Luau.Unity/Runtime/Plugins/win-x64/luau_host.dll.meta",
    "Luau.Unity/Runtime/Plugins/android-arm64/libluau_host.so",
    "Luau.Unity/Runtime/Plugins/android-arm64/libluau_host.so.meta",
    "Luau.Unity/Runtime/Plugins/android-x64/libluau_host.so",
    "Luau.Unity/Runtime/Plugins/android-x64/libluau_host.so.meta",
    "tools/Copy-DotNetArtifactsToUnity.ps1",
    "tools/Copy-NativeArtifactsToUnity.ps1",
    "native/luau-host/include/luau_host.h",
    "native/luau-host/exports/luau_host.exports",
    "native/luau-host/tests/luau_host_conformance.cpp",
    "native/luau-host/tests/luau_host_invalid_abi_fixture.c",
    ".gitmodules",
    "tests/Luau.Tests/Luau.Tests.csproj",
    "tests/Luau.Tests/PublicApiContractTests.cs",
    "tests/Luau.Tests/NativeAbiHandshakeTests.cs",
    "tests/Luau.ConsumerContract/Luau.ConsumerContract.csproj",
    "tests/Luau.ConsumerContract/PublicConsumerProbe.cs"
)
foreach ($compatibilityPath in $compatibilityPathsCoveredByUnfilteredPullRequests) {
    Get-RepositoryFile $compatibilityPath | Out-Null
}
Write-Host "PASS: unfiltered pull requests validate API baselines, interop, plugins, native ABI inputs, and deterministic package paths."

$workflowDirectory = Join-Path $repositoryRoot ".github/workflows"
$workflowFiles = @(
    Get-ChildItem -LiteralPath $workflowDirectory -File |
        Where-Object { $_.Extension -in @(".yml", ".yaml") } |
        Sort-Object Name
)
if ($workflowFiles.Count -eq 0) {
    throw "No GitHub Actions workflows were found."
}
$externalActionCount = 0
foreach ($workflowFile in $workflowFiles) {
    $workflowText = [IO.File]::ReadAllText($workflowFile.FullName)
    if ($workflowText.IndexOf('${{ secrets.', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Workflow '$($workflowFile.Name)' must not consume fork secrets."
    }
    if (![Regex]::IsMatch($workflowText, '(?m)^permissions:\s*$') -or
        ![Regex]::IsMatch($workflowText, '(?m)^\s{2}contents:\s+read\s*$')) {
        throw "Workflow '$($workflowFile.Name)' must declare least privilege with contents: read."
    }

    $usesMatches = [Regex]::Matches(
        $workflowText,
        '(?m)^\s*uses:\s*(?<reference>[^\s#]+)(?:\s+#\s*(?<comment>.+?))?\s*$')
    foreach ($usesMatch in $usesMatches) {
        $reference = $usesMatch.Groups["reference"].Value
        if ($reference.StartsWith("./", [StringComparison]::Ordinal)) {
            continue
        }
        $externalActionCount++
        if ($reference -notmatch '^[^@\s]+@[0-9a-fA-F]{40}$') {
            throw "Workflow '$($workflowFile.Name)' action is not pinned by a full commit SHA: $reference"
        }
        if ([string]::IsNullOrWhiteSpace($usesMatch.Groups["comment"].Value)) {
            throw "Workflow '$($workflowFile.Name)' SHA pin lacks a readable release/version comment: $reference"
        }
    }
}
if ($externalActionCount -lt 1) {
    throw "Workflow validation found no externally pinned actions."
}

$dependabot = Read-RepositoryText ".github/dependabot.yml"
foreach ($required in @(
    "version: 2",
    "package-ecosystem: github-actions",
    "interval: weekly"
)) {
    Assert-ContainsLiteral "Reviewed automated action-pin updates" $dependabot $required
}

$nativeWorkflow = Read-RepositoryText ".github/workflows/build-luau-host.yml"
foreach ($required in @(
    "workflow_call:",
    "pull_request:",
    "paths:",
    '".gitmodules"',
    '"native/luau"',
    '"native/luau/**"',
    '"native/luau-host/**"',
    '"Luau.Unity/Runtime/Interop/**"',
    '"Luau.Unity/Runtime/Plugins/**"',
    '"tools/Copy-NativeArtifactsToUnity.ps1"',
    "workflow_dispatch:",
    "linux-sanitize",
    "Linux ASan, UBSan, and bounded fuzz smoke",
    "timeout-minutes: 30",
    'test "$(clang-18 -dumpversion)" = "18.1.3"',
    "luau_host_fuzz_smoke",
    "if: failure()",
    "retention-days: 30",
    "needs: [build-windows, build-android, sanitize-and-fuzz]",
    "tools/Test-UnityPackageRelease.ps1",
    "com.qll.luau.unity-0.2.0.tgz",
    "luau-unity-upm-release-candidate"
)) {
    Assert-ContainsLiteral "Native pull-request/security workflow" $nativeWorkflow $required
}
Assert-LiteralCount "Native submodule gitlink CI trigger" $nativeWorkflow '"native/luau"' 2
Assert-NotContainsLiteral "Docs-only native workflow filtering" $nativeWorkflow '"docs/**"'

$releaseWorkflow = Read-RepositoryText ".github/workflows/release.yml"
foreach ($required in @(
    '"v*.*.*"',
    "Quantum-Lion-Labs/Luau-Unity",
    "git merge-base --is-ancestor HEAD origin/main",
    "needs: require-release-source",
    "uses: ./.github/workflows/validate-managed-package.yml",
    "uses: ./.github/workflows/build-luau-host.yml",
    "needs: [validate-managed, validate-native]",
    "contents: write",
    'RELEASE_TAG: ${{ github.ref_name }}',
    "-Tag `$env:RELEASE_TAG",
    "-SkipUnityConsumer",
    "tools/Test-UnityPackageRelease.ps1",
    "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
    "gh release create",
    "--verify-tag",
    "--generate-notes"
)) {
    Assert-ContainsLiteral "Tag-triggered release workflow" $releaseWorkflow $required
}
Assert-LiteralOrder "Release publication gates" $releaseWorkflow @(
    "Require canonical repository",
    "Require tagged commit on main",
    "needs: [validate-managed, validate-native]",
    "Require tag to match package version",
    "Validate exact tag and build deterministic package",
    "Retain published package inputs",
    "Publish GitHub release"
)

$cmakePresets = Read-RepositoryText "native/luau-host/CMakePresets.json"
foreach ($required in @(
    '"generator": "Visual Studio 17 2022"',
    '"CMAKE_SYSTEM_VERSION": "10.0.22621.0"',
    '"value": "v143"',
    '"VCToolsVersion": "14.42.34433"',
    '"CMAKE_C_COMPILER": "clang-18"',
    '"CMAKE_CXX_COMPILER": "clang++-18"',
    '"LUAU_HOST_ENABLE_SANITIZERS": "ON"',
    '"LUAU_HOST_BUILD_FUZZERS": "ON"'
)) {
    Assert-ContainsLiteral "Pinned sanitizer/fuzzer preset" $cmakePresets $required
}
$hostCMake = Read-RepositoryText "native/luau-host/CMakeLists.txt"
foreach ($required in @(
    "-fsanitize=address,undefined",
    "-fno-sanitize-recover=all",
    "luau_host_compiler_fuzzer",
    "luau_host_abi_fuzzer",
    "luau_host_fuzz_smoke",
    "-runs=5000",
    "-runs=10000",
    "fuzz-artifacts"
)) {
    Assert-ContainsLiteral "Native sanitizer/fuzzer targets" $hostCMake $required
}
Assert-LiteralCount "Native libFuzzer per-target timeout bound" $hostCMake "-timeout=5" 2
Assert-LiteralCount "Native libFuzzer per-target RSS bound" $hostCMake "-rss_limit_mb=2048" 2

$artifactFuzzEngine = Read-RepositoryText "fuzz/Luau.ArtifactFuzz/ArtifactFuzzEngine.cs"
$runOneStart = $artifactFuzzEngine.IndexOf(
    "    bool RunOne(",
    [StringComparison]::Ordinal)
if ($runOneStart -lt 0) {
    throw "Managed artifact fuzz per-input evaluation boundary is missing."
}
$evaluateMethodStart = $artifactFuzzEngine.IndexOf(
    "    static ParseOutcome Evaluate(byte[] input)",
    $runOneStart,
    [StringComparison]::Ordinal)
if ($evaluateMethodStart -lt 0) {
    throw "Managed artifact fuzz per-input evaluation boundary is missing."
}
$runOneText = $artifactFuzzEngine.Substring($runOneStart, $evaluateMethodStart - $runOneStart)
Assert-LiteralOrder "Managed artifact fuzz per-input checkpoint" $runOneText @(
    "ReproducerStore.Checkpoint(options.ReproducerDirectory, input, context);",
    "var outcome = Evaluate(input);"
)
Assert-NotContainsLiteral "Managed artifact fuzz per-input checkpoint retention" $runOneText "ReproducerStore.ClearCheckpoint"
Assert-LiteralCount `
    "Managed artifact fuzz successful-run checkpoint cleanup" `
    $artifactFuzzEngine `
    "ReproducerStore.ClearCheckpoint(options.ReproducerDirectory);" `
    2

$artifactFuzzReproducerStore = Read-RepositoryText "fuzz/Luau.ArtifactFuzz/ReproducerStore.cs"
foreach ($required in @(
    'const string CheckpointBinaryName = "current-input.bin";',
    'const string CheckpointReportName = "current-input.txt";',
    "WriteAtomically(Path.Combine(directory, CheckpointBinaryName), input);",
    "Path.Combine(directory, CheckpointReportName),",
    "foreach (var name in new[] { CheckpointBinaryName, CheckpointReportName })",
    "File.Delete(path);"
)) {
    Assert-ContainsLiteral "Managed artifact fuzz in-flight checkpoint storage" $artifactFuzzReproducerStore $required
}

$windowsNativeWorkflow = Read-RepositoryText ".github/workflows/build-luau-host.windows.yml"
foreach ($required in @(
    "Provision pinned MSVC 14.42 compatibility toolset",
    '$reviewedComponent = "Microsoft.VisualStudio.Component.VC.14.42.17.12.x86.x64"',
    "Microsoft.VisualStudio.Product.Enterprise",
    '-version "[17.0,18.0)"',
    "Microsoft Visual Studio/Installer/setup.exe",
    '"modify"',
    "Start-Process",
    "-WindowStyle Hidden",
    'if ($installerProcess.ExitCode -ne 0)',
    '"LUAU_HOST_VS_INSTALLATION_PATH=$installationPath"',
    '$installationPath = $env:LUAU_HOST_VS_INSTALLATION_PATH',
    '"-DCMAKE_GENERATOR_INSTANCE=$env:LUAU_HOST_VS_INSTALLATION_PATH"',
    "Verify pinned MSVC toolchain",
    '$reviewedToolsVersion = "14.42.34433"',
    '$reviewedCompilerVersion = [Version]"19.42.34444.0"',
    '$reviewedLinkerVersion = [Version]"14.42.34444.0"',
    '$actualCompilerVersion -ne $reviewedCompilerVersion',
    '$actualLinkerVersion -ne $reviewedLinkerVersion',
    '$reviewedSdkVersion = "10.0.22621.0"',
    "Windows Kits/10",
    "Require current shipped Windows plugin",
    "Shape shipping host and external symbols",
    "native/luau-host/out/shipping/win-x64/",
    "native/luau-host/out/symbols/win-x64/"
)) {
    Assert-ContainsLiteral "Windows native release artifacts" $windowsNativeWorkflow $required
}
Assert-LiteralOrder "Windows native pinned toolchain provisioning" $windowsNativeWorkflow @(
    "Provision pinned MSVC 14.42 compatibility toolset",
    "Microsoft.VisualStudio.Component.VC.14.42.17.12.x86.x64",
    '"LUAU_HOST_VS_INSTALLATION_PATH=$installationPath"',
    "Verify pinned MSVC toolchain",
    '$installationPath = $env:LUAU_HOST_VS_INSTALLATION_PATH',
    '"-DCMAKE_GENERATOR_INSTANCE=$env:LUAU_HOST_VS_INSTALLATION_PATH"'
)
Assert-LiteralCount "Windows native Enterprise instance discovery" $windowsNativeWorkflow "Microsoft.VisualStudio.Product.Enterprise" 1
Assert-LiteralCount "Windows native VS 2022 version filter" $windowsNativeWorkflow '-version "[17.0,18.0)"' 1
$nativeCmake = Read-RepositoryText "native/luau-host/CMakeLists.txt"
foreach ($required in @(
    'set(_luau_host_android_build_recipe "android-shipping-v1")',
    'inputs=${LUAU_HOST_BUILD_INPUT_FINGERPRINT}',
    'ndk=${LUAU_HOST_APPROVED_ANDROID_NDK_REVISION}',
    'compiler=${CMAKE_CXX_COMPILER_ID}-${CMAKE_CXX_COMPILER_VERSION}',
    '"LINKER:--build-id=0x${_luau_host_android_build_id}"'
)) {
    Assert-ContainsLiteral "Android native deterministic build ID" $nativeCmake $required
}
$androidNativeWorkflow = Read-RepositoryText ".github/workflows/build-luau-host.android.yml"
foreach ($required in @(
    "Require current shipped Android plugin",
    "Shape stripped shipping host and external symbols",
    'native/luau-host/out/shipping/${{ matrix.platform }}/',
    'native/luau-host/out/symbols/${{ matrix.platform }}/'
)) {
    Assert-ContainsLiteral "Android native release artifacts" $androidNativeWorkflow $required
}
Write-Host "PASS: workflows use reviewed SHA pins, least privilege, native-sensitive PR gates, pinned sanitizer/fuzz lanes, reproducible artifact uploads, and automated pin maintenance."

Write-Host "Unity package static policy validation passed."
