#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CSHARP = ROOT / "csharp"

REQUIRED = [
    ROOT / "global.json",
    ROOT / ".github/workflows/csharp-p5-windows-x64-installer.yml",
    CSHARP / "BiliSubStudio.sln",
    CSHARP / "scripts/compile_app_codebehind_contract.py",
    CSHARP / "scripts/package_windows_candidate.ps1",
    CSHARP / "scripts/build_windows_installer.ps1",
    CSHARP / "scripts/verify.ps1",
    CSHARP / "installer/BiliSubStudio.iss",
    CSHARP / "src/BiliSubStudio.App/Assets/BiliSubStudio.ico",
    CSHARP / "Directory.Build.props",
    CSHARP / "Directory.Packages.props",
    CSHARP / "src/BiliSubStudio.Core/Configuration/AppConfig.cs",
    CSHARP / "src/BiliSubStudio.Core/Configuration/JsonConfigStore.cs",
    CSHARP / "src/BiliSubStudio.Core/Application/SettingsApplicationService.cs",
    CSHARP / "src/BiliSubStudio.App/App.xaml",
    CSHARP / "src/BiliSubStudio.App/MainWindow.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/SettingsPage.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/VideoPage.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/SubtitlePage.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/OcrPage.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/EditorPage.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/HardwarePage.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/AccountPage.xaml",
    CSHARP / "src/BiliSubStudio.App/Pages/SupportPage.xaml",
    CSHARP / "src/BiliSubStudio.Core/Application/BiliSubApplication.cs",
    CSHARP / "src/BiliSubStudio.Core/Video/RangeDownloader.cs",
    CSHARP / "src/BiliSubStudio.Core/Video/VideoDownloadService.cs",
    CSHARP / "src/BiliSubStudio.Core/Ocr/OcrScanner.cs",
    CSHARP / "src/BiliSubStudio.Core/Ocr/OcrCheckpointStore.cs",
    CSHARP / "src/BiliSubStudio.Core/Editor/VideoEditorService.cs",
    CSHARP / "src/BiliSubStudio.Core/Authentication/SessionStore.cs",
    CSHARP / "src/BiliSubStudio.Core/Maintenance/UpdateService.cs",
    CSHARP / "src/BiliSubStudio.Core/Maintenance/BugReportService.cs",
    CSHARP / "tests/BiliSubStudio.Core.ContractTests/Program.cs",
    ROOT / "docs/migration/CSHARP_WINUI3_CALL_MAP.md",
    ROOT / "docs/migration/CSHARP_CODE_MAP.generated.md",
    ROOT / "docs/migration/MIGRATION_LEDGER.md",
    ROOT / "docs/migration/UI_V2_SOURCE_MAP.md",
    ROOT / "docs/migration/VALIDATION_REPORT_CSHARP_P5.md",
    ROOT / "docs/migration/WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md",
]

FORBIDDEN_APP_MARKERS = (
    "http://localhost",
    "https://localhost",
    "WebView2",
    "BiliSubStudioCore.exe",
    '"/api/',
)

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


for required in REQUIRED:
    if not required.is_file():
        fail(f"missing {required.relative_to(ROOT)}")

global_sdk = (ROOT / "global.json").read_text(encoding="utf-8")
if '"version": "10.0.400"' not in global_sdk or '"rollForward": "disable"' not in global_sdk:
    fail("global.json must pin the exact reviewed .NET 10.0.400 SDK")

build_props = (CSHARP / "Directory.Build.props").read_text(encoding="utf-8")
if "4.0.0-beta.12-csharp-p5" not in build_props:
    fail("C# informational version must match the P5 installer checkpoint")

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
    code = code_file.read_text(encoding="utf-8") if code_file.is_file() else ""
    for element in root.iter():
        for attribute, handler in element.attrib.items():
            event_name = attribute.rsplit("}", 1)[-1]
            if event_name in event_attributes and not handler.startswith("{"):
                if not re.search(rf"\b{re.escape(handler)}\s*\(", code):
                    fail(f"XAML event {event_name}={handler} has no handler in {code_file.relative_to(ROOT)}")

packages = (CSHARP / "Directory.Packages.props").read_text(encoding="utf-8")
if 'Include="Microsoft.WindowsAppSDK" Version="2.4.0"' not in packages:
    fail("Windows App SDK must stay pinned to reviewed stable version 2.4.0")

verify_script = (CSHARP / "scripts/verify.ps1").read_text(encoding="utf-8")
package_script = (CSHARP / "scripts/package_windows_candidate.ps1").read_text(encoding="utf-8")
workflow = (ROOT / ".github/workflows/csharp-p5-windows-x64-installer.yml").read_text(encoding="utf-8")
installer_script = (CSHARP / "scripts/build_windows_installer.ps1").read_text(encoding="utf-8")
inno_script = (CSHARP / "installer/BiliSubStudio.iss").read_text(encoding="utf-8")
for marker in (
    "Invoke-Checked",
    "Get-SourceIdentity",
    "Assert-Pe32PlusX64",
    'release_candidate = $false',
    'promotion_allowed = $false',
    "SOURCE_SHA256SUMS.txt",
    "SHA256SUMS.txt",
    "CSharp-P5-WindowsBuildCandidate",
    "4.0.0-beta.12-csharp-p5",
    "WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md",
    "startup-smoke-test",
    "WinUI startup smoke test failed",
    "STARTUP_SMOKE_LOG.txt",
    'winui_startup_smoke = $true',
    "publish is missing compiled WinUI XBF resources",
    "publish is missing the WinUI package resource index",
):
    if marker not in verify_script:
        fail(f"Windows verification gate missing {marker}")
for marker in (
    "Assert-ChecksumFile",
    "SOURCE_SHA256SUMS.txt",
    "sourceInventoryPath",
    "CANDIDATE_GATE_STATUS.json",
    "CANDIDATE_SHA256SUMS.txt",
    'release_candidate = $false',
    'promotion_allowed = $false',
    "CSharp-P5-WindowsBuildCandidate",
    "WINDOWS_FIELD_CHECKLIST_CSHARP_P5.md",
    "build_windows_installer.ps1",
    "primary_user_artifact",
    "INSTALLER_STARTUP_SMOKE_LOG.txt",
    "installer_install_smoke = $true",
):
    if marker not in package_script:
        fail(f"Windows candidate packaging gate missing {marker}")
for marker in (
    "runs-on: windows-2025",
    'dotnet-version: "10.0.400"',
    "./csharp/scripts/verify.ps1",
    "./csharp/scripts/package_windows_candidate.ps1",
    "actions/upload-artifact@v4",
    "C# P5 Windows x64 installer candidate",
    "innosetup-7.0.2-x64.exe",
    "gh release verify-asset",
):
    if marker not in workflow:
        fail(f"Windows CI workflow missing {marker}")
for forbidden in ("softprops/action-gh-release", "google-drive", "drive.google.com"):
    if forbidden in workflow.lower():
        fail(f"Windows CI workflow must not promote automatically: {forbidden}")

for marker in (
    "Assert-Pe32PlusX64",
    "CSharp-P5-WindowsBuildCandidate",
    "BiliSubStudio_Setup_v4.0.0-beta.12-csharp-p5_x64",
    'requires_admin = $false',
    'release_candidate = $false',
    "INSTALLER_GATE_STATUS.json",
    'if ($env:GITHUB_ACTIONS -eq "true")',
    "bilisub-installed-startup-smoke.txt",
    "installed WinUI startup smoke failed",
    "installer_install_smoke = $installerInstallSmoke",
    'install_directory_user_selectable = $true',
    'selected_parent_appends_product_directory = $true',
    '"/DIR=`"$installRoot`""',
    "BiliSub Studio Custom Location\\BiliSub Studio",
    'foreach ($protectedRoot in @("Data", "Tools", "Temp", "Cache", "Downloads"))',
):
    if marker not in installer_script:
        fail(f"one-file installer gate missing {marker}")
for marker in (
    "PrivilegesRequired=lowest",
    "SetupArchitecture=x64",
    "ArchitecturesAllowed=x64compatible",
    "{localappdata}\\Programs\\BiliSub Studio",
    "uninsneveruninstall",
    "BiliSubStudio.exe",
    "DisableProgramGroupPage=yes",
    "DisableDirPage=no",
    "AppendDefaultDirName=yes",
    "AllowNoIcons=no",
    "UsePreviousAppDir=yes",
):
    if marker not in inno_script:
        fail(f"Inno Setup contract missing {marker}")

app_project = (CSHARP / "src/BiliSubStudio.App/BiliSubStudio.App.csproj").read_text(encoding="utf-8")
for marker in (
    "net10.0-windows10.0.26100.0",
    "<UseWinUI>true</UseWinUI>",
    "<WindowsPackageType>None</WindowsPackageType>",
    "<RuntimeIdentifier>win-x64</RuntimeIdentifier>",
    "<ApplicationIcon>Assets\\BiliSubStudio.ico</ApplicationIcon>",
    "<SatelliteResourceLanguages>en-US;vi-VN</SatelliteResourceLanguages>",
    "CopyCompiledXamlResourcesToPublish",
    "$(OutputPath)**\\*.xbf",
    "$(AssemblyName).pri",
):
    if marker not in app_project:
        fail(f"app project missing {marker}")
if 'Link="Assets\\worker.py"' not in app_project or "internal\\ocr\\worker.py" not in app_project:
    fail("app project must package the exact frozen OCR worker asset")

app_sources = "\n".join(path.read_text(encoding="utf-8") for path in (CSHARP / "src/BiliSubStudio.App").rglob("*.*") if path.suffix in {".cs", ".xaml"})
if 'SizeChanged="' in app_sources or re.search(r"\b(?:Page|RootGrid)_SizeChanged\b", app_sources):
    fail("layout must not mutate Grid or NavigationView from SizeChanged; use stable XAML/visual states")
for marker in FORBIDDEN_APP_MARKERS:
    if marker in app_sources:
        fail(f"production C# UI contains forbidden marker {marker}")

core_sources = "\n".join(path.read_text(encoding="utf-8") for path in (CSHARP / "src/BiliSubStudio.Core").rglob("*.cs"))
if "Microsoft.UI" in core_sources or "Windows.Storage" in core_sources:
    fail("BiliSubStudio.Core crossed into UI/WinRT ownership")

config_source = (CSHARP / "src/BiliSubStudio.Core/Configuration/AppConfig.cs").read_text(encoding="utf-8")
actual_fields = tuple(re.findall(r'JsonPropertyName\("([^"]+)"\)', config_source))
if actual_fields != EXPECTED_JSON_FIELDS:
    fail(f"config JSON schema drift: {actual_fields}")

settings_xaml = (CSHARP / "src/BiliSubStudio.App/Pages/SettingsPage.xaml").read_text(encoding="utf-8")
settings_code = (CSHARP / "src/BiliSubStudio.App/Pages/SettingsPage.xaml.cs").read_text(encoding="utf-8")
for marker in ("Cleanup_Click", "ResetTools_Click", "RemoveOcr_Click", "ConfirmAsync"):
    if marker not in settings_xaml + settings_code:
        fail(f"migrated maintenance action lost owner/confirmation: {marker}")
update_source = (CSHARP / "src/BiliSubStudio.Core/Maintenance/UpdateService.cs").read_text(encoding="utf-8")
for marker in ("winui3-portable-zip", "không tải nhầm payload không tương thích", "SHA-256 bản cập nhật không khớp", "BreakawayLauncher", "ValidatePayloadLayout", "PreservedRootDirectories", "0x00004550", "0x8664", "0x020B", "ApplyPayloadTransactionalAsync"):
    if marker not in update_source:
        fail(f"safe WinUI update gate missing {marker}")

video_service = (CSHARP / "src/BiliSubStudio.Core/Video/VideoDownloadService.cs").read_text(encoding="utf-8")
for marker in ('"stable" => 1', '"turbo" => 16', "_ => 8", "CancelJob"):
    if marker not in video_service + core_sources:
        fail(f"download control contract missing {marker}")

ocr_xaml = (CSHARP / "src/BiliSubStudio.App/Pages/OcrPage.xaml").read_text(encoding="utf-8")
ocr_code = (CSHARP / "src/BiliSubStudio.App/Pages/OcrPage.xaml.cs").read_text(encoding="utf-8")
editor_xaml = (CSHARP / "src/BiliSubStudio.App/Pages/EditorPage.xaml").read_text(encoding="utf-8")
editor_code = (CSHARP / "src/BiliSubStudio.App/Pages/EditorPage.xaml.cs").read_text(encoding="utf-8")
for owner, source in (("OCR", ocr_xaml + ocr_code), ("Editor", editor_xaml + editor_code)):
    for marker in ("MediaPlayerElement", 'AreTransportControlsEnabled="True"', "IsFullWindow", "MediaSource.CreateFromStorageFile", "PositionChanged"):
        if marker not in source:
            fail(f"{owner} native playback/fullscreen contract missing {marker}")
    for marker in ("WorkspaceGrid", 'Grid.Column="1"', 'Height="600"'):
        if marker not in source:
            fail(f"{owner} stable desktop layout contract missing {marker}")
for marker in ('SelectionChanged="CueList_SelectionChanged"', "SyncCueSelection", "PlaybackSession.Position"):
    if marker not in ocr_xaml + ocr_code:
        fail(f"OCR cue/timeline synchronization missing {marker}")

range_source = (CSHARP / "src/BiliSubStudio.Core/Video/RangeDownloader.cs").read_text(encoding="utf-8")
for marker in ("ContentRange", "PartialContent", "Oversized Range body", 'DeleteFiles(segmentDirectory, "*.tmp")'):
    if marker not in range_source:
        fail(f"strict Range/resume contract missing {marker}")

subtitle_source = (CSHARP / "src/BiliSubStudio.Core/Subtitle/SubtitleService.cs").read_text(encoding="utf-8")
resolver_source = (CSHARP / "src/BiliSubStudio.Core/Video/YtDlpResolver.cs").read_text(encoding="utf-8")
for marker in ("ParseTimedText", "TryParseTimestamp", '"json" => JsonSerializer.Serialize'):
    if marker not in subtitle_source:
        fail(f"subtitle multi-source normalization missing {marker}")
if '"official:"' not in resolver_source or '"ai:"' not in resolver_source:
    fail("official and AI subtitle tracks are not distinct identities")

composition = (CSHARP / "src/BiliSubStudio.Core/Application/BiliSubApplication.cs").read_text(encoding="utf-8")
for marker in ("PrepareShutdownAsync", "PauseJobAsync", "WindowsProcessContainment", "StartOcrScan", "StartEditor", "StartSubtitle", "StartVideo"):
    if marker not in composition:
        fail(f"application composition root missing {marker}")

worker_client = (CSHARP / "src/BiliSubStudio.Core/Ocr/OcrWorkerClient.cs").read_text(encoding="utf-8")
for marker in ('start.ArgumentList.Add("--model-cache")', 'start.ArgumentList.Add("--device")'):
    if marker not in worker_client:
        fail(f"OCR worker command line lost required argument: {marker}")

containment_source = (CSHARP / "src/BiliSubStudio.Core/Processes/WindowsProcessContainment.cs").read_text(encoding="utf-8")
for marker in ("JobObjectLimitKillOnJobClose", "JobObjectLimitBreakawayOk"):
    if marker not in containment_source:
        fail(f"Windows child-process containment missing {marker}")

main_xaml = (CSHARP / "src/BiliSubStudio.App/MainWindow.xaml").read_text(encoding="utf-8")
main_code = (CSHARP / "src/BiliSubStudio.App/MainWindow.xaml.cs").read_text(encoding="utf-8")
for marker in ("Config.CheckUpdates", "CheckForUpdatesOnLaunchAsync", "ApplyUpdateInfo"):
    if marker not in main_code:
        fail(f"automatic update-check setting has no startup owner: {marker}")
for marker in ('IsPaneToggleButtonVisible="True"', 'PaneDisplayMode="Left"', 'OpenPaneLength="216"'):
    if marker not in main_xaml:
        fail(f"stable navigation contract missing {marker}")
for marker in ("RunLayoutSmokeAsync", "layout-smoke-page", "new SizeInt32(800, 600)", "new SizeInt32(1_500, 900)"):
    if marker not in main_code:
        fail(f"multi-viewport layout smoke contract missing {marker}")
for tag in re.findall(r'Tag="([^"]+)"', main_xaml):
    if f'["{tag}"]' not in main_code:
        fail(f"navigation tag has no native page owner: {tag}")

app_xaml = (CSHARP / "src/BiliSubStudio.App/App.xaml").read_text(encoding="utf-8")
if "ResourceDictionary.ThemeDictionaries" not in app_xaml or 'x:Key="Light"' not in app_xaml:
    fail("theme setting must update real dark/light resource dictionaries")
if "XamlControlsResources" not in app_xaml:
    fail("WinUI app resources must merge XamlControlsResources before constructing controls")

startup_diagnostics = (CSHARP / "src/BiliSubStudio.App/Services/StartupDiagnostics.cs").read_text(encoding="utf-8")
app_code = (CSHARP / "src/BiliSubStudio.App/App.xaml.cs").read_text(encoding="utf-8")
for marker in ("startup.log", "MessageBoxW", "startup-smoke-test", "WriteSmokeSentinelAsync"):
    if marker not in startup_diagnostics:
        fail(f"visible startup diagnostic contract missing {marker}")
for marker in ("StartupDiagnostics.Initialize", "ShowFatalError", "MainWindow.Initialization", "RunLayoutSmokeAsync"):
    if marker not in app_code:
        fail(f"app startup owner missing {marker}")

ledger = (ROOT / "docs/migration/MIGRATION_LEDGER.md").read_text(encoding="utf-8")
if "9be4abd8184d2d7d24159dd736b6accfbe1cda90" not in ledger:
    fail("migration ledger lost exact baseline commit")

print("PASS: C# migration static contract")
