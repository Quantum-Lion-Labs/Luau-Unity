<#
.SYNOPSIS
Prepares and optionally validates a disposable Unity project against luau_host.

.DESCRIPTION
Copies only Assets, Packages, and ProjectSettings from
tests/Luau.Unity.Integration into an ignored directory below
native/luau-host/out. The standalone Luau.Unity package is staged under the
disposable project's Packages directory, and the copied manifest and lock file
are normalized to that self-contained package copy. The script builds the
managed runtime and installs the selected luau_host native plugins with the
existing reviewed Unity importer metadata. Both declared package samples are
imported into the disposable Assets tree so Editor compilation and every
supported IL2CPP smoke build compile the same public sample sources.

With no validation switches, the script only prepares the disposable project.
Unity is launched in batch mode only when Compile, EditModeTests, or a smoke
gate is supplied. Smoke gates build and launch the player, capture a dedicated
log, reject LUAU_PLAYER_SMOKE_FAIL, and require LUAU_PLAYER_SMOKE_PASS. A Unity
run will create its own Library under the disposable project; the source
project's Library is never copied or modified.

.PARAMETER Configuration
Managed build configuration. Release is the default.

.PARAMETER OutputRoot
Disposable validation root. It must be a strict descendant of
native/luau-host/out so cleanup cannot affect source or other repository files.

.PARAMETER UnityPath
Path to Unity.exe. When omitted, UNITY_EXE/UNITY_PATH and the Unity Hub install
matching tests/Luau.Unity.Integration/ProjectSettings/ProjectVersion.txt are
searched.

.PARAMETER WindowsPlugin
Absolute path to luau_host.dll. The checked-in package plugin is used when
omitted. Pass this parameter explicitly to validate a fresh native build instead.

.PARAMETER AndroidX64Plugin
Absolute path to libluau_host.so for Android x86_64. The checked-in package
plugin is used when omitted. Pass this parameter explicitly to validate a fresh
native build instead.

.PARAMETER AndroidArm64Plugin
Absolute path to libluau_host.so for Android ARM64. The checked-in package
plugin is used when omitted. Pass this parameter explicitly to validate a fresh
native build instead.

.PARAMETER AdbPath
Path to adb.exe. When omitted, the SDK bundled with the selected Unity editor,
ANDROID_SDK_ROOT/ANDROID_HOME, the user Android SDK, and PATH are searched in
that order.

.PARAMETER AndroidArm64Serial
ADB serial for the connected ARM64 Meta Quest. When omitted, exactly one online
ARM64 Quest must be discoverable. Explicit serials are still validated for
online state, ARM64 ABI, and Quest identity before installation.

.PARAMETER AndroidX64Serial
ADB serial for the Android x64 emulator. When omitted, exactly one online x64
emulator must be discoverable. Explicit serials are still validated for online
state, x86_64 ABI, and emulator identity before installation.

.PARAMETER SmokeTimeoutSeconds
Maximum time to wait for a player to exit or emit a smoke marker. The default
is 120 seconds.

.PARAMETER Compile
Launch Unity once in batch mode to import and compile the disposable project.

.PARAMETER EditModeTests
Run all EditMode tests in the disposable project and write NUnit XML under the
validation output root.

.PARAMETER WindowsSmoke
Build the existing Windows x64 IL2CPP Luau smoke player from the disposable
project, launch it headlessly, and require its pass marker in a dedicated log.

.PARAMETER AndroidArm64Smoke
Build the Android ARM64 IL2CPP smoke APK, install and launch it on a safely
selected connected Meta Quest, require its pass marker, and uninstall it.

.PARAMETER AndroidX64Smoke
Build the existing Android x64 IL2CPP Luau smoke APK from the disposable
project, install and launch it on a safely selected x64 emulator, require its
pass marker, and uninstall it.

.EXAMPLE
pwsh -File tools/Test-UnityHost.ps1

Prepares the host project without launching Unity.

.EXAMPLE
pwsh -File tools/Test-UnityHost.ps1 -Compile -EditModeTests

Prepares the project, verifies Unity compilation, and runs EditMode tests.

.EXAMPLE
pwsh -File tools/Test-UnityHost.ps1 -WindowsSmoke

Builds and runs the disposable Windows x64 IL2CPP smoke player.

.EXAMPLE
pwsh -File tools/Test-UnityHost.ps1 -AndroidArm64Smoke -AndroidArm64Serial 2G0YC1ZF8K06WK

Builds and runs the Android ARM64 smoke on the selected Quest.

.EXAMPLE
pwsh -File tools/Test-UnityHost.ps1 -AndroidX64Smoke -AndroidX64Serial emulator-5554

Builds and runs the Android x64 smoke on the selected emulator.
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $OutputRoot,
    [string] $UnityPath,
    [string] $WindowsPlugin,
    [string] $AndroidArm64Plugin,
    [string] $AndroidX64Plugin,
    [string] $AdbPath,
    [string] $AndroidArm64Serial,
    [string] $AndroidX64Serial,

    [ValidateRange(10, 600)]
    [int] $SmokeTimeoutSeconds = 120,

    [switch] $Compile,
    [switch] $EditModeTests,
    [switch] $WindowsSmoke,
    [switch] $AndroidArm64Smoke,
    [switch] $AndroidX64Smoke
)

$ErrorActionPreference = "Stop"

function Get-AbsolutePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Assert-StrictDescendantPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Parent,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $separator = [System.IO.Path]::DirectorySeparatorChar
    $parentPrefix = $Parent.TrimEnd($separator, [System.IO.Path]::AltDirectorySeparatorChar) + $separator
    if (!$Path.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be a strict descendant of $Parent (received $Path)."
    }
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Command,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    Write-Host "==> $Description"
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Native tools such as adb may write ordinary startup notices to
        # stderr while returning success. Their exit code is authoritative.
        $ErrorActionPreference = "Continue"
        & $Command @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Command,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = @(& $Command @Arguments 2>&1 | ForEach-Object { $_.ToString() })
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        $details = $output -join [Environment]::NewLine
        throw "$Description failed with exit code $exitCode.$([Environment]::NewLine)$details"
    }

    return $output
}

function Resolve-FirstExistingFile {
    param(
        [string] $ExplicitPath,
        [string[]] $Candidates,
        [Parameter(Mandatory = $true)]
        [string] $BasePath,
        [Parameter(Mandatory = $true)]
        [string] $Description,
        [switch] $Required
    )

    if (![string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (![System.IO.Path]::IsPathRooted($ExplicitPath)) {
            throw "$Description must be selected with an absolute path when supplied explicitly."
        }
        $resolved = Get-AbsolutePath -Path $ExplicitPath -BasePath $BasePath
        if (!(Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "$Description was not found: $resolved"
        }
        return $resolved
    }

    foreach ($candidate in $Candidates) {
        $resolved = Get-AbsolutePath -Path $candidate -BasePath $BasePath
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            return $resolved
        }
    }

    if ($Required) {
        throw "$Description was not found. Refresh the checked-in package artifact or pass an explicit path."
    }

    return $null
}

function Resolve-UnityEditor {
    param(
        [string] $ExplicitPath,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,
        [switch] $Required
    )

    $candidates = New-Object System.Collections.Generic.List[string]
    if (![string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates.Add($ExplicitPath)
    }
    if (![string]::IsNullOrWhiteSpace($env:UNITY_EXE)) {
        $candidates.Add($env:UNITY_EXE)
    }
    if (![string]::IsNullOrWhiteSpace($env:UNITY_PATH)) {
        $candidates.Add($env:UNITY_PATH)
    }

    $projectVersionPath = Join-Path $RepositoryRoot "tests/Luau.Unity.Integration/ProjectSettings/ProjectVersion.txt"
    if (Test-Path -LiteralPath $projectVersionPath -PathType Leaf) {
        $versionLine = Select-String -LiteralPath $projectVersionPath -Pattern '^m_EditorVersion:\s*(\S+)' | Select-Object -First 1
        if ($versionLine -and $versionLine.Matches.Count -gt 0) {
            $version = $versionLine.Matches[0].Groups[1].Value
            $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
            if (![string]::IsNullOrWhiteSpace($programFiles)) {
                $candidates.Add((Join-Path $programFiles "Unity/Hub/Editor/$version/Editor/Unity.exe"))
            }
        }
    }

    foreach ($candidate in $candidates) {
        $resolved = Get-AbsolutePath -Path $candidate -BasePath $RepositoryRoot
        if (Test-Path -LiteralPath $resolved -PathType Container) {
            $resolved = Join-Path $resolved "Unity.exe"
        }
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            return $resolved
        }
    }

    if ($Required) {
        throw "Unity.exe was not found. Install the project version or pass -UnityPath."
    }

    return $null
}

function Resolve-AdbExecutable {
    param(
        [string] $ExplicitPath,
        [string] $UnityEditor,
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    if (![string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolvedExplicit = Get-AbsolutePath -Path $ExplicitPath -BasePath $RepositoryRoot
        if (Test-Path -LiteralPath $resolvedExplicit -PathType Container) {
            $resolvedExplicit = Join-Path $resolvedExplicit "adb.exe"
        }
        if (!(Test-Path -LiteralPath $resolvedExplicit -PathType Leaf)) {
            throw "The requested adb executable was not found: $resolvedExplicit"
        }
        return $resolvedExplicit
    }

    $candidates = New-Object System.Collections.Generic.List[string]

    if (![string]::IsNullOrWhiteSpace($UnityEditor)) {
        $unityEditorDirectory = Split-Path -Parent $UnityEditor
        $candidates.Add((Join-Path $unityEditorDirectory "Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"))
    }

    foreach ($environmentName in @("ANDROID_SDK_ROOT", "ANDROID_HOME")) {
        $sdkRoot = [Environment]::GetEnvironmentVariable($environmentName)
        if (![string]::IsNullOrWhiteSpace($sdkRoot)) {
            $candidates.Add((Join-Path $sdkRoot "platform-tools/adb.exe"))
        }
    }

    if (![string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA "Android/Sdk/platform-tools/adb.exe"))
    }

    $pathAdb = Get-Command "adb" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pathAdb) {
        $candidates.Add($pathAdb.Source)
    }

    foreach ($candidate in $candidates) {
        $resolved = Get-AbsolutePath -Path $candidate -BasePath $RepositoryRoot
        if (Test-Path -LiteralPath $resolved -PathType Container) {
            $resolved = Join-Path $resolved "adb.exe"
        }
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            return $resolved
        }
    }

    throw "adb.exe was not found. Install Unity Android support or pass -AdbPath."
}

function Get-AdbDeviceInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Adb
    )

    $output = @(Invoke-NativeCapture -Command $Adb -Arguments @("devices", "-l") -Description "List Android devices")
    $devices = New-Object System.Collections.Generic.List[object]
    foreach ($line in $output) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or
            $trimmed.StartsWith("List of devices", [System.StringComparison]::OrdinalIgnoreCase) -or
            $trimmed.StartsWith("* daemon", [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if ($trimmed -match '^(\S+)\s+(\S+)(?:\s+.*)?$') {
            $devices.Add([pscustomobject]@{
                Serial = $Matches[1]
                State = $Matches[2]
            })
        }
    }

    return $devices.ToArray()
}

function Get-AdbProperty {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Adb,
        [Parameter(Mandatory = $true)]
        [string] $Serial,
        [Parameter(Mandatory = $true)]
        [string] $Property
    )

    $output = @(Invoke-NativeCapture `
        -Command $Adb `
        -Arguments @("-s", $Serial, "shell", "getprop", $Property) `
        -Description "Read $Property from Android target $Serial")
    return ($output -join "`n").Trim()
}

function Get-AdbDeviceDetails {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Adb,
        [Parameter(Mandatory = $true)]
        [object] $Device
    )

    return [pscustomobject]@{
        Serial = $Device.Serial
        State = $Device.State
        Abi = Get-AdbProperty -Adb $Adb -Serial $Device.Serial -Property "ro.product.cpu.abi"
        Model = Get-AdbProperty -Adb $Adb -Serial $Device.Serial -Property "ro.product.model"
        Manufacturer = Get-AdbProperty -Adb $Adb -Serial $Device.Serial -Property "ro.product.manufacturer"
        Qemu = Get-AdbProperty -Adb $Adb -Serial $Device.Serial -Property "ro.kernel.qemu"
    }
}

function Format-AdbDeviceSummary {
    param([object[]] $Devices)

    if (!$Devices -or $Devices.Count -eq 0) {
        return "(none)"
    }

    return ($Devices | ForEach-Object {
        $abi = if ($_.PSObject.Properties.Name -contains "Abi") { $_.Abi } else { "unknown" }
        $model = if ($_.PSObject.Properties.Name -contains "Model") { $_.Model } else { "unknown" }
        "$($_.Serial) state=$($_.State) abi=$abi model=$model"
    }) -join "; "
}

function Select-AdbSmokeTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Adb,
        [string] $ExplicitSerial,
        [Parameter(Mandatory = $true)]
        [ValidateSet("QuestArm64", "EmulatorX64")]
        [string] $Kind
    )

    if (![string]::IsNullOrWhiteSpace($ExplicitSerial) -and $ExplicitSerial -notmatch '^[A-Za-z0-9._:-]+$') {
        throw "ADB serial contains unsupported characters: $ExplicitSerial"
    }

    $inventory = @(Get-AdbDeviceInventory -Adb $Adb)
    if (![string]::IsNullOrWhiteSpace($ExplicitSerial)) {
        $selectedInventory = @($inventory | Where-Object { $_.Serial -eq $ExplicitSerial })
        if ($selectedInventory.Count -ne 1) {
            throw "ADB serial $ExplicitSerial was not found. Devices: $(Format-AdbDeviceSummary -Devices $inventory)"
        }
        if ($selectedInventory[0].State -ne "device") {
            throw "ADB serial $ExplicitSerial is not online (state=$($selectedInventory[0].State))."
        }
        $candidates = @(Get-AdbDeviceDetails -Adb $Adb -Device $selectedInventory[0])
    }
    else {
        $online = @($inventory | Where-Object { $_.State -eq "device" })
        $candidates = @($online | ForEach-Object { Get-AdbDeviceDetails -Adb $Adb -Device $_ })
    }

    if ($Kind -eq "QuestArm64") {
        $eligible = @($candidates | Where-Object {
            $_.Abi -match '^arm64' -and
            ($_.Model -match 'Quest' -or $_.Manufacturer -match '^(Oculus|Meta)')
        })
        $description = "online ARM64 Meta Quest"
    }
    else {
        $eligible = @($candidates | Where-Object {
            $_.Abi -eq "x86_64" -and ($_.Serial -match '^emulator-' -or $_.Qemu -eq "1")
        })
        $description = "online Android x64 emulator"
    }

    if ($eligible.Count -ne 1) {
        $summary = Format-AdbDeviceSummary -Devices $candidates
        if (![string]::IsNullOrWhiteSpace($ExplicitSerial)) {
            throw "ADB serial $ExplicitSerial is not an $description. Target: $summary"
        }
        throw "Expected exactly one $description but found $($eligible.Count). Candidates: $summary. Pass an explicit serial when multiple eligible targets are connected."
    }

    return $eligible[0]
}

function Set-DisposableAndroidApplicationIdentifier {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectSettingsPath,
        [Parameter(Mandatory = $true)]
        [string] $PackageName
    )

    $lines = [System.IO.File]::ReadAllLines($ProjectSettingsPath)
    $applicationIdentifierIndex = -1
    $androidIdentifierIndex = -1
    for ($index = 0; $index -lt $lines.Length; $index++) {
        if ($lines[$index] -match '^  applicationIdentifier:\s*$') {
            $applicationIdentifierIndex = $index
            continue
        }

        if ($applicationIdentifierIndex -ge 0 -and $lines[$index] -match '^    Android:\s+') {
            $androidIdentifierIndex = $index
            break
        }

        if ($applicationIdentifierIndex -ge 0 -and $lines[$index] -match '^  \S') {
            break
        }
    }

    if ($androidIdentifierIndex -lt 0) {
        throw "Android application identifier was not found in $ProjectSettingsPath"
    }

    $lines[$androidIdentifierIndex] = "    Android: $PackageName"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllLines($ProjectSettingsPath, $lines, $utf8NoBom)
}

function Assert-SmokeLogPassed {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LogPath,
        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if (!(Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        throw "$Description did not produce a log: $LogPath"
    }

    $content = [System.IO.File]::ReadAllText($LogPath)
    if ($content.Contains("LUAU_PLAYER_SMOKE_FAIL")) {
        throw "$Description emitted LUAU_PLAYER_SMOKE_FAIL. See $LogPath"
    }
    if (!$content.Contains("LUAU_PLAYER_SMOKE_PASS")) {
        throw "$Description did not emit LUAU_PLAYER_SMOKE_PASS. See $LogPath"
    }
}

function Invoke-WindowsPlayerSmoke {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Executable,
        [Parameter(Mandatory = $true)]
        [string] $LogPath,
        [Parameter(Mandatory = $true)]
        [int] $TimeoutSeconds
    )

    if (Test-Path -LiteralPath $LogPath) {
        Remove-Item -LiteralPath $LogPath -Force
    }

    $quotedLogPath = '"' + $LogPath.Replace('"', '\"') + '"'
    Write-Host "==> Launch Windows x64 IL2CPP smoke player"
    $process = Start-Process `
        -FilePath $Executable `
        -ArgumentList @("-batchmode", "-nographics", "-logFile", $quotedLogPath) `
        -PassThru `
        -WindowStyle Hidden

    try {
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "Windows smoke player timed out after $TimeoutSeconds seconds. See $LogPath"
        }
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    Assert-SmokeLogPassed -LogPath $LogPath -Description "Windows smoke player"
    if ($exitCode -ne 0) {
        throw "Windows smoke player exited with code $exitCode despite its log marker. See $LogPath"
    }
}

function Wait-ForAndroidSmokeMarker {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Adb,
        [Parameter(Mandatory = $true)]
        [string] $Serial,
        [Parameter(Mandatory = $true)]
        [string] $LogPath,
        [Parameter(Mandatory = $true)]
        [string] $BoundaryToken,
        [Parameter(Mandatory = $true)]
        [int] $TimeoutSeconds,
        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    do {
        $logLines = @(Invoke-NativeCapture `
            -Command $Adb `
            -Arguments @(
                "-s", $Serial,
                "logcat", "-d", "-v", "threadtime",
                "LuauHost:I", "Unity:I", "AndroidRuntime:E", "*:S"
            ) `
            -Description "Read Android logcat from $Serial")
        $content = $logLines -join "`n"
        $boundaryIndex = $content.LastIndexOf($BoundaryToken, [System.StringComparison]::Ordinal)
        if ($boundaryIndex -lt 0) {
            Start-Sleep -Milliseconds 1000
            continue
        }

        $scopedContent = $content.Substring($boundaryIndex + $BoundaryToken.Length)
        [System.IO.File]::WriteAllText($LogPath, $scopedContent, $utf8NoBom)
        if ($scopedContent.Contains("LUAU_PLAYER_SMOKE_FAIL")) {
            throw "$Description emitted LUAU_PLAYER_SMOKE_FAIL. See $LogPath"
        }
        if ($scopedContent.Contains("LUAU_PLAYER_SMOKE_PASS")) {
            Assert-SmokeLogPassed -LogPath $LogPath -Description $Description
            return
        }

        Start-Sleep -Milliseconds 1000
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$Description timed out after $TimeoutSeconds seconds without LUAU_PLAYER_SMOKE_PASS. See $LogPath"
}

function Invoke-AndroidPlayerSmoke {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Adb,
        [Parameter(Mandatory = $true)]
        [object] $Device,
        [Parameter(Mandatory = $true)]
        [string] $Apk,
        [Parameter(Mandatory = $true)]
        [string] $PackageName,
        [Parameter(Mandatory = $true)]
        [string] $LogPath,
        [Parameter(Mandatory = $true)]
        [int] $TimeoutSeconds,
        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $installed = $false
    try {
        Invoke-NativeCommand `
            -Command $Adb `
            -Arguments @("-s", $Device.Serial, "install", "-r", "-t", "-d", $Apk) `
            -Description "Install $Description APK on $($Device.Serial)"
        $installed = $true

        Invoke-NativeCommand `
            -Command $Adb `
            -Arguments @("-s", $Device.Serial, "shell", "am", "force-stop", $PackageName) `
            -Description "Stop prior $Description process on $($Device.Serial)"

        $boundaryToken = "LUAU_HOST_BOUNDARY_" + [Guid]::NewGuid().ToString("N")
        Invoke-NativeCommand `
            -Command $Adb `
            -Arguments @("-s", $Device.Serial, "shell", "log", "-t", "LuauHost", $boundaryToken) `
            -Description "Mark Android log boundary on $($Device.Serial)"

        $launchOutput = @(Invoke-NativeCapture `
            -Command $Adb `
            -Arguments @(
                "-s", $Device.Serial,
                "shell", "monkey",
                "-p", $PackageName,
                "-c", "android.intent.category.LAUNCHER",
                "1"
            ) `
            -Description "Launch $Description on $($Device.Serial)")
        if (!(($launchOutput -join "`n").Contains("Events injected: 1"))) {
            throw "$Description launch did not inject a launcher event on $($Device.Serial)."
        }

        Wait-ForAndroidSmokeMarker `
            -Adb $Adb `
            -Serial $Device.Serial `
            -LogPath $LogPath `
            -BoundaryToken $boundaryToken `
            -TimeoutSeconds $TimeoutSeconds `
            -Description $Description
    }
    finally {
        if ($installed) {
            & $Adb -s $Device.Serial shell am force-stop $PackageName *> $null
            & $Adb -s $Device.Serial uninstall $PackageName *> $null
        }
    }
}

function Find-ManagedArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ArtifactsRoot,
        [Parameter(Mandatory = $true)]
        [string] $ProjectName,
        [Parameter(Mandatory = $true)]
        [string] $FileName,
        [string] $Framework
    )

    $projectBin = Join-Path (Join-Path $ArtifactsRoot "bin") $ProjectName
    if (!(Test-Path -LiteralPath $projectBin -PathType Container)) {
        throw "Managed artifact directory was not produced: $projectBin"
    }

    $matches = @(Get-ChildItem -LiteralPath $projectBin -Recurse -File -Filter $FileName)
    if (![string]::IsNullOrWhiteSpace($Framework)) {
        $matches = @($matches | Where-Object { $_.FullName.IndexOf($Framework, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })
    }

    if ($matches.Count -ne 1) {
        $found = ($matches | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
        throw "Expected exactly one $FileName under $projectBin but found $($matches.Count).$([Environment]::NewLine)$found"
    }

    return $matches[0].FullName
}

function Install-HostPlugin {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PluginSource,
        [Parameter(Mandatory = $true)]
        [string] $PluginPath
    )

    $pluginMeta = "$PluginPath.meta"
    if (!(Test-Path -LiteralPath $pluginMeta -PathType Leaf)) {
        throw "Reviewed Unity importer metadata was not found: $pluginMeta"
    }

    $sourceHash = (Get-FileHash -LiteralPath $PluginSource -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $PluginSource -Destination $PluginPath -Force
    $destinationHash = (Get-FileHash -LiteralPath $PluginPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $destinationHash) {
        throw "Installed host plugin failed SHA256 verification: $PluginPath"
    }

    Write-Host "Installed host plugin: $PluginPath (SHA256=$destinationHash)"
}

function Set-DisposablePackageReferences {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ManifestPath,
        [Parameter(Mandatory = $true)]
        [string] $LockPath,
        [Parameter(Mandatory = $true)]
        [string] $PackageName,
        [Parameter(Mandatory = $true)]
        [string] $PackageReference,
        [Parameter(Mandatory = $true)]
        [string] $LockReference
    )

    if (!(Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Disposable Unity manifest was not found: $ManifestPath"
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if (!$manifest.dependencies) {
        throw "Disposable Unity manifest has no dependencies object: $ManifestPath"
    }

    $manifestDependency = $manifest.dependencies.PSObject.Properties[$PackageName]
    if ($null -eq $manifestDependency) {
        $manifest.dependencies | Add-Member -NotePropertyName $PackageName -NotePropertyValue $PackageReference
    }
    else {
        $manifestDependency.Value = $PackageReference
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $manifestJson = $manifest | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText($ManifestPath, $manifestJson + [Environment]::NewLine, $utf8NoBom)

    if (Test-Path -LiteralPath $LockPath -PathType Leaf) {
        $lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
        $lockDependency = if ($lock.dependencies) {
            $lock.dependencies.PSObject.Properties[$PackageName]
        }
        else {
            $null
        }

        if ($null -eq $lockDependency) {
            # A lock without the package-under-test entry cannot be normalized
            # safely. Let Unity regenerate it solely from the staged manifest.
            Remove-Item -LiteralPath $LockPath -Force
        }
        else {
            $lockDependency.Value.version = $LockReference
            $lockDependency.Value.source = "embedded"
            $lockJson = $lock | ConvertTo-Json -Depth 100
            [System.IO.File]::WriteAllText($LockPath, $lockJson + [Environment]::NewLine, $utf8NoBom)
        }
    }

    $normalizedManifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $normalizedDependency = $normalizedManifest.dependencies.PSObject.Properties[$PackageName]
    if ($null -eq $normalizedDependency -or $normalizedDependency.Value -ne $PackageReference) {
        throw "Disposable Unity manifest did not normalize $PackageName to $PackageReference."
    }

    if (Test-Path -LiteralPath $LockPath -PathType Leaf) {
        $normalizedLock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
        $normalizedLockDependency = $normalizedLock.dependencies.PSObject.Properties[$PackageName]
        if ($null -eq $normalizedLockDependency -or
            $normalizedLockDependency.Value.version -ne $LockReference -or
            $normalizedLockDependency.Value.source -ne "embedded") {
            throw "Disposable Unity lock did not normalize $PackageName to the staged package."
        }
    }

    foreach ($packageStatePath in @($ManifestPath, $LockPath)) {
        if (!(Test-Path -LiteralPath $packageStatePath -PathType Leaf)) {
            continue
        }

        $packageState = [System.IO.File]::ReadAllText($packageStatePath)
        if ($packageState -match 'file:\.\.[\\/]\.\.[\\/]\.\.[\\/]Luau\.Unity') {
            throw "Checkout-relative Luau.Unity dependency survived in $packageStatePath"
        }
    }
}

function Import-DeclaredPackageSamples {
    param(
        [Parameter(Mandatory = $true)]
        [string] $StagedPackage,
        [Parameter(Mandatory = $true)]
        [string] $ProjectRoot,
        [Parameter(Mandatory = $true)]
        [string] $PackageName
    )

    $packageJsonPath = Join-Path $StagedPackage "package.json"
    if (!(Test-Path -LiteralPath $packageJsonPath -PathType Leaf)) {
        throw "Staged package metadata was not found: $packageJsonPath"
    }

    $package = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json
    if ($package.name -cne $PackageName) {
        throw "Staged package has an unexpected identity: $($package.name)"
    }
    if ([string]::IsNullOrWhiteSpace($package.version) -or
        $package.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw "Staged package has an unsafe or invalid version: $($package.version)"
    }

    $declaredSamples = @($package.samples)
    $expectedSampleNames = @("Getting Started", "Full Luau Scripting Demo")
    $expectedSamplePaths = @(
        "Samples~/Getting Started",
        "Samples~/Full Luau Scripting Demo")
    if ($declaredSamples.Count -ne $expectedSampleNames.Count) {
        throw (
            "The staged package must declare exactly the two maintained samples; " +
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

    $assetsRoot = [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot "Assets"))
    if (!(Test-Path -LiteralPath $assetsRoot -PathType Container)) {
        throw "Disposable Unity Assets directory was not found: $assetsRoot"
    }
    $sampleImportRoot = [System.IO.Path]::GetFullPath((Join-Path $assetsRoot (
        "Samples/Luau.Unity/" + $package.version)))
    Assert-StrictDescendantPath `
        -Path $sampleImportRoot `
        -Parent $assetsRoot `
        -Description "Disposable sample import root"
    if (Test-Path -LiteralPath $sampleImportRoot) {
        throw "Disposable sample import root already exists: $sampleImportRoot"
    }

    New-Item -ItemType Directory -Path $sampleImportRoot -Force | Out-Null
    foreach ($sample in $declaredSamples) {
        $sampleSource = [System.IO.Path]::GetFullPath((Join-Path $StagedPackage $sample.path))
        Assert-StrictDescendantPath `
            -Path $sampleSource `
            -Parent $StagedPackage `
            -Description "Declared package sample source"
        if (!(Test-Path -LiteralPath $sampleSource -PathType Container)) {
            throw "Declared package sample is missing: $($sample.path)"
        }

        $sampleDestination = [System.IO.Path]::GetFullPath((
            Join-Path $sampleImportRoot $sample.displayName))
        Assert-StrictDescendantPath `
            -Path $sampleDestination `
            -Parent $sampleImportRoot `
            -Description "Declared package sample destination"
        New-Item -ItemType Directory -Path $sampleDestination -Force | Out-Null
        Get-ChildItem -LiteralPath $sampleSource -Force | ForEach-Object {
            Copy-Item `
                -LiteralPath $_.FullName `
                -Destination $sampleDestination `
                -Recurse `
                -Force
        }

        Write-Host (
            "Imported declared package sample for disposable player builds: " +
            "$($sample.displayName) -> $sampleDestination")
    }
}

function Invoke-UnityBatch {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Editor,
        [Parameter(Mandatory = $true)]
        [string] $Project,
        [Parameter(Mandatory = $true)]
        [string] $LogPath,
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,
        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $common = @(
        "-batchmode",
        "-nographics",
        "-projectPath", $Project,
        "-logFile", $LogPath
    )

    # Windows PowerShell does not wait for GUI-subsystem executables invoked
    # with &, so a later Unity gate could race the still-importing project and
    # fail on its lock file. Start the editor explicitly and wait for its real
    # exit code before continuing.
    $processArguments = @(($common + $Arguments) | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + $_.Replace('"', '\"') + '"'
        }
        else {
            $_
        }
    })

    Write-Host "==> $Description"
    $process = Start-Process `
        -FilePath $Editor `
        -ArgumentList $processArguments `
        -PassThru `
        -WindowStyle Hidden
    try {
        # Waiting on the returned process handle follows the actual editor
        # lifetime. Start-Process -Wait can remain blocked on short-lived
        # Unity descendants after the batch editor has already exited.
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode. See $LogPath"
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$nativeOut = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "native/luau-host/out"))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $nativeOut "unity-host"
}
$validationRoot = Get-AbsolutePath -Path $OutputRoot -BasePath $repositoryRoot
Assert-StrictDescendantPath -Path $validationRoot -Parent $nativeOut -Description "OutputRoot"

$sourceUnityProject = Join-Path $repositoryRoot "tests/Luau.Unity.Integration"
$sourcePackage = Join-Path $repositoryRoot "Luau.Unity"
$projectRoot = Join-Path $validationRoot "project"
$dotnetArtifacts = Join-Path $validationRoot "dotnet-artifacts"
$logsRoot = Join-Path $validationRoot "logs"
$resultsRoot = Join-Path $validationRoot "results"
$buildsRoot = Join-Path $validationRoot "builds"
$luauPackageName = "com.qll.luau.unity"
$androidPackageName = "com.luauunity.host.smoke"

if (Test-Path -LiteralPath $validationRoot) {
    # validationRoot is checked above before any recursive deletion occurs.
    Remove-Item -LiteralPath $validationRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $projectRoot, $dotnetArtifacts, $logsRoot, $resultsRoot, $buildsRoot -Force | Out-Null

foreach ($folderName in @("Assets", "Packages", "ProjectSettings")) {
    $source = Join-Path $sourceUnityProject $folderName
    if (!(Test-Path -LiteralPath $source -PathType Container)) {
        throw "Unity source folder was not found: $source"
    }
    Copy-Item -LiteralPath $source -Destination $projectRoot -Recurse -Force
}

if (!(Test-Path -LiteralPath $sourcePackage -PathType Container)) {
    throw "Standalone Unity package was not found: $sourcePackage"
}
$stagedPackage = Join-Path $projectRoot "Packages/$luauPackageName"
Copy-Item -LiteralPath $sourcePackage -Destination $stagedPackage -Recurse -Force

# package.json.meta was valid only while the package was embedded under Assets.
# Ignore a locally regenerated copy if Unity has recreated it in the checkout.
$stagedPackageManifestMeta = Join-Path $stagedPackage "package.json.meta"
if (Test-Path -LiteralPath $stagedPackageManifestMeta -PathType Leaf) {
    Remove-Item -LiteralPath $stagedPackageManifestMeta -Force
}

Import-DeclaredPackageSamples `
    -StagedPackage $stagedPackage `
    -ProjectRoot $projectRoot `
    -PackageName $luauPackageName

# Do not carry generated assembly folders nested under project Assets or the
# staged package into the disposable copy.
foreach ($generatedSearchRoot in @((Join-Path $projectRoot "Assets"), $stagedPackage)) {
    $generatedAssetDirectories = @(Get-ChildItem -LiteralPath $generatedSearchRoot -Directory -Recurse |
        Where-Object { $_.Name -eq "bin" -or $_.Name -eq "obj" } |
        Sort-Object { $_.FullName.Length } -Descending)
    foreach ($directory in $generatedAssetDirectories) {
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
        $metaPath = "$($directory.FullName).meta"
        if (Test-Path -LiteralPath $metaPath) {
            Remove-Item -LiteralPath $metaPath -Force
        }
    }
}

$disposableManifest = Join-Path $projectRoot "Packages/manifest.json"
$disposableLock = Join-Path $projectRoot "Packages/packages-lock.json"
Set-DisposablePackageReferences `
    -ManifestPath $disposableManifest `
    -LockPath $disposableLock `
    -PackageName $luauPackageName `
    -PackageReference "file:$luauPackageName" `
    -LockReference "file:$luauPackageName"

Set-DisposableAndroidApplicationIdentifier `
    -ProjectSettingsPath (Join-Path $projectRoot "ProjectSettings/ProjectSettings.asset") `
    -PackageName $androidPackageName

$windowsPlugin = Resolve-FirstExistingFile `
    -ExplicitPath $WindowsPlugin `
    -Candidates @(
        "Luau.Unity/Runtime/Plugins/win-x64/luau_host.dll"
    ) `
    -BasePath $repositoryRoot `
    -Description "Windows checked-in host plugin" `
    -Required

$androidPlugin = Resolve-FirstExistingFile `
    -ExplicitPath $AndroidX64Plugin `
    -Candidates @(
        "Luau.Unity/Runtime/Plugins/android-x64/libluau_host.so"
    ) `
    -BasePath $repositoryRoot `
    -Description "Android x64 checked-in host plugin" `
    -Required

$androidArm64Plugin = Resolve-FirstExistingFile `
    -ExplicitPath $AndroidArm64Plugin `
    -Candidates @(
        "Luau.Unity/Runtime/Plugins/android-arm64/libluau_host.so"
    ) `
    -BasePath $repositoryRoot `
    -Description "Android ARM64 checked-in host plugin" `
    -Required

$luauProject = Join-Path $repositoryRoot "src/Luau/Luau.csproj"
$dotnetArguments = @(
    "build", $luauProject,
    "--configuration", $Configuration,
    "--framework", "netstandard2.1",
    "--artifacts-path", $dotnetArtifacts,
    "-p:LuauHostNativePath=$windowsPlugin"
)
Invoke-NativeCommand -Command "dotnet" -Arguments $dotnetArguments -Description "Build Luau.dll"

$managedLuau = Find-ManagedArtifact -ArtifactsRoot $dotnetArtifacts -ProjectName "Luau" -FileName "Luau.dll"
$sourceGeneratorProject = Join-Path $repositoryRoot "src/Luau.SourceGenerator/Luau.SourceGenerator.csproj"
Invoke-NativeCommand -Command "dotnet" -Arguments @(
    "build", $sourceGeneratorProject,
    "--configuration", $Configuration,
    "--artifacts-path", $dotnetArtifacts
) -Description "Build Luau source generator"
$managedGenerator = Find-ManagedArtifact -ArtifactsRoot $dotnetArtifacts -ProjectName "Luau.SourceGenerator" -FileName "Luau.SourceGenerator.dll"

$runtimeDestination = Join-Path $stagedPackage "Runtime"
if (!(Test-Path -LiteralPath $runtimeDestination -PathType Container)) {
    throw "Staged package Runtime directory was not found: $runtimeDestination"
}
Copy-Item -LiteralPath $managedLuau -Destination (Join-Path $runtimeDestination "Luau.dll") -Force
Copy-Item -LiteralPath $managedGenerator -Destination (Join-Path $runtimeDestination "Luau.SourceGenerator.dll") -Force
foreach ($managedArtifact in @(
    @{ Source = $managedLuau; Destination = (Join-Path $runtimeDestination "Luau.dll") },
    @{ Source = $managedGenerator; Destination = (Join-Path $runtimeDestination "Luau.SourceGenerator.dll") }
)) {
    $sourceHash = (Get-FileHash -LiteralPath $managedArtifact.Source -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $managedArtifact.Destination -Algorithm SHA256).Hash
    if ($sourceHash -ne $destinationHash) {
        throw "Installed managed package artifact failed SHA256 verification: $($managedArtifact.Destination)"
    }
    Write-Host "Installed managed package artifact: $($managedArtifact.Destination) (SHA256=$destinationHash)"
}

$pluginsRoot = Join-Path $stagedPackage "Runtime/Plugins"
$copiedWindowsPlugin = Join-Path $pluginsRoot "win-x64/luau_host.dll"
Install-HostPlugin `
    -PluginSource $windowsPlugin `
    -PluginPath $copiedWindowsPlugin

if ($androidPlugin) {
    $copiedAndroidPlugin = Join-Path $pluginsRoot "android-x64/libluau_host.so"
    Install-HostPlugin `
        -PluginSource $androidPlugin `
        -PluginPath $copiedAndroidPlugin
}

if ($androidArm64Plugin) {
    $copiedAndroidArm64Plugin = Join-Path $pluginsRoot "android-arm64/libluau_host.so"
    Install-HostPlugin `
        -PluginSource $androidArm64Plugin `
        -PluginPath $copiedAndroidArm64Plugin
}

$runUnity = $Compile -or $EditModeTests -or $WindowsSmoke -or $AndroidArm64Smoke -or $AndroidX64Smoke
$resolvedUnity = Resolve-UnityEditor `
    -ExplicitPath $UnityPath `
    -RepositoryRoot $repositoryRoot `
    -Required:$runUnity

$resolvedAdb = $null
$androidArm64Device = $null
$androidX64Device = $null
if ($AndroidArm64Smoke -or $AndroidX64Smoke) {
    $resolvedAdb = Resolve-AdbExecutable `
        -ExplicitPath $AdbPath `
        -UnityEditor $resolvedUnity `
        -RepositoryRoot $repositoryRoot
    if ($AndroidArm64Smoke) {
        $androidArm64Device = Select-AdbSmokeTarget `
            -Adb $resolvedAdb `
            -ExplicitSerial $AndroidArm64Serial `
            -Kind "QuestArm64"
    }
    if ($AndroidX64Smoke) {
        $androidX64Device = Select-AdbSmokeTarget `
            -Adb $resolvedAdb `
            -ExplicitSerial $AndroidX64Serial `
            -Kind "EmulatorX64"
    }
}

Write-Host "Disposable Unity host project prepared at $projectRoot"
Write-Host "Source Unity project preserved at $sourceUnityProject"
Write-Host "Source Unity package preserved at $sourcePackage"
if ($resolvedUnity) {
    Write-Host "Unity editor: $resolvedUnity"
}
if ($resolvedAdb) {
    Write-Host "ADB executable: $resolvedAdb"
}
if ($androidArm64Device) {
    Write-Host "Android ARM64 Quest: $($androidArm64Device.Serial) model=$($androidArm64Device.Model) abi=$($androidArm64Device.Abi)"
}
if ($androidX64Device) {
    Write-Host "Android x64 emulator: $($androidX64Device.Serial) model=$($androidX64Device.Model) abi=$($androidX64Device.Abi)"
}

if ($Compile) {
    $compileLog = Join-Path $logsRoot "compile.log"
    Invoke-UnityBatch `
        -Editor $resolvedUnity `
        -Project $projectRoot `
        -LogPath $compileLog `
        -Arguments @("-quit") `
        -Description "Compile disposable Unity host project"

    $compilerErrors = @(Select-String -LiteralPath $compileLog -Pattern 'error CS[0-9]{4}:|Scripts have compiler errors|Compilation failed' -ErrorAction SilentlyContinue)
    if ($compilerErrors.Count -gt 0) {
        throw "Unity reported compiler errors. See $compileLog"
    }
}

if ($EditModeTests) {
    $editModeLog = Join-Path $logsRoot "editmode-tests.log"
    $editModeResults = Join-Path $resultsRoot "editmode-tests.xml"
    Invoke-UnityBatch `
        -Editor $resolvedUnity `
        -Project $projectRoot `
        -LogPath $editModeLog `
        -Arguments @("-runTests", "-testPlatform", "EditMode", "-testResults", $editModeResults) `
        -Description "Run disposable Unity EditMode tests"

    if (!(Test-Path -LiteralPath $editModeResults -PathType Leaf)) {
        throw "Unity did not produce EditMode results: $editModeResults"
    }
    [xml] $testDocument = Get-Content -LiteralPath $editModeResults -Raw
    $testRun = $testDocument.SelectSingleNode("/test-run")
    if (!$testRun -or $testRun.result -ne "Passed" -or [int] $testRun.failed -ne 0) {
        throw "Unity EditMode tests did not pass. See $editModeResults and $editModeLog"
    }
}

if ($WindowsSmoke) {
    $windowsOutput = Join-Path $buildsRoot "windows-x64/LuauSmoke.exe"
    New-Item -ItemType Directory -Path (Split-Path -Parent $windowsOutput) -Force | Out-Null
    Invoke-UnityBatch `
        -Editor $resolvedUnity `
        -Project $projectRoot `
        -LogPath (Join-Path $logsRoot "windows-x64-smoke-build.log") `
        -Arguments @(
            "-buildTarget", "Win64",
            "-executeMethod", "Luau.Unity.Editor.LuauPlayerSmokeBuild.BuildWindows64Il2Cpp",
            "-luauSmokeOutput", $windowsOutput,
            "-quit"
        ) `
        -Description "Build disposable Windows x64 IL2CPP smoke player"
    if (!(Test-Path -LiteralPath $windowsOutput -PathType Leaf)) {
        throw "Windows smoke player was not produced: $windowsOutput"
    }
    Invoke-WindowsPlayerSmoke `
        -Executable $windowsOutput `
        -LogPath (Join-Path $logsRoot "windows-x64-player.log") `
        -TimeoutSeconds $SmokeTimeoutSeconds
}

if ($AndroidArm64Smoke) {
    $androidArm64Output = Join-Path $buildsRoot "android-arm64/LuauSmoke.apk"
    New-Item -ItemType Directory -Path (Split-Path -Parent $androidArm64Output) -Force | Out-Null
    Invoke-UnityBatch `
        -Editor $resolvedUnity `
        -Project $projectRoot `
        -LogPath (Join-Path $logsRoot "android-arm64-smoke-build.log") `
        -Arguments @(
            "-buildTarget", "Android",
            "-executeMethod", "Luau.Unity.Editor.LuauPlayerSmokeBuild.BuildAndroidArm64Il2Cpp",
            "-luauSmokeOutput", $androidArm64Output,
            "-quit"
        ) `
        -Description "Build disposable Android ARM64 IL2CPP smoke APK"
    if (!(Test-Path -LiteralPath $androidArm64Output -PathType Leaf)) {
        throw "Android ARM64 smoke APK was not produced: $androidArm64Output"
    }
    Invoke-AndroidPlayerSmoke `
        -Adb $resolvedAdb `
        -Device $androidArm64Device `
        -Apk $androidArm64Output `
        -PackageName $androidPackageName `
        -LogPath (Join-Path $logsRoot "android-arm64-player.log") `
        -TimeoutSeconds $SmokeTimeoutSeconds `
        -Description "Android ARM64 Quest smoke player"
}

if ($AndroidX64Smoke) {
    $androidOutput = Join-Path $buildsRoot "android-x64/LuauSmoke.apk"
    New-Item -ItemType Directory -Path (Split-Path -Parent $androidOutput) -Force | Out-Null
    Invoke-UnityBatch `
        -Editor $resolvedUnity `
        -Project $projectRoot `
        -LogPath (Join-Path $logsRoot "android-x64-smoke-build.log") `
        -Arguments @(
            "-buildTarget", "Android",
            "-executeMethod", "Luau.Unity.Editor.LuauPlayerSmokeBuild.BuildAndroidX64Il2Cpp",
            "-luauSmokeOutput", $androidOutput,
            "-quit"
        ) `
        -Description "Build disposable Android x64 IL2CPP smoke APK"
    if (!(Test-Path -LiteralPath $androidOutput -PathType Leaf)) {
        throw "Android x64 smoke APK was not produced: $androidOutput"
    }
    Invoke-AndroidPlayerSmoke `
        -Adb $resolvedAdb `
        -Device $androidX64Device `
        -Apk $androidOutput `
        -PackageName $androidPackageName `
        -LogPath (Join-Path $logsRoot "android-x64-player.log") `
        -TimeoutSeconds $SmokeTimeoutSeconds `
        -Description "Android x64 emulator smoke player"
}

Write-Host "Unity host validation completed."
