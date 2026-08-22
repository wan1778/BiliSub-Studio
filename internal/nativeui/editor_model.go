package nativeui

import "bilisubstudio/internal/videoedit"

type editorModel struct {
	regions  []videoedit.Region
	selected int
	undo     [][]videoedit.Region
}

func newEditorModel() editorModel { return editorModel{selected: -1} }

func cloneRegions(in []videoedit.Region) []videoedit.Region {
	out := make([]videoedit.Region, len(in))
	copy(out, in)
	return out
}

func (m *editorModel) reset() {
	m.regions = nil
	m.undo = nil
	m.selected = -1
}

func (m *editorModel) snapshot() {
	m.undo = append(m.undo, cloneRegions(m.regions))
	if len(m.undo) > 30 {
		m.undo = append([][]videoedit.Region(nil), m.undo[len(m.undo)-30:]...)
	}
}

func (m *editorModel) selectedRegion() (videoedit.Region, bool) {
	if m.selected < 0 || m.selected >= len(m.regions) {
		return videoedit.Region{}, false
	}
	return m.regions[m.selected], true
}

func (m *editorModel) setSelectedRegion(r videoedit.Region) bool {
	return m.replaceSelected(r, true)
}

func (m *editorModel) replaceSelected(r videoedit.Region, saveUndo bool) bool {
	if m.selected < 0 || m.selected >= len(m.regions) {
		return false
	}
	if saveUndo {
		m.snapshot()
	}
	m.regions[m.selected] = normalizeEditorRegion(r)
	return true
}

func (m *editorModel) add(r videoedit.Region) {
	m.snapshot()
	m.regions = append(m.regions, normalizeEditorRegion(r))
	m.selected = len(m.regions) - 1
}

func (m *editorModel) addPreset(kind string, duration float64) {
	r := videoedit.Region{Whole: true, Start: 0, End: duration}
	if kind == "watermark" {
		r.X, r.Y, r.W, r.H = .78, .04, .18, .10
		r.Effect, r.Strength = "mosaic", 12
	} else {
		r.X, r.Y, r.W, r.H = .08, .72, .84, .18
		r.Effect, r.Strength = "blur", 18
	}
	m.add(r)
}

func (m *editorModel) deleteSelected() bool {
	if m.selected < 0 || m.selected >= len(m.regions) {
		return false
	}
	m.snapshot()
	m.regions = append(m.regions[:m.selected], m.regions[m.selected+1:]...)
	if len(m.regions) == 0 {
		m.selected = -1
	} else if m.selected >= len(m.regions) {
		m.selected = len(m.regions) - 1
	}
	return true
}

func (m *editorModel) undoLast() bool {
	if len(m.undo) == 0 {
		return false
	}
	last := m.undo[len(m.undo)-1]
	m.undo = m.undo[:len(m.undo)-1]
	m.regions = cloneRegions(last)
	if len(m.regions) == 0 {
		m.selected = -1
	} else if m.selected < 0 || m.selected >= len(m.regions) {
		m.selected = len(m.regions) - 1
	}
	return true
}

func normalizeEditorRegion(r videoedit.Region) videoedit.Region {
	if r.X < 0 {
		r.X = 0
	}
	if r.Y < 0 {
		r.Y = 0
	}
	if r.X > .99 {
		r.X = .99
	}
	if r.Y > .99 {
		r.Y = .99
	}
	if r.W < .002 {
		r.W = .002
	}
	if r.H < .002 {
		r.H = .002
	}
	if r.X+r.W > 1 {
		r.W = 1 - r.X
	}
	if r.Y+r.H > 1 {
		r.H = 1 - r.Y
	}
	if r.Effect != "blur" && r.Effect != "mosaic" && r.Effect != "cover" {
		r.Effect = "blur"
	}
	if r.Strength < 2 {
		r.Strength = 2
	}
	if r.Strength > 40 {
		r.Strength = 40
	}
	if r.Start < 0 {
		r.Start = 0
	}
	if r.End < r.Start {
		r.End = r.Start
	}
	return r
}
