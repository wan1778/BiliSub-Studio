//go:build windows

package ocr

import (
	"context"
	"syscall"
	"time"
	"unsafe"
)

type memoryStatusEx struct {
	Length               uint32
	MemoryLoad           uint32
	TotalPhys            uint64
	AvailPhys            uint64
	TotalPageFile        uint64
	AvailPageFile        uint64
	TotalVirtual         uint64
	AvailVirtual         uint64
	AvailExtendedVirtual uint64
}

type fileTime struct {
	LowDateTime  uint32
	HighDateTime uint32
}

var (
	kernel32Resource         = syscall.NewLazyDLL("kernel32.dll")
	procGlobalMemoryStatusEx = kernel32Resource.NewProc("GlobalMemoryStatusEx")
	procGetSystemTimes       = kernel32Resource.NewProc("GetSystemTimes")
)

func probePlatformResources(ctx context.Context) autoResourceSnapshot {
	snap := autoResourceSnapshot{}
	var mem memoryStatusEx
	mem.Length = uint32(unsafe.Sizeof(mem))
	if ret, _, _ := procGlobalMemoryStatusEx.Call(uintptr(unsafe.Pointer(&mem))); ret != 0 && mem.TotalPhys > 0 {
		snap.TotalRAM = mem.TotalPhys
		snap.AvailableRAM = mem.AvailPhys
		snap.RAMValid = true
	}
	idle1, kernel1, user1, ok := readSystemTimes()
	if !ok {
		return snap
	}
	timer := time.NewTimer(80 * time.Millisecond)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return snap
	case <-timer.C:
	}
	idle2, kernel2, user2, ok := readSystemTimes()
	if !ok {
		return snap
	}
	idleDelta := idle2 - idle1
	totalDelta := (kernel2 - kernel1) + (user2 - user1)
	if totalDelta > 0 && idleDelta <= totalDelta {
		snap.CPUPercent = 100 * float64(totalDelta-idleDelta) / float64(totalDelta)
		snap.CPUValid = true
	}
	return snap
}

func readSystemTimes() (idle, kernel, user uint64, ok bool) {
	var i, k, u fileTime
	ret, _, _ := procGetSystemTimes.Call(
		uintptr(unsafe.Pointer(&i)),
		uintptr(unsafe.Pointer(&k)),
		uintptr(unsafe.Pointer(&u)),
	)
	if ret == 0 {
		return 0, 0, 0, false
	}
	toUint64 := func(v fileTime) uint64 { return uint64(v.HighDateTime)<<32 | uint64(v.LowDateTime) }
	return toUint64(i), toUint64(k), toUint64(u), true
}
