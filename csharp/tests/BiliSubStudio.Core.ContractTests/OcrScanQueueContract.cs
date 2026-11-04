using BiliSubStudio.Core.Ocr;

namespace BiliSubStudio.Core.ContractTests;

// OCR queue (09/10: scan several videos back to back, each with its own setup,
// an error is skipped and scanning continues, it lives only for the app session). Contract: exact FIFO
// ordering, one scan at a time, a Paused item is resumed first, a Failed item
// does not block the queue, only Waiting items can move up/down/be removed, a request keeps its own per-video setup.
internal static class OcrScanQueueContract
{
    public static Task RunAsync()
    {
        var regionA = new OcrRegion(0.05, 0.65, 0.90, 0.29);
        var regionB = new OcrRegion(0.10, 0.70, 0.80, 0.25);

        // 1. FIFO in add order + queue numbers 1..N.
        var queue = new OcrScanQueue();
        var first = queue.Enqueue("a.mp4", regionA, "accurate", "gpu", "8", 100);
        var second = queue.Enqueue("b.mp4", regionB, "fast", "cpu", "4", 200);
        var third = queue.Enqueue("c.mp4", regionA, "balanced", "auto", "auto", 300);
        Check(queue.WaitingCount == 3, "enqueue 3 must report 3 waiting");
        Check(first.QueueNumber == 1 && second.QueueNumber == 2 && third.QueueNumber == 3,
            "enqueue must number items 1..N in order");
        Check(ReferenceEquals(queue.TakeNext(), first), "TakeNext must return first Waiting (FIFO)");
        Check(first.Status == OcrQueueItemStatus.Scanning, "taken item must be Scanning");

        // 2. While something is Scanning, TakeNext returns null (one scan at a time).
        Check(queue.TakeNext() is null, "TakeNext must refuse while one item is Scanning");
        Check(second.Status == OcrQueueItemStatus.Waiting, "second item must stay Waiting");

        // 3. Done leaves the queue at once, the rest renumber; Failed does not block the queue.
        queue.MarkDone(first.Id, "a_Chinese.srt", "xong");
        Check(queue.Items.Count == 2, "done item must leave the queue immediately");
        Check(second.QueueNumber == 1 && third.QueueNumber == 2, "remaining items must renumber after one leaves");
        Check(ReferenceEquals(queue.TakeNext(), second), "after Done, TakeNext must advance to second");
        queue.MarkFailed(second.Id, "probe hỏng");
        Check(second.Status == OcrQueueItemStatus.Failed, "failed item must be Failed");
        Check(ReferenceEquals(queue.TakeNext(), third), "after Failed, TakeNext must skip to third (lỗi bỏ qua, chạy tiếp)");

        // 4. Paused is resumed before freshly Waiting items.
        queue.MarkPaused(third.Id);
        var fourth = queue.Enqueue("d.mp4", regionA, "fast", "cpu", "2", 400);
        Check(ReferenceEquals(queue.TakeNext(), third), "TakeNext must resume Paused item before new Waiting items");
        queue.MarkCancelled(third.Id, "user hủy");
        Check(third.Status == OcrQueueItemStatus.Cancelled, "cancelled item must be Cancelled");

        // 5. The request keeps its own per-video setup.
        var request = fourth.ToRequest();
        Check(request.Path == "d.mp4" && request.Region.Equals(regionA)
            && request.Mode == "fast" && request.Device == "cpu" && request.Parallelism == "2"
            && request.Duration == 400 && request.Sensitivity == 1,
            "ToRequest must carry the per-video setup");

        // 6. Only Waiting items can move; a Scanning item cannot be removed.
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

        // 7. ClearFinished only clears Failed/Cancelled (Done already left itself), numbers reset.
        Check(queue.ClearFinished() == 2, "ClearFinished must remove 2 failed/cancelled items");
        Check(queue.WaitingCount == 1 && ReferenceEquals(queue.Items[0], fourth)
            && fourth.QueueNumber == 1, "only the Waiting item must remain, renumbered to 1");

        // 8. Stuck ghost (15/10: an item still Scanning but its job died, the whole queue sits
        // silently frozen): RequeueStuck returns it to Paused so TakeNext can resume it.
        var queue3 = new OcrScanQueue();
        var s1 = queue3.Enqueue("s1.mp4", regionA, "balanced", "gpu", "8", 100);
        var s2 = queue3.Enqueue("s2.mp4", regionA, "balanced", "gpu", "8", 200);
        Check(ReferenceEquals(queue3.TakeNext(), s1), "stuck-fixture must take s1 first");
        Check(queue3.TakeNext() is null, "TakeNext must refuse while s1 is Scanning");
        Check(!queue3.RequeueStuck(s2.Id), "RequeueStuck must refuse a non-Scanning item");
        Check(!queue3.RequeueStuck(Guid.NewGuid()), "RequeueStuck must refuse an unknown id");
        Check(queue3.RequeueStuck(s1.Id), "RequeueStuck must park the stuck Scanning item as Paused");
        Check(s1.Status == OcrQueueItemStatus.Paused, "requeued stuck item must be Paused");
        Check(ReferenceEquals(queue3.TakeNext(), s1), "TakeNext must resume the requeued item before s2");

        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("OcrScanQueueContract: " + message);
    }
}
