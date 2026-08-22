package nativeui

import (
	"fmt"
	"math"
	"strconv"
	"strings"
)

type ocrRegionInput struct {
	Top, Bottom, Left, Right string
}

type editorInput struct {
	X, Y, W, H string
	Strength   string
	Whole      bool
	Start, End string
	Duration   float64
}

func parseFiniteFloat(raw, name string) (float64, error) {
	v, err := strconv.ParseFloat(strings.TrimSpace(raw), 64)
	if err != nil || math.IsNaN(v) || math.IsInf(v, 0) {
		return 0, fmt.Errorf("%s phải là số hợp lệ", name)
	}
	return v, nil
}

func validateOCRRegionInput(v ocrRegionInput) error {
	top, err := parseFiniteFloat(v.Top, "ROI Trên")
	if err != nil {
		return err
	}
	bottom, err := parseFiniteFloat(v.Bottom, "ROI Dưới")
	if err != nil {
		return err
	}
	left, err := parseFiniteFloat(v.Left, "ROI Trái")
	if err != nil {
		return err
	}
	right, err := parseFiniteFloat(v.Right, "ROI Phải")
	if err != nil {
		return err
	}
	for _, p := range []struct {
		name  string
		value float64
	}{
		{"ROI Trên", top}, {"ROI Dưới", bottom}, {"ROI Trái", left}, {"ROI Phải", right},
	} {
		if p.value < 0 || p.value > 100 {
			return fmt.Errorf("%s phải nằm trong 0–100", p.name)
		}
	}
	if bottom <= top {
		return fmt.Errorf("ROI Dưới phải lớn hơn ROI Trên")
	}
	if right <= left {
		return fmt.Errorf("ROI Phải phải lớn hơn ROI Trái")
	}
	if bottom-top < .2 || right-left < .2 {
		return fmt.Errorf("ROI quá nhỏ; hãy kéo một vùng phụ đề rõ ràng trên preview")
	}
	return nil
}

func validateEditorGeometryInput(v editorInput) error {
	x, err := parseFiniteFloat(v.X, "X")
	if err != nil {
		return err
	}
	y, err := parseFiniteFloat(v.Y, "Y")
	if err != nil {
		return err
	}
	width, err := parseFiniteFloat(v.W, "Rộng")
	if err != nil {
		return err
	}
	height, err := parseFiniteFloat(v.H, "Cao")
	if err != nil {
		return err
	}
	if x < 0 || x > 100 {
		return fmt.Errorf("X phải nằm trong 0–100")
	}
	if y < 0 || y > 100 {
		return fmt.Errorf("Y phải nằm trong 0–100")
	}
	if width <= 0 || width > 100 {
		return fmt.Errorf("Rộng phải lớn hơn 0 và không vượt quá 100")
	}
	if height <= 0 || height > 100 {
		return fmt.Errorf("Cao phải lớn hơn 0 và không vượt quá 100")
	}
	if x+width > 100.000001 {
		return fmt.Errorf("X + Rộng phải nhỏ hơn hoặc bằng 100")
	}
	if y+height > 100.000001 {
		return fmt.Errorf("Y + Cao phải nhỏ hơn hoặc bằng 100")
	}
	strength, err := strconv.Atoi(strings.TrimSpace(v.Strength))
	if err != nil || strength < 2 || strength > 40 {
		return fmt.Errorf("Độ mạnh phải là số nguyên từ 2 đến 40")
	}
	return nil
}

func validateEditorTimingInput(v editorInput) error {
	if v.Whole {
		return nil
	}
	start, err := parseFiniteFloat(v.Start, "Bắt đầu")
	if err != nil {
		return err
	}
	end, err := parseFiniteFloat(v.End, "Kết thúc")
	if err != nil {
		return err
	}
	if start < 0 {
		return fmt.Errorf("Bắt đầu không được âm")
	}
	if end <= start {
		return fmt.Errorf("Kết thúc phải lớn hơn Bắt đầu")
	}
	if v.Duration > 0 && end > v.Duration+.05 {
		return fmt.Errorf("Kết thúc vượt quá thời lượng video")
	}
	return nil
}

func validateEditorInput(v editorInput) error {
	if err := validateEditorGeometryInput(v); err != nil {
		return err
	}
	return validateEditorTimingInput(v)
}
