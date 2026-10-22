from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")

def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"FAIL: {message}")

window_xaml = read("csharp/src/BiliSubStudio.App/BrowserLoginWindow.xaml")
window = read("csharp/src/BiliSubStudio.App/BrowserLoginWindow.xaml.cs")
account_xaml = read("csharp/src/BiliSubStudio.App/Pages/AccountPage.xaml")
account = read("csharp/src/BiliSubStudio.App/Pages/AccountPage.xaml.cs")
app_project = read("csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj")

require("WebView2" in window_xaml and 'x:Name="Browser"' in window_xaml,
        "browser login window must host the official Bilibili page in a WebView2")
require("<Button" not in window_xaml and "Lưu phiên đăng nhập" not in window_xaml and "StatusText" not in window_xaml,
        "browser login window must not expose a custom login or manual-capture UI")
require("passport.bilibili.com/login" in window, "browser login must open the real Bilibili passport page")
require("EnsureCoreWebView2Async" in window and "GetCookiesAsync" in window,
        "browser login must use the embedded WebView2 cookie manager")
require("WEBVIEW2_USER_DATA_FOLDER" in window and "DeleteProfileAsync" in window,
        "browser login must isolate and clean its WebView2 profile")
require("args.Handled = true" in window, "login helper windows must stay inside the embedded tab")
require('"SESSDATA=" + session.Value' in window and 'string.Equals(cookie.Name, "SESSDATA"' in window,
        "browser login must persist only SESSDATA, the proven-good session shape")
require("_application.Authentication.SetCookieAsync" in window,
        "browser login must validate through the existing authentication service")
require("_pollTimer" in window and "NavigationCompleted" in window and "TryCaptureAsync" in window,
        "browser login must capture the session automatically after navigation and while polling")
require("Đăng nhập bằng trình duyệt" in account_xaml and "BrowserLogin_Click" in account_xaml,
        "account page must expose the browser login entry point")
require("SyncChromeAsync" in account and "_chromeBridge.SyncAsync" in account and "RefreshStatusAsync" in account,
        "both account login entry points must route through the Chrome tab bridge and refresh status")
require("new BrowserLoginWindow" not in account,
        "account page must not open an embedded login window")
require("Microsoft.Web.WebView2" not in app_project,
        "WebView2 must stay the WindowsAppSDK transitive dependency, not a second pinned package")
require("CookieBox.Password" not in window, "browser login must never read or log cookie values from the account UI")
