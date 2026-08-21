//go:build !windows

package ocr

import "context"

func detectNVIDIAGPUPlatform(context.Context) GPUInfo {
	return GPUInfo{Error: "NVIDIA GPU detection chỉ khả dụng trên Windows"}
}

func probeNVIDIAResourcesPlatform(context.Context) autoResourceSnapshot {
	return autoResourceSnapshot{}
}
