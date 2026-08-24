#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CSHARP = ROOT / "csharp"
VERSION_RE = re.compile(r"^4\.0\.0-beta\.\d+-csharp-p5$")

REQUIRED = [
    ROOT / "global.json",
    ROOT / ".github/workflows/csharp-p5-windows-x64-installer.yml",
    CSHARP / "BiliSubStudio.sln",
    CSHARP / "Directory.Build.props",
    CSHARP / "Directory.Packages.props",
    CSHARP / "scripts/compile_app_codebehind_contract.py",
    CSHARP / "scripts/package_windows_candidate.ps1",
    CSHARP / "scripts/build_windows_installer.ps1",
    CSHARP / "scripts/verify.ps1",
    CSHARP / "scripts/verify_media_bundle_contract.py",
    CSHARP / "scripts/verify_global_log_ui_contract.py",
    CSHARP / "installer/BiliSubStudio.iss",
    CSHARP / "src/BiliSubStudio.App/Assets/BiliSubStudio.ico",
    CSHARP / "src/BiliSubStudio.App/App.xaml",
    CSHARP / "src/BiliSubStudio.App/MainWindow.xaml",
    CSHARP / "src/BiliSubStudio.App/MainWindow.xaml.cs",
    CSHARP / "src/BiliSubStudio.App/Pages/SettingsPage.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/VideoPage.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/VideoPage.xaml.cs",
    CSHARP / "src/BiliSubStudio.App/Pages/OcrPage.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/EditorPage.xaml",
    CSHARP / "src/BiliSubStudio.Core/Application/BiliSubApplication.cs",
    CSHARP / "src/BiliSubStudio.Core/Authentication/SessionStore.cs",
    CSHARP / "src/BiliSubStudio.Core/Configuration/AppConfig.cs",
    CSHARP / "src/BiliSubStudio.Core/Maintenance/UpdateService.cs",
    CSHARP / "src/BiliSubStudio.Core/Processes/WindowsProcessContainment.cs",
    CSHARP / "src/BiliSubStudio.Core/Subtitle/SubtitleService.cs",
    CSHARP / "src/BiliSubStudio.Core/Video/BilibiliPlayurlClient.cs",
    CSHARP / "src/BiliSubStudio.Core/Video/BilibiliSubtitleClient.cs",
    CSHARP / "src/BiliSubStudio.Core/Video/RangeDownloader.cs",
    CSHARP / "src/BiliSubStudio.Core/Video/VideoDownloadService.cs",
    CSHARP / "src/BiliSubStudio.Core/Video/YtDlpResolver.cs",
    CSHARP / "tests/BiliSubStudio.Core.ContractTests/Program.cs",
    CSHARP / "tests/BiliSubStudio.CdnRegression/Program.cs",
    CSHARP / "tests/BiliSubStudio.CdnFailoverRegression/Program.cs",
    CSHARP / "tests/BiliSubStudio.SubtitleRegression/Program.cs",
    ROOT / "docs/migration/CSHARP_WINUI3_CALL_MAP.md",
    ROOT / "docs/migration/CSHARP_CODE_MAP.generated.md",
    ROOT / "docs/migration/MIGRATION_LEDGER.md",
    ROOT / "docs/migration/WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md",
]

EXPECTED_JSON_FIELDS = (
    "theme",
    "output_dir",
    "sub_format",
    "video_speed",
    "video_container",
    "video_mode",
    "check_updates",
    "ocr_device",
    "ocr_top",
    "ocr_bottom",
    "ocr_left",
    "ocr_right",
)


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


for required in REQUIRED:
    require(required.is_file(), f"missing {required.relative_to(ROOT)}")

global_sdk = read(ROOT / "global.json")
require('"version": "10.0.400"' in global_sdk and '"rollForward": "disable"' in global_sdk,
        "global.json must pin exact .NET SDK 10.0.400")

props_root = ET.parse(CSHARP / "Directory.Build.props").getroot()
info_node = props_root.find(".//InformationalVersion")
version = (info_node.text or "").strip() if info_node is not None else ""
require(bool(VERSION_RE.fullmatch(version)), f"unexpected C# public-beta InformationalVersion: {version!r}")

for xml_file in list(CSHARP.rglob("*.xaml")) + list(CSHARP.rglob("*.csproj")) + [
    CSHARP / "Directory.Build.props",
    CSHARP / "Directory.Packages.props",
]:
    try:
        ET.parse(xml_file)
    except ET.ParseError as exc:
        fail(f"invalid XML {xml_file.relative_to(ROOT)}: {exc}")

event_attributes = {
    "Click", "Checked", "Toggled", "SelectionChanged", "TextChanged", "ValueChanged",
    "PointerPressed", "PointerMoved", "PointerReleased", "Loaded", "Unloaded", "SizeChanged",
}
for xaml_file in CSHARP.rglob("*.xaml"):
    root = ET.parse(xaml_file).getroot()
    code_file = Path(str(xaml_file) + ".cs")
    partials = sorted(xaml_file.parent.glob(xaml_file.stem + "*.cs"))
    code = "\n".join(read(path) for path in partials) if partials else (read(code_file) if code_file.is_file() else "")
    for element in root.iter():
        for attribute, handler in element.attrib.items():
            event_name = attribute.rsplit("}", 1)[-1]
            if event_name in event_attributes and not handler.startswith("{"):
                require(bool(re.search(rf"\b{re.escape(handler)}\s*\(", code)),
                        f"XAML event {event_name}={handler} has no handler in {code_file.relative_to(ROOT)}")

packages = read(CSHARP / "Directory.Packages.props")
require('Include="Microsoft.WindowsAppSDK" Version="2.4.0"' in packages,
        "Windows App SDK must stay pinned to reviewed stable version 2.4.0")

verify_script = read(CSHARP / "scripts/verify.ps1")
package_script = read(CSHARP / "scripts/package_windows_candidate.ps1")
installer_script = read(CSHARP / "scripts/build_windows_installer.ps1")
workflow = read(ROOT / ".github/workflows/csharp-p5-windows-x64-installer.yml")
inno_script = read(CSHARP / "installer/BiliSubStudio.iss")

for marker in (
    "Get-SourceIdentity", "Assert-Pe32PlusX64", "Directory.Build.props", "$informationalVersion",
    "SOURCE_SHA256SUMS.txt", "SHA256SUMS.txt", "CSharp-P5-WindowsBuildCandidate",
    "startup-smoke-test", "STARTUP_SMOKE_LOG.txt", "winui_startup_smoke = $true",
    "publish is missing compiled WinUI XBF resources", "publish is missing the WinUI package resource index",
):
    require(marker in verify_script, f"Windows verification gate missing {marker}")
require("informational_version = $informationalVersion" in verify_script,
        "BUILD_IDENTITY must derive version from Directory.Build.props")

for marker in (
    "$version = ([string]$identity.informational_version).Trim()", "CANDIDATE_GATE_STATUS.json",
    "CANDIDATE_SHA256SUMS.txt", "SOURCE_SHA256SUMS.txt", "build_windows_installer.ps1",
    "primary_user_artifact", "INSTALLER_STARTUP_SMOKE_LOG.txt", "installer_install_smoke = $true",
):
    require(marker in package_script, f"Windows beta packaging gate missing {marker}")
require('BiliSubStudio_v$version-source-$sourceTag.zip' in package_script,
        "exact-source archive name must derive from build version")

for marker in (
    "$version = ([string]$identity.informational_version).Trim()", "Assert-Pe32PlusX64",
    "BiliSubStudio_Setup_v${version}_${sourceTag}_x64", "INSTALLER_GATE_STATUS.json",
    'requires_admin = $false', "root_launcher_smoke = $rootLauncherSmoke",
    "installer_install_smoke = $installerInstallSmoke", "BiliSub Studio Custom Location\\BiliSub Studio",
):
    require(marker in installer_script, f"one-file installer gate missing {marker}")

for marker in (
    "runs-on: windows-2025", 'dotnet-version: "10.0.400"',
    "Verify Bilibili subtitle priority and AI fallback", "BiliSubStudio.SubtitleRegression",
    "./csharp/scripts/verify.ps1", "./csharp/scripts/package_windows_candidate.ps1",
    "Publish public beta and beta update channel", "actions/upload-artifact@v4",
    "innosetup-7.0.2-x64.exe", "gh release verify-asset",
):
    require(marker in workflow, f"Windows CI workflow missing {marker}")
for forbidden in ("google-drive", "drive.google.com"):
    require(forbidden not in workflow.lower(), f"Windows CI contains forbidden legacy promotion path: {forbidden}")

for marker in (
    "PrivilegesRequired=lowest", "SetupArchitecture=x64", "ArchitecturesAllowed=x64compatible",
    "{localappdata}\\Programs\\BiliSub Studio", "uninsneveruninstall", "BiliSubStudio.exe",
    "DisableProgramGroupPage=yes", "DisableDirPage=no", "AppendDefaultDirName=yes",
    "AllowNoIcons=no", "UsePreviousAppDir=yes",
):
    require(marker in inno_script, f"Inno Setup contract missing {marker}")

app_project = read(CSHARP / "src/BiliSubStudio.App/BiliSubStudio.App.csproj")
for marker in (
    "net10.0-windows10.0.26100.0", "<UseWinUI>true</UseWinUI>",
    "<WindowsPackageType>None</WindowsPackageType>", "<RuntimeIdentifier>win-x64</RuntimeIdentifier>",
    "<ApplicationIcon>Assets\\BiliSubStudio.ico</ApplicationIcon>",
    "<SatelliteResourceLanguages>en-US;vi-VN</SatelliteResourceLanguages>",
    "CopyCompiledXamlResourcesToPublish", "$(OutputPath)**\\*.xbf", "$(AssemblyName).pri",
):
    require(marker in app_project, f"app project missing {marker}")
require('Link="Assets\\worker.py"' in app_project and "internal\\ocr\\worker.py" in app_project,
        "app project must package the embedded OCR worker asset")

app_sources = "\n".join(
    read(path) for path in (CSHARP / "src/BiliSubStudio.App").rglob("*.*") if path.suffix in {".cs", ".xaml"}
)
for marker in ("http://localhost", "https://localhost", "WebView2", "BiliSubStudioCore.exe", '"/api/'):
    require(marker not in app_sources, f"production C# UI contains forbidden marker {marker}")
require('SizeChanged="' not in app_sources and not re.search(r"\b(?:Page|RootGrid)_SizeChanged\b", app_sources),
        "layout must not mutate main grids from SizeChanged")

core_sources = "\n".join(read(path) for path in (CSHARP / "src/BiliSubStudio.Core").rglob("*.cs"))
require("Microsoft.UI" not in core_sources and "Windows.Storage" not in core_sources,
        "BiliSubStudio.Core crossed into UI/WinRT ownership")

config_source = read(CSHARP / "src/BiliSubStudio.Core/Configuration/AppConfig.cs")
actual_fields = tuple(re.findall(r'JsonPropertyName\("([^"]+)"\)', config_source))
require(actual_fields == EXPECTED_JSON_FIELDS, f"config JSON schema drift: {actual_fields}")

update_source = read(CSHARP / "src/BiliSubStudio.Core/Maintenance/UpdateService.cs")
for marker in (
    "winui3-portable-zip", "SHA-256 bản cập nhật không khớp", "BreakawayLauncher",
    "ValidatePayloadLayout", "PreservedRootDirectories", "0x00004550", "0x8664", "0x020B",
    "ApplyPayloadTransactionalAsync", "BetaManifestUrl",
):
    require(marker in update_source, f"safe WinUI update gate missing {marker}")

video_service = read(CSHARP / "src/BiliSubStudio.Core/Video/VideoDownloadService.cs")
for marker in ('"stable" => 1', '"turbo" => 8', "_ => 4", "CancelJob"):
    require(marker in video_service + core_sources, f"download control contract missing {marker}")

range_source = read(CSHARP / "src/BiliSubStudio.Core/Video/RangeDownloader.cs")
for marker in ("ContentRange", "PartialContent", "Oversized Range body", 'DeleteFiles(segmentDirectory, "*.tmp")'):
    require(marker in range_source, f"strict Range/resume contract missing {marker}")

subtitle_source = read(CSHARP / "src/BiliSubStudio.Core/Subtitle/SubtitleService.cs")
resolver_source = read(CSHARP / "src/BiliSubStudio.Core/Video/YtDlpResolver.cs")
subtitle_client = read(CSHARP / "src/BiliSubStudio.Core/Video/BilibiliSubtitleClient.cs")
subtitle_fixture = read(CSHARP / "tests/BiliSubStudio.SubtitleRegression/Program.cs")
for marker in ("ParseTimedText", "TryParseTimestamp", '"json" => JsonSerializer.Serialize'):
    require(marker in subtitle_source, f"subtitle multi-source normalization missing {marker}")
require('"official:"' in resolver_source and '"ai:"' in resolver_source,
        "official and AI subtitle tracks must remain distinct identities")
for marker in ("x/player/v2", "x/v2/subtitle/web/view", "SESSDATA", "preferred_language=ai-zh"):
    require(marker in subtitle_client, f"native Bilibili subtitle discovery missing {marker}")
for marker in ("available subtitle > Bilibili AI subtitle", "normal metadata empty", "Protobuf"):
    require(marker in subtitle_fixture, f"subtitle regression fixture missing {marker}")

composition = read(CSHARP / "src/BiliSubStudio.Core/Application/BiliSubApplication.cs")
for marker in ("PrepareShutdownAsync", "PauseJobAsync", "WindowsProcessContainment", "StartOcrScan", "StartEditor", "StartEditorAsr", "StartEditorTts", "SaveEditorKaraokeAssAsync", "StartSubtitle", "StartVideo"):
    require(marker in composition, f"application composition root missing {marker}")

worker_client = read(CSHARP / "src/BiliSubStudio.Core/Ocr/OcrWorkerClient.cs")
for marker in ('start.ArgumentList.Add("--model-cache")', 'start.ArgumentList.Add("--device")'):
    require(marker in worker_client, f"OCR worker command line lost required argument: {marker}")

containment_source = read(CSHARP / "src/BiliSubStudio.Core/Processes/WindowsProcessContainment.cs")
for marker in ("JobObjectLimitKillOnJobClose", "JobObjectLimitBreakawayOk"):
    require(marker in containment_source, f"Windows child-process containment missing {marker}")

ocr = read(CSHARP / "src/BiliSubStudio.App/Pages/OcrPage.xaml") + read(CSHARP / "src/BiliSubStudio.App/Pages/OcrPage.xaml.cs")
editor_xaml = read(CSHARP / "src/BiliSubStudio.App/Pages/EditorPage.xaml")
editor_main = read(CSHARP / "src/BiliSubStudio.App/Pages/EditorPage.xaml.cs")
editor_partials = "\n".join(read(path) for path in (CSHARP / "src/BiliSubStudio.App/Pages").glob("EditorPage*.cs"))
editor = editor_xaml + editor_partials
for owner, source in (("OCR", ocr), ("Editor", editor)):
    for marker in ("MediaPlayerElement", "IsFullWindow", "MediaSource.CreateFromStorageFile", "PositionChanged"):
        require(marker in source, f"{owner} native playback/fullscreen contract missing {marker}")
require('AreTransportControlsEnabled="True"' in ocr, "OCR native transport contract missing")
require('AreTransportControlsEnabled="False"' in editor, "Editor must own preview chrome and disable native MediaPlayer transport")
for forbidden in (
    "SubtitleModeButton.Click +=", "BlurModeButton.Click +=", "AudioModeButton.Click +=", "ExportModeButton.Click +=",
    "OpenVideoButton.Click +=", "RenderButton.Click -=", "RenderButton.IsEnabledChanged +=",
    "OnNavigatedTo(", "OnApplyTemplate(", "Render_Click(sender, e)", "EditorParity_Loaded", "HookEditorLivePreviewEvents",
    "PlayerPlayPauseButton.Click +=", "PlaybackButton", "Playback_Click",
):
    require(forbidden not in editor_partials, f"Editor cleanup regression reintroduced {forbidden}")
require("EnsureEditorParityInitialized();" in editor_partials and "EnsureImageFeatureInitialized();" in editor_partials,
        "Editor must initialize parity and image tools from one lifecycle owner")
playback_source = read(CSHARP / "src/BiliSubStudio.App/Pages/EditorPage.Playback.cs")
require('Click="PlayerPlayPause_Click"' in editor_xaml
        and "await _playback.ToggleAsync();" in playback_source
        and "private sealed class EditorPlaybackController" in playback_source,
        "PREVIEW-03 requires one XAML Play/Pause handler forwarding to one playback controller")
for legacy_owner in (
    "private MediaPlayer? _player;", "private bool _playerMode;", "private bool _previewRendering;",
    "private string? _playerPreviewPath;", "private CancellationTokenSource? _playbackPreviewCancellation;",
):
    require(legacy_owner not in editor_main, f"PREVIEW-03 legacy page playback owner returned: {legacy_owner}")
for owned_state in (
    "private MediaPlayer? _player;", "private readonly EditorPreviewRequestCoordinator _previewRequests = new();",
    "internal bool IsPreviewMode { get; private set; }", "internal bool IsRendering => _previewRequests.IsActive;",
    "private string? _previewPath;", "private void PlayerMediaEnded(", "private void PlayerMediaFailed(",
):
    require(owned_state in playback_source, f"PREVIEW-03 playback controller ownership missing: {owned_state}")
require(editor_partials.count("private MediaPlayer? _player;") == 1
        and editor_partials.count("private readonly EditorPreviewRequestCoordinator _previewRequests = new();") == 1
        and "private CancellationTokenSource? _renderCancellation;" not in editor_partials
        and editor_partials.count("internal bool IsPreviewMode { get; private set; }") == 1,
        "PREVIEW-03 playback state must have exactly one owner")
toggle_playback = playback_source.split("internal async Task ToggleAsync()", 1)[1].split("internal async Task EnterFullscreenAsync()", 1)[0]
require("await PlayFromStartAsync();" in toggle_playback
        and "SetModeAsync(enabled: true, play: true)" not in toggle_playback
        and "_page.Timeline.Value" not in toggle_playback
        and "private Task PlayFromStartAsync() => LoadSegmentAsync(0, play: true);" in playback_source,
        "PREVIEW-04 initial Play must request source time zero instead of the edit/seek position")
require("if (IsPlaying) PauseAtCurrentFrame();" in toggle_playback
        and "private void PauseAtCurrentFrame() => _player?.Pause();" in playback_source
        and "SetModeAsync(enabled: false" not in toggle_playback
        and "ApplyPresentation(processed: false)" not in toggle_playback,
        "PREVIEW-05 Pause must hold the current MediaPlayer frame without leaving processed preview")
require("else ResumeFromCurrentFrame();" in toggle_playback
        and "private void ResumeFromCurrentFrame() => _player?.Play();" in playback_source
        and playback_source.count("ResumeFromCurrentFrame();") == 2,
        "PREVIEW-06 Resume must reuse the paused MediaPlayer source/position without rendering a segment")
seek_playback = playback_source.split("internal async Task SeekAsync(double sourcePosition)", 1)[1].split("internal Task DisposeForSourceChangeAsync()", 1)[0]
require("else await SeekPausedAsync(sourcePosition);" in seek_playback
        and "private Task SeekPausedAsync(double sourcePosition) => LoadSegmentAsync(sourcePosition, play: false);" in playback_source
        and "var segmentPosition = PositionInSegment(segment, requestedStart);" in playback_source
        and "player.PlaybackSession.Position = TimeSpan.FromSeconds(segmentPosition);" in playback_source
        and "var lastFrame = Math.Max(0, segment.Duration - .05);" in playback_source
        and "return Math.Clamp(requestedStart - segment.SourceStart, 0, lastFrame);" in playback_source,
        "PREVIEW-07 paused Seek must show the requested source frame inside the cache segment and remain paused")
require("if (IsPlaying) await SeekPlayingAsync(sourcePosition);" in seek_playback
        and "private async Task SeekPlayingAsync(double sourcePosition)" in playback_source,
        "PREVIEW-08 playing Seek must use an explicit pause-render-resume operation")
seek_playing = playback_source.split("private async Task SeekPlayingAsync(double sourcePosition)", 1)[1].split("private Task SeekPausedAsync", 1)[0]
require("PauseAtCurrentFrame();" in seek_playing
        and "await LoadSegmentAsync(sourcePosition, play: true);" in seek_playing
        and seek_playing.index("PauseAtCurrentFrame();") < seek_playing.index("await LoadSegmentAsync(sourcePosition, play: true);"),
        "PREVIEW-08 playing Seek must stop the old position before rendering and resume at the target")
preview_request_source = read(CSHARP / "src/BiliSubStudio.Core/Editor/EditorPreviewRequestCoordinator.cs")
require("EditorPreviewRequestCoordinator" in playback_source
        and "_previewRequests.RunLatestAsync" in playback_source
        and "await _previewRequests.CancelAsync();" in playback_source
        and "private CancellationTokenSource? _renderCancellation;" not in playback_source
        and "await previousCompletion;" in preview_request_source
        and preview_request_source.index("await previousCompletion;")
            < preview_request_source.index("await operation(cancellation.Token);")
        and "RequestCancellation(previousCancellation);" in preview_request_source
        and "cancellation.Dispose();" in preview_request_source
        and "completion.TrySetResult(true);" in preview_request_source,
        "PREVIEW-09 rapid Seek must use one latest-request owner that serializes cancellation cleanup")
contract_tests_source = read(CSHARP / "tests/BiliSubStudio.Core.ContractTests/Program.cs")
require("VideoEditorService.NextPreviewStart(" in playback_source
        and "await LoadSegmentAsync(nextStart.Value, play: true);" in playback_source
        and "EditorFullVideoPlaybackContractAsync" in contract_tests_source,
        "PREVIEW-10 MediaEnded must follow tested segment boundaries until the full source end")
require("SubtitleCueList" in editor and "SubtitleRetranslateCueButton" in editor and "SubtitleSaveSrtButton" in editor,
        "Editor static subtitle cue editor controls missing")
require("ForceFresh = false" in read(CSHARP / "src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs")
        and "if (request.ForceFresh) TryDelete(checkpointPath);" in read(CSHARP / "src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs"),
        "Editor force-fresh cue translation checkpoint reset missing")
require("LayoutUpdated += Subtitle" not in editor_partials and "SubtitleRetranslateCue_Click(sender" not in editor_partials,
        "Editor cue editor must not sync from LayoutUpdated or call one event handler from another")
require('Click="ImportSubtitle_Click"' in editor and "private async void ImportSubtitle_Click" in editor_partials
        and "await ImportSubtitleAsync();" in editor_partials,
        "SUB-01 requires one Import Subtitle handler calling one ImportSubtitleAsync method")
require("ImportSrt_Click" not in editor_partials and "ImportSrtAsync" not in editor_partials,
        "SUB-01 legacy Import SRT handler/method returned")
require("var path = await _picker.PickSubtitleAsync();" in editor_partials
        and "if (string.IsNullOrWhiteSpace(path)) return;" in editor_partials,
        "SUB-02/SUB-04 subtitle picker cancel-safe path missing")
require("candidate = await _application.LoadEditorSubtitleAsync(path, CancellationToken.None);" in editor_partials
        and editor_partials.index("candidate = await _application.LoadEditorSubtitleAsync(path, CancellationToken.None);")
            < editor_partials.index("_subtitleSource = candidate;"),
        "SUB-05 must validate candidate SRT before replacing current subtitle state")
require("SRT không hợp lệ:" in editor_partials and "AttachSubtitleToProject(string.Empty);" in editor_partials
        and "if (_project is not null) await SaveProjectNowAsync();" in editor_partials,
        "SUB-03/SUB-05 project attach or semantic invalid-SRT error contract missing")
require("SeekEditorToSubtitleCueAsync" in editor_partials and "await SeekEditorToSubtitleCueAsync(cue.Start);" in editor_partials
        and "if (_playback.IsPreviewMode) await _playback.SeekAsync(target);" in editor_partials,
        "SUB-06 cue selection must seek the compact Player to cue start")
require("if (_playback.IsPreviewMode) await _playback.SetModeAsync(false, false);" not in editor_partials,
        "SUB-06 cue selection regressed to leaving processed Player mode before seek")
require("RenderSubtitlePlacement" in editor_partials and "SubtitlePreviewText(cue)" in editor_partials
        and "PreviewSubtitleBurn()" in editor_partials,
        "SUB-07 subtitle caption must render on edit-frame and processed Preview paths")
require("_subtitleDrag = true;" in editor_partials and "HitTestSubtitle(point)" in editor_partials
        and "ResizeOrMove(_subtitleDragOriginal" in editor_partials,
        "SUB-08 subtitle drag ownership missing")
for direction in ("North", "South", "East", "West", "NorthEast", "NorthWest", "SouthEast", "SouthWest"):
    require(f"DragKind.{direction}" in editor_partials, f"SUB-09 subtitle resize direction missing: {direction}")
require("SourceOverride" in editor_partials and "SubtitleSourceEdit.Text.Trim()" in editor_partials,
        "SUB-10 Chinese cue edit state missing")
require("Preview hiển thị bản nháp" in editor_partials and "RenderOverlays();" in editor_partials
        and "SubtitleVietnameseEdit.Text.Trim()" in editor_partials,
        "SUB-11 Vietnamese cue edit must update Preview draft immediately")
require("state.Locked" in editor_partials and "locked.TryGetValue(c.Id, out var keep)" in editor_partials,
        "SUB-12 locked cue protection missing from full Vietsub merge")
require("await RetranslateSelectedCueAsync();" in editor_partials and "ForceFresh: true" in editor_partials
        and "SubtitleRetranslateCue_Click(sender" not in editor_partials,
        "SUB-13 clean force-fresh cue retranslation contract missing")
translation_source = read(CSHARP / "src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs")
require("TranslationSkillBundle.Load" in translation_source and "ValidateUnchangedTimeline(source, translated)" in translation_source
        and "Qwen3-8B" in translation_source,
        "SUB-14 local AI + skill + exact timeline Vietsub contract missing")
require('string.Equals(snapshot.Status, "cancelled"' in editor_partials
        and "finally { TryDelete(temporary); }" in translation_source
        and "cleanupAwareCancel: true" in composition,
        "SUB-15 translation cancellation/checkpoint cleanup contract missing")
require("await SaveCurrentSubtitleCueAsync();" in editor_partials and "EditorSubtitleDocument.RenderVietnamese(cues)" in editor_partials,
        "SUB-16 save Vietnamese SRT must include latest cue edit")
require("MarkTranslatedOutputStale();" in editor_partials and "OutputPath = string.Empty" in editor_partials
        and "File.Exists(_project?.Subtitle?.OutputPath)" in editor_partials,
        "SUB-17 stale Vietnamese SRT protection missing")
require("RestoreSubtitleAsync(_project.Subtitle)" in editor_partials and "SubtitleManualStore.LoadAsync" in editor_partials
        and "EditorSubtitleManualStore.Apply" in editor_partials,
        "SUB-18 project reopen must restore translation/edit/lock state")
require("Loaded += EditorPage_Loaded;" in editor and "private void EditorPage_Loaded" in editor_partials,
        "Editor must use the actual Loaded event as its single feature initialization lifecycle")

picker_source = read(CSHARP / "src/BiliSubStudio.App/Services/FilePickerService.cs")
for picker_marker in ("GetOpenFileNameW", "CommDlgExtendedError", "catch (OperationCanceledException)", "Fallback Win32"):
    require(picker_marker in picker_source, f"Editor picker fallback/cancel contract missing {picker_marker}")
require('Click="OpenVideo_Click"' in editor and "private async void OpenVideo_Click" in editor_partials,
        "Editor Open Video must have one XAML handler named OpenVideo_Click")
require("Pick_Click(" not in editor_partials, "legacy Pick_Click handler returned")
open_video = editor_partials.split("private async Task OpenVideoAsync()", 1)[1].split("private async Task SaveCurrentSourceStateForSwitchAsync()", 1)[0]
require(open_video.count("SaveCurrentSourceStateForSwitchAsync();") == 1,
        "OpenVideoAsync must save the old source state exactly once")
require(open_video.count("DisposePreviewForSourceChangeAsync();") == 1,
        "OpenVideoAsync must dispose the old preview exactly once")
require("EditorSourceSelection.IsSameSource" in open_video, "same-source no-op guard missing")
require("_application.Media.ProbeAsync(candidatePath" in open_video and "_path = candidatePath;" in open_video
        and open_video.index("_application.Media.ProbeAsync(candidatePath") < open_video.index("_path = candidatePath;"),
        "candidate video must be probed before mutating current Editor source state")

for marker in (
    "SubtitleModeButton", "BlurModeButton", "AudioModeButton", "ExportModeButton",
    "SubtitleInspectorPanel", "BlurInspectorPanel", "AudioInspectorPanel", "ExportInspectorPanel",
    "RunLayoutSmokeAsync", "ImportSrtButton.IsEnabled = idle && !_playback.IsPreviewMode;", "PrepareAiButton.IsEnabled = idle && !_playback.IsPreviewMode;",
    "_inspectorMode == InspectorMode.Blur", "_inspectorMode == InspectorMode.Subtitle",
    "EditorPlaybackController", "CreateEditorPreviewSegmentAsync", "PlayerMediaEnded",
):
    require(marker in editor, f"Editor icon-mode/action-state contract missing {marker}")
require("ImportSrtButton.IsEnabled = idle && hasMedia" not in editor,
        "Editor SRT picker regressed to requiring a selected video")
require("PrepareAiButton.IsEnabled = idle && hasMedia" not in editor,
        "Editor AI preparation regressed to requiring a selected video")
for marker in (
    "CreateAsrButton", "CreateAsr_Click", "CreateAsrButton.IsEnabled = editable;", "PollAsrJobAsync",
    "GenerateTtsButton", "GenerateTts_Click", "KaraokeToggle", "CurrentCueVoiceBox", "SaveKaraokeAssButton",
    "EditorSpeechProject", "EditorTtsProject",
):
    require(marker in editor, f"Editor Whisper timing/TTS UI-state contract missing {marker}")

editor_service = read(CSHARP / "src/BiliSubStudio.Core/Editor/VideoEditorService.cs")
for marker in (
    "CreatePreviewSegmentAsync", "BuildPreviewSlice", "BuildPreviewArguments", "BuildFilterCore",
    'Path.Combine(paths.Temp, "Editor", "Preview")', '"-preset", "ultrafast"',
    "BuildAudioArgumentsCore(audio, mp4: true, resetTimestamps: true)", "BuildVoiceAudioFilter", "BuildKaraokeText", "DeletePreviewSegmentAsync",
):
    require(marker in editor_service, f"Editor processed-preview/render parity contract missing {marker}")

asr_installer = read(CSHARP / "src/BiliSubStudio.Core/Editor/LocalAsrInstaller.cs")
asr_service = read(CSHARP / "src/BiliSubStudio.Core/Editor/LocalAsrService.cs")
asr_worker = read(ROOT / "internal/asr/worker.py")
attributes = read(ROOT / ".gitattributes")
for worker_path in ("internal/ocr/worker.py text eol=lf", "internal/asr/worker.py text eol=lf", "internal/tts/worker.py text eol=lf"):
    require(worker_path in attributes, f"Windows worker byte provenance missing {worker_path}")
for marker in (
    'FasterWhisperVersion = "1.2.1"', 'CTranslate2Version = "4.8.1"',
    "79a66ad50688c0b794dd501dc340a736992a6342f7f95e5811be60b5224a26a7",
    "49f96e861b57301f0b76a082109bde2cac8204a6b4fedc870883008271e82251",
    'ModelRevision = "536b0662742c02347bc0e980a01041f333bce120"',
    "3e305921506d8872816023e4c273e75d2419fb89b24da97b4fe7bce14170d671",
    "EnsurePrivatePythonAsync", "SHA-256 model ASR", "HF_HUB_OFFLINE",
):
    require(marker in asr_installer, f"ASR pinned installer contract missing {marker}")
for marker in ("SelectRuntimeAsync", "ProbeRealtimeFactor", "asr-probe-gpu", "asr-probe-cpu", "SaveCheckpointAsync", "RunStreamingAsync", "OwnedProcessGroup"):
    require(marker in asr_service, f"ASR benchmark/checkpoint/process contract missing {marker}")
for marker in ('local_files_only=True', 'language="zh"', "word_timestamps=True", "vad_filter=True", '"event": "segment"', '"voice_class"', '"median_pitch_hz"'):
    require(marker in asr_worker, f"ASR worker offline/Chinese/timestamp contract missing {marker}")
require("WhisperModel(" in asr_worker and "str(model_dir)" in asr_worker,
        "ASR worker must load only the verified local model directory")


tts_installer = read(CSHARP / "src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs")
tts_service = read(CSHARP / "src/BiliSubStudio.Core/Editor/LocalTtsService.cs")
tts_worker = read(ROOT / "internal/tts/worker.py")
for marker in (
    'PiperVersion = "1.4.2"',
    "9c4a3a11f5889ea9d0df4414dce2bd9bee5ce7d9cf604c8fd5e307441d4c031f",
    'VoiceRepository = "rhasspy/piper-voices"',
    'ModelRevision = "3d796cc2f2c884b3517c527507e084f7bb245aea"',
    'VoiceRevision = ModelRevision + "-profile-v1"',
    'BaseVoice = "vi_VN-vais1000-medium"',
    'MaleVoice = "vais1000-male-profile-v1"', 'FemaleVoice = "vais1000-female-profile-v1"',
    "ec7c89e2c85f4d1edc24b6120c18aaf1bda614f06b511567eb9c7c0de15e2dab",
    "fafb9da1354ed4b77c31af228ed41fb41cd825c14cffa105454b25e6ae751ee0",
    "DownloadVerifiedAsync", "EnsurePrivatePythonAsync",
):
    require(marker in tts_installer, f"licensed VAIS/Piper installer contract missing {marker}")
for retired in ("sannht/vi_voice", "deepman3909", "calmwoman3688"):
    require(retired not in tts_installer and retired not in tts_service and retired not in tts_worker,
            f"retired ambiguous NghiTTS weight returned to production: {retired}")
for marker in ("whisper-rhythm-v1", "BuildRhythmGroups", "SelectVoice", "EditorSpeechAnalysisDocument.MapToCues", "OwnedProcessGroup"):
    require(marker in tts_service, f"local TTS timing/cache/process contract missing {marker}")
for marker in (
    "PiperVoice.load", "SynthesisConfig", "length_scale", "atempo=", "voice-master.flac",
    'MALE_PITCH_FACTOR = 0.84', 'VOICE_PROFILE_REVISION = "3d796cc2f2c884b3517c527507e084f7bb245aea-profile-v1"',
    "ensure_profile_cache(output_root)", '"engine": "piper-vais1000-profiles"', '"event": "cue"', '"event": "block"',
):
    require(marker in tts_worker, f"licensed VAIS TTS worker contract missing {marker}")

main_xaml = read(CSHARP / "src/BiliSubStudio.App/MainWindow.xaml")
main_code = read(CSHARP / "src/BiliSubStudio.App/MainWindow.xaml.cs")
for marker in ("Config.CheckUpdates", "CheckForUpdatesOnLaunchAsync", "ApplyUpdateInfo"):
    require(marker in main_code, f"automatic update-check setting has no startup owner: {marker}")
for marker in ('IsPaneToggleButtonVisible="True"', 'PaneDisplayMode="Left"', 'OpenPaneLength="216"'):
    require(marker in main_xaml, f"stable navigation contract missing {marker}")
for marker in ("RunLayoutSmokeAsync", "layout-smoke-page", "new SizeInt32(800, 600)", "new SizeInt32(1_500, 900)"):
    require(marker in main_code, f"multi-viewport layout smoke contract missing {marker}")
require("await editorPage.RunLayoutSmokeAsync()" in main_code,
        "multi-viewport layout smoke no longer exercises Editor icon rail/action state")

app_xaml = read(CSHARP / "src/BiliSubStudio.App/App.xaml")
require("ResourceDictionary.ThemeDictionaries" in app_xaml and 'x:Key="Light"' in app_xaml and "XamlControlsResources" in app_xaml,
        "WinUI theme/control resources are incomplete")

startup = read(CSHARP / "src/BiliSubStudio.App/Services/StartupDiagnostics.cs")
app_code = read(CSHARP / "src/BiliSubStudio.App/App.xaml.cs")
for marker in ("startup.log", "MessageBoxW", "startup-smoke-test", "WriteSmokeSentinelAsync"):
    require(marker in startup, f"visible startup diagnostic contract missing {marker}")
for marker in ("StartupDiagnostics.Initialize", "ShowFatalError", "MainWindow.Initialization", "RunLayoutSmokeAsync"):
    require(marker in app_code, f"app startup owner missing {marker}")

ledger = read(ROOT / "docs/migration/MIGRATION_LEDGER.md")
require("9be4abd8184d2d7d24159dd736b6accfbe1cda90" in ledger,
        "migration ledger lost the historical baseline identity")

print(f"PASS: C# migration/static contract · version {version}")
