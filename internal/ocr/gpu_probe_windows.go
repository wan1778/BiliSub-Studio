//go:build windows

package ocr

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"syscall"
	"unsafe"
)

const nvmlSuccess = 0

type nvmlMemory struct {
	Total uint64
	Free  uint64
	Used  uint64
}

type nvmlUtilization struct {
	GPU    uint32
	Memory uint32
}

type nvmlLibrary struct {
	dll               *syscall.DLL
	init              *syscall.Proc
	shutdown          *syscall.Proc
	deviceGetCount    *syscall.Proc
	deviceGetHandle   *syscall.Proc
	systemDriver      *syscall.Proc
	deviceName        *syscall.Proc
	deviceMemory      *syscall.Proc
	deviceUtilization *syscall.Proc
}

func detectNVIDIAGPUPlatform(ctx context.Context) GPUInfo {
	select {
	case <-ctx.Done():
		return GPUInfo{Error: ctx.Err().Error()}
	default:
	}

	cuda, err := syscall.LoadDLL("nvcuda.dll")
	if err != nil {
		return GPUInfo{Error: "không phát hiện NVIDIA display driver (nvcuda.dll)"}
	}
	defer cuda.Release()

	cuInit, err := cuda.FindProc("cuInit")
	if err != nil {
		return GPUInfo{Error: "NVIDIA driver thiếu CUDA Driver API"}
	}
	cuDriverGetVersion, err := cuda.FindProc("cuDriverGetVersion")
	if err != nil {
		return GPUInfo{Error: "NVIDIA driver thiếu cuDriverGetVersion"}
	}
	cuDeviceGetCount, err := cuda.FindProc("cuDeviceGetCount")
	if err != nil {
		return GPUInfo{Error: "NVIDIA driver thiếu cuDeviceGetCount"}
	}
	cuDeviceGet, err := cuda.FindProc("cuDeviceGet")
	if err != nil {
		return GPUInfo{Error: "NVIDIA driver thiếu cuDeviceGet"}
	}
	cuDeviceGetName, err := cuda.FindProc("cuDeviceGetName")
	if err != nil {
		return GPUInfo{Error: "NVIDIA driver thiếu cuDeviceGetName"}
	}

	if rc, _, _ := cuInit.Call(0); rc != 0 {
		return GPUInfo{Error: fmt.Sprintf("CUDA Driver API khởi tạo thất bại (%d)", rc)}
	}
	var count int32
	if rc, _, _ := cuDeviceGetCount.Call(uintptr(unsafe.Pointer(&count))); rc != 0 || count <= 0 {
		return GPUInfo{Error: "không phát hiện NVIDIA CUDA GPU khả dụng"}
	}
	var cudaVersion int32
	if rc, _, _ := cuDriverGetVersion.Call(uintptr(unsafe.Pointer(&cudaVersion))); rc != 0 {
		return GPUInfo{Detected: true, Error: fmt.Sprintf("không đọc được CUDA driver version (%d)", rc)}
	}
	var device int32
	if rc, _, _ := cuDeviceGet.Call(uintptr(unsafe.Pointer(&device)), 0); rc != 0 {
		return GPUInfo{Detected: true, Error: fmt.Sprintf("không mở được NVIDIA GPU 0 (%d)", rc)}
	}
	nameBuf := make([]byte, 256)
	name := "NVIDIA GPU"
	if rc, _, _ := cuDeviceGetName.Call(uintptr(unsafe.Pointer(&nameBuf[0])), uintptr(len(nameBuf)), uintptr(device)); rc == 0 {
		if n := bytesBeforeNUL(nameBuf); n > 0 {
			name = string(nameBuf[:n])
		}
	}

	info := GPUInfo{Detected: true, Name: name, Driver: formatCUDADriverVersion(int(cudaVersion))}
	if nvml, err := loadNVML(); err == nil {
		if driver, ok := nvmlDriverVersion(nvml); ok && driver != "" {
			info.Driver = driver
		}
		nvml.close()
	}
	if index, ok := gpuWheelForCUDADriver(int(cudaVersion)); ok {
		info.Usable = true
		info.IndexURL = index
		return info
	}
	info.Error = fmt.Sprintf("NVIDIA driver chỉ hỗ trợ %s; PaddlePaddle GPU cần CUDA driver 11.8+", formatCUDADriverVersion(int(cudaVersion)))
	return info
}

func probeNVIDIAResourcesPlatform(ctx context.Context) autoResourceSnapshot {
	select {
	case <-ctx.Done():
		return autoResourceSnapshot{}
	default:
	}
	lib, err := loadNVML()
	if err != nil {
		return autoResourceSnapshot{}
	}
	defer lib.close()

	var count uint32
	if rc, _, _ := lib.deviceGetCount.Call(uintptr(unsafe.Pointer(&count))); rc != nvmlSuccess || count == 0 {
		return autoResourceSnapshot{}
	}
	var device uintptr
	if rc, _, _ := lib.deviceGetHandle.Call(0, uintptr(unsafe.Pointer(&device))); rc != nvmlSuccess || device == 0 {
		return autoResourceSnapshot{}
	}

	snap := autoResourceSnapshot{}
	var mem nvmlMemory
	if rc, _, _ := lib.deviceMemory.Call(device, uintptr(unsafe.Pointer(&mem))); rc == nvmlSuccess && mem.Total > 0 {
		snap.TotalVRAM = mem.Total
		snap.UsedVRAM = mem.Used
		snap.VRAMValid = true
	}
	var util nvmlUtilization
	if rc, _, _ := lib.deviceUtilization.Call(device, uintptr(unsafe.Pointer(&util))); rc == nvmlSuccess {
		gpu := float64(util.GPU)
		if gpu > 100 {
			gpu = 100
		}
		snap.GPUPercent = gpu
		snap.GPUValid = true
	}
	return snap
}

func loadNVML() (*nvmlLibrary, error) {
	candidates := nvmlCandidates()
	var lastErr error
	for _, path := range candidates {
		dll, err := syscall.LoadDLL(path)
		if err != nil {
			lastErr = err
			continue
		}
		lib := &nvmlLibrary{dll: dll}
		if err := lib.bind(); err != nil {
			_ = dll.Release()
			lastErr = err
			continue
		}
		if rc, _, _ := lib.init.Call(); rc != nvmlSuccess {
			_ = dll.Release()
			lastErr = fmt.Errorf("nvmlInit_v2=%d", rc)
			continue
		}
		return lib, nil
	}
	if lastErr == nil {
		lastErr = fmt.Errorf("nvml.dll không tìm thấy")
	}
	return nil, lastErr
}

func (n *nvmlLibrary) bind() error {
	var err error
	if n.init, err = n.dll.FindProc("nvmlInit_v2"); err != nil {
		return err
	}
	if n.shutdown, err = n.dll.FindProc("nvmlShutdown"); err != nil {
		return err
	}
	if n.deviceGetCount, err = n.dll.FindProc("nvmlDeviceGetCount_v2"); err != nil {
		return err
	}
	if n.deviceGetHandle, err = n.dll.FindProc("nvmlDeviceGetHandleByIndex_v2"); err != nil {
		return err
	}
	if n.systemDriver, err = n.dll.FindProc("nvmlSystemGetDriverVersion"); err != nil {
		return err
	}
	if n.deviceName, err = n.dll.FindProc("nvmlDeviceGetName"); err != nil {
		return err
	}
	if n.deviceMemory, err = n.dll.FindProc("nvmlDeviceGetMemoryInfo"); err != nil {
		return err
	}
	if n.deviceUtilization, err = n.dll.FindProc("nvmlDeviceGetUtilizationRates"); err != nil {
		return err
	}
	return nil
}

func (n *nvmlLibrary) close() {
	if n == nil || n.dll == nil {
		return
	}
	if n.shutdown != nil {
		_, _, _ = n.shutdown.Call()
	}
	_ = n.dll.Release()
}

func nvmlDriverVersion(n *nvmlLibrary) (string, bool) {
	if n == nil || n.systemDriver == nil {
		return "", false
	}
	buf := make([]byte, 96)
	rc, _, _ := n.systemDriver.Call(uintptr(unsafe.Pointer(&buf[0])), uintptr(len(buf)))
	if rc != nvmlSuccess {
		return "", false
	}
	end := bytesBeforeNUL(buf)
	if end <= 0 {
		return "", false
	}
	return strings.TrimSpace(string(buf[:end])), true
}

func nvmlCandidates() []string {
	unique := map[string]bool{}
	out := make([]string, 0, 4)
	add := func(path string) {
		path = strings.TrimSpace(path)
		if path == "" || unique[strings.ToLower(path)] {
			return
		}
		unique[strings.ToLower(path)] = true
		out = append(out, path)
	}
	if root := os.Getenv("SystemRoot"); root != "" {
		add(filepath.Join(root, "System32", "nvml.dll"))
	}
	if pf := os.Getenv("ProgramW6432"); pf != "" {
		add(filepath.Join(pf, "NVIDIA Corporation", "NVSMI", "nvml.dll"))
	}
	if pf := os.Getenv("ProgramFiles"); pf != "" {
		add(filepath.Join(pf, "NVIDIA Corporation", "NVSMI", "nvml.dll"))
	}
	// Last fallback asks the Windows loader. This is still a DLL dependency on
	// the installed display driver, never an executable/CLI dependency.
	add("nvml.dll")
	return out
}

func bytesBeforeNUL(b []byte) int {
	for i, c := range b {
		if c == 0 {
			return i
		}
	}
	return len(b)
}
