from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"FAIL: {message}")


main_xaml = read("csharp/src/BiliSubStudio.App/MainWindow.xaml")
main_code = read("csharp/src/BiliSubStudio.App/MainWindow.xaml.cs")
settings_xaml = read("csharp/src/BiliSubStudio.App/Pages/SettingsPage.xaml")
settings_code = read("csharp/src/BiliSubStudio.App/Pages/SettingsPage.xaml.cs")
media_xaml = read("csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml")
media_code = read("csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml.cs")
log_code = read("csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs")
job_code = read("csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs")
manager_code = read("csharp/src/BiliSubStudio.Core/Jobs/JobManager.cs")
support_code = read("csharp/src/BiliSubStudio.App/Pages/SupportPage.xaml.cs")

nav = re.findall(r'<NavigationViewItem\s+Content="([^"]+)"\s+Tag="([^"]+)"\s*/>', main_xaml)
require(nav == [
    ("Tải media", "video"),
    ("OCR phụ đề", "ocr"),
    ("Chỉnh video", "editor"),
    ("Cài đặt", "settings"),
], f"production navigation drifted: {nav}")
for forbidden in ("Hiệu năng", "Đăng nhập", "Cập nhật &amp; hỗ trợ"):
    require(f'<NavigationViewItem Content="{forbidden}"' not in main_xaml,
            f"{forbidden} must not be a top-level navigation item")

for marker in (
    'x:Name="GlobalLogPanel"',
    'x:Name="GlobalLogList"',
    'Content="Nhật ký toàn ứng dụng"',
    'Text="Bình thường"',
    'Text="Cảnh báo"',
    'Text="Lỗi"',
    'SuccessSoftBrush',
    'WarningSoftBrush',
    'DangerSoftBrush',
    'Click="OpenLogFile_Click"',
    'Click="ClearLogView_Click"',
):
    require(marker in main_xaml, f"shared log shell missing {marker}")

for marker in (
    'new ApplicationLog(paths.Data)',
    '_application.Jobs.AttachLog(_globalLog)',
    'if (entry.Level == AppLogLevel.Error)',
    'ShowGlobalLog(true)',
    'await _settingsPage.RunLayoutSmokeAsync()',
):
    require(marker in main_code, f"shared log/layout behavior missing {marker}")

for marker in (
    'Path.Combine(Path.GetFullPath(dataDirectory), "Logs")',
    'Path.Combine(directory, "application.log")',
    'MaxMemoryEntries = 2_000',
    'MaxLogBytes = 5L * 1024 * 1024',
    'AppLogLevel.Info',
    'AppLogLevel.Warning',
    'AppLogLevel.Error',
):
    require(marker in log_code, f"persistent application log contract missing {marker}")

for marker in (
    'level == AppLogLevel.Info',
    'message.Contains("cảnh báo", StringComparison.OrdinalIgnoreCase)',
    'message.Contains("bỏ qua", StringComparison.OrdinalIgnoreCase)',
    'message.Contains("thất bại; chuyển yt-dlp fallback", StringComparison.OrdinalIgnoreCase)',
    'level = AppLogLevel.Warning',
):
    require(marker in log_code, f"recoverable warning auto-classification missing {marker}")

require('ApplicationLog? applicationLog = null' in job_code, "AppJob must accept the shared log owner")
require('public void Warn(string message)' in job_code, "AppJob warning level is missing")
require('public void Error(string message)' in job_code, "AppJob error level is missing")
require('public void AttachLog(ApplicationLog applicationLog)' in manager_code, "JobManager cannot attach the shell log")

for section, label in (
    ("general", "Chung"),
    ("hardware", "Hiệu năng"),
    ("account", "Đăng nhập"),
    ("support", "Cập nhật &amp; hỗ trợ"),
):
    require(f'Tag="{section}"' in settings_xaml and f'Content="{label}"' in settings_xaml,
            f"Settings section missing {label}")
require('SectionHost.Content = section switch' in settings_code, "Settings must host embedded operational sections")
require('new[] { "general", "hardware", "account", "support" }' in settings_code,
        "Settings layout smoke must cover all embedded sections")

require('x:Name="MainColumns"' in media_xaml, "Media must use the horizontal main-column composition")
require('<ColumnDefinition Width="1.35*" />' in media_xaml and '<ColumnDefinition Width="0.95*" />' in media_xaml,
        "Media horizontal space allocation drifted")
require('x:Name="LogBox"' not in media_xaml, "Media must not restore a page-local log box")
require('Nhật ký đã gộp' in media_xaml, "Media must explain that diagnostics moved to the shared log")
require('GetSnapshot(_jobId, int.MaxValue)' in media_code, "Media must not re-render job log lines locally")

require('["application"] = string.Join' in support_code and '_log.Snapshot().TakeLast(500)' in support_code,
        "Bug reports must include the sanitized shared application log")

print("PASS: four-item shell / embedded settings / shared persistent colored log / compact media contracts")
