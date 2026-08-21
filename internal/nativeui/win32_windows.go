//go:build windows

package nativeui

import (
	"fmt"
	"runtime"
	"syscall"
	"unsafe"
)

const (
	wsOverlappedWindow = 0x00CF0000
	wsExControlParent  = 0x00010000
	wsExTopmost        = 0x00000008
	wsPopup            = 0x80000000
	wsVisible          = 0x10000000
	wsChild            = 0x40000000
	wsTabStop          = 0x00010000
	wsBorder           = 0x00800000
	wsVScroll          = 0x00200000
	wsDisabled         = 0x08000000
	wsHScroll          = 0x00100000
	esAutoHScroll      = 0x0080
	esMultiline        = 0x0004
	esReadOnly         = 0x0800
	esWantReturn       = 0x1000
	bsPushButton       = 0x00000000
	bsAutoCheckBox     = 0x00000003
	cbsDropDownList    = 0x0003
	lbsNotify          = 0x0001
	ssLeft             = 0x00000000
	ssCenter           = 0x00000001
	ssNotify           = 0x00000100

	progressClass = "msctls_progress32"
	tooltipClass  = "tooltips_class32"
	ttsAlwaysTip  = 0x01
	ttsNoPrefix   = 0x02
	ttfIDIsHwnd   = 0x0001
	ttfSubclass   = 0x0010

	wmCreate         = 0x0001
	wmDestroy        = 0x0002
	wmSize           = 0x0005
	wmDpiChanged     = 0x02E0
	wmPaint          = 0x000F
	wmCtlColorEdit   = 0x0133
	wmCtlColorList   = 0x0134
	wmCtlColorStatic = 0x0138
	wmClose          = 0x0010
	wmGetMinMaxInfo  = 0x0024
	wmCommand        = 0x0111
	wmTimer          = 0x0113
	wmHScroll        = 0x0114
	wmKeyDown        = 0x0100
	wmSetFont        = 0x0030
	wmGetFont        = 0x0031
	wmAppAsync       = 0x8001
	wmAppFrame       = 0x8002
	wmAppState       = 0x8003
	wmAppCloseReady  = 0x8004

	swShow                  = 5
	swHide                  = 0
	swpNoZOrder             = 0x0004
	swpNoActivate           = 0x0010
	swpFrameChanged         = 0x0020
	monitorDefaultToNearest = 0x00000002

	bnClicked    = 0
	cbnSelChange = 1
	lbnSelChange = 1
	enChange     = 0x0300

	cbAddString    = 0x0143
	cbResetContent = 0x014B
	cbSetCurSel    = 0x014E
	cbGetCurSel    = 0x0147
	cbGetLBTextLen = 0x0149
	cbGetLBText    = 0x0148
	lbAddString    = 0x0180
	lbResetContent = 0x0184
	lbSetCurSel    = 0x0186
	lbGetCurSel    = 0x0188
	lbGetTopIndex  = 0x018E
	lbSetTopIndex  = 0x0197
	emSetSel       = 0x00B1
	emReplaceSel   = 0x00C2
	emScrollCaret  = 0x00B7

	pbmSetPos         = 0x0402
	pbmSetRange32     = 0x0406
	ttmAddToolW       = 0x0432
	ttmSetMaxTipWidth = 0x0418

	tbmGetPos   = 0x0400
	tbmSetPos   = 0x0405
	tbmSetRange = 0x0406

	bmGetCheck = 0x00F0
	bmSetCheck = 0x00F1
	bstChecked = 1

	gwlpStyle         = -16
	gclpHbrBackground = -10
	colorWindow       = 5
	defaultGUIFont    = 17
	idcArrow          = 32512
	vkEscape          = 0x1B
	vkControl         = 0x11
	vkF1              = 0x70
	mbYesNo           = 0x00000004
	mbIconWarning     = 0x00000030
	mbDefButton2      = 0x00000100
	idYes             = 6
)

var (
	user32     = syscall.NewLazyDLL("user32.dll")
	gdi32      = syscall.NewLazyDLL("gdi32.dll")
	kernel32UI = syscall.NewLazyDLL("kernel32.dll")
	comctl32   = syscall.NewLazyDLL("comctl32.dll")

	registerClassExW              = user32.NewProc("RegisterClassExW")
	createWindowExW               = user32.NewProc("CreateWindowExW")
	defWindowProcW                = user32.NewProc("DefWindowProcW")
	showWindow                    = user32.NewProc("ShowWindow")
	updateWindow                  = user32.NewProc("UpdateWindow")
	getMessageW                   = user32.NewProc("GetMessageW")
	isDialogMessageW              = user32.NewProc("IsDialogMessageW")
	translateMessage              = user32.NewProc("TranslateMessage")
	dispatchMessageW              = user32.NewProc("DispatchMessageW")
	postQuitMessage               = user32.NewProc("PostQuitMessage")
	postMessageW                  = user32.NewProc("PostMessageW")
	sendMessageW                  = user32.NewProc("SendMessageW")
	setWindowTextW                = user32.NewProc("SetWindowTextW")
	messageBoxW                   = user32.NewProc("MessageBoxW")
	getWindowTextLengthW          = user32.NewProc("GetWindowTextLengthW")
	getWindowTextW                = user32.NewProc("GetWindowTextW")
	setWindowPos                  = user32.NewProc("SetWindowPos")
	setFocus                      = user32.NewProc("SetFocus")
	getKeyState                   = user32.NewProc("GetKeyState")
	getDpiForWindow               = user32.NewProc("GetDpiForWindow")
	getDpiForSystem               = user32.NewProc("GetDpiForSystem")
	setProcessDpiAwarenessContext = user32.NewProc("SetProcessDpiAwarenessContext")
	getWindowRect                 = user32.NewProc("GetWindowRect")
	getWindowLongPtrW             = user32.NewProc("GetWindowLongPtrW")
	setWindowLongPtrW             = user32.NewProc("SetWindowLongPtrW")
	setClassLongPtrW              = user32.NewProc("SetClassLongPtrW")
	monitorFromWindow             = user32.NewProc("MonitorFromWindow")
	getMonitorInfoW               = user32.NewProc("GetMonitorInfoW")
	destroyWindow                 = user32.NewProc("DestroyWindow")
	enableWindow                  = user32.NewProc("EnableWindow")
	getClientRect                 = user32.NewProc("GetClientRect")
	invalidateRect                = user32.NewProc("InvalidateRect")
	beginPaint                    = user32.NewProc("BeginPaint")
	endPaint                      = user32.NewProc("EndPaint")
	fillRect                      = user32.NewProc("FillRect")
	setBkMode                     = gdi32.NewProc("SetBkMode")
	setBkColor                    = gdi32.NewProc("SetBkColor")
	setTextColor                  = gdi32.NewProc("SetTextColor")
	createSolidBrush              = gdi32.NewProc("CreateSolidBrush")
	createFontW                   = gdi32.NewProc("CreateFontW")
	deleteObject                  = gdi32.NewProc("DeleteObject")
	getStockObject                = gdi32.NewProc("GetStockObject")
	stretchDIBits                 = gdi32.NewProc("StretchDIBits")
	loadCursorW                   = user32.NewProc("LoadCursorW")
	getModuleHandleW              = kernel32UI.NewProc("GetModuleHandleW")
	setTimer                      = user32.NewProc("SetTimer")
	killTimer                     = user32.NewProc("KillTimer")
	initCommonControlsEx          = comctl32.NewProc("InitCommonControlsEx")
	uxtheme                       = syscall.NewLazyDLL("uxtheme.dll")
	setWindowTheme                = uxtheme.NewProc("SetWindowTheme")
	dwmapi                        = syscall.NewLazyDLL("dwmapi.dll")
	dwmSetWindowAttribute         = dwmapi.NewProc("DwmSetWindowAttribute")
)

type point struct{ X, Y int32 }
type rect struct{ Left, Top, Right, Bottom int32 }
type monitorInfo struct {
	Size    uint32
	Monitor rect
	Work    rect
	Flags   uint32
}
type minMaxInfo struct {
	Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize point
}
type msg struct {
	Hwnd           uintptr
	Message        uint32
	WParam, LParam uintptr
	Time           uint32
	Pt             point
	LPrivate       uint32
}
type paintStruct struct {
	Hdc                uintptr
	Erase              int32
	RcPaint            rect
	Restore, IncUpdate int32
	RGBReserved        [32]byte
}
type wndClassEx struct {
	Size, Style                        uint32
	WndProc                            uintptr
	ClsExtra, WndExtra                 int32
	Instance, Icon, Cursor, Background uintptr
	MenuName, ClassName                *uint16
	IconSm                             uintptr
}
type initCommonControls struct {
	Size uint32
	ICC  uint32
}
type toolInfo struct {
	Size     uint32
	Flags    uint32
	Hwnd     uintptr
	ID       uintptr
	Rect     rect
	Instance uintptr
	Text     *uint16
	Param    uintptr
	Reserved uintptr
}

type bitmapInfoHeader struct {
	Size                         uint32
	Width, Height                int32
	Planes, BitCount             uint16
	Compression, SizeImage       uint32
	XPelsPerMeter, YPelsPerMeter int32
	ClrUsed, ClrImportant        uint32
}
type bitmapInfo struct {
	Header bitmapInfoHeader
	Colors [1]uint32
}

func utf16Ptr(s string) *uint16 { p, _ := syscall.UTF16PtrFromString(s); return p }
func loword(v uintptr) uint16   { return uint16(v & 0xffff) }
func hiword(v uintptr) uint16   { return uint16((v >> 16) & 0xffff) }

func enablePerMonitorDPI() {
	// DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == (HANDLE)-4.
	setProcessDpiAwarenessContext.Call(^uintptr(3))
}

func systemDPI() uint32 {
	r, _, _ := getDpiForSystem.Call()
	if r == 0 {
		return 96
	}
	return uint32(r)
}

func windowDPI(hwnd uintptr) uint32 {
	r, _, _ := getDpiForWindow.Call(hwnd)
	if r == 0 {
		return systemDPI()
	}
	return uint32(r)
}

func createUIFont(points int, weight int, dpi uint32) uintptr {
	if dpi == 0 {
		dpi = 96
	}
	height := -int(float64(points)*float64(dpi)/72.0 + 0.5)
	r, _, _ := createFontW.Call(uintptr(int32(height)), 0, 0, 0, uintptr(weight), 0, 0, 0, 1, 0, 0, 5, 0, uintptr(unsafe.Pointer(utf16Ptr("Segoe UI"))))
	return r
}

func setFont(hwnd, font uintptr) {
	if hwnd != 0 && font != 0 {
		sendMessageW.Call(hwnd, wmSetFont, font, 1)
	}
}

func createControl(parent uintptr, class, text string, style uint32, id int) uintptr {
	h, _, _ := createWindowExW.Call(0, uintptr(unsafe.Pointer(utf16Ptr(class))), uintptr(unsafe.Pointer(utf16Ptr(text))), uintptr(style|wsChild|wsVisible), 0, 0, 10, 10, parent, uintptr(id), 0, 0)
	font, _, _ := getStockObject.Call(defaultGUIFont)
	if h != 0 && font != 0 {
		sendMessageW.Call(h, wmSetFont, font, 1)
	}
	return h
}
func setText(hwnd uintptr, s string) { setWindowTextW.Call(hwnd, uintptr(unsafe.Pointer(utf16Ptr(s)))) }
func text(hwnd uintptr) string {
	n, _, _ := getWindowTextLengthW.Call(hwnd)
	b := make([]uint16, int(n)+2)
	getWindowTextW.Call(hwnd, uintptr(unsafe.Pointer(&b[0])), uintptr(len(b)))
	return syscall.UTF16ToString(b)
}
func move(hwnd uintptr, x, y, w, h int) {
	setWindowPos.Call(hwnd, 0, uintptr(x), uintptr(y), uintptr(w), uintptr(h), swpNoZOrder|swpNoActivate)
}
func show(hwnd uintptr, on bool) {
	c := uintptr(swHide)
	if on {
		c = swShow
	}
	showWindow.Call(hwnd, c)
}
func enable(hwnd uintptr, on bool) {
	v := uintptr(0)
	if on {
		v = 1
	}
	enableWindow.Call(hwnd, v)
}
func comboReset(hwnd uintptr) { sendMessageW.Call(hwnd, cbResetContent, 0, 0) }
func comboAdd(hwnd uintptr, s string) {
	sendMessageW.Call(hwnd, cbAddString, 0, uintptr(unsafe.Pointer(utf16Ptr(s))))
}
func comboSet(hwnd uintptr, i int) { sendMessageW.Call(hwnd, cbSetCurSel, uintptr(i), 0) }
func comboIndex(hwnd uintptr) int {
	r, _, _ := sendMessageW.Call(hwnd, cbGetCurSel, 0, 0)
	return int(int32(r))
}
func comboText(hwnd uintptr) string {
	i := comboIndex(hwnd)
	if i < 0 {
		return ""
	}
	n, _, _ := sendMessageW.Call(hwnd, cbGetLBTextLen, uintptr(i), 0)
	b := make([]uint16, int(n)+2)
	sendMessageW.Call(hwnd, cbGetLBText, uintptr(i), uintptr(unsafe.Pointer(&b[0])))
	return syscall.UTF16ToString(b)
}
func listReset(hwnd uintptr) { sendMessageW.Call(hwnd, lbResetContent, 0, 0) }
func listAdd(hwnd uintptr, s string) {
	sendMessageW.Call(hwnd, lbAddString, 0, uintptr(unsafe.Pointer(utf16Ptr(s))))
}
func listIndex(hwnd uintptr) int {
	r, _, _ := sendMessageW.Call(hwnd, lbGetCurSel, 0, 0)
	return int(int32(r))
}
func listSelect(hwnd uintptr, i int) {
	sendMessageW.Call(hwnd, lbSetCurSel, uintptr(i), 0)
	sendMessageW.Call(hwnd, lbSetTopIndex, uintptr(maxInt(0, i-3)), 0)
}
func appendEdit(hwnd uintptr, line string) {
	if hwnd == 0 || line == "" {
		return
	}
	n, _, _ := getWindowTextLengthW.Call(hwnd)
	prefix := ""
	if n > 0 {
		prefix = "\r\n"
	}
	sendMessageW.Call(hwnd, emSetSel, n, n)
	p := utf16Ptr(prefix + line)
	sendMessageW.Call(hwnd, emReplaceSel, 0, uintptr(unsafe.Pointer(p)))
	sendMessageW.Call(hwnd, emScrollCaret, 0, 0)
}

func progressInit(hwnd uintptr) {
	sendMessageW.Call(hwnd, pbmSetRange32, 0, 1000)
	sendMessageW.Call(hwnd, pbmSetPos, 0, 0)
}
func progressSet(hwnd uintptr, pct float64) {
	if pct < 0 {
		pct = 0
	}
	if pct > 100 {
		pct = 100
	}
	sendMessageW.Call(hwnd, pbmSetPos, uintptr(int(pct*10+0.5)), 0)
}
func focused(hwnd uintptr) {
	if hwnd != 0 {
		setFocus.Call(hwnd)
	}
}
func ctrlDown() bool {
	r, _, _ := getKeyState.Call(vkControl)
	return int16(r&0xffff) < 0
}

func trackRange(hwnd uintptr, min, max int) {
	sendMessageW.Call(hwnd, tbmSetRange, 1, uintptr(uint32(min)|uint32(max)<<16))
}
func trackSet(hwnd uintptr, pos int) { sendMessageW.Call(hwnd, tbmSetPos, 1, uintptr(pos)) }
func trackPos(hwnd uintptr) int      { r, _, _ := sendMessageW.Call(hwnd, tbmGetPos, 0, 0); return int(r) }
func checkSet(hwnd uintptr, on bool) {
	v := uintptr(0)
	if on {
		v = bstChecked
	}
	sendMessageW.Call(hwnd, bmSetCheck, v, 0)
}
func checkGet(hwnd uintptr) bool {
	r, _, _ := sendMessageW.Call(hwnd, bmGetCheck, 0, 0)
	return r == bstChecked
}
func clientRect(hwnd uintptr) rect {
	var r rect
	getClientRect.Call(hwnd, uintptr(unsafe.Pointer(&r)))
	return r
}
func invalidate(hwnd uintptr) { invalidateRect.Call(hwnd, 0, 0) }
func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

func initControls() {
	c := initCommonControls{Size: uint32(unsafe.Sizeof(initCommonControls{})), ICC: 0x00000024}
	initCommonControlsEx.Call(uintptr(unsafe.Pointer(&c)))
}

func drawBGRA(hdc uintptr, dst rect, w, h int, pixels []byte) {
	if w <= 0 || h <= 0 || len(pixels) < w*h*4 {
		return
	}
	bi := bitmapInfo{}
	bi.Header.Size = uint32(unsafe.Sizeof(bitmapInfoHeader{}))
	bi.Header.Width = int32(w)
	bi.Header.Height = -int32(h)
	bi.Header.Planes = 1
	bi.Header.BitCount = 32
	bi.Header.Compression = 0
	stretchDIBits.Call(hdc, uintptr(dst.Left), uintptr(dst.Top), uintptr(dst.Right-dst.Left), uintptr(dst.Bottom-dst.Top), 0, 0, uintptr(w), uintptr(h), uintptr(unsafe.Pointer(&pixels[0])), uintptr(unsafe.Pointer(&bi)), 0, 0x00CC0020)
	runtime.KeepAlive(pixels)
}

func windowRect(hwnd uintptr) rect {
	var r rect
	getWindowRect.Call(hwnd, uintptr(unsafe.Pointer(&r)))
	return r
}
func styleIndex() uintptr           { v := int32(gwlpStyle); return uintptr(v) }
func classBackgroundIndex() uintptr { v := int32(gclpHbrBackground); return uintptr(v) }
func windowStyle(hwnd uintptr) uintptr {
	r, _, _ := getWindowLongPtrW.Call(hwnd, styleIndex())
	return r
}
func setWindowStyle(hwnd uintptr, style uintptr) {
	setWindowLongPtrW.Call(hwnd, styleIndex(), style)
}
func monitorRectForWindow(hwnd uintptr) rect {
	h, _, _ := monitorFromWindow.Call(hwnd, monitorDefaultToNearest)
	if h == 0 {
		return windowRect(hwnd)
	}
	mi := monitorInfo{Size: uint32(unsafe.Sizeof(monitorInfo{}))}
	if ok, _, _ := getMonitorInfoW.Call(h, uintptr(unsafe.Pointer(&mi))); ok == 0 {
		return windowRect(hwnd)
	}
	return mi.Monitor
}

func confirm(parent uintptr, title, message string) bool {
	r, _, _ := messageBoxW.Call(parent, uintptr(unsafe.Pointer(utf16Ptr(message))), uintptr(unsafe.Pointer(utf16Ptr(title))), mbYesNo|mbIconWarning|mbDefButton2)
	return r == idYes
}
func winErr(prefix string, err error) error {
	if err == nil {
		return fmt.Errorf("%s", prefix)
	}
	return fmt.Errorf("%s: %w", prefix, err)
}
