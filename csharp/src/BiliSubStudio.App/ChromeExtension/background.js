const BILIBILI_URL = "https://www.bilibili.com/";
const SESSION_COOKIE_NAMES = [
  "SESSDATA",
  "buvid3",
  "buvid4",
  "b_nut",
  "buvid_fp",
  "buvid_fp_plain",
  "b_lsid",
  "bili_jct",
  "DedeUserID",
  "DedeUserID__ckMd5",
  "sid",
  "bili_ticket",
  "bili_ticket_expires",
  "CURRENT_FNVAL",
  "CURRENT_QUALITY",
  "_uuid",
  "fingerprint",
  "fingerprint3"
];

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "getBilibiliSession") {
    chrome.cookies.getAll({ url: BILIBILI_URL }, (cookies) => {
      if (chrome.runtime.lastError) {
        sendResponse({ ok: false, error: chrome.runtime.lastError.message });
        return;
      }

      const values = new Map();
      for (const cookie of cookies || []) {
        if (!cookie?.value || !SESSION_COOKIE_NAMES.includes(cookie.name) || values.has(cookie.name)) continue;
        values.set(cookie.name, cookie.value);
      }

      const sessdata = values.get("SESSDATA") || "";
      if (!sessdata) {
        sendResponse({ ok: false, needsLogin: true, error: "Chưa đăng nhập Bilibili trong Chrome." });
        return;
      }

      const parts = [];
      for (const name of SESSION_COOKIE_NAMES) {
        const value = values.get(name);
        if (value) parts.push(`${name}=${value}`);
      }
      sendResponse({ ok: true, sessdata, cookies: parts.join("; ") });
    });
    return true;
  }

  if (message?.type === "openLoginTab") {
    chrome.tabs.create({ url: "https://passport.bilibili.com/login", active: true }, () => {
      sendResponse(chrome.runtime.lastError
        ? { ok: false, error: chrome.runtime.lastError.message }
        : { ok: true });
    });
    return true;
  }

  if (message?.type === "closeBridgeTab" && sender.tab?.id != null) {
    chrome.tabs.remove(sender.tab.id, () => {
      sendResponse({ ok: !chrome.runtime.lastError });
    });
    return true;
  }
  return false;
});
