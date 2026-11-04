using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// The timeline waveform: a peak envelope in 20 ms buckets (silence flat, full
// scale at the top of the column) with a self-describing cache next to the
// voice file. A regenerated voice must never reuse the old peaks and a corrupt
// cache must be ignored so the timeline rebuilds instead of drawing garbage.
internal static class EditorVoiceWaveformContract
{
    public static Task RunAsync()
    {
        // One peak per 20 ms bucket: 40 samples at the 2 kHz decode rate.
        var silence = new byte[2 * 40 * 10];
        var peaks = EditorVoiceWaveform.ComputePeaks(silence, 2000, 20);
        Require(peaks.Length == 10, "the bucket count must match the decoded length");
        Require(peaks.All(peak => peak == 0), "silence must read as a flat line");

        var loud = new byte[2 * 40];
        for (var i = 0; i < 40; i++)
        {
            // short.MinValue (-32768): the loudest sample a voice file can hold.
            loud[i * 2] = 0x00;
            loud[i * 2 + 1] = 0x80;
        }
        var loudPeaks = EditorVoiceWaveform.ComputePeaks(loud, 2000, 20);
        Require(loudPeaks.Length == 1 && loudPeaks[0] == 255,
            "a full-scale sample must reach the top of the waveform column");

        var root = Path.Combine(Path.GetTempPath(), "bilisub-voice-waveform-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var voice = Path.Combine(root, "voice-master-test.flac");
            File.WriteAllBytes(voice, new byte[1234]);
            var cache = EditorVoiceWaveform.CachePathFor(voice);
            Require(string.Equals(Path.GetFullPath(Path.GetDirectoryName(cache)!), Path.GetFullPath(root),
                    StringComparison.OrdinalIgnoreCase),
                "the cache file must live next to the voice file");
            var info = new FileInfo(voice);
            Require(Path.GetFileName(cache) == $"waveform-1234-{info.LastWriteTimeUtc.Ticks}.bin",
                "the cache name must pin the voice file size and write time");

            File.WriteAllBytes(voice, new byte[5678]);
            var regenerated = EditorVoiceWaveform.CachePathFor(voice);
            Require(regenerated != cache,
                "a regenerated voice must never reuse the envelope of the old file");

            var envelope = new EditorVoiceWaveform(0.02, 0.12, [1, 2, 3, 255, 0, 9]);
            File.WriteAllBytes(regenerated, EditorVoiceWaveform.Serialize(envelope));
            var loaded = EditorVoiceWaveform.TryLoadCache(voice);
            Require(loaded is not null, "a valid cache file must load");
            var restored = loaded!;
            Require(restored.BucketSeconds == envelope.BucketSeconds
                    && restored.DurationSeconds == envelope.DurationSeconds
                    && restored.Peaks.SequenceEqual(envelope.Peaks),
                "the envelope must round-trip through the cache byte for byte");

            File.WriteAllBytes(regenerated, "XXXX"u8.ToArray());
            Require(EditorVoiceWaveform.TryLoadCache(voice) is null,
                "a corrupt cache must be rejected so the caller rebuilds it");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
