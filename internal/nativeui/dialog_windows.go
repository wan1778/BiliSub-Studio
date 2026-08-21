//go:build windows

package nativeui

import (
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"unicode/utf16"
	"unsafe"
)

const (
	ofPathMustExist         = 0x00000800
	ofFileMustExist         = 0x00001000
	ofExplorer              = 0x00080000
	ofNoChangeDir           = 0x00000008
	bifReturnOnlyFSDirs     = 0x00000001
	bifEditBox              = 0x00000010
	bifNewDialogStyle       = 0x00000040
	coinitApartmentThreaded = 0x2
	swShowNormal            = 1
)

var (
	comdlg32             = syscall.NewLazyDLL("comdlg32.dll")
	getOpenFileNameW     = comdlg32.NewProc("GetOpenFileNameW")
	commDlgExtendedError = comdlg32.NewProc("CommDlgExtendedError")
	shell32              = syscall.NewLazyDLL("shell32.dll")
	shBrowseForFolderW   = shell32.NewProc("SHBrowseForFolderW")
	shGetPathFromIDListW = shell32.NewProc("SHGetPathFromIDListW")
	shellExecuteW        = shell32.NewProc("ShellExecuteW")
	ole32                = syscall.NewLazyDLL("ole32.dll")
	coInitializeEx       = ole32.NewProc("CoInitializeEx")
	coUninitialize       = ole32.NewProc("CoUninitialize")
	coTaskMemFree        = ole32.NewProc("CoTaskMemFree")
)

type openFileNameW struct {
	Size                         uint32
	Owner, Instance              uintptr
	Filter, CustomFilter         *uint16
	MaxCustomFilter, FilterIndex uint32
	File                         *uint16
	MaxFile                      uint32
	FileTitle                    *uint16
	MaxFileTitle                 uint32
	InitialDir, Title            *uint16
	Flags                        uint32
	FileOffset, FileExtension    uint16
	DefExt                       *uint16
	CustData, Hook, Template     uintptr
	Reserved                     uintptr
	Reserved2, FlagsEx           uint32
}
type browseInfoW struct {
	Owner, Root        uintptr
	DisplayName, Title *uint16
	Flags              uint32
	Callback, LParam   uintptr
	Image              int32
}

func utf16z(s string) []uint16 { r := utf16.Encode([]rune(s)); return append(r, 0) }
func utf16Filter(parts ...string) []uint16 {
	var out []uint16
	for _, p := range parts {
		out = append(out, utf16.Encode([]rune(p))...)
		out = append(out, 0)
	}
	return append(out, 0)
}
func initialDir(initial string) string {
	initial = strings.TrimSpace(initial)
	if initial == "" {
		return ""
	}
	if st, e := os.Stat(initial); e == nil {
		if st.IsDir() {
			return initial
		}
		return filepath.Dir(initial)
	}
	if filepath.Ext(initial) != "" {
		return filepath.Dir(initial)
	}
	return initial
}
func pickVideo(owner uintptr, initial string) (string, bool, error) {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	buf := make([]uint16, 32768)
	filter := utf16Filter("Video", "*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi", "Tất cả file", "*.*")
	title := utf16z("Chọn video - BiliSub Studio")
	var initialPtr *uint16
	var initialBuf []uint16
	if d := initialDir(initial); d != "" {
		initialBuf = utf16z(d)
		initialPtr = &initialBuf[0]
	}
	of := openFileNameW{Owner: owner, Filter: &filter[0], FilterIndex: 1, File: &buf[0], MaxFile: uint32(len(buf)), InitialDir: initialPtr, Title: &title[0], Flags: ofExplorer | ofFileMustExist | ofPathMustExist | ofNoChangeDir}
	of.Size = uint32(unsafe.Sizeof(of))
	ok, _, _ := getOpenFileNameW.Call(uintptr(unsafe.Pointer(&of)))
	runtime.KeepAlive(filter)
	runtime.KeepAlive(title)
	runtime.KeepAlive(initialBuf)
	runtime.KeepAlive(buf)
	if ok == 0 {
		code, _, _ := commDlgExtendedError.Call()
		if code == 0 {
			return "", true, nil
		}
		return "", false, fmt.Errorf("Windows file dialog lỗi 0x%X", code)
	}
	return syscall.UTF16ToString(buf), false, nil
}
func pickFolder(owner uintptr, initial string) (string, bool, error) {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	hr, _, _ := coInitializeEx.Call(0, coinitApartmentThreaded)
	if uint32(hr) != 0 && uint32(hr) != 1 {
		return "", false, fmt.Errorf("Windows COM init lỗi 0x%X", uint32(hr))
	}
	defer coUninitialize.Call()
	display := make([]uint16, syscall.MAX_PATH)
	title := utf16z("Chọn thư mục lưu - BiliSub Studio")
	bi := browseInfoW{Owner: owner, DisplayName: &display[0], Title: &title[0], Flags: bifReturnOnlyFSDirs | bifEditBox | bifNewDialogStyle}
	pidl, _, _ := shBrowseForFolderW.Call(uintptr(unsafe.Pointer(&bi)))
	if pidl == 0 {
		return "", true, nil
	}
	defer coTaskMemFree.Call(pidl)
	path := make([]uint16, syscall.MAX_PATH)
	ok, _, _ := shGetPathFromIDListW.Call(pidl, uintptr(unsafe.Pointer(&path[0])))
	if ok == 0 {
		return "", false, fmt.Errorf("Windows không trả về đường dẫn")
	}
	return syscall.UTF16ToString(path), false, nil
}
func openFolder(owner uintptr, path string) error {
	path = strings.TrimSpace(path)
	if path == "" {
		return fmt.Errorf("thư mục rỗng")
	}
	if err := os.MkdirAll(path, 0o755); err != nil {
		return err
	}
	r, _, e := shellExecuteW.Call(owner, uintptr(unsafe.Pointer(utf16Ptr("open"))), uintptr(unsafe.Pointer(utf16Ptr(path))), 0, 0, swShowNormal)
	if r <= 32 {
		return winErr("ShellExecuteW", e)
	}
	return nil
}
