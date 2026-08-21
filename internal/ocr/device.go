package ocr

import (
	"context"
	"fmt"
	"strings"
)

const (
	DeviceAuto   = "auto"
	DeviceCPU    = "cpu"
	DeviceGPU    = "gpu"
	DeviceHybrid = "hybrid"
)

type GPUInfo struct {
	Detected bool   `json:"detected"`
	Usable   bool   `json:"usable"`
	Name     string `json:"name,omitempty"`
	Driver   string `json:"driver,omitempty"`
	IndexURL string `json:"-"`
	Error    string `json:"error,omitempty"`
}

func normalizeDeviceMode(mode string) (string, error) {
	mode = strings.ToLower(strings.TrimSpace(mode))
	if mode == "" {
		return DeviceAuto, nil
	}
	switch mode {
	case DeviceAuto, DeviceCPU, DeviceGPU, DeviceHybrid:
		return mode, nil
	default:
		return "", fmt.Errorf("thiết bị OCR không hợp lệ: %s", mode)
	}
}

// detectNVIDIAGPU uses the NVIDIA driver libraries directly on Windows. It
// intentionally does not execute the vendor CLI: BiliSub must not depend on an
// external executable merely to discover hardware or choose the private Paddle wheel.
func detectNVIDIAGPU(ctx context.Context) GPUInfo {
	return detectNVIDIAGPUPlatform(ctx)
}

func gpuWheelForCUDADriver(cudaVersion int) (string, bool) {
	// cuDriverGetVersion reports major*1000 + minor*10. Prefer the cu126 wheel
	// only when the installed NVIDIA driver advertises CUDA 12.6 or newer.
	// CUDA 11.8 remains the conservative compatibility floor for the pinned
	// PaddlePaddle GPU runtime.
	switch {
	case cudaVersion >= 12060:
		return paddleGPU126Index, true
	case cudaVersion >= 11080:
		return paddleGPU118Index, true
	default:
		return "", false
	}
}

func formatCUDADriverVersion(cudaVersion int) string {
	if cudaVersion <= 0 {
		return ""
	}
	major := cudaVersion / 1000
	minor := (cudaVersion % 1000) / 10
	return fmt.Sprintf("CUDA %d.%d", major, minor)
}
