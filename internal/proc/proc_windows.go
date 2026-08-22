//go:build windows

package proc

import (
	"fmt"
	"os/exec"
	"sync"
	"syscall"
	"unsafe"
)

const (
	createNoWindow          = 0x08000000
	createBreakawayFromJob  = 0x01000000
	jobObjectLimitBreakaway = 0x00000800
	jobObjectLimitKillClose = 0x00002000
	jobExtendedLimitInfo    = 9
)

type ioCounters struct {
	ReadOperationCount  uint64
	WriteOperationCount uint64
	OtherOperationCount uint64
	ReadTransferCount   uint64
	WriteTransferCount  uint64
	OtherTransferCount  uint64
}

type basicLimitInformation struct {
	PerProcessUserTimeLimit int64
	PerJobUserTimeLimit     int64
	LimitFlags              uint32
	MinimumWorkingSetSize   uintptr
	MaximumWorkingSetSize   uintptr
	ActiveProcessLimit      uint32
	Affinity                uintptr
	PriorityClass           uint32
	SchedulingClass         uint32
}

type extendedLimitInformation struct {
	BasicLimitInformation basicLimitInformation
	IoInfo                ioCounters
	ProcessMemoryLimit    uintptr
	JobMemoryLimit        uintptr
	PeakProcessMemoryUsed uintptr
	PeakJobMemoryUsed     uintptr
}

var (
	kernel32Proc             = syscall.NewLazyDLL("kernel32.dll")
	createJobObjectW         = kernel32Proc.NewProc("CreateJobObjectW")
	setInformationJobObject  = kernel32Proc.NewProc("SetInformationJobObject")
	assignProcessToJobObject = kernel32Proc.NewProc("AssignProcessToJobObject")
	getCurrentProcess        = kernel32Proc.NewProc("GetCurrentProcess")
	containmentOnce          sync.Once
	containmentErr           error
	containmentJob           syscall.Handle
)

// EnableContainment places BiliSub itself in a Windows Job Object with
// KILL_ON_JOB_CLOSE. Normal helper processes then inherit the same job
// automatically, so ffmpeg/yt-dlp/Paddle workers cannot survive an app crash or
// normal process exit. The handle intentionally remains open for process life.
func EnableContainment() error {
	containmentOnce.Do(func() {
		h, _, callErr := createJobObjectW.Call(0, 0)
		if h == 0 {
			containmentErr = fmt.Errorf("CreateJobObjectW: %v", callErr)
			return
		}
		job := syscall.Handle(h)
		info := extendedLimitInformation{}
		info.BasicLimitInformation.LimitFlags = jobObjectLimitKillClose | jobObjectLimitBreakaway
		ret, _, callErr := setInformationJobObject.Call(
			uintptr(job),
			jobExtendedLimitInfo,
			uintptr(unsafe.Pointer(&info)),
			unsafe.Sizeof(info),
		)
		if ret == 0 {
			_ = syscall.CloseHandle(job)
			containmentErr = fmt.Errorf("SetInformationJobObject: %v", callErr)
			return
		}
		current, _, _ := getCurrentProcess.Call()
		ret, _, callErr = assignProcessToJobObject.Call(uintptr(job), current)
		if ret == 0 {
			_ = syscall.CloseHandle(job)
			containmentErr = fmt.Errorf("AssignProcessToJobObject: %v", callErr)
			return
		}
		containmentJob = job
	})
	return containmentErr
}

// Hide prevents console windows from flashing for helper CLI processes.
func Hide(cmd *exec.Cmd) *exec.Cmd {
	if cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &syscall.SysProcAttr{}
	}
	cmd.SysProcAttr.HideWindow = true
	cmd.SysProcAttr.CreationFlags |= createNoWindow
	return cmd
}

// Breakaway marks the self-updater as the one process allowed to outlive the
// main BiliSub Job Object long enough to replace and restart the installed EXE.
func Breakaway(cmd *exec.Cmd) *exec.Cmd {
	Hide(cmd)
	cmd.SysProcAttr.CreationFlags |= createBreakawayFromJob
	return cmd
}
