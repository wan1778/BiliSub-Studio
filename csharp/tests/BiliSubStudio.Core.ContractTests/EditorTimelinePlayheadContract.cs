using System.Text.Json;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// Timeline playhead (following FilmCraft): it snaps to the media's frame grid
// (snaptime = frame index / fps), it is saved per project and it auto-scrolls the
// timeline when the playhead leaves the viewport, without scrolling continuously while
// the user scrolls by hand.
internal static class EditorTimelinePlayheadContract
{
    public static async Task RunAsync()
    {
        // Frame-grid snapping: snaptime = frame index / fps.
        Require(Close(EditorTimelinePlayhead.SnapToFrame(1.017, 30), 1),
            "30fps: 1.017s must land on frame 30 (1.000s)");
        Require(Close(EditorTimelinePlayhead.SnapToFrame(1.05, 25), 1.04),
            "25fps: 1.05s must land on frame 26 (1.040s)");
        Require(Close(EditorTimelinePlayhead.SnapToFrame(1.999, 30), 1.9666666666666667),
            "the snapped time must stay inside the frame that contains it");
        Require(Close(EditorTimelinePlayhead.SnapToFrame(0, 30), 0), "the playhead must not go before the start");
        Require(Close(EditorTimelinePlayhead.SnapToFrame(-5, 30), 0), "a negative time must clamp to the start");
        Require(Close(EditorTimelinePlayhead.SnapToFrame(1.017, 0), 1),
            "an unknown frame rate must fall back to 30fps");
        Require(Close(EditorTimelinePlayhead.SnapToFrame(double.NaN, 30), 0), "a NaN time must not break the playhead");
        Require(EditorTimelinePlayhead.FrameIndex(1.017, 30) == 30, "the frame index must floor the time");
        Require(EditorTimelinePlayhead.FrameIndex(-1, 30) == 0, "a negative time must stay on frame 0");
        Require(Close(EditorTimelinePlayhead.FrameTime(30, 30), 1), "the frame time must be index / fps");
        Require(Close(EditorTimelinePlayhead.FrameTime(-5, 30), 0), "a negative frame index must clamp to frame 0");

        // A playhead inside the viewport does not scroll the timeline.
        Require(EditorTimelinePlayhead.IsVisible(10, 0, 10, 800), "a playhead inside the viewport is visible");
        Require(!EditorTimelinePlayhead.IsVisible(-1, 0, 10, 800), "a playhead left of the viewport is not visible");
        Require(!EditorTimelinePlayhead.IsVisible(90, 0, 10, 800), "a playhead right of the viewport is not visible");
        Require(!EditorTimelinePlayhead.IsVisible(10, 0, 0, 800), "an invalid zoom must not report a visible playhead");

        Require(EditorTimelinePlayhead.RevealScrollOffset(10, 0, 10, 800, 10000) is null,
            "a visible playhead must not move the strip");
        var revealed = EditorTimelinePlayhead.RevealScrollOffset(100, 0, 10, 800, 10000);
        Require(revealed is not null, "an off-screen playhead must move the strip");
        Require(Close(revealed!.Value, 96),
            "the revealed strip must leave the playhead 5% of the viewport from the left");
        var revealedLeft = EditorTimelinePlayhead.RevealScrollOffset(1, 50, 10, 800, 10000);
        Require(revealedLeft is not null && Close(revealedLeft.Value, 0),
            "a playhead left of the viewport must scroll the strip back to the start");
        var clamped = EditorTimelinePlayhead.RevealScrollOffset(999, 0, 10, 800, 1000);
        Require(clamped is not null && Close(clamped.Value, 200),
            "the revealed strip must stay inside the content");
        Require(EditorTimelinePlayhead.RevealScrollOffset(10, 0, 0, 800, 10000) is null,
            "an invalid zoom must not move the strip");

        // Follow only when the playhead has just changed frame: the user scrolling by hand leaves
        // the playhead still, so the timeline is not dragged along.
        Require(EditorTimelinePlayhead.FollowScrollOffset(10, 10, 0, 10, 800, 10000, 30) is null,
            "an unchanged playhead must not move the strip");
        Require(EditorTimelinePlayhead.FollowScrollOffset(10, 10.01, 0, 10, 800, 10000, 30) is null,
            "a playhead that stayed inside the same frame must not move the strip");
        Require(EditorTimelinePlayhead.FollowScrollOffset(10, 10.5, 0, 10, 800, 10000, 30) is null,
            "a visible playhead must not move the strip");
        var followed = EditorTimelinePlayhead.FollowScrollOffset(10, 100, 0, 10, 800, 10000, 30);
        Require(followed is not null && Close(followed.Value, 96),
            "a playhead that jumped out of the viewport must move the strip into view");
        Require(EditorTimelinePlayhead.FollowScrollOffset(10, 100, 0, 10, 800, 10000, 0) is not null,
            "an unknown frame rate must still detect the jump");

        await ProjectPlayheadRoundTripsAsync();
    }

    private static async Task ProjectPlayheadRoundTripsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-playhead-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            var video = Path.Combine(root, "source.mp4");
            await File.WriteAllBytesAsync(video, [1, 2, 3]);
            var store = new EditorProjectStore(paths);
            var created = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal(0d, created.PlayheadSeconds);

            await store.SaveAsync(created with { PlayheadSeconds = 12.5 }, CancellationToken.None);
            var reopened = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal(12.5, reopened.PlayheadSeconds);

            // An old project without the playhead_seconds field must still open, with the
            // playhead back at 0, and must not be corrupted.
            var projectPath = store.GetProjectPath(video);
            var id = Path.GetFileNameWithoutExtension(projectPath);
            var info = new FileInfo(video);
            var legacy = $$"""
                {
                  "schema": 7,
                  "id": {{JsonSerializer.Serialize(id)}},
                  "name": "legacy",
                  "source": {
                    "path": {{JsonSerializer.Serialize(video)}},
                    "size": {{info.Length}},
                    "last_write_utc_ticks": {{info.LastWriteTimeUtc.Ticks}},
                    "width": 1920,
                    "height": 1080,
                    "duration": 120
                  },
                  "file_name": "legacy.mp4",
                  "regions": [],
                  "subtitle": null,
                  "updated_utc": "2026-01-01T00:00:00+00:00"
                }
                """;
            await File.WriteAllTextAsync(projectPath, legacy);
            var legacyLoaded = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal("legacy", legacyLoaded.Name);
            Equal(0d, legacyLoaded.PlayheadSeconds);

            // A broken value is discarded rather than corrupting the whole project.
            Equal(0d, EditorProjectStore.NormalizePlayhead(double.NaN));
            Equal(0d, EditorProjectStore.NormalizePlayhead(-5));
            Equal(0d, EditorProjectStore.NormalizePlayhead(null));
            Equal(7.5, EditorProjectStore.NormalizePlayhead(7.5));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static bool Close(double left, double right) => Math.Abs(left - right) < .0005;

    private static void Equal(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 1e-9) throw new InvalidOperationException($"expected {expected} but got {actual}");
    }

    private static void Equal(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"expected '{expected}' but got '{actual}'");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
