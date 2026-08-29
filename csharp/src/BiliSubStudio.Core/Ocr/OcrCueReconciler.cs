namespace BiliSubStudio.Core.Ocr;

public static class OcrCueReconciler
{
    // Recovered full/short/full readings may leave separate touching cues with
    // the same final spelling. Never bridge a real blank or merge similar words.
    public static IReadOnlyList<OcrCue> MergeTouchingIdentical(IEnumerable<OcrCue> cues)
    {
        var output = new List<OcrCue>();
        foreach (var rawCue in cues.OrderBy(cue => cue.Start))
        {
            if (!ChineseSubtitleNormalizer.TryNormalize(rawCue.Text, out var normalized)) continue;
            var cue = rawCue with { Text = normalized };
            if (output.Count > 0 && string.Equals(output[^1].Text, cue.Text, StringComparison.Ordinal)
                && cue.Start <= output[^1].End + .001)
            {
                var previous = output[^1];
                output[^1] = previous with
                {
                    End = Math.Max(previous.End, cue.End),
                    Confidence = Math.Max(previous.Confidence, cue.Confidence),
                };
            }
            else output.Add(cue);
        }
        return output;
    }
}
