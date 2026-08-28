namespace BiliSubStudio.Core.Ocr;

internal sealed class SubtitleTracker
{
    private readonly double _frameSpan;
    private readonly double _candidateGap;
    private readonly double _lowConfidence;
    private readonly bool _exactFrameTiming;
    private readonly List<OcrCue> _committed = [];
    private Candidate? _candidate;
    private SubtextVariant? _subtextVariant;
    private OcrCue? _active;
    private string? _longerVariantText;
    private int _longerVariantHits;
    private double _longerVariantLast;
    private int _emptyHits;
    private double _emptyStart;
    private double _lastFrameDuration;

    public SubtitleTracker(double framesPerSecond, double lowConfidence = 0.68)
        : this(framesPerSecond, lowConfidence, exactFrameTiming: false)
    {
    }

    internal SubtitleTracker(double framesPerSecond, double lowConfidence, bool exactFrameTiming)
    {
        _frameSpan = 1 / Math.Max(0.25, framesPerSecond);
        _candidateGap = Math.Max(0.75, _frameSpan * 2.5);
        _lowConfidence = Math.Clamp(lowConfidence, 0, 1);
        _exactFrameTiming = exactFrameTiming;
        _lastFrameDuration = _frameSpan;
    }

    public bool CanCheckpoint => _candidate is null && _subtextVariant is null && _longerVariantHits == 0 && _emptyHits == 0;
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
        _subtextVariant = null;
        _longerVariantText = null;
        _longerVariantHits = 0;
        _longerVariantLast = 0;
        _emptyHits = 0;
    }

    public void Observe(double at, OcrResult result) => Observe(at, _frameSpan, result);

    public void Observe(double at, double frameDuration, OcrResult result)
    {
        frameDuration = NormalizeFrameDuration(frameDuration);
        _lastFrameDuration = frameDuration;
        if (!result.Ok || !result.Detected || string.IsNullOrWhiteSpace(result.Text))
        {
            ObserveEmpty(at, frameDuration);
            return;
        }
        if (!ChineseSubtitleNormalizer.TryNormalize(result.Text, out var text))
        {
            // Foreign-script OCR garbage is inconclusive for an already active subtitle,
            // but it must break an unconfirmed candidate so distant hits cannot combine.
            _candidate = null;
            _longerVariantText = null;
            _longerVariantHits = 0;
            _longerVariantLast = 0;
            return;
        }
        if (_active is not null && _exactFrameTiming && IsStrictSubtext(_active.Text, text))
        {
            _longerVariantText = null;
            _longerVariantHits = 0;
            _longerVariantLast = 0;
            ObserveSubtextVariant(at, frameDuration, text, result.Confidence);
            return;
        }
        if (_active is not null && _subtextVariant is not null)
        {
            if (!IsContinuousVariant(_active.Text, text))
            {
                CommitActive(_subtextVariant.Start);
                _subtextVariant = null;
            }
            else
            {
                _subtextVariant = null;
            }
        }
        if (_active is not null && IsContinuousVariant(_active.Text, text))
        {
            _emptyHits = 0;
            _candidate = null;
            var currentText = _active.Text;
            var resolvedText = PreferText(currentText, _active.Confidence, text, result.Confidence);
            if (resolvedText == currentText
                && text.EnumerateRunes().Count() > currentText.EnumerateRunes().Count()
                && result.Confidence >= Math.Max(.45, _lowConfidence - .20)
                && text.Contains(currentText, StringComparison.Ordinal))
            {
                if (string.Equals(_longerVariantText, text, StringComparison.Ordinal)
                    && at - _longerVariantLast <= _candidateGap)
                {
                    _longerVariantHits++;
                }
                else
                {
                    _longerVariantText = text;
                    _longerVariantHits = 1;
                }
                _longerVariantLast = at;
                // A single longer read can be a hallucinated edge glyph. Two
                // matching compatible reads inside the same short evidence window
                // recover a glyph even when Paddle alternates full/short/full.
                if (_longerVariantHits >= 2)
                {
                    resolvedText = _longerVariantText ?? text;
                    _longerVariantText = null;
                    _longerVariantHits = 0;
                    _longerVariantLast = 0;
                }
            }
            else if (_longerVariantHits > 0
                && string.Equals(text, currentText, StringComparison.Ordinal)
                && at - _longerVariantLast <= _candidateGap)
            {
                // Keep recent fuller-text evidence across an intermittent short
                // base reading. The evidence still expires after _candidateGap.
            }
            else
            {
                _longerVariantText = null;
                _longerVariantHits = 0;
                _longerVariantLast = 0;
            }
            _active = _active with
            {
                End = Math.Max(_active.End, at + ActiveFrameEnd(frameDuration)),
                Text = resolvedText,
                Confidence = Math.Max(_active.Confidence, result.Confidence),
            };
            return;
        }
        _longerVariantText = null;
        _longerVariantHits = 0;
        _longerVariantLast = 0;
        _emptyHits = 0;
        var required = result.Confidence < _lowConfidence || (_exactFrameTiming && text.EnumerateRunes().Count() <= 1)
            ? 3
            : 2;
        if (_candidate is null || at - _candidate.Last > _candidateGap || Similarity(_candidate.Text, text) < 0.80)
        {
            _candidate = new Candidate(text, EstimateBoundary(at, frameDuration), at, result.Confidence, 1, required);
            return;
        }
        _candidate = _candidate with
        {
            Last = at,
            Confidence = Math.Max(_candidate.Confidence, result.Confidence),
            Hits = _candidate.Hits + 1,
            Required = Math.Max(_candidate.Required, required),
            Text = PreferText(_candidate.Text, _candidate.Confidence, text, result.Confidence),
        };
        if (_candidate.Hits >= _candidate.Required) PromoteCandidate(frameDuration);
    }

    public void Finish(double end)
    {
        _candidate = null;
        if (_active is not null)
        {
            var activeEnd = _subtextVariant?.Start ?? end;
            CommitActive(Math.Max(activeEnd, _active.Start + MinimumCueDuration(_lastFrameDuration)));
        }
        _subtextVariant = null;
        _longerVariantText = null;
        _longerVariantHits = 0;
        _longerVariantLast = 0;
    }

    private void ObserveEmpty(double at, double frameDuration)
    {
        _candidate = null;
        _longerVariantText = null;
        _longerVariantHits = 0;
        _longerVariantLast = 0;
        if (_active is null)
        {
            _emptyHits = 0;
            return;
        }
        if (_emptyHits == 0) _emptyStart = at;
        if (++_emptyHits >= 2)
        {
            var end = _subtextVariant?.Start ?? EstimateBoundary(_emptyStart, frameDuration);
            CommitActive(Math.Max(end, _active.Start + MinimumCueDuration(frameDuration)));
            _subtextVariant = null;
            _emptyHits = 0;
        }
    }

    private void ObserveSubtextVariant(double at, double frameDuration, string text, double confidence)
    {
        if (_active is null) return;
        if (_subtextVariant is null || !IsContinuousVariant(_subtextVariant.Text, text))
        {
            _subtextVariant = new SubtextVariant(text, at, at, confidence);
            return;
        }
        _subtextVariant = _subtextVariant with
        {
            Last = at,
            Text = PreferText(_subtextVariant.Text, _subtextVariant.Confidence, text, confidence),
            Confidence = Math.Max(_subtextVariant.Confidence, confidence),
        };
        // A short suffix can be a fade/reveal of the active cue. If it remains
        // on screen for this long, it is a real repeated subtitle (for example
        // "天天被幸福包围" followed by "幸福") and must become its own cue.
        if (at + ActiveFrameEnd(frameDuration) - _subtextVariant.Start < .75) return;
        var variant = _subtextVariant;
        CommitActive(variant.Start);
        _active = new OcrCue(variant.Start, at + ActiveFrameEnd(frameDuration), variant.Text, variant.Confidence);
        _subtextVariant = null;
        _emptyHits = 0;
    }

    private void PromoteCandidate(double frameDuration)
    {
        if (_candidate is null) return;
        if (_active is not null) CommitActive(_candidate.Start);
        _active = new OcrCue(_candidate.Start, _candidate.Last + ActiveFrameEnd(frameDuration), _candidate.Text, _candidate.Confidence);
        _candidate = null;
    }

    private void CommitActive(double end)
    {
        if (_active is null) return;
        if (ChineseSubtitleNormalizer.TryNormalize(_active.Text, out var text))
        {
            _committed.Add(_active with
            {
                Text = text,
                End = Math.Clamp(end, _active.Start + MinimumCueDuration(_lastFrameDuration), _active.Start + 30),
            });
        }
        _active = null;
        _longerVariantText = null;
        _longerVariantHits = 0;
        _longerVariantLast = 0;
    }

    private double EstimateBoundary(double sampledAt, double frameDuration) => _exactFrameTiming
        ? Math.Max(0, sampledAt)
        : Math.Max(0, sampledAt - _frameSpan / 2);

    private double ActiveFrameEnd(double frameDuration) => _exactFrameTiming ? frameDuration : _frameSpan / 2;

    private double MinimumCueDuration(double frameDuration) => _exactFrameTiming
        ? Math.Max(0.001, frameDuration)
        : _frameSpan;

    private double NormalizeFrameDuration(double value) => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value)
        ? value
        : _frameSpan;

    private static string PreferText(string current, double currentConfidence, string candidate, double candidateConfidence)
    {
        if (candidateConfidence > currentConfidence + 0.035) return candidate;
        // Paddle can recover a trailing/leading CJK glyph on the next frame while
        // reporting nearly the same line confidence. Do not require a two-character
        // gain: that was the source of stable one-character truncation.
        if (candidate.EnumerateRunes().Count() > current.EnumerateRunes().Count()
            && candidateConfidence >= currentConfidence - 0.08) return candidate;
        return current;
    }

    private static double Similarity(string left, string right)
    {
        if (left == right) return 1;
        if (left.Length == 0 || right.Length == 0) return 0;
        var leftRunes = left.EnumerateRunes().ToArray();
        var rightRunes = right.EnumerateRunes().ToArray();
        var previous = Enumerable.Range(0, rightRunes.Length + 1).ToArray();
        for (var i = 1; i <= leftRunes.Length; i++)
        {
            var current = new int[rightRunes.Length + 1];
            current[0] = i;
            for (var j = 1; j <= rightRunes.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (SameTrackingRune(leftRunes[i - 1].Value, rightRunes[j - 1].Value) ? 0 : 1));
            }
            previous = current;
        }
        return 1 - previous[^1] / (double)Math.Max(leftRunes.Length, rightRunes.Length);
    }

    // Paddle can alternate between simplified and traditional glyphs on adjacent
    // video frames. This is a tracking equivalence only: the cue keeps the best
    // observed spelling and SRT text is not rewritten by this map.
    private static bool SameTrackingRune(int left, int right) =>
        left == right || CanonicalTrackingRune(left) == CanonicalTrackingRune(right);

    private static int CanonicalTrackingRune(int value) => value switch
    {
        // Paddle can alternate between ASCII and Chinese punctuation across
        // adjacent frames of the same caption. This is visual/OCR variance,
        // not a new subtitle, and must not create a one-frame cue split.
        '?' => '？', '!' => '！', ',' => '，', '.' => '。',
        ';' => '；', ':' => '：',
        '別' => '别', '長' => '长', '萬' => '万', '師' => '师', '這' => '这',
        '為' => '为', '還' => '还', '讓' => '让', '與' => '与', '從' => '从',
        '來' => '来', '後' => '后', '時' => '时', '過' => '过', '個' => '个',
        '們' => '们', '說' => '说', '問' => '问', '開' => '开', '關' => '关',
        '當' => '当', '無' => '无', '實' => '实', '見' => '见', '對' => '对',
        '發' => '发', '現' => '现', '於' => '于', '國' => '国', '靈' => '灵',
        '體' => '体', '劍' => '剑', '門' => '门', '龍' => '龙', '風' => '风',
        '雲' => '云', '戰' => '战', '聖' => '圣', '術' => '术', '學' => '学',
        '練' => '练', '藥' => '药', '寶' => '宝', '氣' => '气', '陣' => '阵',
        '權' => '权', '場' => '场', '聲' => '声', '頭' => '头', '臉' => '脸',
        '淚' => '泪', '傷' => '伤', '愛' => '爱', '點' => '点', '將' => '将',
        '應' => '应', '該' => '该', '誰' => '谁', '請' => '请', '講' => '讲',
        '話' => '话', '認' => '认', '證' => '证', '變' => '变', '處' => '处',
        '選' => '选', '進' => '进', '遠' => '远', '終' => '终', '離' => '离',
        '繼' => '继', '續' => '续', '護' => '护', '靜' => '静', '覺' => '觉',
        '隻' => '只', '隱' => '隐', '顯' => '显', '驚' => '惊', '難' => '难',
        '億' => '亿', '歲' => '岁', '壽' => '寿', '屆' => '届',
        _ => value,
    };

    private static bool IsContinuousVariant(string active, string observed) =>
        Similarity(active, observed) >= 0.80
        // Every-frame OCR sees subtitle reveals/fades one glyph at a time. A
        // strict edit-distance threshold turns e.g. "你走吧" -> "走" into two
        // cues even though the shorter reading is a frame-local fragment of the
        // active visual subtitle. Containment is intentionally limited to the
        // active cue only; candidates still require their normal confirmation.
        || active.Contains(observed, StringComparison.Ordinal)
        || observed.Contains(active, StringComparison.Ordinal);

    private static bool IsStrictSubtext(string active, string observed) =>
        active.Length > observed.Length && active.Contains(observed, StringComparison.Ordinal);

    private sealed record Candidate(string Text, double Start, double Last, double Confidence, int Hits, int Required);
    private sealed record SubtextVariant(string Text, double Start, double Last, double Confidence);
}
