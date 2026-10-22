const BILIBILI_URL = "https://www.bilibili.com/";

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "getBilibiliSession") {
    chrome.cookies.get({ url: BILIBILI_URL, name: "SESSDATA" }, (cookie) => {
      if (chrome.runtime.lastError) {
        sendResponse({ ok: false, error: chrome.runtime.lastError.message });
      } else if (!cookie?.value) {
        sendResponse({ ok: false, error: "Chưa đăng nhập Bilibili trong Chrome." });
      } else {
        sendResponse({ ok: true, sessdata: cookie.value });
      }
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
