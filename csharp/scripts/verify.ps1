[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SourceIdentity {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $excludedSegments = @(".git", ".vs", "bin", "obj", "artifacts", "TestResults")
    $rootFull = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $paths = Get-ChildItem $rootFull -Recurse -File -Force |
        ForEach-Object {
            $relative = $_.FullName.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')
            $segments = $relative.Split('/')
            if (-not ($segments | Where-Object { $excludedSegments -contains $_ })) {
                $relative
            }
        }

    $pathArray = [string[]]$paths
    [Array]::Sort($pathArray, [StringComparer]::Ordinal)
    $inventory = foreach ($relative in $pathArray) {
        $absolute = Join-Path $rootFull $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        "$(Get-Sha256 $absolute)  $relative"
    }
    $canonical = ($inventory -join "`n") + "`n"
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $hasher.ComputeHash($utf8.GetBytes($canonical))
    }
    finally {
        $hasher.Dispose()
    }

    return [pscustomobject]@{
        Hash = ([BitConverter]::ToString($digest)).Replace("-", "").ToLowerInvariant()
        Inventory = [string[]]$inventory
        FileCount = $pathArray.Length
    }
}

function Assert-Pe32PlusX64 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 256 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "$Path is not a PE image"
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0x40 -or $peOffset + 26 -gt $bytes.Length) {
        throw "$Path has an invalid PE header offset"
    }
    if ([BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
        throw "$Path is missing the PE signature"
    }
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) {
        throw "$Path is not x86-64"
    }
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 24) -ne 0x020B) {
        throw "$Path does not use the PE32+ optional header"
    }
}

if ([System.Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "The real WinUI XAML/XBF/PRI verification gate must run on Windows"
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = "1"
$env:NUGET_XMLDOC_MODE = "skip"

$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw "dotnet --version failed with exit code $LASTEXITCODE" }
if ($sdk -ne "10.0.400") { throw "global.json requires exact .NET SDK 10.0.400; got $sdk" }

$propsPath = Join-Path $root "csharp/Directory.Build.props"
[xml]$props = Get-Content $propsPath -Raw
$informationalVersion = [string]$props.Project.PropertyGroup.InformationalVersion
$informationalVersion = $informationalVersion.Trim()
if ([string]::IsNullOrWhiteSpace($informationalVersion) -or $informationalVersion -notmatch '^4\.0\.0-beta\.\d+-csharp-p5$') {
    throw "Directory.Build.props has an unexpected public-beta InformationalVersion: $informationalVersion"
}

# Compute this before restore/build so generated obj/bin files can never affect source identity.
$sourceRevision = "unversioned-archive-checkpoint"
if (Test-Path (Join-Path $root ".git") -PathType Container) {
    $headRevision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw "git rev-parse HEAD failed with exit code $LASTEXITCODE" }
    if ($env:GITHUB_SHA -and $headRevision -ne $env:GITHUB_SHA) {
        throw "checked-out Git revision $headRevision differs from GITHUB_SHA $($env:GITHUB_SHA)"
    }
    $dirty = & git status --porcelain=v1 --untracked-files=all
    if ($LASTEXITCODE -ne 0) { throw "git status failed with exit code $LASTEXITCODE" }
    if ($dirty) { throw "Windows verification requires a clean Git checkout before build" }
    $sourceRevision = $headRevision
}
$sourceIdentity = Get-SourceIdentity $root

Invoke-Checked python @("csharp/scripts/validate_csharp_migration.py")
Invoke-Checked python @("csharp/scripts/verify_editor_audio_state_contract.py")
Invoke-Checked python @("csharp/scripts/verify_editor_voice_preview_contract.py")
Invoke-Checked python @("csharp/scripts/verify_editor_voice_cue_preview_contract.py")
Invoke-Checked python @("csharp/scripts/verify_editor_voice_mix_contract.py")
Invoke-Checked python @("csharp/scripts/verify_editor_voice_preview_export_contract.py")
Invoke-Checked python @("csharp/scripts/verify_editor_voice_reopen_contract.py")
Invoke-Checked python @("csharp/scripts/verify_editor_voice_generate_contract.py")
Invoke-Checked python @("csharp/scripts/verify_editor_voice_duration_contract.py")
Invoke-Checked python @("csharp/scripts/verify_editor_tts_cancel_contract.py")
Invoke-Checked python @("csharp/scripts/verify_editor_tts_restart_contract.py")
Invoke-Checked python @("csharp/scripts/verify_editor_preview_unload_contract.py")
Invoke-Checked python @("csharp/scripts/generate_csharp_code_map.py", "--check")
Invoke-Checked python @("csharp/scripts/verify_global_log_ui_contract.py")
Invoke-Checked python @("csharp/scripts/verify_logging_contract.py")
Invoke-Checked python @("csharp/scripts/verify_ocr_worker_contract.py")
Invoke-Checked python @("csharp/scripts/verify_ocr_scanner_contract.py")
Invoke-Checked python @("csharp/scripts/verify_asr_hybrid_contract.py")
Invoke-Checked python @("csharp/scripts/verify_asr_checkpoint_contract.py")
Invoke-Checked dotnet @("restore", "csharp/BiliSubStudio.sln", "-p:Platform=x64", "-p:NuGetAudit=false")
Invoke-Checked dotnet @("build", "csharp/BiliSubStudio.sln", "-c", "Release", "-p:Platform=x64", "-p:UseSharedCompilation=false", "-p:ContinuousIntegrationBuild=true", "--no-restore")
Invoke-Checked dotnet @("run", "--project", "csharp/tests/BiliSubStudio.Core.ContractTests/BiliSubStudio.Core.ContractTests.csproj", "-c", "Release", "--no-build", "--no-restore")
Invoke-Checked dotnet @("run", "--project", "csharp/tests/BiliSubStudio.RangeRegression/BiliSubStudio.RangeRegression.csproj", "-c", "Release", "-p:NuGetAudit=false")

$publish = "csharp/src/BiliSubStudio.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Invoke-Checked dotnet @(
    "publish", "csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj",
    "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "-p:Platform=x64", "-p:UseSharedCompilation=false", "-p:ContinuousIntegrationBuild=true", "--no-restore"
)

$exe = "$publish/BiliSubStudio.exe"
if (-not (Test-Path $exe -PathType Leaf)) { throw "publish missing BiliSubStudio.exe" }
if (Test-Path "$publish/BiliSubStudioCore.exe") { throw "publish contains forbidden second backend" }
foreach ($protected in @("Data", "Tools", "Temp", "Cache", "Downloads")) {
    if (Test-Path "$publish/$protected") { throw "publish contains protected portable data root: $protected" }
}
$worker = "$publish/Assets/worker.py"
if (-not (Test-Path $worker -PathType Leaf)) { throw "publish missing Assets/worker.py" }
$sourceWorker = "internal/ocr/worker.py"
if ((Get-Sha256 $worker) -ne (Get-Sha256 $sourceWorker)) {
    throw "published OCR worker differs from embedded source"
}
$asrWorker = "$publish/Assets/ASR/worker.py"
if (-not (Test-Path $asrWorker -PathType Leaf)) { throw "publish missing integrated ASR worker" }
$sourceAsrWorker = "internal/asr/worker.py"
if ((Get-Sha256 $asrWorker) -ne (Get-Sha256 $sourceAsrWorker)) {
    throw "published ASR worker differs from embedded source"
}
$translationSkill = "$publish/Assets/Translation/dich-trung-tu-tien.zip"
$ttsWorker = "$publish/Assets/TTS/worker.py"
if (-not (Test-Path $ttsWorker -PathType Leaf) -or (Get-Sha256 $ttsWorker) -ne (Get-Sha256 "internal/tts/worker.py")) {
    throw "published TTS worker differs from reviewed NGHI source"
}
if (-not (Test-Path $translationSkill -PathType Leaf)) { throw "publish missing integrated translation skill" }
$sourceTranslationSkill = "internal/translation/dich-trung-tu-tien.zip"
if ((Get-Sha256 $translationSkill) -ne (Get-Sha256 $sourceTranslationSkill)) {
    throw "published translation skill differs from reviewed source"
}

Assert-Pe32PlusX64 $exe

$publishedXbf = @(Get-ChildItem $publish -Recurse -File -Filter "*.xbf")
if ($publishedXbf.Count -lt 2) {
    throw "publish is missing compiled WinUI XBF resources"
}
$publishedPri = @(
    @(
        (Join-Path $publish "BiliSubStudio.pri"),
        (Join-Path $publish "resources.pri")
    ) | Where-Object { Test-Path $_ -PathType Leaf }
)
if ($publishedPri.Count -eq 0) {
    throw "publish is missing the WinUI package resource index"
}

# A successful compile is not proof that WinUI resources can initialize. Launch the
# exact published executable and require its Loaded path to write a sentinel.
$smokeTempRoot = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [System.IO.Path]::GetTempPath()
}
else {
    $env:RUNNER_TEMP
}
$smokeSentinel = Join-Path $smokeTempRoot "bilisub-winui-startup-smoke.txt"
if (Test-Path $smokeSentinel) { Remove-Item $smokeSentinel -Force }
$startupLog = Join-Path $env:LOCALAPPDATA "BiliSub Studio\Logs\startup.log"
if (Test-Path $startupLog) { Remove-Item $startupLog -Force }
$smokeArgument = '--startup-smoke-test="' + $smokeSentinel + '"'
$smokeProcess = Start-Process $exe -ArgumentList $smokeArgument -WindowStyle Hidden -PassThru
if (-not $smokeProcess.WaitForExit(30000)) {
    Stop-Process -Id $smokeProcess.Id -Force -ErrorAction SilentlyContinue
    throw "WinUI startup smoke test timed out"
}
if (-not (Test-Path $smokeSentinel -PathType Leaf) -or (Get-Content $smokeSentinel -Raw).Trim() -ne "PASS") {
    $diagnostic = if (Test-Path $startupLog) { Get-Content $startupLog -Raw } else { "startup log was not created" }
    throw "WinUI startup smoke test failed. Diagnostic: $diagnostic"
}
if (-not (Test-Path $startupLog -PathType Leaf)) {
    throw "WinUI startup smoke passed without the required persistent startup log"
}
Copy-Item $startupLog (Join-Path $publish "STARTUP_SMOKE_LOG.txt") -Force

# Portable-mode startup intentionally creates persistent roots beside the executable.
# The smoke test runs from the publish directory, so those roots are test byproducts,
# not runtime payload. Remove them before checksumming/packaging so installer/update
# artifacts can never carry Data/Tools/Temp/Cache/Downloads into Runtime.
$protectedPublishRoots = @("Data", "Tools", "Temp", "Cache", "Downloads")
foreach ($protected in $protectedPublishRoots) {
    $generatedRoot = Join-Path $publish $protected
    if (Test-Path $generatedRoot) {
        Remove-Item $generatedRoot -Recurse -Force
    }
}
foreach ($protected in $protectedPublishRoots) {
    if (Test-Path (Join-Path $publish $protected)) {
        throw "startup smoke contaminated publish payload with protected root: $protected"
    }
}

$exeHash = Get-Sha256 $exe
$workerHash = Get-Sha256 $worker
$sourceIdentity.Inventory | Set-Content "$publish/SOURCE_SHA256SUMS.txt" -Encoding UTF8

$identity = [ordered]@{
    schema = 1
    checkpoint = "CSharp-P5-WindowsBuildCandidate"
    informational_version = $informationalVersion
    created_utc = [DateTime]::UtcNow.ToString("o")
    candidate_status = "build_verified_field_qa_pending"
    release_candidate = $false
    promotion_allowed = $false
    pending_gate = "docs/migration/WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md"
    source_revision = $sourceRevision
    source_tree_sha256 = $sourceIdentity.Hash
    source_file_count = $sourceIdentity.FileCount
    dotnet_sdk = $sdk
    windows = [System.Environment]::OSVersion.VersionString
    windows_app_sdk = "2.4.0"
    target = "net10.0-windows10.0.26100.0/win-x64"
    self_contained = $true
    winui_startup_smoke = $true
    startup_smoke_log = "STARTUP_SMOKE_LOG.txt"
    xbf_resource_count = $publishedXbf.Count
    pri_resource = [System.IO.Path]::GetFileName($publishedPri[0])
    exe_sha256 = $exeHash
    worker_sha256 = $workerHash
    github = [ordered]@{
        repository = $env:GITHUB_REPOSITORY
        workflow = $env:GITHUB_WORKFLOW
        run_id = $env:GITHUB_RUN_ID
        run_attempt = $env:GITHUB_RUN_ATTEMPT
    }
}
$identity | ConvertTo-Json -Depth 5 | Set-Content "$publish/BUILD_IDENTITY.json" -Encoding UTF8

$publishFull = (Resolve-Path $publish).Path.TrimEnd('\', '/')
$sumLines = Get-ChildItem $publish -Recurse -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($publishFull.Length).TrimStart('\', '/').Replace('\', '/')
        "$(Get-Sha256 $_.FullName)  $relative"
    }
$sumLines | Set-Content "$publish/SHA256SUMS.txt" -Encoding UTF8

# Read back every checksum before this directory can become a workflow artifact.
foreach ($line in $sumLines) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "invalid SHA256SUMS entry: $line" }
    $expected = $Matches[1]
    $relative = $Matches[2]
    $actual = Get-Sha256 (Join-Path $publishFull $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if ($actual -ne $expected) { throw "publish checksum readback failed: $relative" }
}

if ($env:GITHUB_OUTPUT) {
    "publish_directory=$((Resolve-Path $publish).Path)" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8
    "exe_sha256=$exeHash" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8
    "source_tree_sha256=$($sourceIdentity.Hash)" | Out-File $env:GITHUB_OUTPUT -Append -Encoding utf8
}

Write-Host "PASS: Windows C# compile, global-log/shell contract, OCR worker/scanner contracts, contract runner, short-read regression, self-contained WinUI publish, real startup smoke, worker identity, PE32+ x64 and checksum readback"
Write-Host "Version: $informationalVersion"
Write-Host "BiliSubStudio.exe SHA-256: $exeHash"
Write-Host "Source tree SHA-256: $($sourceIdentity.Hash)"
