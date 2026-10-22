from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"FAIL: {message}")


server = read("csharp/src/BiliSubStudio.Core/Authentication/ChromeLoginBridgeServer.cs")
bridge = read("csharp/src/BiliSubStudio.App/Services/ChromeSessionBridge.cs")
account_xaml = read("csharp/src/BiliSubStudio.App/Pages/AccountPage.xaml")
account_code = read("csharp/src/BiliSubStudio.App/Pages/AccountPage.xaml.cs")
app_project = read("csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj")
manifest = json.loads(read("csharp/src/BiliSubStudio.App/ChromeExtension/manifest.json"))
background = read("csharp/src/BiliSubStudio.App/ChromeExtension/background.js")
content = read("csharp/src/BiliSubStudio.App/ChromeExtension/bridge.js")
readme = read("csharp/src/BiliSubStudio.App/ChromeExtension/README.txt")
agents = read("AGENTS.md")
call_map = read("docs/migration/CSHARP_WINUI3_CALL_MAP.md")

require("TcpListener(IPAddress.Loopback, 0)" in server,
        "Chrome bridge must use an ephemeral loopback port")
require("127.0.0.1" in server and "localhost" not in server,
        "Chrome bridge must report 127.0.0.1 and must not use a localhost alias")
require("RandomNumberGenerator.GetBytes(32)" in server and "FixedTimeEquals" in server,
        "Chrome bridge token must be random and compared in constant time")
require('"/bilisub-bridge"' in server and '"/session"' in server and '"/error"' in server,
        "Chrome bridge HTTP routes are incomplete")
require('"SESSDATA=" + sessdata' in server and "payload.Cookies" in server,
        "Chrome bridge must accept the full current browser cookie header with SESSDATA fallback")
require("SessionStore.NormalizeCookie" in server and "TryGetCookie" in server,
        "Chrome bridge must normalize and validate browser cookies before import")
require("_importSession" in server and "WaitForSessionAsync" in server,
        "Chrome bridge must expose one validated session import and completion wait")
require("bilisub-bridge-secret" in server and "BuildPage(Token, _pageSecret)" in server,
        "Chrome bridge page must carry the per-install extension secret")
require("Cache-Control: no-store" in server and "X-Content-Type-Options: nosniff" in server,
        "Chrome bridge responses must disable caching and sniffing")
require("<input" not in server and "password" not in server.lower(),
        "Chrome bridge page must not host a login form or collect credentials")
for forbidden in ("Local State", "Network\\\\Cookies", "sqlite", "CryptUnprotectData"):
    require(forbidden.lower() not in server.lower() and forbidden.lower() not in bridge.lower(),
            f"Chrome bridge must not read a browser cookie database: {forbidden}")

require("ChromeLoginBridgeServer" in bridge and "SyncAsync" in bridge,
        "app Chrome bridge must own the one-shot handoff")
require("FindChrome" in bridge and "chrome.exe" in bridge and "--new-tab" in bridge,
        "app Chrome bridge must launch the user's Chrome with one handoff tab")
require("EnsureExtensionDirectory" in bridge and "ChromeExtension" in bridge,
        "app Chrome bridge must stage the user-installed extension")
require("bridge-secret.js" in bridge and "RandomNumberGenerator" in bridge and "ReadExtensionSecret" in bridge,
        "app Chrome bridge must generate and read the per-install extension secret")
require("_application.Authentication.SetCookieAsync" in bridge,
        "Chrome handoff must validate through the existing authentication service")
require("TimeSpan.FromMinutes(5)" in bridge,
        "Chrome login tab must allow enough time for QR/SMS login")
require("SESSDATA=" not in bridge,
        "app Chrome bridge must not construct a SESSDATA value itself")

require("ChromeSyncButton" in account_xaml and 'Click="ChromeSync_Click"' in account_xaml,
        "Account page must expose the Chrome sync button")
require("ChromeExtensionButton" in account_xaml and 'Click="ChromeExtension_Click"' in account_xaml,
        "Account page must expose the extension folder button")
require("ChromeSync_Click" in account_code and "_chromeBridge.SyncAsync" in account_code,
        "Account page must call the Chrome bridge sync owner")
require("BrowserLogin_Click" in account_code and "SyncChromeAsync" in account_code,
        "both account login buttons must use the Chrome tab bridge")
require("new BrowserLoginWindow" not in account_code,
        "Account page must not open an embedded login window")
require("ChromeExtension_Click" in account_code and "_chromeBridge.OpenExtensionFolder" in account_code,
        "Account page must open the staged extension folder")
require("ChromeExtension" in app_project,
        "app project must package the Chrome extension folder")

require(manifest.get("manifest_version") == 3, "BiliSub Bridge must use Manifest V3")
require("cookies" in manifest.get("permissions", []),
        "BiliSub Bridge must request the cookies permission")
require(any("http://127.0.0.1/" in pattern
            for script in manifest.get("content_scripts", [])
            for pattern in script.get("matches", [])),
        "BiliSub Bridge content script must run on the loopback handoff page")
scripts = [name for script in manifest.get("content_scripts", []) for name in script.get("js", [])]
require(scripts.index("bridge-secret.js") < scripts.index("bridge.js"),
        "BiliSub Bridge must load the per-install secret before the bridge script")
require("chrome.cookies.getAll" in background and '"SESSDATA"' in background,
        "BiliSub Bridge must read the Bilibili browser cookie set")
require("SESSION_COOKIE_NAMES" in background and "buvid3" in background and "b_nut" in background,
        "BiliSub Bridge must forward the device cookies required by Bilibili APIs")
require("openLoginTab" in background and "passport.bilibili.com/login" in background,
        "BiliSub Bridge must open the official login page as a Chrome tab")
require("needsLogin" in background and "needsLogin" in content,
        "BiliSub Bridge must distinguish missing login from other errors")
require("/session" in content and "/error" in content and "closeBridgeTab" in content,
        "BiliSub Bridge must post the session, report errors and close its helper tab")
require("BILISUB_BRIDGE_SECRET" in content and "bilisub-bridge-secret" in content,
        "BiliSub Bridge must reject a page without the per-install secret")
require("sessdata" in content and "cookies" in content and "token" in content and "openLoginTab" in content,
        "BiliSub Bridge must post the full cookie header and poll after opening the official login tab")
require("attempt < 140" in content,
        "BiliSub Bridge must wait up to five minutes for login")
require("password" not in (background + content).lower(),
        "BiliSub Bridge must never handle passwords")
require("Load unpacked" in readme and "chrome://extensions" in readme,
        "BiliSub Bridge README must document the one-time unpacked install")

require("ephemeral loopback listener" in agents and "per-install shared secret" in agents and "session cookies" in agents,
        "AGENTS.md must document the scoped ephemeral Chrome bridge exception, session cookies and page secret")
require("ChromeLoginBridgeServer" in call_map and "ephemeral" in call_map and "per-install" in call_map,
        "call map must document the Chrome bridge ownership, ephemeral listener and page secret")

print("PASS: Chrome session bridge contract")
