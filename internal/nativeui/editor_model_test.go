package nativeui

import "testing"

func TestEditorModelPresetsMultiRegionDeleteUndo(t *testing.T) {
	m := newEditorModel()
	m.addPreset("subtitle", 120)
	m.addPreset("watermark", 120)
	if len(m.regions) != 2 || m.selected != 1 {
		t.Fatalf("regions=%d selected=%d", len(m.regions), m.selected)
	}
	if m.regions[0].Effect != "blur" || m.regions[1].Effect != "mosaic" {
		t.Fatalf("unexpected presets: %+v", m.regions)
	}
	if !m.deleteSelected() || len(m.regions) != 1 {
		t.Fatalf("delete failed: %+v", m.regions)
	}
	if !m.undoLast() || len(m.regions) != 2 {
		t.Fatalf("undo failed: %+v", m.regions)
	}
}

func TestEditorModelNormalizesRegion(t *testing.T) {
	m := newEditorModel()
	m.addPreset("subtitle", 10)
	r, _ := m.selectedRegion()
	r.X, r.Y, r.W, r.H, r.Strength = .95, .95, .9, .9, 100
	if !m.setSelectedRegion(r) {
		t.Fatal("set selected")
	}
	r, _ = m.selectedRegion()
	if r.X+r.W > 1.000001 || r.Y+r.H > 1.000001 || r.Strength != 40 {
		t.Fatalf("not normalized: %+v", r)
	}
}
