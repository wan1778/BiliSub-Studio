package nativeui

import "testing"

func TestValidateOCRRegionInputStrictOrdering(t *testing.T) {
	good := ocrRegionInput{Top: "65", Bottom: "94", Left: "5", Right: "95"}
	if err := validateOCRRegionInput(good); err != nil {
		t.Fatalf("good ROI rejected: %v", err)
	}
	cases := []ocrRegionInput{
		{Top: "94", Bottom: "65", Left: "5", Right: "95"},
		{Top: "65", Bottom: "94", Left: "95", Right: "5"},
		{Top: "65", Bottom: "65", Left: "5", Right: "95"},
		{Top: "NaN", Bottom: "94", Left: "5", Right: "95"},
	}
	for _, tc := range cases {
		if err := validateOCRRegionInput(tc); err == nil {
			t.Fatalf("invalid ROI accepted: %+v", tc)
		}
	}
}

func TestValidateEditorGeometryBoundsSum(t *testing.T) {
	good := editorInput{X: "5", Y: "70", W: "90", H: "20", Strength: "18", Whole: true}
	if err := validateEditorInput(good); err != nil {
		t.Fatalf("good editor input rejected: %v", err)
	}
	for _, tc := range []editorInput{
		{X: "20", Y: "70", W: "90", H: "20", Strength: "18", Whole: true},
		{X: "5", Y: "90", W: "90", H: "20", Strength: "18", Whole: true},
		{X: "5", Y: "70", W: "0", H: "20", Strength: "18", Whole: true},
		{X: "5", Y: "70", W: "90", H: "20", Strength: "41", Whole: true},
	} {
		if err := validateEditorInput(tc); err == nil {
			t.Fatalf("invalid editor geometry accepted: %+v", tc)
		}
	}
}

func TestValidateEditorTiming(t *testing.T) {
	good := editorInput{X: "5", Y: "70", W: "90", H: "20", Strength: "18", Start: "1", End: "3", Duration: 10}
	if err := validateEditorInput(good); err != nil {
		t.Fatalf("good timing rejected: %v", err)
	}
	bad := good
	bad.End = "11"
	if err := validateEditorInput(bad); err == nil {
		t.Fatal("end beyond duration accepted")
	}
	whole := bad
	whole.Whole = true
	if err := validateEditorInput(whole); err != nil {
		t.Fatalf("whole-video timing should be ignored: %v", err)
	}
}
