[CmdletBinding()]
param(
    [string]$PublishDirectory = "csharp/src/BiliSubStudio.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish",
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
    if ($bytes.Length -lt 256 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { throw "installer is not a PE image" }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0x40 -or $peOffset + 26 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
        throw "installer PE header is invalid"
    }
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664 -or [BitConverter]::ToUInt16($bytes, $peOffset + 24) -ne 0x020B) {
        throw "installer must be PE32+ x64"
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
if ($identity.checkpoint -ne "CSharp-P5-WindowsBuildCandidate" -or $identity.release_candidate -ne $false -or $identity.promotion_allowed -ne $false) {
    throw "installer input must be the matching non-promotable P5 Windows publish"
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
$outputBase = "BiliSubStudio_Setup_v4.0.0-beta.12-csharp-p5_x64"
$script = Join-Path $root "csharp/installer/BiliSubStudio.iss"
& $IsccPath "/Qp" "/DAppVersion=$($identity.informational_version)" "/DPublishDir=$publishFull" "/DOutputDir=$outputFull" "/DOutputBaseFilename=$outputBase" $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler failed with exit code $LASTEXITCODE" }

$installer = Join-Path $outputFull ($outputBase + ".exe")
if (-not (Test-Path $installer -PathType Leaf)) { throw "installer output missing: $installer" }
Assert-Pe32PlusX64 $installer
$installerHash = Get-Sha256 $installer
$signature = Get-AuthenticodeSignature $installer

$statusPath = Join-Path $outputFull "INSTALLER_GATE_STATUS.json"
[ordered]@{
    schema = 1
    created_utc = [DateTime]::UtcNow.ToString("o")
    status = "installer_built_field_qa_pending"
    primary_user_artifact = [System.IO.Path]::GetFileName($installer)
    installer_sha256 = $installerHash
    installer_pe = "PE32+ x64"
    install_scope = "current_user"
    requires_admin = $false
    install_root = "%LOCALAPPDATA%\Programs\BiliSub Studio"
    protected_roots_preserved = @("Data", "Tools", "Temp", "Cache", "Downloads")
    authenticode_status = [string]$signature.Status
    release_candidate = $false
    promotion_allowed = $false
    field_qa_complete = $false
    pending_gate = "docs/migration/WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md"
    source_revision = $identity.source_revision
    source_tree_sha256 = $identity.source_tree_sha256
    app_exe_sha256 = $identity.exe_sha256
} | ConvertTo-Json -Depth 5 | Set-Content $statusPath -Encoding UTF8

if ($env:GITHUB_OUTPUT) {
    "installer_path=$installer" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8
    "installer_sha256=$installerHash" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8
}

Write-Host "PASS: one-file per-user installer compiled as PE32+ x64"
Write-Host "Installer SHA-256: $installerHash"
Write-Host "Status remains field-QA pending; promotion is forbidden"
