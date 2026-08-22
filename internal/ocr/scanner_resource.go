package ocr

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"time"
)

const (
	scanAutoCPUStopPercent       = 90.0
	scanAutoGPUStopPercent       = 96.0
	scanAutoRAMSafetyMinBytes    = uint64(2 * 1024 * 1024 * 1024)
	scanAutoRAMSafetyFraction    = 0.15
	scanAutoVRAMSafetyMinBytes   = uint64(768 * 1024 * 1024)
	scanAutoVRAMSafetyFraction   = 0.18
	scanAutoResourceSamplePeriod = 750 * time.Millisecond
)

type autoResourceSnapshot struct {
	TotalRAM     uint64
	AvailableRAM uint64
	TotalVRAM    uint64
	UsedVRAM     uint64
	CPUPercent   float64
	GPUPercent   float64
	RAMValid     bool
	VRAMValid    bool
	CPUValid     bool
	GPUValid     bool
}

type autoResourceProbe func(context.Context) autoResourceSnapshot

type autoResourceGateDecision struct {
	Allow  bool
	Reason string
	Detail string
}

func (s *Scanner) autoResourceProbe() autoResourceProbe {
	if s != nil && s.resourceProbe != nil {
		return s.resourceProbe
	}
	return probeAutoResources
}

func probeAutoResources(ctx context.Context) autoResourceSnapshot {
	snap := probePlatformResources(ctx)
	gpu := probeNVIDIAResources(ctx)
	if gpu.VRAMValid {
		snap.TotalVRAM = gpu.TotalVRAM
		snap.UsedVRAM = gpu.UsedVRAM
		snap.VRAMValid = true
	}
	if gpu.GPUValid {
		snap.GPUPercent = gpu.GPUPercent
		snap.GPUValid = true
	}
	return snap
}

func probeNVIDIAResources(ctx context.Context) autoResourceSnapshot {
	return probeNVIDIAResourcesPlatform(ctx)
}

func mergeAutoResourcePeak(dst, src autoResourceSnapshot) autoResourceSnapshot {
	if src.RAMValid {
		if !dst.RAMValid {
			dst.TotalRAM = src.TotalRAM
			dst.AvailableRAM = src.AvailableRAM
			dst.RAMValid = true
		} else {
			if src.TotalRAM > 0 {
				dst.TotalRAM = src.TotalRAM
			}
			if src.AvailableRAM < dst.AvailableRAM {
				dst.AvailableRAM = src.AvailableRAM
			}
		}
	}
	if src.VRAMValid {
		if !dst.VRAMValid {
			dst.TotalVRAM = src.TotalVRAM
			dst.UsedVRAM = src.UsedVRAM
			dst.VRAMValid = true
		} else {
			if src.TotalVRAM > 0 {
				dst.TotalVRAM = src.TotalVRAM
			}
			if src.UsedVRAM > dst.UsedVRAM {
				dst.UsedVRAM = src.UsedVRAM
			}
		}
	}
	if src.CPUValid {
		dst.CPUValid = true
		if src.CPUPercent > dst.CPUPercent {
			dst.CPUPercent = src.CPUPercent
		}
	}
	if src.GPUValid {
		dst.GPUValid = true
		if src.GPUPercent > dst.GPUPercent {
			dst.GPUPercent = src.GPUPercent
		}
	}
	return dst
}

func startAutoResourceSampler(ctx context.Context, probe autoResourceProbe) func() autoResourceSnapshot {
	if probe == nil {
		return func() autoResourceSnapshot { return autoResourceSnapshot{} }
	}
	type resourceAccumulator struct {
		memoryWorst autoResourceSnapshot
		cpuSum      float64
		cpuSamples  int
		gpuSum      float64
		gpuSamples  int
	}
	sampleCtx, cancel := context.WithCancel(ctx)
	var mu sync.Mutex
	acc := resourceAccumulator{}
	done := make(chan struct{})
	go func() {
		defer close(done)
		ticker := time.NewTicker(scanAutoResourceSamplePeriod)
		defer ticker.Stop()
		for {
			snap := probe(sampleCtx)
			mu.Lock()
			acc.memoryWorst = mergeAutoResourcePeak(acc.memoryWorst, snap)
			if snap.CPUValid {
				acc.cpuSum += snap.CPUPercent
				acc.cpuSamples++
			}
			if snap.GPUValid {
				acc.gpuSum += snap.GPUPercent
				acc.gpuSamples++
			}
			mu.Unlock()
			select {
			case <-sampleCtx.Done():
				return
			case <-ticker.C:
			}
		}
	}()
	return func() autoResourceSnapshot {
		cancel()
		<-done
		mu.Lock()
		defer mu.Unlock()
		result := acc.memoryWorst
		if acc.cpuSamples > 0 {
			result.CPUPercent = acc.cpuSum / float64(acc.cpuSamples)
			result.CPUValid = true
		}
		if acc.gpuSamples > 0 {
			result.GPUPercent = acc.gpuSum / float64(acc.gpuSamples)
			result.GPUValid = true
		}
		return result
	}
}

func autoResourceSafetyMargin(total, minimum uint64, fraction float64) uint64 {
	fractional := uint64(float64(total) * fraction)
	if fractional > minimum {
		return fractional
	}
	return minimum
}

func evaluateAutoResourceGate(baseline, current autoResourceSnapshot, currentLevel, nextLevel int) autoResourceGateDecision {
	if currentLevel < 1 || nextLevel <= currentLevel {
		return autoResourceGateDecision{Allow: false, Reason: "resource_gate_invalid", Detail: "topology Auto không hợp lệ"}
	}
	if current.CPUValid && current.CPUPercent >= scanAutoCPUStopPercent {
		return autoResourceGateDecision{Allow: false, Reason: "cpu_pressure", Detail: fmt.Sprintf("CPU peak %.0f%%", current.CPUPercent)}
	}
	if current.GPUValid && current.GPUPercent >= scanAutoGPUStopPercent {
		return autoResourceGateDecision{Allow: false, Reason: "gpu_pressure", Detail: fmt.Sprintf("GPU peak %.0f%%", current.GPUPercent)}
	}

	if current.RAMValid && current.TotalRAM > 0 {
		margin := autoResourceSafetyMargin(current.TotalRAM, scanAutoRAMSafetyMinBytes, scanAutoRAMSafetyFraction)
		predictedAvailable := current.AvailableRAM
		if baseline.RAMValid && baseline.TotalRAM > 0 && current.TotalRAM == baseline.TotalRAM {
			baselineUsed := baseline.TotalRAM - minUint64(baseline.AvailableRAM, baseline.TotalRAM)
			currentUsed := current.TotalRAM - minUint64(current.AvailableRAM, current.TotalRAM)
			workloadDelta := uint64(0)
			if currentUsed > baselineUsed {
				workloadDelta = currentUsed - baselineUsed
			}
			if workloadDelta > 0 {
				predictedDelta := uint64(float64(workloadDelta) * float64(nextLevel) / float64(currentLevel))
				predictedUsed := baselineUsed + predictedDelta
				if predictedUsed >= current.TotalRAM {
					predictedAvailable = 0
				} else {
					predictedAvailable = current.TotalRAM - predictedUsed
				}
			}
		}
		if predictedAvailable < margin {
			return autoResourceGateDecision{Allow: false, Reason: "ram_guard", Detail: fmt.Sprintf("RAM dự kiến còn %.1f GB, cần chừa %.1f GB", bytesToGiB(predictedAvailable), bytesToGiB(margin))}
		}
	}

	if current.VRAMValid && current.TotalVRAM > 0 {
		margin := autoResourceSafetyMargin(current.TotalVRAM, scanAutoVRAMSafetyMinBytes, scanAutoVRAMSafetyFraction)
		currentUsed := minUint64(current.UsedVRAM, current.TotalVRAM)
		predictedUsed := currentUsed
		if baseline.VRAMValid && baseline.TotalVRAM == current.TotalVRAM {
			baselineUsed := minUint64(baseline.UsedVRAM, baseline.TotalVRAM)
			workloadDelta := uint64(0)
			if currentUsed > baselineUsed {
				workloadDelta = currentUsed - baselineUsed
			}
			if workloadDelta > 0 {
				predictedDelta := uint64(float64(workloadDelta) * float64(nextLevel) / float64(currentLevel))
				predictedUsed = baselineUsed + predictedDelta
			}
		}
		predictedAvailable := uint64(0)
		if predictedUsed < current.TotalVRAM {
			predictedAvailable = current.TotalVRAM - predictedUsed
		}
		if predictedAvailable < margin {
			return autoResourceGateDecision{Allow: false, Reason: "vram_guard", Detail: fmt.Sprintf("VRAM dự kiến còn %.0f MB, cần chừa %.0f MB", bytesToMiB(predictedAvailable), bytesToMiB(margin))}
		}
	}

	known := current.RAMValid || current.VRAMValid || current.CPUValid || current.GPUValid
	if !known {
		return autoResourceGateDecision{Allow: true, Reason: "resource_unknown", Detail: "không đọc được telemetry tài nguyên; dùng benchmark có watchdog"}
	}
	return autoResourceGateDecision{Allow: true, Reason: "resource_ok", Detail: formatAutoResourceSnapshot(current)}
}

func formatAutoResourceSnapshot(s autoResourceSnapshot) string {
	parts := make([]string, 0, 4)
	if s.CPUValid {
		parts = append(parts, fmt.Sprintf("CPU %.0f%%", s.CPUPercent))
	}
	if s.GPUValid {
		parts = append(parts, fmt.Sprintf("GPU %.0f%%", s.GPUPercent))
	}
	if s.RAMValid {
		parts = append(parts, fmt.Sprintf("RAM trống %.1f GB", bytesToGiB(s.AvailableRAM)))
	}
	if s.VRAMValid && s.TotalVRAM > 0 {
		free := uint64(0)
		if s.UsedVRAM < s.TotalVRAM {
			free = s.TotalVRAM - s.UsedVRAM
		}
		parts = append(parts, fmt.Sprintf("VRAM trống %.0f MB", bytesToMiB(free)))
	}
	if len(parts) == 0 {
		return "telemetry tài nguyên không khả dụng"
	}
	return strings.Join(parts, " · ")
}

func minUint64(a, b uint64) uint64 {
	if a < b {
		return a
	}
	return b
}

func bytesToGiB(v uint64) float64 { return float64(v) / (1024 * 1024 * 1024) }
func bytesToMiB(v uint64) float64 { return float64(v) / (1024 * 1024) }
