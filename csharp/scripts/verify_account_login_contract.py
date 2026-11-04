from __future__ import annotations

import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[2]
APP = ROOT / "csharp" / "src" / "BiliSubStudio.App"
CORE = ROOT / "csharp" / "src" / "BiliSubStudio.Core"
SCRIPTS = ROOT / "csharp" / "scripts"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("FAIL account login contract: " + message)


def read(path: pathlib.Path) -> str:
    require(path.is_file(), f"missing file: {path}")
    return path.read_text(encoding="utf-8")


def main() -> int:
    account_xaml = read(APP / "Pages" / "AccountPage.xaml")
    account_code = read(APP / "Pages" / "AccountPage.xaml.cs")
    auth = read(CORE / "Authentication" / "BilibiliAuthService.cs")
    browser_cookie = read(CORE / "Authentication" / "BrowserCookieReader.cs")
    provider = read(CORE / "Authentication" / "BilibiliDeviceCookieProvider.cs")
    store = read(CORE / "Authentication" / "SessionStore.cs")
    playurl = read(CORE / "Video" / "BilibiliPlayurlClient.cs")
    subtitles = read(CORE / "Video" / "BilibiliSubtitleClient.cs")
    app_project = read(APP / "BiliSubStudio.App.csproj")
    verify = read(SCRIPTS / "verify.ps1")

    # Login paths: Bilibili's official QR, importing the session the user
    # already has in a local browser, and pasting a cookie by hand.
    require('Click="Qr_Click"' in account_xaml, "account page must expose the native QR login")
    require('x:Name="CookieBox"' in account_xaml and 'Click="SaveCookie_Click"' in account_xaml,
            "account page must expose the manual cookie fallback")
    for marker in ("ChromeSyncButton", "ChromeExtensionButton", "BrowserLoginButton", "ChromeSync_Click",
                   "ChromeExtension_Click", "BrowserLogin_Click", "SyncChromeAsync"):
        require(marker not in account_xaml and marker not in account_code,
                f"account page still exposes the Chrome handoff path: {marker}")

    # No Chrome bridge, extension or WebView2 window remains in the product.
    def source_files(root: pathlib.Path) -> list[pathlib.Path]:
        return [
            path for path in root.rglob("*")
            if path.is_file() and not ({"bin", "obj"} & set(path.parts))
        ]

    for removed in ("ChromeExtension", "ChromeLoginBridgeServer.cs", "ChromeSessionBridge.cs",
                    "BrowserLoginWindow.xaml", "BrowserLoginWindow.xaml.cs"):
        leftovers = [
            path for path in source_files(APP) + source_files(CORE)
            if removed in path.name or removed in str(path.parent)
        ]
        require(not leftovers, f"removed login surface still present: {removed}")
    require("ChromeExtension" not in app_project, "app project still packages the Chrome extension folder")
    for script in ("verify_chrome_bridge_contract.py", "verify_browser_login_contract.py"):
        require(not (SCRIPTS / script).exists(), f"obsolete contract script still present: {script}")
        require(script not in verify, f"verify.ps1 still runs the obsolete contract: {script}")

    # Both QR and cookie paste must add the device cookie themselves so that
    # Bilibili does not answer 412.
    require("BilibiliDeviceCookieProvider.MergeAsync" in auth,
            "auth service does not merge device cookies into the stored session")
    require(auth.count("BilibiliDeviceCookieProvider.MergeAsync") >= 2,
            "both the QR path and the manual cookie path must merge device cookies")
    require("BilibiliDeviceCookieProvider.MergeAsync" in playurl,
            "playurl API requests do not carry the device fingerprint")
    require("BilibiliDeviceCookieProvider.MergeAsync" in subtitles,
            "subtitle API requests do not carry the device fingerprint")
    require("https://api.bilibili.com/x/frontend/finger/spi" in provider,
            "device cookie provider must read buvid from the official finger endpoint")
    require('parts.Add("buvid3=' in provider and 'parts.Add("buvid4=' in provider,
            "device cookie provider must publish buvid3 and buvid4")
    require("ContainsCookie" in provider, "device cookie provider must not overwrite an existing cookie")
    require("WriteNetscapeFileAsync" in store and "output.Append" in store,
            "the Netscape cookie file writer must stay so yt-dlp and the API clients share one session")

    # The session state must clearly say it expired, never pushing the user back
    # to Chrome.
    require("StatusAsync(validate: true" in account_code, "account page must validate the stored session")
    require("StartQrAsync" in account_code, "account page must be able to start the native QR flow")

    # BUG-LOGIN-LOCK: a QR session must never lock the manual cookie path, and
    # the wait itself must be bounded (Bilibili QR lives ~180s) so the page can
    # never look frozen to a user whose stored cookie has expired.
    require("SetQrSession" in account_code and "SetActionBusy" in account_code,
            "account page must separate a QR session from a single action")
    require("ValidateButton.IsEnabled = !busy;" not in account_code
            and "DeleteButton.IsEnabled = !busy;" not in account_code,
            "a QR session must not disable the validate/delete controls")
    require("deadline = DateTime.UtcNow.AddMinutes(3)" in account_code
            and "poll.Expired || DateTime.UtcNow >= deadline" in account_code,
            "the QR wait must end on expiry or on its own deadline")
    require("SaveCookieButton.IsEnabled = !string.IsNullOrWhiteSpace(CookieBox.Password);" in account_code,
            "the manual cookie button must depend only on having text to save")

    # BROWSER-SESSION-IMPORT: logging in on the browser is enough - the app
    # reads that session out of the browser's own cookie store and runs it
    # through the SAME validate + DPAPI save path (never a bypass, never an
    # extension or an in-app browser window).
    require('x:Name="ImportBrowserCookieButton"' in account_xaml and 'Click="ImportBrowserCookie_Click"' in account_xaml,
            "the account page must expose the browser session import")
    require("BrowserCookieReader.ReadBilibiliSessions()" in account_code
            and "ImportBrowserSessionAsync" in account_code,
            "the browser session import must read the local browser cookie store")
    require("_application.Authentication.SetCookieAsync(session.CookieHeader" in account_code,
            "an imported session must go through the same validation and DPAPI save as a pasted cookie")
    require("AddMinutes(3)" in account_code and "WatchBrowserLoginAsync" in account_code,
            "the after-browser-login watch must be bounded")
    for source in (account_xaml, account_code, browser_cookie):
        require("WebView2" not in source, "no in-app browser host may return")

    require("verify_account_login_contract.py" in verify, "verify.ps1 does not run this contract")

    print("PASS account login: native QR + browser session import + manual cookie paste, no Chrome extension "
          "bridge, no in-app browser host, device cookies merged into every Bilibili call")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
