(async () => {
  const meta = document.querySelector('meta[name="bilisub-bridge"]');
  if (!meta) return;

  const token = meta.getAttribute("content") || "";
  const expectedSecret = self.BILISUB_BRIDGE_SECRET || "";
  const secretMeta = document.querySelector('meta[name="bilisub-bridge-secret"]');
  const pageSecret = secretMeta ? secretMeta.getAttribute("content") || "" : "";
  if (!expectedSecret || pageSecret !== expectedSecret) return;

  const status = document.getElementById("status");
  const setStatus = (text) => {
    if (!status) return;
    status.dataset.done = "1";
    status.textContent = text;
  };
  const delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

  const postError = async (message) => {
    setStatus(message);
    await fetch("/error", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ token, error: message })
    }).catch(() => {});
  };

  const postSession = async (session) => {
    setStatus("ÄÃ£ tÃ¬m tháº¥y phiÃªn, Ä‘ang gá»­i sang BiliSub Studioâ€¦");
    const response = await fetch("/session", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ token, sessdata: session.sessdata, cookies: session.cookies || "" })
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok || !result.ok) {
      throw new Error(result.error || "BiliSub Studio khÃ´ng nháº­n Ä‘Æ°á»£c phiÃªn.");
    }
    setStatus("ÄÃ£ Ä‘á»“ng bá»™. Tab nÃ y sáº½ tá»± Ä‘Ã³ng.");
    await chrome.runtime.sendMessage({ type: "closeBridgeTab" }).catch(() => {});
  };

  try {
    setStatus("Äang Ä‘á»c phiÃªn Bilibili tá»« Chromeâ€¦");
    let session = await chrome.runtime.sendMessage({ type: "getBilibiliSession" });
    if (session?.ok) {
      await postSession(session);
      return;
    }
    if (!session?.needsLogin) {
      await postError(session?.error || "KhÃ´ng Ä‘á»c Ä‘Æ°á»£c phiÃªn Bilibili tá»« Chrome.");
      return;
    }

    setStatus("ChÆ°a Ä‘Äƒng nháº­p. Äang má»Ÿ trang Ä‘Äƒng nháº­p Bilibili trong Chromeâ€¦");
    const opened = await chrome.runtime.sendMessage({ type: "openLoginTab" });
    if (!opened?.ok) {
      await postError(opened?.error || "KhÃ´ng má»Ÿ Ä‘Æ°á»£c trang Ä‘Äƒng nháº­p Bilibili trong Chrome.");
      return;
    }

    for (let attempt = 0; attempt < 140; attempt += 1) {
      await delay(2000);
      session = await chrome.runtime.sendMessage({ type: "getBilibiliSession" });
      if (session?.ok) {
        await postSession(session);
        return;
      }
      if (!session?.needsLogin) {
        await postError(session?.error || "KhÃ´ng Ä‘á»c Ä‘Æ°á»£c phiÃªn Bilibili tá»« Chrome.");
        return;
      }
    }
    await postError("Háº¿t thá»i gian chá» Ä‘Äƒng nháº­p Bilibili. HÃ£y báº¥m Ä‘Äƒng nháº­p láº¡i.");
  } catch (error) {
    setStatus(error?.message || "KhÃ´ng Ä‘á»“ng bá»™ Ä‘Æ°á»£c phiÃªn Bilibili.");
  }
})();
