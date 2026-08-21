package appstate

import (
	"path/filepath"
	"testing"
)

func TestOCRPathUsesSingleGenericOCRDirectory(t *testing.T) {
	root := t.TempDir()
	st, err := New(root, "test")
	if err != nil {
		t.Fatal(err)
	}
	want := filepath.Join(root, "Tools", "OCR")
	if st.Paths.OCR != want {
		t.Fatalf("OCR path = %q, want %q", st.Paths.OCR, want)
	}
}

func TestOCRDeviceDefaultsToAutoAndNormalizesInvalidValues(t *testing.T) {
	root := t.TempDir()
	st, err := New(root, "test")
	if err != nil {
		t.Fatal(err)
	}
	if st.SnapshotConfig().OCRDevice != "auto" {
		t.Fatalf("default OCR device=%q", st.SnapshotConfig().OCRDevice)
	}
	if err := st.UpdateConfig(func(c *Config) { c.OCRDevice = "GPU" }); err != nil {
		t.Fatal(err)
	}
	if st.SnapshotConfig().OCRDevice != "gpu" {
		t.Fatalf("normalized OCR device=%q", st.SnapshotConfig().OCRDevice)
	}
	if err := st.UpdateConfig(func(c *Config) { c.OCRDevice = "cuda" }); err != nil {
		t.Fatal(err)
	}
	if st.SnapshotConfig().OCRDevice != "auto" {
		t.Fatalf("invalid OCR device should fall back to auto, got %q", st.SnapshotConfig().OCRDevice)
	}
}
