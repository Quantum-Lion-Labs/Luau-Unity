param(
    [string] $UnityPath,
    [string] $PackageReference,
    [string] $OutputRoot,
    [string] $ExpectedGitCommit = "",

    [ValidateRange(1, 180)]
    [int] $UnityTimeoutMinutes = 20
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$integrationProjectRoot = Join-Path $repositoryRoot "tests/Luau.Unity.Integration"
$packageRoot = Join-Path $repositoryRoot "Luau.Unity"
$fixtureRoot = Join-Path $repositoryRoot "tests/Luau.Unity.PackageConsumerProbe"
$allowedOutputRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "native/luau-host/out"))
$versionFile = Join-Path $integrationProjectRoot "ProjectSettings/ProjectVersion.txt"
$passedMarker = "LUAU_PACKAGE_CONSUMER_PASS"
$failedMarker = "LUAU_PACKAGE_CONSUMER_FAIL"

function Assert-ExistingFile([string] $Path, [string] $Description) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
}

function Assert-ExistingDirectory([string] $Path, [string] $Description) {
    if (!(Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description is missing: $Path"
    }
}

function Test-IsSameOrDescendant([string] $Path, [string] $Parent) {
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $parentPrefix = $fullParent + [System.IO.Path]::DirectorySeparatorChar

    return $fullPath.Equals($fullParent, [System.StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-StrictDescendant([string] $Path, [string] $Parent) {
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $parentPrefix = $fullParent + [System.IO.Path]::DirectorySeparatorChar

    if (!$fullPath.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path that is not a strict descendant of $fullParent`: $fullPath"
    }
}

function Remove-DisposableProject([string] $Path) {
    Assert-StrictDescendant $Path $allowedOutputRoot
    $lastError = $null
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            $lastError = $_
            if ($attempt -lt 20) {
                Start-Sleep -Milliseconds 250
            }
        }
    }

    throw "The generated Unity package consumer passed but cleanup failed: $Path`n$lastError"
}

function ConvertTo-UpmPackageReference([string] $Reference) {
    if ([string]::IsNullOrWhiteSpace($Reference)) {
        Assert-ExistingDirectory $packageRoot "Standalone package"
        return "file:" + $packageRoot.Replace(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    }

    $candidate = $Reference.Trim()
    $pathCandidate = $candidate
    $hasFilePrefix = $candidate.StartsWith(
        "file:",
        [System.StringComparison]::OrdinalIgnoreCase)
    if ($hasFilePrefix) {
        $pathCandidate = $candidate.Substring(5)
    }

    if ($hasFilePrefix -or [System.IO.Path]::IsPathRooted($pathCandidate) -or
        (Test-Path -LiteralPath (Join-Path $repositoryRoot $pathCandidate))) {
        if (![System.IO.Path]::IsPathRooted($pathCandidate)) {
            $pathCandidate = Join-Path $repositoryRoot $pathCandidate
        }
        if (!(Test-Path -LiteralPath $pathCandidate)) {
            throw "The local package reference does not exist: $pathCandidate"
        }

        $fullPackagePath = [System.IO.Path]::GetFullPath($pathCandidate)
        return "file:" + $fullPackagePath.Replace(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    }

    return $candidate
}

function Resolve-PackageContentRoot(
    [string] $ResolvedReference,
    [string] $MaterializationRoot) {
    if ($ResolvedReference.StartsWith(
        "file:",
        [System.StringComparison]::OrdinalIgnoreCase)) {
        if (![string]::IsNullOrWhiteSpace($ExpectedGitCommit)) {
            throw "ExpectedGitCommit can only be verified for an exact git package reference."
        }
        $localPath = $ResolvedReference.Substring(5)
        Assert-ExistingDirectory $localPath "Referenced local package"
        return [System.IO.Path]::GetFullPath($localPath)
    }

    $gitReference = [regex]::Match(
        $ResolvedReference,
        '^(?<repository>.+?\.git)(?:\?path=(?<path>[^#]+))?#(?<revision>[^#]+)$')
    if (!$gitReference.Success) {
        throw (
            "Sample validation requires a local file package or an exact git reference " +
            "ending in .git?path=<package>#<revision>: $ResolvedReference")
    }

    $repository = $gitReference.Groups["repository"].Value
    $revision = $gitReference.Groups["revision"].Value
    if ($repository.StartsWith("-", [System.StringComparison]::Ordinal) -or
        $revision.StartsWith("-", [System.StringComparison]::Ordinal) -or
        $revision -notmatch '^[A-Za-z0-9._/-]+$' -or
        $revision.Contains("..")) {
        throw "The exact git package reference contains an unsafe repository or revision."
    }

    Assert-StrictDescendant $MaterializationRoot $allowedOutputRoot
    $gitOutput = (& git init --quiet $MaterializationRoot 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to initialize the exact package materialization: $gitOutput"
    }
    $gitOutput = (& git -C $MaterializationRoot remote add origin $repository 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to configure the exact package origin: $gitOutput"
    }
    $gitOutput = (& git -C $MaterializationRoot fetch --quiet --depth 1 origin $revision 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to fetch exact package revision '$revision': $gitOutput"
    }
    $gitOutput = (& git -C $MaterializationRoot checkout --quiet --detach FETCH_HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to check out exact package revision '$revision': $gitOutput"
    }
    $materializedCommit = (& git -C $MaterializationRoot rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $materializedCommit -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Unable to resolve the materialized exact package commit: $materializedCommit"
    }
    if (![string]::IsNullOrWhiteSpace($ExpectedGitCommit)) {
        if ($ExpectedGitCommit -notmatch '^[0-9a-fA-F]{40}$' -or
            !$materializedCommit.Equals(
                $ExpectedGitCommit,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw (
                "Materialized package commit '$materializedCommit' does not match expected " +
                "commit '$ExpectedGitCommit'.")
        }
    }

    $relativePackagePath = [Uri]::UnescapeDataString(
        $gitReference.Groups["path"].Value).Trim([char[]]@('/', '\'))
    $contentRoot = if ([string]::IsNullOrEmpty($relativePackagePath)) {
        [System.IO.Path]::GetFullPath($MaterializationRoot)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $MaterializationRoot $relativePackagePath))
    }
    if (!(Test-IsSameOrDescendant $contentRoot $MaterializationRoot)) {
        throw "The git package path escapes its exact materialization: $relativePackagePath"
    }
    Assert-ExistingDirectory $contentRoot "Materialized exact package content"
    return $contentRoot
}

Assert-ExistingFile $versionFile "Integration-project Unity version"
Assert-ExistingDirectory $fixtureRoot "Package-consumer source fixture"

$requiredFixtureFiles = @(
    "ConsumerProbe.asmdef",
    "ConsumerProbe.asmdef.meta",
    "ConsumerApiProbe.cs",
    "ConsumerApiProbe.cs.meta",
    "ConsumerGeneratedLibrary.cs",
    "ConsumerGeneratedLibrary.cs.meta",
    "Editor.meta",
    "Editor/RunConsumerProbe.cs",
    "Editor/RunConsumerProbe.cs.meta"
)
foreach ($relativePath in $requiredFixtureFiles) {
    Assert-ExistingFile (Join-Path $fixtureRoot $relativePath) "Package-consumer fixture file"
}

$unexpectedFixtureFiles = @(Get-ChildItem -LiteralPath $fixtureRoot -Recurse -File |
    ForEach-Object {
        $_.FullName.Substring($fixtureRoot.Length + 1).Replace(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    } |
    Where-Object { $requiredFixtureFiles -notcontains $_ })
if ($unexpectedFixtureFiles.Count -ne 0) {
    throw "The package-consumer fixture contains unexpected files: $($unexpectedFixtureFiles -join ', ')"
}

$versionLine = Get-Content -LiteralPath $versionFile |
    Where-Object { $_ -like "m_EditorVersion:*" } |
    Select-Object -First 1
if (!$versionLine) {
    throw "The integration-project Unity version is missing from $versionFile."
}
$unityVersion = ($versionLine -split ":", 2)[1].Trim()

if (!$UnityPath) {
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:UNITY_EXE) {
        $candidates.Add($env:UNITY_EXE)
    }
    if ($env:UNITY_PATH) {
        $candidates.Add($env:UNITY_PATH)
    }
    if ($env:ProgramFiles) {
        $candidates.Add((Join-Path $env:ProgramFiles "Unity/Hub/Editor/$unityVersion/Editor/Unity.exe"))
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates.Add(
            (Join-Path ${env:ProgramFiles(x86)} "Unity/Hub/Editor/$unityVersion/Editor/Unity.exe"))
    }

    $UnityPath = $candidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}

if (!$UnityPath -or !(Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity $unityVersion was not found. Pass -UnityPath with the matching Unity.exe."
}
$UnityPath = (Resolve-Path -LiteralPath $UnityPath).Path
$resolvedPackageReference = ConvertTo-UpmPackageReference $PackageReference

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = $allowedOutputRoot
}
$outputParent = [System.IO.Path]::GetFullPath($OutputRoot)
if (!(Test-IsSameOrDescendant $outputParent $allowedOutputRoot)) {
    throw "The package-consumer output root must stay under $allowedOutputRoot`: $outputParent"
}
New-Item -ItemType Directory -Path $outputParent -Force | Out-Null

$projectPath = Join-Path $outputParent (
    "unity-package-consumer-" + [Guid]::NewGuid().ToString("N"))
Assert-StrictDescendant $projectPath $allowedOutputRoot
$assetsPath = Join-Path $projectPath "Assets/ConsumerProbe"
$packagesPath = Join-Path $projectPath "Packages"
$projectSettingsPath = Join-Path $projectPath "ProjectSettings"
$logPath = Join-Path $projectPath "Logs/package-consumer.log"

New-Item -ItemType Directory -Path $assetsPath -Force | Out-Null
New-Item -ItemType Directory -Path $packagesPath -Force | Out-Null
New-Item -ItemType Directory -Path $projectSettingsPath -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $logPath) -Force | Out-Null
Get-ChildItem -LiteralPath $fixtureRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $assetsPath -Recurse -Force
}

$packageContentRoot = Resolve-PackageContentRoot `
    $resolvedPackageReference `
    (Join-Path $projectPath "PackageSource")
$resolvedPackageJsonPath = Join-Path $packageContentRoot "package.json"
Assert-ExistingFile $resolvedPackageJsonPath "Referenced package metadata"
Assert-ExistingFile `
    (Join-Path $packageContentRoot "Runtime/Luau.xml") `
    "Referenced managed XML IntelliSense artifact"
$packageMetadata = Get-Content -LiteralPath $resolvedPackageJsonPath -Raw | ConvertFrom-Json
if ($packageMetadata.name -cne "com.qll.luau.unity") {
    throw "The referenced package has an unexpected identity: $($packageMetadata.name)"
}
$declaredSamples = @($packageMetadata.samples)
$expectedSampleNames = @("Getting Started", "Full Luau Scripting Demo")
$expectedSamplePaths = @(
    "Samples~/Getting Started",
    "Samples~/Full Luau Scripting Demo")
if ($declaredSamples.Count -ne $expectedSampleNames.Count) {
    throw (
        "The referenced package must declare exactly the two maintained samples; " +
        "found $($declaredSamples.Count).")
}
for ($sampleIndex = 0; $sampleIndex -lt $expectedSampleNames.Count; $sampleIndex++) {
    $sample = $declaredSamples[$sampleIndex]
    if ($sample.displayName -cne $expectedSampleNames[$sampleIndex] -or
        $sample.path -cne $expectedSamplePaths[$sampleIndex]) {
        throw (
            "Declared package sample mismatch at index $sampleIndex. Expected " +
            "'$($expectedSampleNames[$sampleIndex])' at '$($expectedSamplePaths[$sampleIndex])'; " +
            "found '$($sample.displayName)' at '$($sample.path)'.")
    }
}
$sampleImportRoot = Join-Path $projectPath (
    "Assets/Samples/Luau.Unity/" + $packageMetadata.version)
foreach ($sample in $declaredSamples) {
    if ([string]::IsNullOrWhiteSpace($sample.displayName) -or
        [string]::IsNullOrWhiteSpace($sample.path)) {
        throw "Every declared package sample requires a displayName and path."
    }

    if (!$sample.path.StartsWith("Samples~/", [System.StringComparison]::Ordinal) -or
        $sample.displayName.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $sample.displayName.Contains("..")) {
        throw "Declared package sample contains an unsafe display name or path."
    }

    $sampleSource = [System.IO.Path]::GetFullPath((Join-Path $packageContentRoot $sample.path))
    if (!(Test-IsSameOrDescendant $sampleSource $packageContentRoot)) {
        throw "Declared package sample escapes the referenced package: $($sample.path)"
    }
    Assert-ExistingDirectory $sampleSource "Declared package sample '$($sample.displayName)'"
    $sampleDestination = [System.IO.Path]::GetFullPath(
        (Join-Path $sampleImportRoot $sample.displayName))
    if (!(Test-IsSameOrDescendant $sampleDestination $sampleImportRoot)) {
        throw "Declared package sample destination escapes the consumer Assets tree."
    }
    New-Item -ItemType Directory -Path $sampleDestination -Force | Out-Null
    Get-ChildItem -LiteralPath $sampleSource -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $sampleDestination -Recurse -Force
    }
}

# This consumer intentionally compiles the reusable core after applying the
# documented "delete Demo Game" workflow. Test-UnityHost imports the complete
# sample separately, so both the starter-kit and full-demo shapes are covered.
$fullDemoImport = [System.IO.Path]::GetFullPath(
    (Join-Path $sampleImportRoot "Full Luau Scripting Demo"))
$demoGameImport = [System.IO.Path]::GetFullPath(
    (Join-Path $fullDemoImport "Demo Game"))
$demoGameMetaImport = $demoGameImport + ".meta"
Assert-ExistingDirectory $demoGameImport "Imported Full Luau Scripting Demo game content"
Assert-ExistingFile $demoGameMetaImport "Imported Full Luau Scripting Demo game metadata"
Assert-StrictDescendant $demoGameImport $projectPath
Remove-Item -LiteralPath $demoGameImport -Recurse -Force
Remove-Item -LiteralPath $demoGameMetaImport -Force

Copy-Item -LiteralPath $versionFile -Destination (
    Join-Path $projectSettingsPath "ProjectVersion.txt") -Force

$manifest = [ordered]@{
    dependencies = [ordered]@{
        "com.qll.luau.unity" = $resolvedPackageReference
        "com.unity.modules.audio" = "1.0.0"
        "com.unity.modules.physics2d" = "1.0.0"
    }
}
$manifestJson = $manifest | ConvertTo-Json -Depth 4
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText(
    (Join-Path $packagesPath "manifest.json"),
    $manifestJson,
    $utf8WithoutBom)

Write-Host (
    "Running generated minimal Unity package consumer with Unity $unityVersion " +
    "after deleting the demo game and retaining its reusable Core.")
Write-Host "Disposable project: $projectPath"
$unityArguments = @(
    "-batchmode",
    "-nographics",
    "-quit",
    "-projectPath",
    ('"' + $projectPath + '"'),
    "-executeMethod",
    "Luau.Unity.PackageConsumerProbe.RunConsumerProbe.Execute",
    "-logFile",
    ('"' + $logPath + '"')
)

$unityProcess = Start-Process `
    -FilePath $UnityPath `
    -ArgumentList $unityArguments `
    -WindowStyle Hidden `
    -PassThru
$timedOut = $false
$exitCode = $null
try {
    $timeoutMilliseconds = [int]([TimeSpan]::FromMinutes($UnityTimeoutMinutes).TotalMilliseconds)
    if (!$unityProcess.WaitForExit($timeoutMilliseconds)) {
        $timedOut = $true
        $processId = $unityProcess.Id
        Write-Host "Unity consumer timed out after $UnityTimeoutMinutes minute(s); terminating process tree $processId."
        $taskkill = Join-Path $env:SystemRoot "System32/taskkill.exe"
        & $taskkill /PID $processId /T /F 2>&1 | Write-Host
        $null = $unityProcess.WaitForExit(30000)
    }
    else {
        $unityProcess.Refresh()
        $exitCode = $unityProcess.ExitCode
    }
}
finally {
    $unityProcess.Dispose()
}

$log = if (Test-Path -LiteralPath $logPath -PathType Leaf) {
    Get-Content -LiteralPath $logPath -Raw
} else {
    ""
}
$reportedFailure = $log -match [regex]::Escape($failedMarker)
$compilerOrPackageFailure = $log -match (
    "(?im)^.*(?:error CS\d+|Scripts have compiler errors|Compilation failed|" +
    "Failed to resolve packages?|An error occurred while resolving packages?|" +
    "DllNotFoundException.*luau_host|EntryPointNotFoundException.*luau_host).*$")
$passed = $log -match [regex]::Escape($passedMarker)

if ($timedOut -or $exitCode -ne 0 -or !$passed -or $reportedFailure -or $compilerOrPackageFailure) {
    if ($log) {
        Get-Content -LiteralPath $logPath -Tail 160
    }

    Write-Host "Failed disposable project and log retained at $projectPath"
    throw (
        "The generated Unity package consumer failed " +
        "(timed out: $timedOut, Unity exit code $exitCode, pass marker present: $passed).")
}

if (![string]::IsNullOrWhiteSpace($ExpectedGitCommit)) {
    try {
        $packageLockPath = Join-Path $packagesPath "packages-lock.json"
        Assert-ExistingFile $packageLockPath "Generated consumer package lock"
        $packageLock = Get-Content -LiteralPath $packageLockPath -Raw | ConvertFrom-Json
        $lockProperty = $packageLock.dependencies.PSObject.Properties |
            Where-Object { $_.Name -ceq $packageMetadata.name } |
            Select-Object -First 1
        if ($null -eq $lockProperty) {
            throw "Generated package lock is missing '$($packageMetadata.name)'."
        }
        $lockEntry = $lockProperty.Value
        if ($lockEntry.source -cne "git" -or
            $lockEntry.version -cne $resolvedPackageReference -or
            !$lockEntry.hash.Equals($ExpectedGitCommit, [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                "Generated package lock did not resolve the exact requested git package. " +
                "Expected version '$resolvedPackageReference' and commit '$ExpectedGitCommit'; " +
                "found source '$($lockEntry.source)', version '$($lockEntry.version)', hash '$($lockEntry.hash)'.")
        }
    }
    catch {
        Write-Host "Failed disposable project and log retained at $projectPath"
        throw
    }
}

Remove-DisposableProject $projectPath
if (Test-Path -LiteralPath $projectPath) {
    throw "The generated Unity package consumer passed but cleanup failed: $projectPath"
}

Write-Host (
    "Generated minimal Unity consumer resolved the package, compiled all imported samples and " +
    "source-generator output, validated XML IntelliSense, loaded the native VM, and executed successfully.")
