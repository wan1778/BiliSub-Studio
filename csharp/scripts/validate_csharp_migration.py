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
    code = read(code_file) if code_file.is_file() else ""
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
for marker in ("PrepareShutdownAsync", "PauseJobAsync", "WindowsProcessContainment", "StartOcrScan", "StartEditor", "StartSubtitle", "StartVideo"):
    require(marker in composition, f"application composition root missing {marker}")

worker_client = read(CSHARP / "src/BiliSubStudio.Core/Ocr/OcrWorkerClient.cs")
for marker in ('start.ArgumentList.Add("--model-cache")', 'start.ArgumentList.Add("--device")'):
    require(marker in worker_client, f"OCR worker command line lost required argument: {marker}")

containment_source = read(CSHARP / "src/BiliSubStudio.Core/Processes/WindowsProcessContainment.cs")
for marker in ("JobObjectLimitKillOnJobClose", "JobObjectLimitBreakawayOk"):
    require(marker in containment_source, f"Windows child-process containment missing {marker}")

ocr = read(CSHARP / "src/BiliSubStudio.App/Pages/OcrPage.xaml") + read(CSHARP / "src/BiliSubStudio.App/Pages/OcrPage.xaml.cs")
editor = read(CSHARP / "src/BiliSubStudio.App/Pages/EditorPage.xaml") + read(CSHARP / "src/BiliSubStudio.App/Pages/EditorPage.xaml.cs")
for owner, source in (("OCR", ocr), ("Editor", editor)):
    for marker in ("MediaPlayerElement", 'AreTransportControlsEnabled="True"', "IsFullWindow", "MediaSource.CreateFromStorageFile", "PositionChanged"):
        require(marker in source, f"{owner} native playback/fullscreen contract missing {marker}")
for marker in (
    "SubtitleModeButton", "BlurModeButton", "AudioModeButton", "ExportModeButton",
    "SubtitleInspectorPanel", "BlurInspectorPanel", "AudioInspectorPanel", "ExportInspectorPanel",
    "RunLayoutSmokeAsync", "ImportSrtButton.IsEnabled = idle;", "PrepareAiButton.IsEnabled = idle;",
    "_inspectorMode == InspectorMode.Blur", "_inspectorMode == InspectorMode.Subtitle",
):
    require(marker in editor, f"Editor icon-mode/action-state contract missing {marker}")
require("ImportSrtButton.IsEnabled = idle && hasMedia" not in editor,
        "Editor SRT picker regressed to requiring a selected video")
require("PrepareAiButton.IsEnabled = idle && hasMedia" not in editor,
        "Editor AI preparation regressed to requiring a selected video")

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
