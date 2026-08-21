//go:build windows

package api

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"unicode/utf16"
	"unsafe"
)

const createNoWindow = 0x08000000

const coinitApartmentThreaded = 0x2

const (
	ofnPathMustExist = 0x00000800
	ofnFileMustExist = 0x00001000
	ofnExplorer      = 0x00080000
	ofnNoChangeDir   = 0x00000008

	bifReturnOnlyFSDirs = 0x00000001
	bifEditBox          = 0x00000010
	bifNewDialogStyle   = 0x00000040
)

var (
	modComdlg32              = syscall.NewLazyDLL("comdlg32.dll")
	procGetOpenFileNameW     = modComdlg32.NewProc("GetOpenFileNameW")
	procCommDlgExtendedError = modComdlg32.NewProc("CommDlgExtendedError")

	modShell32               = syscall.NewLazyDLL("shell32.dll")
	procSHBrowseForFolderW   = modShell32.NewProc("SHBrowseForFolderW")
	procSHGetPathFromIDListW = modShell32.NewProc("SHGetPathFromIDListW")

	modOle32           = syscall.NewLazyDLL("ole32.dll")
	procCoInitializeEx = modOle32.NewProc("CoInitializeEx")
	procCoUninitialize = modOle32.NewProc("CoUninitialize")
	procCoTaskMemFree  = modOle32.NewProc("CoTaskMemFree")

	modUser32               = syscall.NewLazyDLL("user32.dll")
	procGetForegroundWindow = modUser32.NewProc("GetForegroundWindow")
)

type openFileNameW struct {
	LStructSize       uint32
	HwndOwner         uintptr
	HInstance         uintptr
	LpstrFilter       *uint16
	LpstrCustomFilter *uint16
	NMaxCustFilter    uint32
	NFilterIndex      uint32
	LpstrFile         *uint16
	NMaxFile          uint32
	LpstrFileTitle    *uint16
	NMaxFileTitle     uint32
	LpstrInitialDir   *uint16
	LpstrTitle        *uint16
	Flags             uint32
	NFileOffset       uint16
	NFileExtension    uint16
	LpstrDefExt       *uint16
	LCustData         uintptr
	LpfnHook          uintptr
	LpTemplateName    *uint16
	PvReserved        uintptr
	DwReserved        uint32
	FlagsEx           uint32
}

type browseInfoW struct {
	HwndOwner      uintptr
	PidlRoot       uintptr
	PszDisplayName *uint16
	LpszTitle      *uint16
	UlFlags        uint32
	Lpfn           uintptr
	LParam         uintptr
	IImage         int32
}

// hidden is only for non-interactive child processes. Native pickers below do
// not spawn a helper process at all; they execute inside BiliSubStudio.exe.
func hidden(cmd *exec.Cmd) *exec.Cmd {
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: createNoWindow}
	return cmd
}

func launchBrowser(url string) error {
	return hidden(exec.Command("cmd.exe", "/C", "start", "", url)).Start()
}

func openFolderNative(path string) error {
	return hidden(exec.Command("explorer.exe", path)).Start()
}

func foregroundWindow() uintptr {
	hwnd, _, _ := procGetForegroundWindow.Call()
	return hwnd
}

func utf16z(s string) []uint16 {
	out := utf16.Encode([]rune(s))
	return append(out, 0)
}

func utf16Filter(parts ...string) []uint16 {
	var out []uint16
	for _, p := range parts {
		out = append(out, utf16.Encode([]rune(p))...)
		out = append(out, 0)
	}
	return append(out, 0)
}

func nativeInitialDir(initial string) string {
	initial = strings.TrimSpace(initial)
	if initial == "" {
		return ""
	}
	if st, err := os.Stat(initial); err == nil {
		if st.IsDir() {
			return initial
		}
		return filepath.Dir(initial)
	}
	if ext := filepath.Ext(initial); ext != "" {
		return filepath.Dir(initial)
	}
	return initial
}

// pickVideoNative uses the Win32 common-dialog API directly. This avoids the
// PowerShell/WinForms host used by beta.11, which could block the HTTP request
// while the user-facing dialog never became visible on some Windows systems.
func pickVideoNative(initial string) (string, bool, error) {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()

	fileBuf := make([]uint16, 32768)
	filter := utf16Filter(
		"Video", "*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi",
		"Tất cả file", "*.*",
	)
	title := utf16z("Chọn video - BiliSub Studio")
	var initialPtr *uint16
	if dir := nativeInitialDir(initial); dir != "" {
		initialDir := utf16z(dir)
		initialPtr = &initialDir[0]
		// Keep the backing array alive until the Win32 call returns.
		defer runtime.KeepAlive(initialDir)
	}

	ofn := openFileNameW{
		HwndOwner:       foregroundWindow(),
		LpstrFilter:     &filter[0],
		NFilterIndex:    1,
		LpstrFile:       &fileBuf[0],
		NMaxFile:        uint32(len(fileBuf)),
		LpstrInitialDir: initialPtr,
		LpstrTitle:      &title[0],
		Flags:           ofnExplorer | ofnFileMustExist | ofnPathMustExist | ofnNoChangeDir,
	}
	ofn.LStructSize = uint32(unsafe.Sizeof(ofn))

	ok, _, _ := procGetOpenFileNameW.Call(uintptr(unsafe.Pointer(&ofn)))
	runtime.KeepAlive(filter)
	runtime.KeepAlive(title)
	runtime.KeepAlive(fileBuf)
	if ok == 0 {
		code, _, _ := procCommDlgExtendedError.Call()
		if code == 0 {
			return "", true, nil
		}
		return "", false, fmt.Errorf("Windows file dialog lỗi 0x%X", code)
	}
	return syscall.UTF16ToString(fileBuf), false, nil
}

// pickFolderNative uses the Windows Shell folder browser directly. It also
// avoids PowerShell so all interactive pickers share the same native policy.
func pickFolderNative(initial string) (string, bool, error) {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()

	// SHBrowseForFolder with BIF_NEWDIALOGSTYLE requires COM initialized on
	// the calling thread as a single-threaded apartment. Keep the goroutine
	// pinned for the entire dialog lifetime and balance every successful COM
	// initialization (S_OK or S_FALSE) with CoUninitialize.
	hr, _, _ := procCoInitializeEx.Call(0, coinitApartmentThreaded)
	hresult := uint32(hr)
	if hresult != 0 && hresult != 1 {
		return "", false, fmt.Errorf("Windows COM init lỗi 0x%X", hresult)
	}
	defer procCoUninitialize.Call()

	display := make([]uint16, syscall.MAX_PATH)
	title := utf16z("Chọn thư mục lưu - BiliSub Studio")
	bi := browseInfoW{
		HwndOwner:      foregroundWindow(),
		PszDisplayName: &display[0],
		LpszTitle:      &title[0],
		UlFlags:        bifReturnOnlyFSDirs | bifEditBox | bifNewDialogStyle,
	}
	pidl, _, _ := procSHBrowseForFolderW.Call(uintptr(unsafe.Pointer(&bi)))
	runtime.KeepAlive(title)
	runtime.KeepAlive(display)
	if pidl == 0 {
		return "", true, nil
	}
	defer procCoTaskMemFree.Call(pidl)

	pathBuf := make([]uint16, syscall.MAX_PATH)
	ok, _, _ := procSHGetPathFromIDListW.Call(pidl, uintptr(unsafe.Pointer(&pathBuf[0])))
	runtime.KeepAlive(pathBuf)
	if ok == 0 {
		return "", false, fmt.Errorf("Windows không trả về được đường dẫn thư mục")
	}
	return syscall.UTF16ToString(pathBuf), false, nil
}
