(async () => {
  const meta = document.querySelector('meta[name="bilisub-bridge"]');
  if (!meta) return;

  const token = meta.getAttribute("content") || "";
  const status = document.getElementById("status");
  const setStatus = (text) => {
    if (!status) return;
    status.dataset.done = "1";
    status.textContent = text;
  };

  try {
    setStatus("Đang đọc phiên Bilibili từ Chrome…");
    const session = await chrome.runtime.sendMessage({ type: "getBilibiliSession" });
    if (!session?.ok) {
      const message = session?.error || "Không đọc được phiên Bilibili từ Chrome.";
      setStatus(message);
      await fetch("/error", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token, error: message })
      }).catch(() => {});
      return;
    }

    setStatus("Đã tìm thấy phiên, đang gửi sang BiliSub Studio…");
    const response = await fetch("/session", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ token, sessdata: session.sessdata })
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok || !result.ok) {
      throw new Error(result.error || "BiliSub Studio không nhận được phiên.");
    }

    setStatus("Đã đồng bộ. Tab sẽ tự đóng.");
    await chrome.runtime.sendMessage({ type: "closeBridgeTab" }).catch(() => {});
  } catch (error) {
    setStatus(error?.message || "Không đồng bộ được phiên Bilibili.");
  }
})();
