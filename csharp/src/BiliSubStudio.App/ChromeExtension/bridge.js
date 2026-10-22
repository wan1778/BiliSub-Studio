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

  const postSession = async (sessdata) => {
    setStatus("Đã tìm thấy phiên, đang gửi sang BiliSub Studio…");
    const response = await fetch("/session", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ token, sessdata })
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok || !result.ok) {
      throw new Error(result.error || "BiliSub Studio không nhận được phiên.");
    }
    setStatus("Đã đồng bộ. Tab này sẽ tự đóng.");
    await chrome.runtime.sendMessage({ type: "closeBridgeTab" }).catch(() => {});
  };

  try {
    setStatus("Đang đọc phiên Bilibili từ Chrome…");
    let session = await chrome.runtime.sendMessage({ type: "getBilibiliSession" });
    if (session?.ok) {
      await postSession(session.sessdata);
      return;
    }
    if (!session?.needsLogin) {
      await postError(session?.error || "Không đọc được phiên Bilibili từ Chrome.");
      return;
    }

    setStatus("Chưa đăng nhập. Đang mở trang đăng nhập Bilibili trong Chrome…");
    const opened = await chrome.runtime.sendMessage({ type: "openLoginTab" });
    if (!opened?.ok) {
      await postError(opened?.error || "Không mở được trang đăng nhập Bilibili trong Chrome.");
      return;
    }

    for (let attempt = 0; attempt < 140; attempt += 1) {
      await delay(2000);
      session = await chrome.runtime.sendMessage({ type: "getBilibiliSession" });
      if (session?.ok) {
        await postSession(session.sessdata);
        return;
      }
      if (!session?.needsLogin) {
        await postError(session?.error || "Không đọc được phiên Bilibili từ Chrome.");
        return;
      }
    }
    await postError("Hết thời gian chờ đăng nhập Bilibili. Hãy bấm đăng nhập lại.");
  } catch (error) {
    setStatus(error?.message || "Không đồng bộ được phiên Bilibili.");
  }
})();
