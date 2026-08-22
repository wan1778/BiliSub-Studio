namespace BiliSubStudio.Core.Ocr;

internal sealed class SubtitleTracker
{
    private readonly double _frameSpan;
    private readonly double _lowConfidence;
    private readonly List<OcrCue> _committed = [];
    private Candidate? _candidate;
    private OcrCue? _active;
    private int _emptyHits;
    private double _emptyStart;

    public SubtitleTracker(double framesPerSecond, double lowConfidence = 0.68)
    {
        _frameSpan = 1 / Math.Max(0.25, framesPerSecond);
        _lowConfidence = Math.Clamp(lowConfidence, 0, 1);
    }

    public bool CanCheckpoint => _candidate is null && _emptyHits == 0;
    public IReadOnlyList<OcrCue> Cues => _committed;
    public OcrCue? Active => _active;

    public void Restore(IEnumerable<OcrCue> cues, OcrCue? active)
    {
        _committed.Clear();
        foreach (var cue in cues)
        {
            if (ChineseSubtitleNormalizer.TryNormalize(cue.Text, out var text)) _committed.Add(cue with { Text = text });
        }
        _active = active is not null && ChineseSubtitleNormalizer.TryNormalize(active.Text, out var activeText)
            ? active with { Text = activeText } : null;
        _candidate = null;
        _emptyHits = 0;
    }

    public void Observe(double at, OcrResult result)
    {
        if (!result.Ok || !result.Detected || string.IsNullOrWhiteSpace(result.Text))
        {
            ObserveEmpty(at);
            return;
        }
        if (!ChineseSubtitleNormalizer.TryNormalize(result.Text, out var text))
        {
            // Foreign-script OCR garbage is inconclusive, never an empty frame.
            return;
        }
        if (_active is not null && Similarity(_active.Text, text) >= 0.80)
        {
            _emptyHits = 0;
            _candidate = null;
            _active = _active with
            {
                End = Math.Max(_active.End, at + _frameSpan),
                Text = result.Confidence > _active.Confidence + 0.035 || text.Length > _active.Text.Length + 2 ? text : _active.Text,
                Confidence = Math.Max(_active.Confidence, result.Confidence),
            };
            return;
        }
        _emptyHits = 0;
        var required = text.EnumerateRunes().Count() <= 1 || result.Confidence < _lowConfidence ? 3 : 2;
        if (_candidate is null || Similarity(_candidate.Text, text) < 0.80)
        {
            _candidate = new Candidate(text, at, at, result.Confidence, 1, required);
            return;
        }
        _candidate = _candidate with
        {
            Last = at,
            Confidence = Math.Max(_candidate.Confidence, result.Confidence),
            Hits = _candidate.Hits + 1,
            Required = Math.Max(_candidate.Required, required),
            Text = result.Confidence > _candidate.Confidence + 0.035 || text.Length > _candidate.Text.Length + 2 ? text : _candidate.Text,
        };
        if (_candidate.Hits >= _candidate.Required) PromoteCandidate();
    }

    public void Finish(double end)
    {
        _candidate = null;
        if (_active is not null) CommitActive(Math.Max(end, _active.Start + _frameSpan));
    }

    private void ObserveEmpty(double at)
    {
        _candidate = null;
        if (_active is null)
        {
            _emptyHits = 0;
            return;
        }
        if (_emptyHits == 0) _emptyStart = at;
        if (++_emptyHits >= 2)
        {
            CommitActive(Math.Max(_emptyStart, _active.Start + _frameSpan));
            _emptyHits = 0;
        }
    }

    private void PromoteCandidate()
    {
        if (_candidate is null) return;
        if (_active is not null) CommitActive(_candidate.Start);
        _active = new OcrCue(_candidate.Start, _candidate.Last + _frameSpan, _candidate.Text, _candidate.Confidence);
        _candidate = null;
    }

    private void CommitActive(double end)
    {
        if (_active is null) return;
        if (ChineseSubtitleNormalizer.TryNormalize(_active.Text, out var text))
        {
            _committed.Add(_active with { Text = text, End = Math.Clamp(end, _active.Start + 0.12, _active.Start + 30) });
        }
        _active = null;
    }

    private static double Similarity(string left, string right)
    {
        if (left == right) return 1;
        if (left.Length == 0 || right.Length == 0) return 0;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            }
            previous = current;
        }
        return 1 - previous[^1] / (double)Math.Max(left.Length, right.Length);
    }

    private sealed record Candidate(string Text, double Start, double Last, double Confidence, int Hits, int Required);
}
