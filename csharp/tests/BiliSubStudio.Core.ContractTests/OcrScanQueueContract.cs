using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// Hàng chờ OCR (09/10: quét nhiều video nối tiếp, mỗi video setup riêng,
// lỗi bỏ qua chạy tiếp, chỉ sống trong phiên app). Hợp đồng: FIFO đúng thứ
// tự, một scan tại một thời điểm, mục Paused được resume trước, mục Failed
// không chặn hàng, chỉ Waiting mới lên/xuống/xóa, request giữ nguyên setup
// riêng từng video.
internal static class OcrScanQueueContract
{
    public static Task RunAsync()
    {
        var regionA = new OcrRegion(0.05, 0.65, 0.90, 0.29);
        var regionB = new OcrRegion(0.10, 0.70, 0.80, 0.25);

        // 1. FIFO đúng thứ tự thêm.
        var queue = new OcrScanQueue();
        var first = queue.Enqueue("a.mp4", regionA, "accurate", "gpu", "8", 100);
        var second = queue.Enqueue("b.mp4", regionB, "fast", "cpu", "4", 200);
        var third = queue.Enqueue("c.mp4", regionA, "balanced", "auto", "auto", 300);
        Check(queue.WaitingCount == 3, "enqueue 3 must report 3 waiting");
        Check(ReferenceEquals(queue.TakeNext(), first), "TakeNext must return first Waiting (FIFO)");
        Check(first.Status == OcrQueueItemStatus.Scanning, "taken item must be Scanning");

        // 2. Đang có Scanning thì TakeNext trả null (một scan tại một thời điểm).
        Check(queue.TakeNext() is null, "TakeNext must refuse while one item is Scanning");
        Check(second.Status == OcrQueueItemStatus.Waiting, "second item must stay Waiting");

        // 3. Done xong mới lấy tiếp; Failed không chặn hàng.
        queue.MarkDone(first.Id, "a_Chinese.srt", "xong");
        Check(first.Status == OcrQueueItemStatus.Done && first.Progress == 100, "done item must be Done at 100%");
        Check(ReferenceEquals(queue.TakeNext(), second), "after Done, TakeNext must advance to second");
        queue.MarkFailed(second.Id, "probe hỏng");
        Check(second.Status == OcrQueueItemStatus.Failed, "failed item must be Failed");
        Check(ReferenceEquals(queue.TakeNext(), third), "after Failed, TakeNext must skip to third (lỗi bỏ qua, chạy tiếp)");

        // 4. Paused được resume trước Waiting mới.
        queue.MarkPaused(third.Id);
        var fourth = queue.Enqueue("d.mp4", regionA, "fast", "cpu", "2", 400);
        Check(ReferenceEquals(queue.TakeNext(), third), "TakeNext must resume Paused item before new Waiting items");
        queue.MarkCancelled(third.Id, "user hủy");
        Check(third.Status == OcrQueueItemStatus.Cancelled, "cancelled item must be Cancelled");

        // 5. Request giữ setup riêng từng video.
        var request = fourth.ToRequest();
        Check(request.Path == "d.mp4" && request.Region.Equals(regionA)
            && request.Mode == "fast" && request.Device == "cpu" && request.Parallelism == "2"
            && request.Duration == 400 && request.Sensitivity == 1,
            "ToRequest must carry the per-video setup");

        // 6. Chỉ Waiting mới lên/xuống; Scanning không xóa được.
        var queue2 = new OcrScanQueue();
        var w1 = queue2.Enqueue("w1.mp4", regionA, "fast", "cpu", "2", 10);
        var w2 = queue2.Enqueue("w2.mp4", regionA, "fast", "cpu", "2", 10);
        Check(queue2.MoveDown(w1.Id), "MoveDown must swap two Waiting items");
        Check(ReferenceEquals(queue2.Items[0], w2), "after MoveDown, w2 must be first");
        Check(queue2.MoveUp(w1.Id), "MoveUp must swap back");
        Check(ReferenceEquals(queue2.Items[0], w1), "after MoveUp, w1 must be first again");
        Check(!queue2.MoveUp(w1.Id), "MoveUp at head must fail");
        queue2.TakeNext();
        Check(!queue2.Remove(w1.Id), "must not remove the Scanning item");
        Check(queue2.Remove(w2.Id), "must remove a Waiting item");

        // 7. ClearFinished chỉ dọn Done/Failed/Cancelled.
        Check(queue.ClearFinished() == 3, "ClearFinished must remove 3 finished items");
        Check(queue.WaitingCount == 1 && ReferenceEquals(queue.Items[0], fourth), "only the Waiting item must remain");

        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("OcrScanQueueContract: " + message);
    }
}
