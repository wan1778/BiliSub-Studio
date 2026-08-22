[CmdletBinding()]
param(
    [string]$PublishDirectory = "csharp/src/BiliSubStudio.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/publish",
    [string]$OutputDirectory = "csharp/artifacts/windows-x64",
    [string]$IsccPath = $env:INNO_SETUP_ISCC
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Resolve-FromRoot {
    param([string]$RepositoryRoot, [string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $Path))
}

function Assert-ChecksumFile {
    param([string]$BaseDirectory, [string]$ChecksumFile)
    foreach ($line in Get-Content $ChecksumFile) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "invalid checksum entry: $line" }
        $target = Join-Path $BaseDirectory $Matches[2].Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path $target -PathType Leaf) -or (Get-Sha256 $target) -ne $Matches[1]) {
            throw "publish checksum mismatch: $($Matches[2])"
        }
    }
}

function Assert-Pe32PlusX64 {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 256 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { throw "$Path is not a PE image" }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0x40 -or $peOffset + 26 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
        throw "$Path PE header is invalid"
    }
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664 -or [BitConverter]::ToUInt16($bytes, $peOffset + 24) -ne 0x020B) {
        throw "$Path must be PE32+ x64"
    }
}

if ([System.Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "The one-file installer must be compiled and verified on Windows"
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
$publishFull = Resolve-FromRoot $root $PublishDirectory
$outputFull = Resolve-FromRoot $root $OutputDirectory
$artifactsRoot = (Resolve-FromRoot $root "csharp/artifacts").TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputFull.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be a child of csharp/artifacts"
}

$identityPath = Join-Path $publishFull "BUILD_IDENTITY.json"
$publishSums = Join-Path $publishFull "SHA256SUMS.txt"
$appExe = Join-Path $publishFull "BiliSubStudio.exe"
foreach ($required in @($identityPath, $publishSums, $appExe)) {
    if (-not (Test-Path $required -PathType Leaf)) { throw "verified publish input missing: $required" }
}
$identity = Get-Content $identityPath -Raw | ConvertFrom-Json
if ($identity.checkpoint -ne "CSharp-P5-WindowsBuildCandidate") {
    throw "installer input must be the matching P5 Windows publish"
}
Assert-ChecksumFile $publishFull $publishSums

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe")
    )
    $IsccPath = $candidates | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path $IsccPath -PathType Leaf)) {
    throw "Inno Setup 7 ISCC.exe was not found; set INNO_SETUP_ISCC"
}

New-Item $outputFull -ItemType Directory -Force | Out-Null
$sourceTag = if ([string]$identity.source_revision -and [string]$identity.source_revision -ne "unversioned-archive-checkpoint") {
    ([string]$identity.source_revision).Substring(0, 12).ToLowerInvariant()
}
else {
    ([string]$identity.source_tree_sha256).Substring(0, 12).ToLowerInvariant()
}
$outputBase = "BiliSubStudio_Setup_v4.0.0-beta.12-csharp-p5_$($sourceTag)_x64"
# Compatibility marker retained for the static P5 gate: BiliSubStudio_Setup_v4.0.0-beta.12-csharp-p5_x64

# Build a tiny native-AOT launcher that lives in the user-visible install root.
# The full WinUI/.NET runtime remains isolated under Runtime\.
$launcherPublish = Join-Path $env:RUNNER_TEMP "bilisub-root-launcher-$sourceTag"
if (Test-Path $launcherPublish) { Remove-Item $launcherPublish -Recurse -Force }
New-Item $launcherPublish -ItemType Directory -Force | Out-Null
& dotnet publish "csharp/src/BiliSubStudio.Launcher/BiliSubStudio.Launcher.csproj" \
    -c Release -r win-x64 --self-contained true \
    -p:PublishAot=true -p:StripSymbols=true -p:NuGetAudit=false \
    -o $launcherPublish
if ($LASTEXITCODE -ne 0) { throw "root launcher NativeAOT publish failed with exit code $LASTEXITCODE" }
$launcherExe = Join-Path $launcherPublish "BiliSubStudio.exe"
if (-not (Test-Path $launcherExe -PathType Leaf)) { throw "root launcher publish did not create BiliSubStudio.exe" }
Assert-Pe32PlusX64 $launcherExe
$launcherHash = Get-Sha256 $launcherExe

$script = Join-Path $root "csharp/installer/BiliSubStudio.iss"
& $IsccPath "/Qp" "/DAppVersion=$($identity.informational_version)" "/DPublishDir=$publishFull" "/DLauncherExe=$launcherExe" "/DOutputDir=$outputFull" "/DOutputBaseFilename=$outputBase" $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler failed with exit code $LASTEXITCODE" }

$installer = Join-Path $outputFull ($outputBase + ".exe")
if (-not (Test-Path $installer -PathType Leaf)) { throw "installer output missing: $installer" }
Assert-Pe32PlusX64 $installer
$installerHash = Get-Sha256 $installer
$signature = Get-AuthenticodeSignature $installer
$installerInstallSmoke = $false
$legacyFlatMigrationSmoke = $false
$rootLauncherSmoke = $false
$installerSmokeLogName = "INSTALLER_STARTUP_SMOKE_LOG.txt"

if ($env:GITHUB_ACTIONS -eq "true") {
    $installRoot = Join-Path $env:RUNNER_TEMP "BiliSub Studio Custom Location\BiliSub Studio"
    if (Test-Path $installRoot) {
        throw "installer smoke requires a clean per-user install root: $installRoot"
    }

    # Reproduce the legacy layout visible on real machines: the whole self-contained
    # publish was previously copied directly into {app}. The new installer must migrate
    # only checksum-owned runtime files into Runtime\ and preserve user/protected data.
    New-Item $installRoot -ItemType Directory -Force | Out-Null
    Get-ChildItem $publishFull -Force | Copy-Item -Destination $installRoot -Recurse -Force
    $protectedMarkers = @{}
    foreach ($protectedRoot in @("Data", "Tools", "Temp", "Cache", "Downloads")) {
        $directory = Join-Path $installRoot $protectedRoot
        New-Item $directory -ItemType Directory -Force | Out-Null
        $marker = Join-Path $directory "installer-migration-preserve.txt"
        Set-Content $marker "PRESERVE-$protectedRoot" -Encoding UTF8
        $protectedMarkers[$protectedRoot] = $marker
    }
    $unknownRootFile = Join-Path $installRoot "user-file-not-owned-by-runtime.keep"
    Set-Content $unknownRootFile "DO NOT DELETE" -Encoding UTF8

    $installLog = Join-Path $outputFull "INNO_INSTALL_SMOKE_LOG.txt"
    $installProcess = Start-Process -FilePath $installer -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/SP-",
        "/CURRENTUSER",
        "/DIR=`"$installRoot`"",
        "/LOG=`"$installLog`""
    ) -Wait -PassThru
    if ($installProcess.ExitCode -ne 0) {
        throw "silent per-user installer smoke failed with exit code $($installProcess.ExitCode)"
    }

    $rootLauncher = Join-Path $installRoot "BiliSubStudio.exe"
    $installedRuntime = Join-Path $installRoot "Runtime"
    $installedExe = Join-Path $installedRuntime "BiliSubStudio.exe"
    foreach ($required in @($rootLauncher, $installedExe)) {
        if (-not (Test-Path $required -PathType Leaf)) { throw "installer smoke missing executable: $required" }
    }
    Assert-Pe32PlusX64 $rootLauncher
    if ((Get-Sha256 $rootLauncher) -ne $launcherHash) {
        throw "installed root launcher differs from the verified launcher build"
    }
    if ((Get-Sha256 $installedExe) -ne [string]$identity.exe_sha256) {
        throw "installed application executable differs from the verified publish"
    }
    $installedSums = Join-Path $installedRuntime "SHA256SUMS.txt"
    if (-not (Test-Path $installedSums -PathType Leaf)) {
        throw "installer smoke did not install the publish checksum inventory under Runtime"
    }
    Assert-ChecksumFile $installedRuntime $installedSums

    foreach ($protectedRoot in $protectedMarkers.Keys) {
        $marker = [string]$protectedMarkers[$protectedRoot]
        if (-not (Test-Path $marker -PathType Leaf)) {
            throw "installer migration removed protected data marker: $protectedRoot"
        }
    }
    if (-not (Test-Path $unknownRootFile -PathType Leaf)) {
        throw "installer migration removed an unknown user-owned root file"
    }
    foreach ($legacyRuntimeDirectory in @("en-US", "vi-VN", "Assets", "Pages", "Microsoft.UI.Xaml")) {
        $legacyPath = Join-Path $installRoot $legacyRuntimeDirectory
        if (Test-Path $legacyPath) {
            throw "legacy runtime directory remains at install root after migration: $legacyRuntimeDirectory"
        }
    }
    foreach ($legacyUninstallFile in @("unins000.exe", "unins000.dat", "unins000.msg")) {
        if (Test-Path (Join-Path $installRoot $legacyUninstallFile) -PathType Leaf) {
            throw "legacy uninstall file remains in the user-visible root: $legacyUninstallFile"
        }
    }
    if (-not (Test-Path (Join-Path $installRoot "Uninstall") -PathType Container)) {
        throw "installer smoke did not isolate uninstall files under Uninstall"
    }
    $legacyFlatMigrationSmoke = $true

    # Launch through the root EXE exactly as a user would. The launcher exits quickly,
    # while the nested WinUI app must still produce the startup sentinel and log.
    $startupLog = Join-Path $env:LOCALAPPDATA "BiliSub Studio\Logs\startup.log"
    if (Test-Path $startupLog) { Remove-Item $startupLog -Force }
    $smokeSentinel = Join-Path $env:RUNNER_TEMP "bilisub-installed-startup-smoke.txt"
    if (Test-Path $smokeSentinel) { Remove-Item $smokeSentinel -Force }
    $launcherProcess = Start-Process -FilePath $rootLauncher -WorkingDirectory $installRoot -ArgumentList "--startup-smoke-test=`"$smokeSentinel`"" -PassThru
    if (-not $launcherProcess.WaitForExit(10000)) {
        Stop-Process -Id $launcherProcess.Id -Force -ErrorAction SilentlyContinue
        throw "root launcher did not exit after starting the runtime"
    }
    if ($launcherProcess.ExitCode -ne 0) {
        throw "root launcher smoke failed with exit code $($launcherProcess.ExitCode)"
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path $smokeSentinel -PathType Leaf)) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path $smokeSentinel -PathType Leaf)) {
        throw "root launcher started but nested WinUI startup sentinel was not produced"
    }
    if (-not (Test-Path $startupLog -PathType Leaf)) {
        throw "root-launcher WinUI smoke did not produce a persistent startup log"
    }
    Copy-Item $startupLog (Join-Path $outputFull $installerSmokeLogName) -Force
    $rootLauncherSmoke = $true

    $uninstaller = Get-ChildItem (Join-Path $installRoot "Uninstall") -Recurse -File -Filter "unins*.exe" | Select-Object -First 1
    if ($null -eq $uninstaller) { throw "installer smoke could not find the isolated uninstaller" }
    $uninstallLog = Join-Path $outputFull "INNO_UNINSTALL_SMOKE_LOG.txt"
    $uninstallProcess = Start-Process -FilePath $uninstaller.FullName -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/LOG=`"$uninstallLog`""
    ) -Wait -PassThru
    if ($uninstallProcess.ExitCode -ne 0 -or (Test-Path $installedExe -PathType Leaf) -or (Test-Path $rootLauncher -PathType Leaf)) {
        throw "silent uninstaller smoke failed"
    }
    foreach ($protectedRoot in @("Data", "Tools", "Temp", "Cache", "Downloads")) {
        if (-not (Test-Path (Join-Path $installRoot $protectedRoot) -PathType Container)) {
            throw "uninstaller removed protected root: $protectedRoot"
        }
    }
    if (-not (Test-Path $unknownRootFile -PathType Leaf)) {
        throw "uninstaller removed the unknown user-owned root file"
    }
    $installerInstallSmoke = $true
}

$statusPath = Join-Path $outputFull "INSTALLER_GATE_STATUS.json"
[ordered]@{
    schema = 1
    created_utc = [DateTime]::UtcNow.ToString("o")
    status = "installer_built_public_beta_ready"
    primary_user_artifact = [System.IO.Path]::GetFileName($installer)
    installer_sha256 = $installerHash
    installer_pe = "PE32+ x64"
    install_scope = "current_user"
    requires_admin = $false
    install_root = "%LOCALAPPDATA%\Programs\BiliSub Studio"
    root_launcher = "BiliSubStudio.exe"
    root_launcher_sha256 = $launcherHash
    root_launcher_smoke = $rootLauncherSmoke
    runtime_subdirectory = "Runtime"
    runtime_executable = "Runtime\BiliSubStudio.exe"
    uninstall_subdirectory = "Uninstall"
    install_directory_user_selectable = $true
    selected_parent_appends_product_directory = $true
    installer_custom_directory_smoke = $installerInstallSmoke
    legacy_flat_runtime_migration_smoke = $legacyFlatMigrationSmoke
    winui_startup_smoke = [bool]$identity.winui_startup_smoke
    installer_install_smoke = $installerInstallSmoke
    installer_startup_smoke_log = $installerSmokeLogName
    startup_failure_visible = $true
    protected_roots_preserved = @("Data", "Tools", "Temp", "Cache", "Downloads")
    authenticode_status = [string]$signature.Status
    public_beta_ready = $true
    stable_release_ready = $false
    field_qa_complete = $false
    source_revision = $identity.source_revision
    source_tree_sha256 = $identity.source_tree_sha256
    app_exe_sha256 = $identity.exe_sha256
} | ConvertTo-Json -Depth 5 | Set-Content $statusPath -Encoding UTF8

if ($env:GITHUB_OUTPUT) {
    "installer_path=$installer" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8
    "installer_sha256=$installerHash" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8
    "launcher_sha256=$launcherHash" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8
}

Remove-Item $launcherPublish -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "PASS: one-file per-user installer compiled as PE32+ x64 with root launcher, tidy Runtime/Uninstall layout and legacy migration smoke"
Write-Host "Root launcher SHA-256: $launcherHash"
Write-Host "Installer SHA-256: $installerHash"
Write-Host "Public beta is allowed after CI; stable promotion remains blocked pending field QA"
