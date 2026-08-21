[CmdletBinding()]
param(
    [string]$PublishDirectory = "csharp/src/BiliSubStudio.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/publish",
    [string]$OutputDirectory = "csharp/artifacts/windows-x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Resolve-FromRoot {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $Path))
}

function Assert-ChecksumFile {
    param(
        [Parameter(Mandatory = $true)][string]$BaseDirectory,
        [Parameter(Mandatory = $true)][string]$ChecksumFile
    )

    foreach ($line in Get-Content $ChecksumFile) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "invalid checksum entry in $ChecksumFile`: $line" }
        $expected = $Matches[1]
        $relative = $Matches[2]
        $target = Join-Path $BaseDirectory $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path $target -PathType Leaf)) { throw "checksum target missing: $relative" }
        if ((Get-Sha256 $target) -ne $expected) { throw "checksum mismatch: $relative" }
    }
}

if ([System.Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "Windows candidate packaging must run after the Windows WinUI publish gate"
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root
$publishFull = Resolve-FromRoot $root $PublishDirectory
$outputFull = Resolve-FromRoot $root $OutputDirectory
$artifactsRoot = Resolve-FromRoot $root "csharp/artifacts"
$requiredPrefix = $artifactsRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputFull.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be a child of csharp/artifacts: $outputFull"
}

if (-not (Test-Path $publishFull -PathType Container)) { throw "publish directory not found: $publishFull" }
$buildIdentityPath = Join-Path $publishFull "BUILD_IDENTITY.json"
$publishSumsPath = Join-Path $publishFull "SHA256SUMS.txt"
if (-not (Test-Path $buildIdentityPath -PathType Leaf)) { throw "publish is missing BUILD_IDENTITY.json" }
if (-not (Test-Path $publishSumsPath -PathType Leaf)) { throw "publish is missing SHA256SUMS.txt" }

$identity = Get-Content $buildIdentityPath -Raw | ConvertFrom-Json
if ($identity.checkpoint -ne "CSharp-P5-WindowsBuildCandidate") { throw "unexpected build checkpoint: $($identity.checkpoint)" }
if ($identity.release_candidate -ne $false -or $identity.promotion_allowed -ne $false) {
    throw "build identity must remain non-promotable until field QA is complete"
}
Assert-ChecksumFile $publishFull $publishSumsPath

# This directory contains generated evidence only and is safe to recreate for the exact build.
if (Test-Path $outputFull) { Remove-Item $outputFull -Recurse -Force }
New-Item $outputFull -ItemType Directory | Out-Null
$stagingRoot = Join-Path $outputFull "staging"
$candidateFolderName = "BiliSubStudio-CSharp-P5-Windows-x64"
$candidateFolder = Join-Path $stagingRoot $candidateFolderName
New-Item $candidateFolder -ItemType Directory -Force | Out-Null
Get-ChildItem $publishFull -Force | Copy-Item -Destination $candidateFolder -Recurse -Force

$candidateArchiveName = "BiliSubStudio_v4.0.0-beta.12-csharp-p5-Windows-x64-BUILD-CANDIDATE.zip"
$candidateArchive = Join-Path $outputFull $candidateArchiveName
Compress-Archive -Path $candidateFolder -DestinationPath $candidateArchive -CompressionLevel Optimal
$candidateArchiveHash = Get-Sha256 $candidateArchive

# Extract and re-check the exact bytes that will be uploaded, not only the pre-ZIP directory.
$readbackRoot = Join-Path $outputFull "readback"
Expand-Archive $candidateArchive -DestinationPath $readbackRoot
$readbackCandidate = Join-Path $readbackRoot $candidateFolderName
Assert-ChecksumFile $readbackCandidate (Join-Path $readbackCandidate "SHA256SUMS.txt")

$sourceInventoryPath = Join-Path $publishFull "SOURCE_SHA256SUMS.txt"
if (-not (Test-Path $sourceInventoryPath -PathType Leaf)) { throw "publish is missing SOURCE_SHA256SUMS.txt" }
Assert-ChecksumFile $root $sourceInventoryPath

# Recreate the exact pre-build source tree from its reviewed inventory. This preserves
# the bytes actually compiled even if Git checkout line-ending conversion is enabled.
$sourceFolderName = "BiliSubStudio-CSharp-P5-source"
$sourceFolder = Join-Path $stagingRoot $sourceFolderName
New-Item $sourceFolder -ItemType Directory -Force | Out-Null
foreach ($line in Get-Content $sourceInventoryPath) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "invalid source inventory entry: $line" }
    $relative = $Matches[2]
    $sourcePath = Join-Path $root $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $destination = Join-Path $sourceFolder $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $destinationParent = Split-Path -Parent $destination
    New-Item $destinationParent -ItemType Directory -Force | Out-Null
    Copy-Item $sourcePath $destination
}
$sourceTag = if ([string]$identity.source_revision -ne "unversioned-archive-checkpoint") {
    ([string]$identity.source_revision).Substring(0, 12)
}
else {
    ([string]$identity.source_tree_sha256).Substring(0, 12)
}
$sourceArchiveName = "BiliSubStudio_v4.0.0-beta.12-csharp-p5-source-$sourceTag.zip"
$sourceArchive = Join-Path $outputFull $sourceArchiveName
Compress-Archive -Path $sourceFolder -DestinationPath $sourceArchive -CompressionLevel Optimal
$sourceArchiveHash = Get-Sha256 $sourceArchive
$sourceReadbackRoot = Join-Path $readbackRoot "source"
Expand-Archive $sourceArchive -DestinationPath $sourceReadbackRoot
Assert-ChecksumFile (Join-Path $sourceReadbackRoot $sourceFolderName) $sourceInventoryPath

Remove-Item $stagingRoot -Recurse -Force
Remove-Item $readbackRoot -Recurse -Force

& "$PSScriptRoot/build_windows_installer.ps1" -PublishDirectory $publishFull -OutputDirectory $outputFull
$installerName = "BiliSubStudio_Setup_v4.0.0-beta.12-csharp-p5_x64.exe"
$installerPath = Join-Path $outputFull $installerName
$installerStatusName = "INSTALLER_GATE_STATUS.json"
$installerStatusPath = Join-Path $outputFull $installerStatusName
if (-not (Test-Path $installerPath -PathType Leaf) -or -not (Test-Path $installerStatusPath -PathType Leaf)) {
    throw "one-file installer evidence is missing"
}
$installerHash = Get-Sha256 $installerPath

$identityEvidenceName = "BUILD_IDENTITY.json"
$publishSumsEvidenceName = "PUBLISH_SHA256SUMS.txt"
$sourceSumsEvidenceName = "SOURCE_SHA256SUMS.txt"
Copy-Item $buildIdentityPath (Join-Path $outputFull $identityEvidenceName)
Copy-Item $publishSumsPath (Join-Path $outputFull $publishSumsEvidenceName)
Copy-Item (Join-Path $publishFull "SOURCE_SHA256SUMS.txt") (Join-Path $outputFull $sourceSumsEvidenceName)

$gateStatusName = "CANDIDATE_GATE_STATUS.json"
$gateStatusPath = Join-Path $outputFull $gateStatusName
$gateStatus = [ordered]@{
    schema = 1
    created_utc = [DateTime]::UtcNow.ToString("o")
    status = "windows_build_verified_field_qa_pending"
    release_candidate = $false
    promotion_allowed = $false
    field_qa_complete = $false
    pending_gate = "docs/migration/WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md"
    source_revision = $identity.source_revision
    source_tree_sha256 = $identity.source_tree_sha256
    source_archive = $sourceArchiveName
    source_archive_sha256 = $sourceArchiveHash
    executable = "BiliSubStudio.exe"
    executable_sha256 = $identity.exe_sha256
    candidate_archive = $candidateArchiveName
    candidate_archive_sha256 = $candidateArchiveHash
    primary_user_artifact = $installerName
    installer_sha256 = $installerHash
    installer_status = "installer_built_field_qa_pending"
}
$gateStatus | ConvertTo-Json -Depth 4 | Set-Content $gateStatusPath -Encoding UTF8

$candidateSumsName = "CANDIDATE_SHA256SUMS.txt"
$candidateSumsPath = Join-Path $outputFull $candidateSumsName
$artifactNames = @(
    $candidateArchiveName,
    $gateStatusName,
    $identityEvidenceName,
    $publishSumsEvidenceName,
    $sourceSumsEvidenceName,
    $installerName,
    $installerStatusName
)
if ($sourceArchiveName) { $artifactNames += $sourceArchiveName }
$artifactNames = $artifactNames | Sort-Object
$artifactSums = foreach ($name in $artifactNames) {
    "$(Get-Sha256 (Join-Path $outputFull $name))  $name"
}
$artifactSums | Set-Content $candidateSumsPath -Encoding UTF8
Assert-ChecksumFile $outputFull $candidateSumsPath

if ($env:GITHUB_OUTPUT) {
    "artifact_directory=$outputFull" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8
    "candidate_archive_sha256=$candidateArchiveHash" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8
}

Write-Host "PASS: candidate ZIP readback, exact source archive and top-level checksums"
Write-Host "Candidate archive SHA-256: $candidateArchiveHash"
Write-Host "Status remains field-QA pending; promotion is forbidden"
