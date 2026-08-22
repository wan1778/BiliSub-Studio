package ocr

import (
	"math"
	"strings"
	"unicode"
)

type subtitleCandidate struct {
	Text     string
	Start    float64
	Last     float64
	Conf     float64
	Hits     int
	Required int
}

type subtitleTracker struct {
	mode       scanMode
	committed  []Cue
	active     *Cue
	candidate  *subtitleCandidate
	emptyHits  int
	emptyStart float64
}

func newSubtitleTracker(mode scanMode) *subtitleTracker {
	return &subtitleTracker{mode: mode}
}

func (t *subtitleTracker) Observe(at float64, out Result) {
	rawText := cleanScanText(out.Text)
	if !out.OK || !out.Detected || rawText == "" {
		t.observeEmpty(at)
		return
	}
	// This scanner has one output contract: Chinese burned-in subtitles.
	// OCR samples that contain no Han character, or contain letters from any
	// non-Han script (Latin, Kana, Hangul, Cyrillic, ...) are treated as
	// inconclusive noise rather than as empty text. This prevents foreign-text
	// hallucinations from becoming cues without shortening a real active cue.
	text, ok := NormalizeChineseSubtitleText(rawText)
	if !ok {
		return
	}

	if t.active != nil && scanSimilarity(t.active.Text, text) >= 0.80 {
		t.emptyHits = 0
		t.emptyStart = 0
		t.candidate = nil
		t.active.End = math.Max(t.active.End, at+t.frameSpan())
		if out.Confidence > t.active.Conf+0.035 || len(text) > len(t.active.Text)+2 {
			t.active.Text = text
			t.active.Conf = out.Confidence
		}
		return
	}

	t.emptyHits = 0
	t.emptyStart = 0
	required := requiredSubtitleConfirmations(text, out.Confidence)
	if t.candidate == nil || scanSimilarity(t.candidate.Text, text) < 0.80 {
		t.candidate = &subtitleCandidate{
			Text: text, Start: at, Last: at, Conf: out.Confidence,
			Hits: 1, Required: required,
		}
		return
	}

	t.candidate.Hits++
	t.candidate.Last = at
	if required > t.candidate.Required {
		t.candidate.Required = required
	}
	if out.Confidence > t.candidate.Conf+0.035 || len(text) > len(t.candidate.Text)+2 {
		t.candidate.Text = text
		t.candidate.Conf = out.Confidence
	}
	if t.candidate.Hits >= t.candidate.Required {
		t.promoteCandidate()
	}
}

func (t *subtitleTracker) NeedsConfirmation() bool {
	return t != nil && (t.candidate != nil || (t.active != nil && t.emptyHits > 0))
}

func (t *subtitleTracker) CanVisualConfirm() bool {
	if t == nil || t.candidate == nil {
		return false
	}
	c := t.candidate
	// Visual confirmation is deliberately conservative. It can replace only
	// the second OCR observation for a normal high-confidence subtitle. Tiny
	// ASCII fragments keep their multi-OCR protection because those are the
	// common A/W/OV false positives in burned-in subtitle regions.
	return c.Hits == 1 && c.Required == 2 && c.Conf >= 0.86 && !isShortASCIIText(c.Text)
}

func (t *subtitleTracker) ConfirmVisual(at float64) bool {
	if !t.CanVisualConfirm() {
		return false
	}
	t.candidate.Hits++
	t.candidate.Last = math.Max(t.candidate.Last, at)
	if t.candidate.Hits < t.candidate.Required {
		return false
	}
	t.promoteCandidate()
	return true
}

func (t *subtitleTracker) CanVisualConfirmEmpty() bool {
	return t != nil && t.active != nil && t.candidate == nil && t.emptyHits == 1
}

func (t *subtitleTracker) ConfirmVisualEmpty(at float64) bool {
	if !t.CanVisualConfirmEmpty() {
		return false
	}
	t.observeEmpty(at)
	return t.emptyHits == 0
}

func (t *subtitleTracker) ExtendActiveVisual(at float64) bool {
	if t == nil || t.active == nil || t.candidate != nil || t.emptyHits != 0 {
		return false
	}
	t.active.End = math.Max(t.active.End, at+t.frameSpan())
	return true
}

func (t *subtitleTracker) HasActive() bool {
	return t != nil && t.active != nil
}

func (t *subtitleTracker) CanCheckpoint() bool {
	return t != nil && t.candidate == nil && t.emptyHits == 0
}

func (t *subtitleTracker) Restore(cues []Cue, active *Cue) {
	if t == nil {
		return
	}
	t.committed = t.committed[:0]
	for _, cue := range cues {
		text, ok := NormalizeChineseSubtitleText(cue.Text)
		if !ok {
			continue
		}
		cue.Text = text
		t.committed = append(t.committed, cue)
	}
	t.active = nil
	if active != nil {
		text, ok := NormalizeChineseSubtitleText(active.Text)
		if ok {
			copyCue := *active
			copyCue.Text = text
			t.active = &copyCue
		}
	}
	t.candidate = nil
	t.emptyHits = 0
	t.emptyStart = 0
}

func (t *subtitleTracker) Finish(end float64) {
	if t == nil {
		return
	}
	// An unconfirmed candidate is deliberately discarded. A confirmed subtitle
	// remains active until the media boundary so a sparse final sample does not
	// truncate it.
	t.candidate = nil
	if t.active != nil {
		if end < t.active.Start+t.frameSpan() {
			end = t.active.Start + t.frameSpan()
		}
		t.commitActive(end)
	}
}

func (t *subtitleTracker) Cues() []Cue {
	if t == nil {
		return nil
	}
	return append([]Cue(nil), t.committed...)
}

func (t *subtitleTracker) Active() *Cue {
	if t == nil || t.active == nil {
		return nil
	}
	copyCue := *t.active
	return &copyCue
}

func (t *subtitleTracker) observeEmpty(at float64) {
	if t.candidate != nil {
		t.candidate = nil
	}
	if t.active == nil {
		t.emptyHits = 0
		t.emptyStart = 0
		return
	}
	if t.emptyHits == 0 {
		t.emptyStart = at
	}
	t.emptyHits++
	if t.emptyHits < 2 {
		return
	}
	end := t.emptyStart
	if end < t.active.Start+t.frameSpan() {
		end = t.active.Start + t.frameSpan()
	}
	t.commitActive(end)
	t.emptyHits = 0
	t.emptyStart = 0
}

func (t *subtitleTracker) promoteCandidate() {
	if t == nil || t.candidate == nil {
		return
	}
	candidate := t.candidate
	if t.active != nil {
		t.commitActive(candidate.Start)
	}
	t.active = &Cue{
		Start: candidate.Start,
		End:   candidate.Last + t.frameSpan(),
		Text:  candidate.Text,
		Conf:  candidate.Conf,
	}
	t.candidate = nil
}

func (t *subtitleTracker) commitActive(end float64) {
	if t == nil || t.active == nil {
		return
	}
	// Defense in depth for restored/legacy tracker state. New observations are
	// filtered in Observe, but checkpoints created by older builds can still
	// contain foreign OCR garbage. Never persist it into the Chinese SRT.
	text, ok := NormalizeChineseSubtitleText(t.active.Text)
	if !ok {
		t.active = nil
		return
	}
	t.active.Text = text
	if end < t.active.Start+0.12 {
		end = t.active.Start + 0.12
	}
	if end > t.active.Start+30 {
		end = t.active.Start + 30
	}
	t.active.End = end
	t.committed = append(t.committed, *t.active)
	t.active = nil
}

func (t *subtitleTracker) frameSpan() float64 {
	if t == nil || t.mode.FPS <= 0 {
		return 0.2
	}
	return 1 / t.mode.FPS
}

func requiredSubtitleConfirmations(text string, confidence float64) int {
	text = strings.TrimSpace(text)
	if isShortASCIIText(text) {
		// Tiny Latin fragments are a common false-positive shape in subtitle ROI
		// noise. Require three observations unless PaddleOCR is exceptionally
		// confident. This rule never rejects short CJK subtitles by length.
		if confidence >= 0.94 {
			return 2
		}
		return 3
	}
	return 2
}

// NormalizeChineseSubtitleText enforces the output contract of the Chinese OCR
// scanner. A valid cue must contain at least one Han ideograph and must not
// contain letters from any other script. Digits, punctuation and symbols are
// allowed only when a Han character is also present (for example "第3集").
//
// The function also collapses a small set of repeated punctuation artifacts
// that PaddleOCR commonly emits with a whitespace gap (for example "， ，").
func NormalizeChineseSubtitleText(text string) (string, bool) {
	text = cleanScanText(text)
	if text == "" {
		return "", false
	}
	for _, pair := range [][2]string{
		{"， ，", "，"}, {"。 。", "。"}, {"！ ！", "！"}, {"？ ？", "？"},
		{"、 、", "、"}, {"； ；", "；"}, {"： ：", "："},
		{", ,", ","}, {". .", "."}, {"! !", "!"}, {"? ?", "?"},
	} {
		for strings.Contains(text, pair[0]) {
			text = strings.ReplaceAll(text, pair[0], pair[1])
		}
	}
	hasHan := false
	for _, r := range text {
		if unicode.Is(unicode.Han, r) {
			hasHan = true
			continue
		}
		if unicode.IsLetter(r) {
			return "", false
		}
	}
	if !hasHan {
		return "", false
	}
	return cleanScanText(text), true
}

func isShortASCIIText(text string) bool {
	count := 0
	for _, r := range strings.TrimSpace(text) {
		if unicode.IsSpace(r) || unicode.IsPunct(r) {
			continue
		}
		if r > unicode.MaxASCII || (!unicode.IsLetter(r) && !unicode.IsDigit(r)) {
			return false
		}
		count++
		if count > 3 {
			return false
		}
	}
	return count > 0
}
