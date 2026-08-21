package ocr

import "testing"

func TestNormalizeDeviceMode(t *testing.T) {
	for _, tc := range []struct {
		in, want string
	}{
		{"", DeviceAuto}, {"AUTO", DeviceAuto}, {"cpu", DeviceCPU}, {"GPU", DeviceGPU}, {"hybrid", DeviceHybrid},
	} {
		got, err := normalizeDeviceMode(tc.in)
		if err != nil {
			t.Fatalf("normalize %q: %v", tc.in, err)
		}
		if got != tc.want {
			t.Fatalf("normalize %q=%q want %q", tc.in, got, tc.want)
		}
	}
	if _, err := normalizeDeviceMode("cuda"); err == nil {
		t.Fatal("invalid OCR device mode must fail")
	}
}

func TestGPUWheelIndexMatchesCUDADriverFloor(t *testing.T) {
	for _, tc := range []struct {
		cuda   int
		usable bool
		index  string
	}{
		{11070, false, ""},
		{11080, true, paddleGPU118Index},
		{12050, true, paddleGPU118Index},
		{12060, true, paddleGPU126Index},
		{13000, true, paddleGPU126Index},
	} {
		index, usable := gpuWheelForCUDADriver(tc.cuda)
		if usable != tc.usable || index != tc.index {
			t.Fatalf("CUDA driver %d -> usable=%v index=%q; want usable=%v index=%q", tc.cuda, usable, index, tc.usable, tc.index)
		}
	}
}

func TestFormatCUDADriverVersion(t *testing.T) {
	for _, tc := range []struct {
		in   int
		want string
	}{
		{0, ""},
		{11080, "CUDA 11.8"},
		{12060, "CUDA 12.6"},
		{13000, "CUDA 13.0"},
	} {
		if got := formatCUDADriverVersion(tc.in); got != tc.want {
			t.Fatalf("format %d=%q want %q", tc.in, got, tc.want)
		}
	}
}
