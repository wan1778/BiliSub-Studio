//go:build windows

package nativeui

import (
	"context"
	"fmt"
	"math"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"

	"bilisubstudio/internal/application"
	"bilisubstudio/internal/nativeplayer"
	"bilisubstudio/internal/ocr"
	"bilisubstudio/internal/qrcode"
	"bilisubstudio/internal/subtitle"
	"bilisubstudio/internal/video"
	"bilisubstudio/internal/videoedit"
)

const (
	idNavSub = 1001 + iota
	idNavVideo
	idNavOCR
	idNavEditor
	idNavSettings
	idSubURL
	idSubAnalyze
	idSubTrack
	idSubFormat
	idSubOut
	idSubPickOut
	idSubOpenOut
	idSubDownload
	idSubCancel
	idVideoURL
	idVideoAnalyze
	idVideoQuality
	idVideoMode
	idVideoSpeed
	idVideoContainer
	idVideoOut
	idVideoPickOut
	idVideoOpenOut
	idVideoDownload
	idVideoCancel
	idOCRPath
	idOCRPick
	idOCRPreset
	idOCRPlay
	idOCRMute
	idOCRFullscreen
	idOCRTimeline
	idOCRTop
	idOCRBottom
	idOCRLeft
	idOCRRight
	idOCRMode
	idOCRSensitivity
	idOCRDevice
	idOCRParallel
	idOCRPrepare
	idOCRTest
	idOCRStart
	idOCRPause
	idOCRRestart
	idOCRClear
	idOCRExport
	idOCROut
	idOCRPickOut
	idOCROpenOut
	idOCRCueList
	idEditorPath
	idEditorPick
	idEditorPlay
	idEditorMute
	idEditorFullscreen
	idEditorSubtitlePreset
	idEditorWatermarkPreset
	idEditorDelete
	idEditorUndo
	idEditorTimeline
	idEditorX
	idEditorY
	idEditorW
	idEditorH
	idEditorEffect
	idEditorStrength
	idEditorWhole
	idEditorStart
	idEditorSetStart
	idEditorEnd
	idEditorSetEnd
	idEditorOut
	idEditorPickOut
	idEditorOpenOut
	idEditorName
	idEditorRegionList
	idEditorExport
	idEditorCancel
	idTheme
	idDefaultOut
	idDefaultOutPick
	idDefaultOutOpen
	idCookie
	idCookieSave
	idCookieDelete
	idQR
	idAutoUpdate
	idCheckUpdate
	idApplyUpdate
	idCleanup
	idResetTools
	idRemoveOCR
	idCloseApp
	idBugNote
	idBugSend
)

const (
	wmLButtonDown = 0x0201
	wmLButtonUp   = 0x0202
	wmMouseMove   = 0x0200
)

type frameState struct {
	mu    sync.Mutex
	frame nativeplayer.Frame
}
type jobBinding struct {
	id    string
	kind  int
	after int
	logs  []string
}

type window struct {
	app       *application.App
	hwnd      uintptr
	page      int
	pages     [5][]uintptr
	captions  [5][]uintptr
	nav       [5]uintptr
	status    uintptr
	version   uintptr
	navHelp   uintptr
	pageTitle [5]uintptr
	pageHelp  [5]uintptr
	async     chan func()
	closed    bool
	closing   bool
	result    RunResult
	dark      bool
	bgBrush   uintptr

	subURL, subAnalyze, subMeta, subTrack, subFormat, subOut, subPickOut, subOpenOut, subDownload, subCancel, subProgress, subState, subLog                                                       uintptr
	subTracks                                                                                                                                                                                     []video.SubtitleTrack
	videoURL, videoAnalyze, videoMeta, videoQuality, videoMode, videoSpeed, videoContainer, videoOut, videoPickOut, videoOpenOut, videoDownload, videoCancel, videoProgress, videoState, videoLog uintptr

	ocrPath, ocrPick, ocrPreset, ocrPlay, ocrMute, ocrFullscreen, ocrTimeline, ocrTime, ocrTopLabel, ocrTop, ocrBottomLabel, ocrBottom, ocrLeftLabel, ocrLeft, ocrRightLabel, ocrRight, ocrMode, ocrSensitivity, ocrDevice, ocrParallel, ocrPrepare, ocrTest, ocrStart, ocrPause, ocrRestart, ocrClear, ocrExport, ocrOut, ocrPickOut, ocrOpenOut, ocrProgress, ocrCueSummary, ocrCueList, ocrStatus, ocrMetrics uintptr
	ocrPlayer                                                                                                                                                                                                                                                                                                                                                                                                    *nativeplayer.Player
	ocrFrame                                                                                                                                                                                                                                                                                                                                                                                                     frameState
	ocrInfoDuration                                                                                                                                                                                                                                                                                                                                                                                              float64
	ocrCues                                                                                                                                                                                                                                                                                                                                                                                                      []ocr.Cue
	ocrTotalCues                                                                                                                                                                                                                                                                                                                                                                                                 int
	ocrHasCheckpoint                                                                                                                                                                                                                                                                                                                                                                                             bool

	editorPath, editorPick, editorPlay, editorMute, editorFullscreen, editorSubtitlePreset, editorWatermarkPreset, editorDelete, editorUndo, editorTimeline, editorTime, editorXLabel, editorX, editorYLabel, editorY, editorWLabel, editorW, editorHLabel, editorH, editorEffect, editorStrength, editorWhole, editorStartLabel, editorStart, editorSetStart, editorEndLabel, editorEnd, editorSetEnd, editorOut, editorPickOut, editorOpenOut, editorName, editorRegionList, editorExport, editorCancel, editorProgress, editorStatus, editorLog uintptr
	editorPlayer                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   *nativeplayer.Player
	editorFrame                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    frameState
	editorDuration                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 float64
	editorInfoW, editorInfoH                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       int
	editorModel                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    editorModel
	syncingEditor                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  bool

	theme, defaultOut, defaultOutPick, defaultOutOpen, settingsRoot, settingsDrive, settingsCookieState, settingsStorage, settingsCookie, cookieSave, cookieDelete, qrBtn, qrState, autoUpdate, checkUpdate, applyUpdate, cleanup, resetTools, removeOCR, closeApp, bugNote, bugSend, settingsStatus uintptr

	active      *jobBinding
	previewRect rect
	dragging    bool
	dragStart   point
	fullscreen  bool
	normalRect  rect
	normalStyle uintptr
	qrRect      rect
	qrCode      qrcode.Matrix
	qrKey       string
	qrTimer     time.Time

	dpi         uint32
	fontNormal  uintptr
	fontTitle   uintptr
	fontCaption uintptr
	fontSmall   uintptr
	tooltip     uintptr
	tooltipKeep []*uint16

	subAnalyzing        bool
	videoAnalyzing      bool
	ocrPreparing        bool
	ocrTesting          bool
	updateBusy          bool
	qrBusy              bool
	cookieBusy          bool
	bugBusy             bool
	videoReady          bool
	updateAvailable     bool
	cookieSaved         bool
	subAnalyzedURL      string
	videoAnalyzedURL    string
	ocrValidationErr    string
	editorValidationErr string
}

var activeWindow *window
var mainWndProc = syscall.NewCallback(wndProc)

func Run(app *application.App) (RunResult, error) {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	enablePerMonitorDPI()
	initControls()
	cfg := app.State.SnapshotConfig()
	dark := !strings.EqualFold(strings.TrimSpace(cfg.Theme), "light")
	w := &window{app: app, page: pageSubtitle, async: make(chan func(), 128), editorModel: newEditorModel(), dark: dark, dpi: systemDPI()}
	activeWindow = w
	className := utf16Ptr("BiliSubStudioNativeWindow")
	hinst, _, _ := getModuleHandleW.Call(0)
	cursor, _, _ := loadCursorW.Call(0, idcArrow)
	bg := uintptr(0x00F6F8FC)
	if dark {
		bg = 0x00252220
	}
	brush, _, _ := createSolidBrush.Call(bg)
	w.bgBrush = brush
	wc := wndClassEx{Size: uint32(unsafe.Sizeof(wndClassEx{})), WndProc: mainWndProc, Instance: hinst, Cursor: cursor, Background: brush, ClassName: className}
	if r, _, e := registerClassExW.Call(uintptr(unsafe.Pointer(&wc))); r == 0 {
		deleteObject.Call(brush)
		return RunResult{}, winErr("RegisterClassExW", e)
	}
	initialW, initialH := w.px(1400), w.px(880)
	h, _, e := createWindowExW.Call(wsExControlParent, uintptr(unsafe.Pointer(className)), uintptr(unsafe.Pointer(utf16Ptr("BiliSub Studio"))), wsOverlappedWindow|wsVisible, uintptr(w.px(100)), uintptr(w.px(70)), uintptr(initialW), uintptr(initialH), 0, 0, hinst, 0)
	if h == 0 {
		deleteObject.Call(brush)
		return RunResult{}, winErr("CreateWindowExW", e)
	}
	w.hwnd = h
	w.dpi = windowDPI(h)
	w.build()
	w.rebuildFonts()
	w.initTooltips()
	w.applyNativeTheme()
	w.layout()
	w.refreshStatus(true)
	setTimer.Call(h, 1, 250, 0)
	showWindow.Call(h, swShow)
	updateWindow.Call(h)
	var m msg
	for {
		r, _, _ := getMessageW.Call(uintptr(unsafe.Pointer(&m)), 0, 0, 0)
		if int32(r) <= 0 {
			break
		}
		if handled, _, _ := isDialogMessageW.Call(w.hwnd, uintptr(unsafe.Pointer(&m))); handled != 0 {
			continue
		}
		translateMessage.Call(uintptr(unsafe.Pointer(&m)))
		dispatchMessageW.Call(uintptr(unsafe.Pointer(&m)))
	}
	killTimer.Call(h, 1)
	if w.ocrPlayer != nil {
		w.ocrPlayer.Close()
	}
	if w.editorPlayer != nil {
		w.editorPlayer.Close()
	}
	w.destroyFonts()
	if w.tooltip != 0 {
		destroyWindow.Call(w.tooltip)
		w.tooltip = 0
	}
	if w.bgBrush != 0 {
		deleteObject.Call(w.bgBrush)
	}
	activeWindow = nil
	return w.result, nil
}

func wndProc(hwnd uintptr, msgID uint32, wparam, lparam uintptr) uintptr {
	w := activeWindow
	if w != nil && (w.hwnd == 0 || w.hwnd == hwnd) {
		switch msgID {
		case wmCommand:
			w.command(int(loword(wparam)), int(hiword(wparam)))
			return 0
		case wmTimer:
			w.tick()
			return 0
		case wmSize:
			w.layout()
			return 0
		case wmDpiChanged:
			w.dpi = uint32(hiword(wparam))
			if w.dpi == 0 {
				w.dpi = windowDPI(hwnd)
			}
			if lparam != 0 {
				r := (*rect)(unsafe.Pointer(lparam))
				setWindowPos.Call(hwnd, 0, uintptr(r.Left), uintptr(r.Top), uintptr(r.Right-r.Left), uintptr(r.Bottom-r.Top), swpNoZOrder|swpNoActivate)
			}
			w.rebuildFonts()
			w.layout()
			return 0
		case wmGetMinMaxInfo:
			if lparam != 0 {
				mmi := (*minMaxInfo)(unsafe.Pointer(lparam))
				mmi.MinTrackSize = point{X: int32(w.px(1100)), Y: int32(w.px(840))}
			}
			return 0
		case wmHScroll:
			w.scroll(lparam)
			return 0
		case wmPaint:
			w.paint()
			return 0
		case wmCtlColorStatic, wmCtlColorEdit, wmCtlColorList:
			if w.dark && w.bgBrush != 0 {
				setTextColor.Call(wparam, 0x00F3F4F6)
				setBkColor.Call(wparam, 0x00252220)
				return w.bgBrush
			}
		case wmAppAsync:
			w.drainAsync()
			return 0
		case wmAppFrame:
			invalidate(hwnd)
			return 0
		case wmAppState:
			w.syncPlayerButtons()
			return 0
		case wmAppCloseReady:
			w.finishClose()
			return 0
		case wmLButtonDown:
			w.mouseDown(lparam)
			return 0
		case wmMouseMove:
			w.mouseMove(lparam)
			return 0
		case wmLButtonUp:
			w.mouseUp(lparam)
			return 0
		case wmKeyDown:
			if w.fullscreen && wparam == vkEscape {
				w.toggleFullscreen()
				return 0
			}
			if ctrlDown() && wparam >= uintptr('1') && wparam <= uintptr('5') {
				w.selectPage(int(wparam - uintptr('1')))
				return 0
			}
			if wparam == vkF1 {
				u := uxForPage(w.page)
				w.setStatus(u.Help)
				return 0
			}
		case wmClose:
			w.requestClose()
			return 0
		case wmDestroy:
			postQuitMessage.Call(0)
			return 0
		}
	}
	r, _, _ := defWindowProcW.Call(hwnd, uintptr(msgID), wparam, lparam)
	return r
}

func (w *window) add(page int, class, textValue string, style uint32, id int) uintptr {
	h := createControl(w.hwnd, class, textValue, style, id)
	if page >= 0 {
		w.pages[page] = append(w.pages[page], h)
	}
	return h
}
func (w *window) label(page int, s string) uintptr { return w.add(page, "STATIC", s, ssLeft, 0) }
func (w *window) caption(page int, s string) uintptr {
	h := w.label(page, s)
	w.captions[page] = append(w.captions[page], h)
	return h
}
func (w *window) edit(page int, s string, readonly bool) uintptr {
	st := uint32(wsBorder | wsTabStop | esAutoHScroll)
	if readonly {
		st |= esReadOnly
	}
	return w.add(page, "EDIT", s, st, 0)
}
func (w *window) editID(page int, s string, readonly bool, id int) uintptr {
	st := uint32(wsBorder | wsTabStop | esAutoHScroll)
	if readonly {
		st |= esReadOnly
	}
	return w.add(page, "EDIT", s, st, id)
}
func (w *window) button(page int, s string, id int) uintptr {
	return w.add(page, "BUTTON", s, wsTabStop|bsPushButton, id)
}
func (w *window) combo(page int, items []string, sel int, id int) uintptr {
	h := w.add(page, "COMBOBOX", "", wsTabStop|cbsDropDownList, id)
	for _, x := range items {
		comboAdd(h, x)
	}
	comboSet(h, sel)
	return h
}
func (w *window) logBox(page int) uintptr {
	return w.add(page, "EDIT", "", wsBorder|wsVScroll|esMultiline|esReadOnly|esWantReturn, 0)
}

func (w *window) build() {
	for i, s := range []string{"Phụ đề", "Video", "OCR phụ đề", "Chỉnh video", "Cài đặt"} {
		w.nav[i] = w.add(-1, "BUTTON", s, wsTabStop|bsPushButton, idNavSub+i)
	}
	w.status = w.add(-1, "STATIC", "Sẵn sàng", ssLeft, 0)
	w.version = w.add(-1, "STATIC", "BiliSub Studio", ssLeft, 0)
	w.navHelp = w.add(-1, "STATIC", "Ctrl+1…5: chuyển tab\r\nTab/Shift+Tab: di chuyển\r\nF1: hướng dẫn", ssLeft, 0)
	for p := pageSubtitle; p <= pageSettings; p++ {
		u := uxForPage(p)
		w.pageTitle[p] = w.label(p, u.Title)
		w.pageHelp[p] = w.label(p, u.Help)
	}
	cfg := w.app.State.SnapshotConfig()
	// Subtitle
	w.caption(pageSubtitle, "Link Bilibili")
	w.subURL = w.editID(pageSubtitle, "", false, idSubURL)
	w.subAnalyze = w.button(pageSubtitle, "Kiểm tra", idSubAnalyze)
	w.subMeta = w.label(pageSubtitle, "Chưa kiểm tra video")
	w.caption(pageSubtitle, "Track phụ đề")
	w.subTrack = w.combo(pageSubtitle, []string{"Chưa có track"}, 0, idSubTrack)
	w.caption(pageSubtitle, "Định dạng")
	w.subFormat = w.combo(pageSubtitle, []string{"SRT", "TXT", "JSON"}, 0, idSubFormat)
	w.caption(pageSubtitle, "Thư mục xuất")
	w.subOut = w.editID(pageSubtitle, cfg.OutputDir, false, idSubOut)
	w.subPickOut = w.button(pageSubtitle, "Chọn", idSubPickOut)
	w.subOpenOut = w.button(pageSubtitle, "Mở", idSubOpenOut)
	w.subDownload = w.button(pageSubtitle, "Tải phụ đề", idSubDownload)
	w.subCancel = w.button(pageSubtitle, "Hủy", idSubCancel)
	w.subProgress = w.add(pageSubtitle, progressClass, "", 0, 0)
	progressInit(w.subProgress)
	w.subState = w.label(pageSubtitle, "Bước tiếp theo: nhập link và bấm Kiểm tra.")
	w.subLog = w.logBox(pageSubtitle)
	setText(w.subLog, "Nhật ký tải phụ đề sẽ xuất hiện tại đây.")
	// Video
	w.caption(pageVideo, "Link Bilibili")
	w.videoURL = w.editID(pageVideo, "", false, idVideoURL)
	w.videoAnalyze = w.button(pageVideo, "Kiểm tra", idVideoAnalyze)
	w.videoMeta = w.label(pageVideo, "Chưa kiểm tra video")
	w.caption(pageVideo, "Chất lượng")
	w.videoQuality = w.combo(pageVideo, []string{"best"}, 0, idVideoQuality)
	w.caption(pageVideo, "Nội dung")
	w.videoMode = w.combo(pageVideo, []string{"Video + Audio", "Chỉ video", "Chỉ audio"}, 0, idVideoMode)
	w.caption(pageVideo, "Tốc độ")
	w.videoSpeed = w.combo(pageVideo, []string{"Ổn định", "Nhanh", "Turbo"}, 1, idVideoSpeed)
	w.caption(pageVideo, "Container")
	w.videoContainer = w.combo(pageVideo, []string{"MP4", "MKV"}, 0, idVideoContainer)
	w.caption(pageVideo, "Thư mục xuất")
	w.videoOut = w.editID(pageVideo, cfg.OutputDir, false, idVideoOut)
	w.videoPickOut = w.button(pageVideo, "Chọn", idVideoPickOut)
	w.videoOpenOut = w.button(pageVideo, "Mở", idVideoOpenOut)
	w.videoDownload = w.button(pageVideo, "Tải video", idVideoDownload)
	w.videoCancel = w.button(pageVideo, "Hủy", idVideoCancel)
	w.videoProgress = w.add(pageVideo, progressClass, "", 0, 0)
	progressInit(w.videoProgress)
	w.videoState = w.label(pageVideo, "Bước tiếp theo: nhập link và bấm Kiểm tra.")
	w.videoLog = w.logBox(pageVideo)
	setText(w.videoLog, "Nhật ký tải video sẽ xuất hiện tại đây.")
	// OCR
	w.caption(pageOCR, "Video nguồn")
	w.ocrPath = w.edit(pageOCR, "", true)
	w.ocrPick = w.button(pageOCR, "Chọn video", idOCRPick)
	w.ocrPreset = w.button(pageOCR, "Vùng phụ đề", idOCRPreset)
	w.ocrPlay = w.button(pageOCR, "Phát", idOCRPlay)
	w.ocrMute = w.button(pageOCR, "Bật/Tắt tiếng", idOCRMute)
	w.ocrFullscreen = w.button(pageOCR, "Toàn màn hình", idOCRFullscreen)
	w.ocrTimeline = w.add(pageOCR, "msctls_trackbar32", "", wsTabStop, idOCRTimeline)
	trackRange(w.ocrTimeline, 0, 10000)
	w.ocrTime = w.label(pageOCR, "00:00 / 00:00")
	w.caption(pageOCR, "ROI phụ đề (%)")
	w.ocrTopLabel = w.label(pageOCR, "Trên")
	w.ocrTop = w.editID(pageOCR, strconv.Itoa(cfg.OCRTop), false, idOCRTop)
	w.ocrBottomLabel = w.label(pageOCR, "Dưới")
	w.ocrBottom = w.editID(pageOCR, strconv.Itoa(cfg.OCRBottom), false, idOCRBottom)
	w.ocrLeftLabel = w.label(pageOCR, "Trái")
	w.ocrLeft = w.editID(pageOCR, strconv.Itoa(cfg.OCRLeft), false, idOCRLeft)
	w.ocrRightLabel = w.label(pageOCR, "Phải")
	w.ocrRight = w.editID(pageOCR, strconv.Itoa(cfg.OCRRight), false, idOCRRight)
	w.caption(pageOCR, "Chế độ quét")
	w.ocrMode = w.combo(pageOCR, []string{"Chính xác · 4 fps", "Cân bằng · 2.5 fps", "Nhanh · 1.5 fps"}, 0, idOCRMode)
	w.caption(pageOCR, "Độ nhạy")
	w.ocrSensitivity = w.combo(pageOCR, []string{"Nhạy", "Cân bằng", "Ít nhạy"}, 0, idOCRSensitivity)
	w.caption(pageOCR, "Thiết bị OCR")
	w.ocrDevice = w.combo(pageOCR, []string{"Tự động · ưu tiên GPU", "GPU · NVIDIA", "CPU", "CPU + GPU"}, deviceIndex(cfg.OCRDevice), idOCRDevice)
	w.caption(pageOCR, "Luồng quét OCR")
	w.ocrParallel = w.combo(pageOCR, []string{"Tự động · theo máy", "1", "2", "4", "8", "16"}, 0, idOCRParallel)
	w.ocrPrepare = w.button(pageOCR, "Chuẩn bị bộ nhận diện", idOCRPrepare)
	w.ocrTest = w.button(pageOCR, "Test OCR", idOCRTest)
	w.ocrStart = w.button(pageOCR, "Bắt đầu / Tiếp tục", idOCRStart)
	w.ocrPause = w.button(pageOCR, "Tạm dừng", idOCRPause)
	w.ocrRestart = w.button(pageOCR, "Quét lại từ đầu", idOCRRestart)
	w.ocrClear = w.button(pageOCR, "Xóa danh sách", idOCRClear)
	w.ocrExport = w.button(pageOCR, "Xuất SRT", idOCRExport)
	w.caption(pageOCR, "Thư mục xuất SRT")
	w.ocrOut = w.editID(pageOCR, cfg.OutputDir, false, idOCROut)
	w.ocrPickOut = w.button(pageOCR, "Chọn", idOCRPickOut)
	w.ocrOpenOut = w.button(pageOCR, "Mở", idOCROpenOut)
	w.ocrProgress = w.add(pageOCR, progressClass, "", 0, 0)
	progressInit(w.ocrProgress)
	w.ocrStatus = w.label(pageOCR, "Chưa chọn video · hãy chọn video nguồn để bắt đầu.")
	w.ocrMetrics = w.logBox(pageOCR)
	setText(w.ocrMetrics, "OCR: chưa có telemetry. Chọn video và Test OCR hoặc bắt đầu quét.")
	w.ocrCueSummary = w.label(pageOCR, "Danh sách phụ đề: 0 / 0 câu")
	w.ocrCueList = w.add(pageOCR, "LISTBOX", "", wsBorder|wsVScroll|lbsNotify, idOCRCueList)
	// Editor
	w.caption(pageEditor, "Video nguồn")
	w.editorPath = w.edit(pageEditor, "", true)
	w.editorPick = w.button(pageEditor, "Chọn video", idEditorPick)
	w.editorPlay = w.button(pageEditor, "Phát", idEditorPlay)
	w.editorMute = w.button(pageEditor, "Bật/Tắt tiếng", idEditorMute)
	w.editorFullscreen = w.button(pageEditor, "Toàn màn hình", idEditorFullscreen)
	w.editorSubtitlePreset = w.button(pageEditor, "Vùng phụ đề", idEditorSubtitlePreset)
	w.editorWatermarkPreset = w.button(pageEditor, "Vùng watermark", idEditorWatermarkPreset)
	w.editorDelete = w.button(pageEditor, "Xóa vùng", idEditorDelete)
	w.editorUndo = w.button(pageEditor, "Hoàn tác", idEditorUndo)
	w.editorTimeline = w.add(pageEditor, "msctls_trackbar32", "", wsTabStop, idEditorTimeline)
	trackRange(w.editorTimeline, 0, 10000)
	w.editorTime = w.label(pageEditor, "00:00 / 00:00")
	w.caption(pageEditor, "Vùng đang chọn (%)")
	w.editorXLabel = w.label(pageEditor, "X")
	w.editorX = w.editID(pageEditor, "5", false, idEditorX)
	w.editorYLabel = w.label(pageEditor, "Y")
	w.editorY = w.editID(pageEditor, "70", false, idEditorY)
	w.editorWLabel = w.label(pageEditor, "Rộng")
	w.editorW = w.editID(pageEditor, "90", false, idEditorW)
	w.editorHLabel = w.label(pageEditor, "Cao")
	w.editorH = w.editID(pageEditor, "20", false, idEditorH)
	w.caption(pageEditor, "Hiệu ứng")
	w.editorEffect = w.combo(pageEditor, []string{"Làm mờ", "Mosaic", "Che đen"}, 0, idEditorEffect)
	w.caption(pageEditor, "Độ mạnh")
	w.editorStrength = w.editID(pageEditor, "18", false, idEditorStrength)
	w.editorWhole = w.add(pageEditor, "BUTTON", "Áp dụng toàn video", wsTabStop|bsAutoCheckBox, idEditorWhole)
	checkSet(w.editorWhole, true)
	w.caption(pageEditor, "Khoảng thời gian")
	w.editorStartLabel = w.label(pageEditor, "Bắt đầu (giây)")
	w.editorStart = w.editID(pageEditor, "0", false, idEditorStart)
	w.editorSetStart = w.button(pageEditor, "Lấy hiện tại", idEditorSetStart)
	w.editorEndLabel = w.label(pageEditor, "Kết thúc (giây)")
	w.editorEnd = w.editID(pageEditor, "0", false, idEditorEnd)
	w.editorSetEnd = w.button(pageEditor, "Lấy hiện tại", idEditorSetEnd)
	w.caption(pageEditor, "Thư mục xuất")
	w.editorOut = w.editID(pageEditor, cfg.OutputDir, false, idEditorOut)
	w.editorPickOut = w.button(pageEditor, "Chọn", idEditorPickOut)
	w.editorOpenOut = w.button(pageEditor, "Mở", idEditorOpenOut)
	w.caption(pageEditor, "Tên file")
	w.editorName = w.editID(pageEditor, "", false, idEditorName)
	w.caption(pageEditor, "Danh sách vùng")
	w.editorRegionList = w.add(pageEditor, "LISTBOX", "", wsBorder|wsVScroll|lbsNotify, idEditorRegionList)
	w.editorExport = w.button(pageEditor, "Xuất video", idEditorExport)
	w.editorCancel = w.button(pageEditor, "Hủy", idEditorCancel)
	w.editorProgress = w.add(pageEditor, progressClass, "", 0, 0)
	progressInit(w.editorProgress)
	w.editorStatus = w.label(pageEditor, "Chưa chọn video · sau khi chọn, kéo trên preview hoặc dùng preset để tạo vùng.")
	w.editorLog = w.logBox(pageEditor)
	setText(w.editorLog, "Nhật ký xuất video sẽ xuất hiện tại đây.")
	// Settings
	w.caption(pageSettings, "Giao diện")
	w.theme = w.combo(pageSettings, []string{"Dark", "Light"}, themeIndex(cfg.Theme), idTheme)
	w.caption(pageSettings, "Dữ liệu ứng dụng")
	w.settingsRoot = w.label(pageSettings, "Thư mục ứng dụng: —")
	w.settingsDrive = w.label(pageSettings, "Ổ đang khóa: —")
	w.settingsCookieState = w.label(pageSettings, "Cookie: —")
	w.caption(pageSettings, "Thư mục lưu mặc định")
	w.defaultOut = w.edit(pageSettings, cfg.OutputDir, true)
	w.defaultOutPick = w.button(pageSettings, "Chọn", idDefaultOutPick)
	w.defaultOutOpen = w.button(pageSettings, "Mở", idDefaultOutOpen)
	w.caption(pageSettings, "Cookie / SESSDATA Bilibili")
	w.settingsCookie = w.editID(pageSettings, "", false, idCookie)
	w.cookieSave = w.button(pageSettings, "Lưu và kiểm tra", idCookieSave)
	w.cookieDelete = w.button(pageSettings, "Xóa đăng nhập", idCookieDelete)
	w.qrBtn = w.button(pageSettings, "Đăng nhập QR", idQR)
	w.qrState = w.label(pageSettings, "QR: chưa tạo")
	w.caption(pageSettings, "Cập nhật ứng dụng")
	w.autoUpdate = w.add(pageSettings, "BUTTON", "Tự kiểm tra cập nhật", wsTabStop|bsAutoCheckBox, idAutoUpdate)
	checkSet(w.autoUpdate, cfg.CheckUpdates)
	w.checkUpdate = w.button(pageSettings, "Kiểm tra cập nhật", idCheckUpdate)
	w.applyUpdate = w.button(pageSettings, "Cập nhật ngay", idApplyUpdate)
	w.caption(pageSettings, "Dung lượng")
	w.settingsStorage = w.label(pageSettings, "Data — · Tools — · OCR — · Temp — · Cache —")
	w.caption(pageSettings, "Dọn dẹp / Ứng dụng")
	w.cleanup = w.button(pageSettings, "Dọn Temp/Cache", idCleanup)
	w.resetTools = w.button(pageSettings, "Đặt lại Tools", idResetTools)
	w.removeOCR = w.button(pageSettings, "Xóa bộ OCR", idRemoveOCR)
	w.closeApp = w.button(pageSettings, "Đóng BiliSub Studio hoàn toàn", idCloseApp)
	w.caption(pageSettings, "Báo lỗi")
	w.bugNote = w.add(pageSettings, "EDIT", "", wsBorder|wsVScroll|esMultiline|esWantReturn|wsTabStop, idBugNote)
	w.bugSend = w.button(pageSettings, "Gửi báo lỗi", idBugSend)
	w.settingsStatus = w.logBox(pageSettings)
	setText(w.settingsStatus, "Trạng thái QR, cập nhật và các thao tác bảo trì sẽ hiển thị tại đây.")
	w.selectPage(pageSubtitle)
	w.syncControls()
}

func deviceIndex(mode string) int {
	switch mode {
	case "gpu":
		return 1
	case "cpu":
		return 2
	case "hybrid":
		return 3
	default:
		return 0
	}
}

func (w *window) px(v int) int {
	if w == nil || w.dpi == 0 || w.dpi == 96 {
		return v
	}
	return int((int64(v)*int64(w.dpi) + 48) / 96)
}

func (w *window) logical(v int) int {
	if w == nil || w.dpi == 0 || w.dpi == 96 {
		return v
	}
	return int((int64(v)*96 + int64(w.dpi)/2) / int64(w.dpi))
}

func (w *window) mv(hwnd uintptr, x, y, width, height int) {
	move(hwnd, w.px(x), w.px(y), w.px(width), w.px(height))
}

func (w *window) pixelRect(r rect) rect {
	return rect{Left: int32(w.px(int(r.Left))), Top: int32(w.px(int(r.Top))), Right: int32(w.px(int(r.Right))), Bottom: int32(w.px(int(r.Bottom)))}
}

func (w *window) destroyFonts() {
	for _, f := range []uintptr{w.fontNormal, w.fontTitle, w.fontCaption, w.fontSmall} {
		if f != 0 {
			deleteObject.Call(f)
		}
	}
	w.fontNormal, w.fontTitle, w.fontCaption, w.fontSmall = 0, 0, 0, 0
}

func (w *window) rebuildFonts() {
	w.destroyFonts()
	dpi := w.dpi
	if dpi == 0 {
		dpi = 96
	}
	w.fontNormal = createUIFont(9, 400, dpi)
	w.fontTitle = createUIFont(15, 600, dpi)
	w.fontCaption = createUIFont(9, 600, dpi)
	w.fontSmall = createUIFont(8, 400, dpi)
	for _, h := range w.allControls() {
		setFont(h, w.fontNormal)
	}
	for p := 0; p < 5; p++ {
		setFont(w.pageTitle[p], w.fontTitle)
		setFont(w.pageHelp[p], w.fontSmall)
		for _, h := range w.captions[p] {
			setFont(h, w.fontCaption)
		}
	}
	setFont(w.version, w.fontCaption)
	setFont(w.navHelp, w.fontSmall)
	setFont(w.status, w.fontSmall)
}

func (w *window) addTooltip(hwnd uintptr, key string) {
	if hwnd == 0 || w.tooltip == 0 {
		return
	}
	txt := strings.TrimSpace(tooltipFor(key))
	if txt == "" {
		return
	}
	p := utf16Ptr(txt)
	w.tooltipKeep = append(w.tooltipKeep, p)
	ti := toolInfo{Size: uint32(unsafe.Sizeof(toolInfo{})), Flags: ttfIDIsHwnd | ttfSubclass, Hwnd: w.hwnd, ID: hwnd, Text: p}
	sendMessageW.Call(w.tooltip, ttmAddToolW, 0, uintptr(unsafe.Pointer(&ti)))
}

func (w *window) initTooltips() {
	if w == nil || w.hwnd == 0 {
		return
	}
	h, _, _ := createWindowExW.Call(wsExTopmost, uintptr(unsafe.Pointer(utf16Ptr(tooltipClass))), 0, wsPopup|ttsAlwaysTip|ttsNoPrefix, 0, 0, 0, 0, w.hwnd, 0, 0, 0)
	if h == 0 {
		return
	}
	w.tooltip = h
	sendMessageW.Call(h, ttmSetMaxTipWidth, 0, uintptr(w.px(520)))
	for control, key := range map[uintptr]string{
		w.subURL: "sub_url", w.subAnalyze: "sub_analyze", w.subTrack: "sub_track", w.subFormat: "sub_format", w.subOut: "sub_output", w.subPickOut: "sub_output", w.subOpenOut: "sub_output", w.subDownload: "sub_download", w.subCancel: "sub_cancel",
		w.videoURL: "video_url", w.videoAnalyze: "video_analyze", w.videoQuality: "video_quality", w.videoMode: "video_mode", w.videoSpeed: "video_speed", w.videoContainer: "video_container", w.videoOut: "video_output", w.videoPickOut: "video_output", w.videoOpenOut: "video_output", w.videoDownload: "video_download", w.videoCancel: "video_cancel",
		w.ocrPick: "ocr_pick", w.ocrPreset: "ocr_preset", w.ocrPlay: "ocr_play", w.ocrMute: "ocr_mute", w.ocrFullscreen: "ocr_fullscreen", w.ocrTimeline: "ocr_timeline", w.ocrTop: "ocr_roi", w.ocrBottom: "ocr_roi", w.ocrLeft: "ocr_roi", w.ocrRight: "ocr_roi",
		w.ocrMode: "ocr_mode", w.ocrSensitivity: "ocr_sensitivity", w.ocrDevice: "ocr_device", w.ocrParallel: "ocr_parallel", w.ocrPrepare: "ocr_prepare", w.ocrTest: "ocr_test", w.ocrStart: "ocr_start", w.ocrPause: "ocr_pause", w.ocrRestart: "ocr_restart", w.ocrClear: "ocr_clear", w.ocrExport: "ocr_export", w.ocrOut: "ocr_output", w.ocrPickOut: "ocr_output", w.ocrOpenOut: "ocr_output", w.ocrCueList: "ocr_cues",
		w.editorPick: "editor_pick", w.editorPlay: "editor_play", w.editorMute: "editor_mute", w.editorFullscreen: "editor_fullscreen", w.editorSubtitlePreset: "editor_presets", w.editorWatermarkPreset: "editor_presets", w.editorDelete: "editor_delete", w.editorUndo: "editor_undo", w.editorX: "editor_region", w.editorY: "editor_region", w.editorW: "editor_region", w.editorH: "editor_region", w.editorEffect: "editor_effect", w.editorStrength: "editor_strength", w.editorWhole: "editor_scope", w.editorStart: "editor_timing", w.editorSetStart: "editor_timing", w.editorEnd: "editor_timing", w.editorSetEnd: "editor_timing", w.editorOut: "editor_output", w.editorPickOut: "editor_output", w.editorOpenOut: "editor_output", w.editorName: "editor_output", w.editorRegionList: "editor_regions", w.editorExport: "editor_export", w.editorCancel: "editor_cancel",
		w.theme: "theme", w.defaultOut: "default_output", w.defaultOutPick: "default_output_pick", w.defaultOutOpen: "default_output_open", w.settingsCookie: "cookie", w.cookieSave: "cookie_save", w.cookieDelete: "cookie_delete", w.qrBtn: "qr", w.autoUpdate: "auto_update", w.checkUpdate: "update", w.applyUpdate: "update", w.cleanup: "cleanup", w.resetTools: "reset_tools", w.removeOCR: "remove_ocr", w.closeApp: "close_app", w.bugNote: "bug", w.bugSend: "bug",
	} {
		w.addTooltip(control, key)
	}
}

func themeIndex(theme string) int {
	if strings.EqualFold(strings.TrimSpace(theme), "light") {
		return 1
	}
	return 0
}

func (w *window) applyNativeTheme() {
	if w == nil || w.hwnd == 0 {
		return
	}
	darkValue := int32(0)
	themeName := "Explorer"
	bg := uintptr(0x00F6F8FC)
	if w.dark {
		darkValue = 1
		themeName = "DarkMode_Explorer"
		bg = 0x00252220
	}
	// DWMWA_USE_IMMERSIVE_DARK_MODE. Windows versions that do not expose it
	// simply ignore the call; all functional controls remain native Win32.
	dwmSetWindowAttribute.Call(w.hwnd, 20, uintptr(unsafe.Pointer(&darkValue)), unsafe.Sizeof(darkValue))
	newBrush, _, _ := createSolidBrush.Call(bg)
	if newBrush != 0 {
		old := w.bgBrush
		w.bgBrush = newBrush
		setClassLongPtrW.Call(w.hwnd, classBackgroundIndex(), newBrush)
		if old != 0 && old != newBrush {
			deleteObject.Call(old)
		}
	}
	for _, h := range w.allControls() {
		setWindowTheme.Call(h, uintptr(unsafe.Pointer(utf16Ptr(themeName))), 0)
		invalidate(h)
	}
	invalidate(w.hwnd)
}

func (w *window) allControls() []uintptr {
	controls := make([]uintptr, 0, 96)
	controls = append(controls, w.nav[:]...)
	controls = append(controls, w.status, w.version, w.navHelp)
	for i := range w.pages {
		controls = append(controls, w.pages[i]...)
	}
	return controls
}

func (w *window) changeTheme() {
	theme := "dark"
	if comboIndex(w.theme) == 1 {
		theme = "light"
	}
	if err := w.app.SetTheme(theme); err != nil {
		w.setStatus(err.Error())
		return
	}
	w.dark = theme == "dark"
	w.applyNativeTheme()
	label := "Dark"
	if theme == "light" {
		label = "Light"
	}
	w.setStatus("Đã chuyển sang " + label + " Mode")
}
func (w *window) focusPageStart(page int) {
	var h uintptr
	switch page {
	case pageSubtitle:
		h = w.subURL
	case pageVideo:
		h = w.videoURL
	case pageOCR:
		if w.ocrPlayer == nil {
			h = w.ocrPick
		} else {
			h = w.ocrPlay
		}
	case pageEditor:
		if w.editorPlayer == nil {
			h = w.editorPick
		} else {
			h = w.editorPlay
		}
	case pageSettings:
		h = w.theme
	}
	focused(h)
}

func (w *window) selectPage(p int) {
	if p < 0 || p > 4 {
		return
	}
	w.page = p
	for i := 0; i < 5; i++ {
		for _, h := range w.pages[i] {
			show(h, i == p)
		}
	}
	w.layout()
	w.syncControls()
	w.focusPageStart(p)
	invalidate(w.hwnd)
}

func (w *window) layout() {
	if w == nil || w.hwnd == 0 {
		return
	}
	rp := clientRect(w.hwnd)
	cw, ch := w.logical(int(rp.Right)), w.logical(int(rp.Bottom))
	navW := 168
	for i, h := range w.nav {
		w.mv(h, 12, 70+i*44, 142, 34)
	}
	w.mv(w.version, 12, 18, 144, 34)
	w.mv(w.navHelp, 12, ch-112, 148, 68)
	x := navW + 12
	pw := cw - x - 12
	if pw < 720 {
		pw = 720
	}
	// Global status uses the whole content width so actionable errors are not
	// truncated in the sidebar.
	w.mv(w.status, x+8, ch-36, pw-20, 24)
	for p := pageSubtitle; p <= pageSettings; p++ {
		w.mv(w.pageTitle[p], x+8, 10, pw-24, 28)
		w.mv(w.pageHelp[p], x+8, 40, pw-24, 34)
	}
	switch w.page {
	case pageSubtitle:
		w.layoutSubtitle(x, pw, ch)
	case pageVideo:
		w.layoutVideo(x, pw, ch)
	case pageOCR:
		w.layoutOCR(x, pw, ch)
	case pageEditor:
		w.layoutEditor(x, pw, ch)
	case pageSettings:
		w.layoutSettings(x, pw, ch)
	}
	if w.fullscreen {
		for i := 0; i < 5; i++ {
			for _, h := range w.pages[i] {
				show(h, false)
			}
		}
		for _, h := range w.nav {
			show(h, false)
		}
		show(w.version, false)
		show(w.navHelp, false)
		show(w.status, false)
		r := clientRect(w.hwnd)
		w.previewRect = rect{0, 0, r.Right, r.Bottom}
	}
}

func (w *window) layoutCaptions(page, x, pw, ch int) {
	boxes := captionLayout(page, x, pw, ch)
	labels := w.captions[page]
	for i, b := range boxes {
		if i >= len(labels) {
			break
		}
		w.mv(labels[i], b.X, b.Y, b.W, b.H)
	}
}

func (w *window) layoutSubtitle(x, pw, ch int) {
	w.layoutCaptions(pageSubtitle, x, pw, ch)
	bottom := ch - 48
	w.mv(w.subURL, x+8, 108, pw-200, 30)
	w.mv(w.subAnalyze, x+pw-180, 108, 160, 30)
	w.mv(w.subMeta, x+8, 144, pw-28, 28)
	w.mv(w.subTrack, x+8, 204, pw/2-24, 220)
	w.mv(w.subFormat, x+pw/2+8, 204, pw/2-28, 220)
	w.mv(w.subOut, x+8, 272, pw-280, 30)
	w.mv(w.subPickOut, x+pw-260, 272, 80, 30)
	w.mv(w.subOpenOut, x+pw-170, 272, 80, 30)
	w.mv(w.subDownload, x+8, 316, 160, 34)
	w.mv(w.subCancel, x+180, 316, 90, 34)
	w.mv(w.subProgress, x+284, 323, pw-304, 18)
	w.mv(w.subState, x+8, 356, pw-28, 28)
	w.mv(w.subLog, x+8, 390, pw-28, maxInt(120, bottom-390))
}
func (w *window) layoutVideo(x, pw, ch int) {
	w.layoutCaptions(pageVideo, x, pw, ch)
	bottom := ch - 48
	w.mv(w.videoURL, x+8, 108, pw-200, 30)
	w.mv(w.videoAnalyze, x+pw-180, 108, 160, 30)
	w.mv(w.videoMeta, x+8, 144, pw-28, 28)
	w.mv(w.videoQuality, x+8, 204, pw/4-20, 220)
	w.mv(w.videoMode, x+pw/4+8, 204, pw/4-20, 220)
	w.mv(w.videoSpeed, x+pw/2+8, 204, pw/4-20, 220)
	w.mv(w.videoContainer, x+3*pw/4+8, 204, pw/4-28, 220)
	w.mv(w.videoOut, x+8, 272, pw-280, 30)
	w.mv(w.videoPickOut, x+pw-260, 272, 80, 30)
	w.mv(w.videoOpenOut, x+pw-170, 272, 80, 30)
	w.mv(w.videoDownload, x+8, 316, 150, 34)
	w.mv(w.videoCancel, x+170, 316, 100, 34)
	w.mv(w.videoProgress, x+284, 323, pw-304, 18)
	w.mv(w.videoState, x+8, 356, pw-28, 28)
	w.mv(w.videoLog, x+8, 390, pw-28, maxInt(120, bottom-390))
}
func (w *window) layoutOCR(x, pw, ch int) {
	w.layoutCaptions(pageOCR, x, pw, ch)
	left := maxInt(430, pw*52/100)
	rightX := x + left + 14
	rightW := pw - left - 22
	w.mv(w.ocrPath, x+8, 104, left-260, 28)
	w.mv(w.ocrPick, x+left-244, 104, 112, 28)
	w.mv(w.ocrPreset, x+left-124, 104, 112, 28)
	previewBottom := minInt(ch-190, 555)
	if previewBottom < 360 {
		previewBottom = 360
	}
	w.previewRect = w.pixelRect(rect{int32(x + 8), 142, int32(x + left - 12), int32(previewBottom)})
	py := previewBottom + 8
	w.mv(w.ocrPlay, x+8, py, 80, 28)
	w.mv(w.ocrMute, x+96, py, 120, 28)
	w.mv(w.ocrFullscreen, x+224, py, 120, 28)
	w.mv(w.ocrTimeline, x+8, py+36, maxInt(180, left-176), 30)
	w.mv(w.ocrTime, x+left-160, py+38, 148, 26)

	col := rightW / 4
	labelY, editY := 104, 122
	for i, pair := range []struct{ label, edit uintptr }{{w.ocrTopLabel, w.ocrTop}, {w.ocrBottomLabel, w.ocrBottom}, {w.ocrLeftLabel, w.ocrLeft}, {w.ocrRightLabel, w.ocrRight}} {
		xx := rightX + i*col
		ww := col - 8
		if i == 3 {
			ww = rightW - 3*col - 8
		}
		w.mv(pair.label, xx, labelY, ww, 18)
		w.mv(pair.edit, xx, editY, ww, 28)
	}
	w.mv(w.ocrMode, rightX, 182, rightW/2-8, 190)
	w.mv(w.ocrSensitivity, rightX+rightW/2, 182, rightW/2-8, 190)
	w.mv(w.ocrDevice, rightX, 244, rightW/2-8, 190)
	w.mv(w.ocrParallel, rightX+rightW/2, 244, rightW/2-8, 190)
	w.mv(w.ocrPrepare, rightX, 282, 158, 30)
	w.mv(w.ocrTest, rightX+166, 282, 104, 30)
	w.mv(w.ocrStart, rightX, 320, 158, 30)
	w.mv(w.ocrPause, rightX+166, 320, 110, 30)
	w.mv(w.ocrRestart, rightX+284, 320, maxInt(122, rightW-292), 30)
	w.mv(w.ocrClear, rightX, 358, 122, 30)
	w.mv(w.ocrExport, rightX+130, 358, 122, 30)
	w.mv(w.ocrOut, rightX, 402, rightW-184, 28)
	w.mv(w.ocrPickOut, rightX+rightW-176, 402, 80, 28)
	w.mv(w.ocrOpenOut, rightX+rightW-88, 402, 80, 28)
	w.mv(w.ocrProgress, rightX, 438, rightW-8, 16)
	w.mv(w.ocrStatus, rightX, 460, rightW-8, 38)
	w.mv(w.ocrMetrics, rightX, 502, rightW-8, 84)
	w.mv(w.ocrCueSummary, rightX, 590, rightW-8, 24)
	w.mv(w.ocrCueList, rightX, 616, rightW-8, maxInt(70, ch-664))
}
func (w *window) layoutEditor(x, pw, ch int) {
	w.layoutCaptions(pageEditor, x, pw, ch)
	left := maxInt(430, pw*58/100)
	rightX := x + left + 14
	rightW := pw - left - 22
	w.mv(w.editorPath, x+8, 104, left-150, 28)
	w.mv(w.editorPick, x+left-132, 104, 120, 28)
	previewBottom := minInt(ch-220, 530)
	if previewBottom < 350 {
		previewBottom = 350
	}
	w.previewRect = w.pixelRect(rect{int32(x + 8), 142, int32(x + left - 12), int32(previewBottom)})
	py := previewBottom + 8
	w.mv(w.editorPlay, x+8, py, 80, 28)
	w.mv(w.editorMute, x+96, py, 120, 28)
	w.mv(w.editorFullscreen, x+224, py, 120, 28)
	w.mv(w.editorTime, x+354, py, maxInt(120, left-374), 28)
	w.mv(w.editorTimeline, x+8, py+36, left-20, 30)
	w.mv(w.editorSubtitlePreset, x+8, py+72, 120, 28)
	w.mv(w.editorWatermarkPreset, x+136, py+72, 130, 28)
	w.mv(w.editorDelete, x+274, py+72, 100, 28)
	w.mv(w.editorUndo, x+382, py+72, 90, 28)

	col := rightW / 4
	for i, pair := range []struct{ label, edit uintptr }{{w.editorXLabel, w.editorX}, {w.editorYLabel, w.editorY}, {w.editorWLabel, w.editorW}, {w.editorHLabel, w.editorH}} {
		xx := rightX + i*col
		ww := col - 8
		if i == 3 {
			ww = rightW - 3*col - 8
		}
		w.mv(pair.label, xx, 104, ww, 18)
		w.mv(pair.edit, xx, 122, ww, 28)
	}
	w.mv(w.editorEffect, rightX, 182, rightW/2-8, 190)
	w.mv(w.editorStrength, rightX+rightW/2, 182, rightW/2-8, 28)
	w.mv(w.editorWhole, rightX, 220, rightW-8, 28)
	w.mv(w.editorStartLabel, rightX, 270, rightW/2-8, 18)
	w.mv(w.editorEndLabel, rightX+rightW/2, 270, rightW/2-8, 18)
	w.mv(w.editorStart, rightX, 290, rightW/2-92, 28)
	w.mv(w.editorSetStart, rightX+rightW/2-84, 290, 76, 28)
	w.mv(w.editorEnd, rightX+rightW/2, 290, rightW/2-92, 28)
	w.mv(w.editorSetEnd, rightX+rightW-84, 290, 76, 28)
	w.mv(w.editorOut, rightX, 350, rightW-184, 28)
	w.mv(w.editorPickOut, rightX+rightW-176, 350, 80, 28)
	w.mv(w.editorOpenOut, rightX+rightW-88, 350, 80, 28)
	w.mv(w.editorName, rightX, 408, rightW-8, 28)
	w.mv(w.editorRegionList, rightX, 466, rightW-8, 68)
	w.mv(w.editorExport, rightX, 542, 140, 34)
	w.mv(w.editorCancel, rightX+148, 542, 90, 34)
	w.mv(w.editorProgress, rightX+248, 551, maxInt(80, rightW-256), 16)
	w.mv(w.editorStatus, rightX, 584, rightW-8, 38)
	w.mv(w.editorLog, rightX, 626, rightW-8, maxInt(60, ch-674))
}
func (w *window) layoutSettings(x, pw, ch int) {
	w.layoutCaptions(pageSettings, x, pw, ch)
	qrSize := minInt(260, maxInt(190, pw/3))
	formW := pw - qrSize - 42
	if formW < 500 {
		formW = pw
		qrSize = 0
	}
	w.mv(w.theme, x+8, 104, 170, 190)
	w.mv(w.settingsRoot, x+8, 166, formW-20, 22)
	w.mv(w.settingsDrive, x+8, 188, formW-20, 22)
	w.mv(w.settingsCookieState, x+8, 210, formW-20, 22)
	w.mv(w.defaultOut, x+8, 260, formW-190, 28)
	w.mv(w.defaultOutPick, x+formW-174, 260, 76, 28)
	w.mv(w.defaultOutOpen, x+formW-90, 260, 76, 28)
	w.mv(w.settingsCookie, x+8, 324, formW-20, 28)
	w.mv(w.cookieSave, x+8, 360, 140, 28)
	w.mv(w.cookieDelete, x+156, 360, 130, 28)
	w.mv(w.qrBtn, x+294, 360, 120, 28)
	w.mv(w.qrState, x+8, 392, formW-20, 24)
	w.mv(w.autoUpdate, x+8, 448, 190, 24)
	w.mv(w.checkUpdate, x+8, 474, 140, 28)
	w.mv(w.applyUpdate, x+156, 474, 130, 28)
	w.mv(w.settingsStorage, x+8, 534, formW-20, 24)
	w.mv(w.cleanup, x+8, 586, 140, 28)
	w.mv(w.resetTools, x+156, 586, 130, 28)
	w.mv(w.removeOCR, x+294, 586, 130, 28)
	w.mv(w.closeApp, x+432, 586, minInt(230, maxInt(140, formW-446)), 28)
	w.mv(w.bugNote, x+8, 648, formW-20, 62)
	w.mv(w.bugSend, x+8, 716, 120, 28)
	if qrSize > 0 {
		w.qrRect = w.pixelRect(rect{Left: int32(x + pw - qrSize - 12), Top: 102, Right: int32(x + pw - 12), Bottom: int32(102 + qrSize)})
		w.mv(w.settingsStatus, x+pw-qrSize-12, 374, qrSize, maxInt(120, ch-422))
	} else {
		w.qrRect = rect{}
		w.mv(w.settingsStatus, x+8, 752, pw-28, maxInt(70, ch-800))
	}
}

func (w *window) command(id, notify int) {
	if id >= idNavSub && id <= idNavSettings {
		w.selectPage(id - idNavSub)
		return
	}
	if notify != bnClicked && notify != cbnSelChange && notify != lbnSelChange && notify != enChange {
		return
	}
	switch id {
	case idSubURL:
		if notify == enChange {
			w.invalidateSubtitleMetadataIfURLChanged()
		}
	case idSubAnalyze:
		w.analyzeSubtitle()
	case idSubOut:
		// Live enable-state refresh is handled by the final syncControls call.
	case idSubPickOut:
		w.pickOutput(w.subOut)
	case idSubOpenOut:
		w.openOutput(w.subOut)
	case idSubDownload:
		w.startSubtitle()
	case idSubCancel:
		w.cancelActive()
	case idVideoURL:
		if notify == enChange {
			w.invalidateVideoMetadataIfURLChanged()
		}
	case idVideoAnalyze:
		w.analyzeVideo()
	case idVideoOut:
		// Live enable-state refresh is handled by the final syncControls call.
	case idVideoPickOut:
		w.pickOutput(w.videoOut)
	case idVideoOpenOut:
		w.openOutput(w.videoOut)
	case idVideoDownload:
		w.startVideo()
	case idVideoCancel:
		w.cancelActive()
	case idOCRPick:
		w.pickOCRVideo()
	case idOCRPreset:
		w.applyOCRSubtitlePreset()
	case idOCRPlay:
		w.togglePlay(w.ocrPlayer)
	case idOCRMute:
		w.toggleMute(w.ocrPlayer)
	case idOCRFullscreen:
		w.toggleFullscreen()
	case idOCRTop, idOCRBottom, idOCRLeft, idOCRRight:
		if notify == enChange {
			w.validateOCRLive()
			invalidate(w.hwnd)
		}
	case idOCROut:
		// Live enable-state refresh is handled by the final syncControls call.
	case idOCRPickOut:
		w.pickOutput(w.ocrOut)
	case idOCROpenOut:
		w.openOutput(w.ocrOut)
	case idOCRPrepare:
		w.prepareOCR()
	case idOCRTest:
		w.testOCR()
	case idOCRStart:
		w.startOCR(false)
	case idOCRPause:
		w.pauseOCR()
	case idOCRRestart:
		w.startOCR(true)
	case idOCRClear:
		if w.ocrHasCheckpoint {
			w.setWorkflowState(pageOCR, "[Không thể xóa] Đang có checkpoint. Dùng Quét lại từ đầu nếu muốn bỏ tiến độ đã lưu.")
			break
		}
		if !confirm(w.hwnd, "Xóa kết quả OCR", "Xóa toàn bộ cue OCR đang giữ trong bộ nhớ? File SRT đã xuất trước đó không bị xóa.") {
			break
		}
		w.ocrCues = nil
		w.ocrTotalCues = 0
		w.renderCues()
		progressSet(w.ocrProgress, 0)
		w.setWorkflowState(pageOCR, "[Đã xóa kết quả] Danh sách cue trong bộ nhớ đã được xóa.")
	case idOCRExport:
		w.exportOCR()
	case idOCRCueList:
		if notify == lbnSelChange {
			w.seekSelectedCue()
		}
	case idEditorPick:
		w.pickEditorVideo()
	case idEditorPlay:
		w.togglePlay(w.editorPlayer)
	case idEditorMute:
		w.toggleMute(w.editorPlayer)
	case idEditorFullscreen:
		w.toggleFullscreen()
	case idEditorSubtitlePreset:
		w.editorAddPreset("subtitle")
	case idEditorWatermarkPreset:
		w.editorAddPreset("watermark")
	case idEditorDelete:
		w.editorDeleteSelected()
	case idEditorUndo:
		w.editorUndoLast()
	case idEditorEffect:
		if w.editorModel.selected >= 0 {
			if err := w.validateEditorSelection(); err != nil {
				w.setEditorValidationError(err)
				break
			}
			w.editorCommitSelected(true)
			w.syncEditorControls()
		}
	case idEditorWhole:
		if w.editorModel.selected >= 0 {
			if err := w.validateEditorSelection(); err != nil {
				w.setEditorValidationError(err)
				break
			}
			w.editorCommitSelected(true)
			w.syncEditorControls()
		}
	case idEditorX, idEditorY, idEditorW, idEditorH, idEditorStrength, idEditorStart, idEditorEnd:
		if notify == enChange && !w.syncingEditor && w.editorModel.selected >= 0 {
			if err := w.validateEditorSelection(); err != nil {
				w.setEditorValidationError(err)
			} else {
				w.clearEditorValidationError()
				w.editorCommitSelected(false)
				invalidate(w.hwnd)
			}
		}
	case idEditorSetStart:
		w.editorSetBoundary(true)
	case idEditorSetEnd:
		w.editorSetBoundary(false)
	case idEditorPickOut:
		w.pickOutput(w.editorOut)
	case idEditorOpenOut:
		w.openOutput(w.editorOut)
	case idEditorOut, idEditorName:
		// Live enable-state refresh is handled by the final syncControls call.
	case idEditorRegionList:
		if notify == lbnSelChange {
			w.editorSelectRegion(listIndex(w.editorRegionList))
		}
	case idEditorExport:
		w.exportEditor()
	case idEditorCancel:
		w.cancelActive()
	case idTheme:
		w.changeTheme()
	case idDefaultOutPick:
		w.pickDefaultOutput()
	case idDefaultOutOpen:
		w.openOutput(w.defaultOut)
	case idCookie:
		// Live enable-state refresh is handled by the final syncControls call.
	case idCookieSave:
		w.saveCookie()
	case idCookieDelete:
		w.deleteCookie()
	case idQR:
		w.startQR()
	case idAutoUpdate:
		_ = w.app.SetUpdateCheck(checkGet(w.autoUpdate))
	case idCheckUpdate:
		w.doCheckUpdate()
	case idApplyUpdate:
		w.doApplyUpdate()
	case idCleanup:
		w.doCleanup()
	case idResetTools:
		w.doResetTools()
	case idRemoveOCR:
		w.doRemoveOCR()
	case idCloseApp:
		w.requestClose()
	case idBugNote:
		// Live enable-state refresh is handled by the final syncControls call.
	case idBugSend:
		w.sendBugReport()
	}
	w.syncControls()
}

func (w *window) asyncDo(fn func()) { go fn() }
func (w *window) post(fn func()) {
	if w.closed {
		return
	}
	select {
	case w.async <- fn:
	default:
		go func() { w.async <- fn }()
	}
	postMessageW.Call(w.hwnd, wmAppAsync, 0, 0)
}
func (w *window) drainAsync() {
	for {
		select {
		case fn := <-w.async:
			if fn != nil {
				fn()
			}
		default:
			return
		}
	}
}
func (w *window) setStatus(s string)               { setText(w.status, s) }
func (w *window) appendLog(h uintptr, line string) { appendEdit(h, line) }
func (w *window) refreshStatus(validate bool) {
	w.asyncDo(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
		defer cancel()
		st := w.app.Status(ctx, validate)
		w.post(func() {
			setText(w.version, "BiliSub v"+st.Version)
			setText(w.settingsRoot, "Thư mục ứng dụng: "+st.Root)
			setText(w.settingsDrive, "Ổ đang khóa: "+st.Drive)
			setText(w.defaultOut, st.Config.OutputDir)
			comboSet(w.theme, themeIndex(st.Config.Theme))
			setText(w.settingsStorage, fmt.Sprintf("Data %s · Tools %s · OCR %s · Temp %s · Cache %s",
				formatBytes(st.Storage["data"]), formatBytes(st.Storage["tools"]), formatBytes(st.Storage["ocr"]), formatBytes(st.Storage["temp"]), formatBytes(st.Storage["cache"])))
			w.cookieSaved = st.CookieSaved
			if st.CookieSaved {
				if st.CookieValid {
					if validate {
						w.setStatus("Đã đăng nhập " + st.CookieUser)
					}
					setText(w.settingsCookieState, "Cookie: Đã xác minh · "+st.CookieUser)
				} else {
					if validate {
						w.setStatus("Đã lưu Cookie")
					}
					setText(w.settingsCookieState, "Cookie: Đã lưu nhưng chưa xác minh")
				}
			} else {
				if validate {
					w.setStatus("Sẵn sàng")
				}
				w.cookieSaved = false
				setText(w.settingsCookieState, "Cookie: Chưa đăng nhập")
			}
			if st.OCR.GPUAvailable && strings.TrimSpace(text(w.ocrPath)) == "" && w.active == nil && !w.ocrHasCheckpoint {
				setText(w.ocrMetrics, "GPU: "+st.OCR.GPUName+" · "+st.OCR.GPUDriver)
			}
			w.syncControls()
		})
	})
}

func formatBytes(n int64) string {
	if n < 1024 {
		return fmt.Sprintf("%d B", n)
	}
	if n < 1<<20 {
		return fmt.Sprintf("%.1f KB", float64(n)/(1<<10))
	}
	if n < 1<<30 {
		return fmt.Sprintf("%.1f MB", float64(n)/(1<<20))
	}
	return fmt.Sprintf("%.2f GB", float64(n)/(1<<30))
}

func normalizedURLField(h uintptr) string {
	return strings.TrimSpace(text(h))
}

func (w *window) invalidateSubtitleMetadataIfURLChanged() {
	current := normalizedURLField(w.subURL)
	if current == w.subAnalyzedURL && w.subAnalyzedURL != "" {
		return
	}
	w.subAnalyzedURL = ""
	w.subTracks = nil
	comboReset(w.subTrack)
	comboAdd(w.subTrack, "Chưa kiểm tra link này")
	comboSet(w.subTrack, 0)
	setText(w.subMeta, "Chưa kiểm tra video cho link hiện tại")
	if current == "" {
		w.setWorkflowState(pageSubtitle, "Bước tiếp theo: nhập link và bấm Kiểm tra.")
	} else {
		w.setWorkflowState(pageSubtitle, "[Cần kiểm tra lại] Link đã thay đổi. Bấm Kiểm tra để lấy metadata/track mới.")
	}
}

func (w *window) invalidateVideoMetadataIfURLChanged() {
	current := normalizedURLField(w.videoURL)
	if current == w.videoAnalyzedURL && w.videoAnalyzedURL != "" {
		return
	}
	w.videoAnalyzedURL = ""
	w.videoReady = false
	comboReset(w.videoQuality)
	comboAdd(w.videoQuality, "Chưa kiểm tra link này")
	comboSet(w.videoQuality, 0)
	setText(w.videoMeta, "Chưa kiểm tra video cho link hiện tại")
	if current == "" {
		w.setWorkflowState(pageVideo, "Bước tiếp theo: nhập link và bấm Kiểm tra.")
	} else {
		w.setWorkflowState(pageVideo, "[Cần kiểm tra lại] Link đã thay đổi. Bấm Kiểm tra để lấy metadata/chất lượng mới.")
	}
}

func (w *window) analyzeSubtitle() {
	raw := normalizedURLField(w.subURL)
	if raw == "" {
		w.setWorkflowState(pageSubtitle, "[Cần thao tác] Hãy nhập link Bilibili trước.")
		w.setStatus("Hãy nhập link Bilibili")
		focused(w.subURL)
		return
	}
	w.subAnalyzing = true
	w.subAnalyzedURL = ""
	w.subTracks = nil
	comboReset(w.subTrack)
	comboAdd(w.subTrack, "Đang đọc track...")
	comboSet(w.subTrack, 0)
	setText(w.subMeta, "Đang kiểm tra metadata và track phụ đề...")
	w.setWorkflowState(pageSubtitle, "[Đang xử lý] Đang đọc thông tin video từ Bilibili...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, c := context.WithTimeout(context.Background(), 90*time.Second)
		defer c()
		m, e := w.app.Metadata(ctx, raw)
		w.post(func() {
			w.subAnalyzing = false
			if normalizedURLField(w.subURL) != raw {
				w.invalidateSubtitleMetadataIfURLChanged()
				w.setStatus("Link đã thay đổi · bỏ kết quả metadata cũ")
				w.syncControls()
				return
			}
			if e != nil {
				w.subAnalyzedURL = ""
				comboReset(w.subTrack)
				comboAdd(w.subTrack, "Chưa có track")
				comboSet(w.subTrack, 0)
				setText(w.subMeta, "Lỗi: "+e.Error())
				w.setWorkflowState(pageSubtitle, "[Lỗi] Không đọc được video. Kiểm tra link, mạng hoặc đăng nhập rồi thử lại.")
				w.setStatus("Lỗi kiểm tra phụ đề: " + e.Error())
				w.syncControls()
				return
			}
			w.subAnalyzedURL = raw
			w.subTracks = m.Subtitles
			comboReset(w.subTrack)
			for _, t := range m.Subtitles {
				label := t.LangDoc
				if t.Official {
					label += " · Chính chủ"
				} else if t.AI {
					label += " · AI"
				}
				comboAdd(w.subTrack, label)
			}
			if len(m.Subtitles) > 0 {
				comboSet(w.subTrack, 0)
				w.setWorkflowState(pageSubtitle, fmt.Sprintf("[Sẵn sàng] Đã tìm thấy %d track. Chọn track/định dạng rồi bấm Tải phụ đề.", len(m.Subtitles)))
			} else {
				comboAdd(w.subTrack, "Không có track phụ đề")
				comboSet(w.subTrack, 0)
				w.setWorkflowState(pageSubtitle, "[Không có dữ liệu] Video này không trả về track phụ đề. Có thể dùng tab OCR phụ đề với video local.")
			}
			setText(w.subMeta, fmt.Sprintf("%s · %d track phụ đề", m.Title, len(m.Subtitles)))
			w.setStatus("Đã kiểm tra video")
			w.syncControls()
		})
	})
}

func (w *window) analyzeVideo() {
	raw := normalizedURLField(w.videoURL)
	if raw == "" {
		w.setWorkflowState(pageVideo, "[Cần thao tác] Hãy nhập link Bilibili trước.")
		w.setStatus("Hãy nhập link Bilibili")
		focused(w.videoURL)
		return
	}
	w.videoAnalyzing = true
	w.videoReady = false
	w.videoAnalyzedURL = ""
	comboReset(w.videoQuality)
	comboAdd(w.videoQuality, "Đang đọc chất lượng...")
	comboSet(w.videoQuality, 0)
	setText(w.videoMeta, "Đang kiểm tra metadata và chất lượng...")
	w.setWorkflowState(pageVideo, "[Đang xử lý] Đang đọc thông tin video từ Bilibili...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, c := context.WithTimeout(context.Background(), 90*time.Second)
		defer c()
		m, e := w.app.Metadata(ctx, raw)
		w.post(func() {
			w.videoAnalyzing = false
			if normalizedURLField(w.videoURL) != raw {
				w.invalidateVideoMetadataIfURLChanged()
				w.setStatus("Link đã thay đổi · bỏ kết quả metadata cũ")
				w.syncControls()
				return
			}
			if e != nil {
				w.videoAnalyzedURL = ""
				comboReset(w.videoQuality)
				comboAdd(w.videoQuality, "Chưa có chất lượng")
				comboSet(w.videoQuality, 0)
				setText(w.videoMeta, "Lỗi: "+e.Error())
				w.setWorkflowState(pageVideo, "[Lỗi] Không đọc được video. Kiểm tra link, mạng hoặc trạng thái đăng nhập.")
				w.setStatus("Lỗi kiểm tra video: " + e.Error())
				w.syncControls()
				return
			}
			w.videoAnalyzedURL = raw
			comboReset(w.videoQuality)
			for _, q := range m.Qualities {
				comboAdd(w.videoQuality, q)
			}
			if len(m.Qualities) > 0 {
				comboSet(w.videoQuality, 0)
				w.videoReady = true
				w.setWorkflowState(pageVideo, "[Sẵn sàng] Chọn chất lượng/nội dung rồi bấm Tải video.")
			} else {
				comboAdd(w.videoQuality, "Không có chất lượng khả dụng")
				comboSet(w.videoQuality, 0)
				w.setWorkflowState(pageVideo, "[Không có dữ liệu] Bilibili không trả về chất lượng tải được cho video này.")
			}
			setText(w.videoMeta, m.Title+" · "+strings.Join(m.Qualities, ", "))
			w.setStatus("Đã kiểm tra video")
			w.syncControls()
		})
	})
}

func (w *window) pickOutput(h uintptr) {
	p, c, e := pickFolder(w.hwnd, text(h))
	if e != nil {
		w.setStatus(e.Error())
		return
	}
	if !c && p != "" {
		setText(h, p)
		_ = w.app.SetOutputDir(p)
	}
}
func (w *window) openOutput(h uintptr) {
	if e := openFolder(w.hwnd, text(h)); e != nil {
		w.setStatus(e.Error())
	}
}
func (w *window) pickDefaultOutput() {
	before := text(w.defaultOut)
	w.pickOutput(w.defaultOut)
	after := text(w.defaultOut)
	if after == "" || after == before && strings.TrimSpace(after) == "" {
		return
	}
	for _, h := range []uintptr{w.subOut, w.videoOut, w.ocrOut, w.editorOut} {
		setText(h, after)
	}
	w.setStatus("Đã đổi thư mục lưu mặc định")
}
func (w *window) startSubtitle() {
	if !w.canStartJob() {
		return
	}
	if !nonEmpty(text(w.subURL)) {
		w.setWorkflowState(pageSubtitle, "[Cần thao tác] Chưa có link Bilibili.")
		focused(w.subURL)
		return
	}
	if !nonEmpty(text(w.subOut)) {
		w.setWorkflowState(pageSubtitle, "[Cần thao tác] Chưa có thư mục xuất.")
		focused(w.subOut)
		return
	}
	if w.subAnalyzedURL == "" || normalizedURLField(w.subURL) != w.subAnalyzedURL {
		w.setWorkflowState(pageSubtitle, "[Cần thao tác] Link hiện tại chưa được Kiểm tra hoặc đã thay đổi. Bấm Kiểm tra trước khi tải.")
		return
	}
	i := comboIndex(w.subTrack)
	if i < 0 || i >= len(w.subTracks) {
		w.setStatus("Chưa chọn track phụ đề")
		return
	}
	format := []string{"srt", "txt", "json"}[maxInt(0, minInt(2, comboIndex(w.subFormat)))]
	req := subtitle.Request{URL: text(w.subURL), Format: format, Track: w.subTracks[i].Lang, OutputDir: text(w.subOut)}
	progressSet(w.subProgress, 0)
	setText(w.subLog, "")
	w.setWorkflowState(pageSubtitle, "[Đang tải] Đang tải và chuyển đổi phụ đề...")
	w.bindJob(w.app.StartSubtitle(req), pageSubtitle)
	w.syncControls()
}
func (w *window) startVideo() {
	if !w.canStartJob() {
		return
	}
	if !nonEmpty(text(w.videoURL)) {
		w.setWorkflowState(pageVideo, "[Cần thao tác] Chưa có link Bilibili.")
		focused(w.videoURL)
		return
	}
	if !nonEmpty(text(w.videoOut)) {
		w.setWorkflowState(pageVideo, "[Cần thao tác] Chưa có thư mục xuất.")
		focused(w.videoOut)
		return
	}
	if !w.videoReady || w.videoAnalyzedURL == "" || normalizedURLField(w.videoURL) != w.videoAnalyzedURL {
		w.setWorkflowState(pageVideo, "[Cần thao tác] Hãy bấm Kiểm tra trước khi tải.")
		return
	}
	mode := []string{"video+audio", "video-only", "audio-only"}[maxInt(0, minInt(2, comboIndex(w.videoMode)))]
	speed := []string{"stable", "fast", "turbo"}[maxInt(0, minInt(2, comboIndex(w.videoSpeed)))]
	container := []string{"mp4", "mkv"}[maxInt(0, minInt(1, comboIndex(w.videoContainer)))]
	req := video.JobRequest{URL: text(w.videoURL), Quality: comboText(w.videoQuality), Mode: mode, Speed: speed, Container: container, OutputDir: text(w.videoOut)}
	progressSet(w.videoProgress, 0)
	setText(w.videoLog, "")
	w.setWorkflowState(pageVideo, "[Đang tải] Đang tải video...")
	w.bindJob(w.app.StartVideo(req), pageVideo)
	w.syncControls()
}
func (w *window) bindJob(id string, kind int) {
	w.active = &jobBinding{id: id, kind: kind}
	w.setStatus("Đang chạy tác vụ...")
}

func (w *window) jobActive(kind int) bool {
	return w.active != nil && w.active.kind == kind
}

func (w *window) setWorkflowState(kind int, message string) {
	switch kind {
	case pageSubtitle:
		setText(w.subState, message)
	case pageVideo:
		setText(w.videoState, message)
	case pageOCR:
		setText(w.ocrStatus, message)
	case pageEditor:
		setText(w.editorStatus, message)
	case pageSettings:
		setText(w.settingsStatus, message)
	}
}

func (w *window) setProgressForKind(kind int, pct float64) {
	switch kind {
	case pageSubtitle:
		progressSet(w.subProgress, pct)
	case pageVideo:
		progressSet(w.videoProgress, pct)
	case pageOCR:
		progressSet(w.ocrProgress, pct)
	case pageEditor:
		progressSet(w.editorProgress, pct)
	}
}

func (w *window) syncControls() {
	if w == nil || w.closed {
		return
	}
	idle := w.active == nil && !w.closing

	// Subtitle workflow.
	hasSubURL := nonEmpty(text(w.subURL))
	hasSubOut := nonEmpty(text(w.subOut))
	metadataMatchesSubURL := w.subAnalyzedURL != "" && normalizedURLField(w.subURL) == w.subAnalyzedURL
	hasTrack := metadataMatchesSubURL && len(w.subTracks) > 0 && comboIndex(w.subTrack) >= 0
	enable(w.subURL, idle && !w.subAnalyzing)
	enable(w.subAnalyze, idle && hasSubURL && !w.subAnalyzing)
	enable(w.subTrack, idle && len(w.subTracks) > 0)
	enable(w.subFormat, idle)
	enable(w.subOut, idle)
	enable(w.subPickOut, idle)
	enable(w.subOpenOut, hasSubOut)
	enable(w.subDownload, idle && hasSubURL && hasSubOut && hasTrack && !w.subAnalyzing)
	enable(w.subCancel, w.jobActive(pageSubtitle))

	// Video download workflow.
	hasVideoURL := nonEmpty(text(w.videoURL))
	hasVideoOut := nonEmpty(text(w.videoOut))
	hasQuality := comboIndex(w.videoQuality) >= 0 && comboText(w.videoQuality) != ""
	metadataMatchesVideoURL := w.videoAnalyzedURL != "" && normalizedURLField(w.videoURL) == w.videoAnalyzedURL
	enable(w.videoURL, idle && !w.videoAnalyzing)
	enable(w.videoAnalyze, idle && hasVideoURL && !w.videoAnalyzing)
	enable(w.videoQuality, idle && w.videoReady && metadataMatchesVideoURL)
	enable(w.videoMode, idle)
	enable(w.videoSpeed, idle)
	enable(w.videoContainer, idle)
	enable(w.videoOut, idle)
	enable(w.videoPickOut, idle)
	enable(w.videoOpenOut, hasVideoOut)
	enable(w.videoDownload, idle && hasVideoURL && hasVideoOut && w.videoReady && metadataMatchesVideoURL && hasQuality)
	enable(w.videoCancel, w.jobActive(pageVideo))

	// OCR workflow. While OCR is running, the preview follows the safe frontier;
	// manual seek/config edits are disabled to keep displayed state unambiguous.
	hasOCRVideo := w.ocrPlayer != nil && nonEmpty(text(w.ocrPath))
	ocrRunning := w.jobActive(pageOCR)
	ocrIdle := !ocrRunning && w.active == nil && !w.closing
	enable(w.ocrPick, ocrIdle)
	enable(w.ocrPreset, ocrIdle && hasOCRVideo)
	enable(w.ocrPlay, ocrIdle && hasOCRVideo)
	enable(w.ocrMute, ocrIdle && hasOCRVideo)
	enable(w.ocrFullscreen, ocrIdle && hasOCRVideo)
	enable(w.ocrTimeline, ocrIdle && hasOCRVideo)
	for _, h := range []uintptr{w.ocrTop, w.ocrBottom, w.ocrLeft, w.ocrRight, w.ocrMode, w.ocrSensitivity, w.ocrDevice, w.ocrParallel} {
		enable(h, ocrIdle && hasOCRVideo)
	}
	enable(w.ocrPrepare, w.active == nil && !w.ocrPreparing && !w.closing)
	regionValid := hasOCRVideo && w.validateOCRRegion() == nil
	enable(w.ocrTest, ocrIdle && regionValid && !w.ocrTesting)
	enable(w.ocrStart, ocrIdle && regionValid && !w.ocrTesting && !w.ocrPreparing)
	enable(w.ocrPause, ocrRunning)
	enable(w.ocrRestart, ocrIdle && hasOCRVideo && (w.ocrHasCheckpoint || w.ocrTotalCues > 0))
	enable(w.ocrClear, ocrIdle && w.ocrTotalCues > 0 && !w.ocrHasCheckpoint)
	enable(w.ocrExport, ocrIdle && !w.ocrHasCheckpoint && len(w.ocrCues) > 0 && nonEmpty(text(w.ocrOut)))
	enable(w.ocrOut, ocrIdle)
	enable(w.ocrPickOut, ocrIdle)
	enable(w.ocrOpenOut, nonEmpty(text(w.ocrOut)))

	// Editor workflow.
	hasEditorVideo := w.editorPlayer != nil && nonEmpty(text(w.editorPath))
	editorRunning := w.jobActive(pageEditor)
	editorIdle := !editorRunning && w.active == nil && !w.closing
	enable(w.editorPick, editorIdle)
	enable(w.editorPlay, editorIdle && hasEditorVideo)
	enable(w.editorMute, editorIdle && hasEditorVideo)
	enable(w.editorFullscreen, editorIdle && hasEditorVideo)
	enable(w.editorTimeline, editorIdle && hasEditorVideo)
	enable(w.editorSubtitlePreset, editorIdle && hasEditorVideo)
	enable(w.editorWatermarkPreset, editorIdle && hasEditorVideo)
	hasSel := w.editorModel.selected >= 0 && w.editorModel.selected < len(w.editorModel.regions)
	editorSelectionValid := hasSel && w.validateEditorSelection() == nil
	for _, h := range []uintptr{w.editorX, w.editorY, w.editorW, w.editorH, w.editorEffect, w.editorStrength, w.editorWhole} {
		enable(h, editorIdle && hasEditorVideo && hasSel)
	}
	timedRegion := hasSel && !checkGet(w.editorWhole)
	for _, h := range []uintptr{w.editorStart, w.editorSetStart, w.editorEnd, w.editorSetEnd} {
		enable(h, editorIdle && hasEditorVideo && timedRegion)
	}
	enable(w.editorDelete, editorIdle && hasSel)
	enable(w.editorUndo, editorIdle && len(w.editorModel.undo) > 0)
	enable(w.editorOut, editorIdle)
	enable(w.editorPickOut, editorIdle)
	enable(w.editorOpenOut, nonEmpty(text(w.editorOut)))
	enable(w.editorName, editorIdle && hasEditorVideo)
	enable(w.editorRegionList, editorIdle && len(w.editorModel.regions) > 0)
	enable(w.editorExport, editorIdle && hasEditorVideo && len(w.editorModel.regions) > 0 && editorSelectionValid && nonEmpty(text(w.editorOut)) && nonEmpty(text(w.editorName)))
	enable(w.editorCancel, editorRunning)

	// Settings/login/update. Destructive runtime operations are disabled while a
	// job is active. Login and bug-report controls remain available when idle.
	enable(w.theme, w.active == nil && !w.closing)
	enable(w.defaultOutPick, w.active == nil && !w.closing)
	enable(w.defaultOutOpen, nonEmpty(text(w.defaultOut)))
	enable(w.settingsCookie, w.active == nil && !w.qrBusy && !w.cookieBusy && !w.closing)
	enable(w.cookieSave, w.active == nil && nonEmpty(text(w.settingsCookie)) && !w.qrBusy && !w.cookieBusy && !w.closing)
	enable(w.cookieDelete, w.active == nil && w.cookieSaved && !w.qrBusy && !w.cookieBusy && !w.closing)
	enable(w.qrBtn, w.active == nil && !w.cookieBusy && !w.closing)
	enable(w.autoUpdate, w.active == nil && !w.updateBusy && !w.closing)
	enable(w.checkUpdate, w.active == nil && !w.updateBusy && !w.closing)
	enable(w.applyUpdate, w.active == nil && w.updateAvailable && !w.updateBusy && !w.closing)
	enable(w.cleanup, w.active == nil && !w.closing)
	enable(w.resetTools, w.active == nil && !w.closing)
	enable(w.removeOCR, w.active == nil && !w.closing)
	enable(w.closeApp, !w.closing)
	enable(w.bugSend, !w.updateBusy && !w.bugBusy && nonEmpty(text(w.bugNote)) && !w.closing)
}

func (w *window) canStartJob() bool {
	if w.active == nil {
		return true
	}
	w.setStatus("Đang có tác vụ hoạt động · hãy chờ hoặc hủy/tạm dừng trước")
	return false
}
func (w *window) cancelActive() {
	if w.active == nil {
		return
	}
	kind := w.active.kind
	_ = w.app.CancelJob(w.active.id)
	w.setWorkflowState(kind, "[Đang hủy] Đang yêu cầu dừng tác vụ...")
	w.setStatus("Đang hủy...")
	w.syncControls()
}

func (w *window) pickOCRVideo() {
	p, c, e := pickVideo(w.hwnd, text(w.ocrPath))
	if e != nil {
		w.setStatus(e.Error())
		return
	}
	if c {
		return
	}
	setText(w.ocrPath, p)
	w.setWorkflowState(pageOCR, "[Đang mở] Đang đọc video và khởi tạo player native...")
	progressSet(w.ocrProgress, 0)
	w.ocrHasCheckpoint = false
	w.ocrCues = nil
	w.ocrTotalCues = 0
	w.renderCues()
	setText(w.ocrStart, "Bắt đầu quét")
	w.loadPreview(pageOCR, p)
}
func (w *window) pickEditorVideo() {
	p, c, e := pickVideo(w.hwnd, text(w.editorPath))
	if e != nil {
		w.setStatus(e.Error())
		return
	}
	if c {
		return
	}
	setText(w.editorPath, p)
	w.setWorkflowState(pageEditor, "[Đang mở] Đang đọc video và khởi tạo player native...")
	progressSet(w.editorProgress, 0)
	w.editorModel.reset()
	w.syncEditorControls()
	base := strings.TrimSuffix(filepath.Base(p), filepath.Ext(p))
	if base != "" {
		setText(w.editorName, base+"_edited.mp4")
	}
	w.loadPreview(pageEditor, p)
}
func (w *window) loadPreview(page int, path string) {
	w.setStatus("Đang đọc video...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, c := context.WithTimeout(context.Background(), 2*time.Minute)
		defer c()
		info, e := w.app.PreviewInfo(ctx, path)
		if e != nil {
			w.post(func() {
				w.setWorkflowState(page, "[Lỗi] Không đọc được video: "+e.Error())
				w.setStatus("Lỗi mở video: " + e.Error())
				w.syncControls()
			})
			return
		}
		ff, e := w.app.EnsureFFmpeg(ctx)
		if e != nil {
			w.post(func() {
				w.setWorkflowState(page, "[Lỗi] Không chuẩn bị được FFmpeg do BiliSub quản lý: "+e.Error())
				w.setStatus("Lỗi FFmpeg: " + e.Error())
				w.syncControls()
			})
			return
		}
		w.post(func() {
			p := nativeplayer.New(ff)
			p.SetStateCallback(func() { postMessageW.Call(w.hwnd, wmAppState, 0, 0) })
			if page == pageOCR {
				p.SetFrameCallback(func(f nativeplayer.Frame) {
					w.ocrFrame.mu.Lock()
					w.ocrFrame.frame = f
					w.ocrFrame.mu.Unlock()
					postMessageW.Call(w.hwnd, wmAppFrame, 0, 0)
				})
				if err := p.Open(nativeplayer.Media{Path: path, Width: info.Width, Height: info.Height, Duration: info.Duration}); err != nil {
					p.Close()
					w.setWorkflowState(pageOCR, "[Lỗi] Player native không mở được video: "+err.Error())
					w.setStatus("Lỗi player: " + err.Error())
					w.syncControls()
					return
				}
				if w.ocrPlayer != nil {
					w.ocrPlayer.Close()
				}
				w.ocrPlayer = p
				w.ocrInfoDuration = info.Duration
				setText(w.ocrStatus, fmt.Sprintf("[Sẵn sàng] %dx%d · %.1f phút · %s · hãy Test OCR trước khi quét", info.Width, info.Height, info.Duration/60, strings.ToUpper(info.Codec)))
				w.validateOCRLive()
				w.refreshOCRCheckpoint()
			} else {
				p.SetFrameCallback(func(f nativeplayer.Frame) {
					w.editorFrame.mu.Lock()
					w.editorFrame.frame = f
					w.editorFrame.mu.Unlock()
					postMessageW.Call(w.hwnd, wmAppFrame, 0, 0)
				})
				if err := p.Open(nativeplayer.Media{Path: path, Width: info.Width, Height: info.Height, Duration: info.Duration}); err != nil {
					p.Close()
					w.setWorkflowState(pageEditor, "[Lỗi] Player native không mở được video: "+err.Error())
					w.setStatus("Lỗi player: " + err.Error())
					w.syncControls()
					return
				}
				if w.editorPlayer != nil {
					w.editorPlayer.Close()
				}
				w.editorPlayer = p
				w.editorDuration = info.Duration
				w.editorInfoW, w.editorInfoH = info.Width, info.Height
				setText(w.editorStatus, fmt.Sprintf("[Sẵn sàng] %dx%d · %.1f phút · kéo trên preview hoặc dùng preset để tạo vùng", info.Width, info.Height, info.Duration/60))
				w.syncEditorControls()
			}
			w.setStatus("Video đã sẵn sàng")
			w.syncPlayerButtons()
			w.syncControls()
		})
	})
}

func (w *window) togglePlay(p *nativeplayer.Player) {
	if p == nil {
		return
	}
	if p.Playing() {
		p.Pause()
	} else if e := p.Play(); e != nil {
		w.setStatus(e.Error())
	}
}
func (w *window) toggleMute(p *nativeplayer.Player) {
	if p != nil {
		p.SetMuted(!p.Muted())
	}
}
func (w *window) syncPlayerButtons() {
	if w.ocrPlayer != nil {
		if w.ocrPlayer.Playing() {
			setText(w.ocrPlay, "Dừng")
		} else {
			setText(w.ocrPlay, "Phát")
		}
		if w.ocrPlayer.Muted() {
			setText(w.ocrMute, "Bật tiếng")
		} else {
			setText(w.ocrMute, "Tắt tiếng")
		}
	}
	if w.editorPlayer != nil {
		if w.editorPlayer.Playing() {
			setText(w.editorPlay, "Dừng")
		} else {
			setText(w.editorPlay, "Phát")
		}
		if w.editorPlayer.Muted() {
			setText(w.editorMute, "Bật tiếng")
		} else {
			setText(w.editorMute, "Tắt tiếng")
		}
	}
}
func (w *window) scroll(source uintptr) {
	if source == w.ocrTimeline && w.ocrPlayer != nil {
		at := float64(trackPos(source)) / 10000 * w.ocrPlayer.Duration()
		_ = w.ocrPlayer.Seek(at)
		w.syncCueToTime(at)
	} else if source == w.editorTimeline && w.editorPlayer != nil {
		at := float64(trackPos(source)) / 10000 * w.editorPlayer.Duration()
		_ = w.editorPlayer.Seek(at)
	}
}

func (w *window) validateOCRRegion() error {
	return validateOCRRegionInput(ocrRegionInput{
		Top: text(w.ocrTop), Bottom: text(w.ocrBottom), Left: text(w.ocrLeft), Right: text(w.ocrRight),
	})
}

func (w *window) validateOCRLive() {
	if w.ocrPlayer == nil || !nonEmpty(text(w.ocrPath)) {
		w.ocrValidationErr = ""
		return
	}
	if err := w.validateOCRRegion(); err != nil {
		w.ocrValidationErr = err.Error()
		w.setWorkflowState(pageOCR, "[Sai ROI] "+err.Error()+" · Test/Bắt đầu quét đang bị khóa.")
		return
	}
	if w.ocrValidationErr != "" {
		w.setWorkflowState(pageOCR, "[ROI hợp lệ] Có thể Test OCR hoặc bắt đầu quét.")
	}
	w.ocrValidationErr = ""
}

func parsePct(h uintptr, def float64) float64 {
	v, e := strconv.ParseFloat(strings.TrimSpace(text(h)), 64)
	if e != nil {
		return def
	}
	return math.Max(0, math.Min(100, v))
}
func (w *window) ocrRegion() ocr.ScanRegion {
	l := parsePct(w.ocrLeft, 5) / 100
	r := parsePct(w.ocrRight, 95) / 100
	t := parsePct(w.ocrTop, 65) / 100
	b := parsePct(w.ocrBottom, 94) / 100
	if r < l {
		l, r = r, l
	}
	if b < t {
		t, b = b, t
	}
	return ocr.ScanRegion{X: l, Y: t, W: math.Max(.002, r-l), H: math.Max(.002, b-t)}
}

func (w *window) applyOCRSubtitlePreset() {
	setText(w.ocrLeft, "5")
	setText(w.ocrRight, "95")
	setText(w.ocrTop, "65")
	setText(w.ocrBottom, "94")
	invalidate(w.hwnd)
	w.setStatus("Đã đặt preset vùng phụ đề")
}
func (w *window) prepareOCR() {
	if w.ocrPreparing {
		return
	}
	dev := []string{"auto", "gpu", "cpu", "hybrid"}[maxInt(0, minInt(3, comboIndex(w.ocrDevice)))]
	if e := w.app.EnsureOCR(dev); e != nil {
		w.setWorkflowState(pageOCR, "[Lỗi] Không bắt đầu chuẩn bị OCR: "+e.Error())
		w.setStatus("Lỗi OCR: " + e.Error())
		return
	}
	w.ocrPreparing = true
	w.setWorkflowState(pageOCR, "[Đang chuẩn bị] Đang kiểm tra runtime, model và worker PaddleOCR...")
	w.setStatus("Đang chuẩn bị PaddleOCR...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Minute)
		defer cancel()
		ticker := time.NewTicker(700 * time.Millisecond)
		defer ticker.Stop()
		for {
			st := w.app.OCRStatus(ctx)
			if st.Ready {
				w.post(func() {
					w.ocrPreparing = false
					detail := st.Engine
					if st.Model != "" {
						detail += " · " + st.Model
					}
					if st.ActiveMode != "" {
						detail += " · " + st.ActiveMode
					}
					w.setWorkflowState(pageOCR, "[Sẵn sàng] Bộ OCR đã sẵn sàng · "+detail)
					w.setStatus("Bộ OCR đã sẵn sàng")
					w.syncControls()
				})
				return
			}
			if st.State == ocr.StateFailed {
				errText := st.Error
				if errText == "" {
					errText = "khởi tạo OCR thất bại"
				}
				w.post(func() {
					w.ocrPreparing = false
					w.setWorkflowState(pageOCR, "[Lỗi] "+errText)
					w.setStatus("Lỗi OCR: " + errText)
					w.syncControls()
				})
				return
			}
			select {
			case <-ctx.Done():
				w.post(func() {
					w.ocrPreparing = false
					w.setWorkflowState(pageOCR, "[Lỗi] Hết thời gian chuẩn bị OCR.")
					w.setStatus("Hết thời gian chuẩn bị OCR")
					w.syncControls()
				})
				return
			case <-ticker.C:
			}
		}
	})
}
func (w *window) testOCR() {
	if w.ocrPlayer == nil {
		w.setWorkflowState(pageOCR, "[Cần thao tác] Hãy chọn video trước khi Test OCR.")
		return
	}
	if err := w.validateOCRRegion(); err != nil {
		w.setWorkflowState(pageOCR, "[Sai ROI] "+err.Error())
		w.setStatus(err.Error())
		return
	}
	if w.ocrTesting {
		return
	}
	w.ocrTesting = true
	req := application.OCRFrameRequest{Path: text(w.ocrPath), Time: w.ocrPlayer.Position(), Region: w.ocrRegion(), Device: []string{"auto", "gpu", "cpu", "hybrid"}[maxInt(0, minInt(3, comboIndex(w.ocrDevice)))]}
	w.setWorkflowState(pageOCR, "[Đang test] Đang nhận diện frame hiện tại trong ROI...")
	w.setStatus("Đang test OCR...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, c := context.WithTimeout(context.Background(), 2*time.Minute)
		defer c()
		res, e := w.app.OCRFrame(ctx, req)
		w.post(func() {
			w.ocrTesting = false
			if e != nil {
				w.setWorkflowState(pageOCR, "[Lỗi Test OCR] "+e.Error())
				w.setStatus("Lỗi Test OCR: " + e.Error())
				w.syncControls()
				return
			}
			if strings.TrimSpace(res.Text) == "" {
				w.setWorkflowState(pageOCR, "[Không nhận diện] Frame hiện tại chưa có chữ phù hợp. Hãy kiểm tra ROI/timeline/độ nhạy.")
			} else {
				w.setWorkflowState(pageOCR, fmt.Sprintf("[Test PASS] %.0f%% · %s", res.Confidence*100, res.Text))
			}
			w.setStatus("Test OCR hoàn tất")
			w.syncControls()
		})
	})
}

func (w *window) scanRequest() ocr.ScanRequest {
	mode := []string{"accurate", "balanced", "fast"}[maxInt(0, minInt(2, comboIndex(w.ocrMode)))]
	sens := []float64{.75, 1, 1.25}[maxInt(0, minInt(2, comboIndex(w.ocrSensitivity)))]
	dev := []string{"auto", "gpu", "cpu", "hybrid"}[maxInt(0, minInt(3, comboIndex(w.ocrDevice)))]
	par := []string{"auto", "1", "2", "4", "8", "16"}[maxInt(0, minInt(5, comboIndex(w.ocrParallel)))]
	return ocr.ScanRequest{Path: text(w.ocrPath), Region: w.ocrRegion(), Mode: mode, Device: dev, Parallelism: par, Sensitivity: sens, Duration: w.ocrInfoDuration}
}
func (w *window) startOCR(restart bool) {
	if !w.canStartJob() {
		return
	}
	if w.ocrPlayer == nil || !nonEmpty(text(w.ocrPath)) {
		w.setWorkflowState(pageOCR, "[Cần thao tác] Hãy chọn video trước.")
		return
	}
	if err := w.validateOCRRegion(); err != nil {
		w.setWorkflowState(pageOCR, "[Sai ROI] "+err.Error())
		w.setStatus(err.Error())
		return
	}
	if restart && (w.ocrHasCheckpoint || w.ocrTotalCues > 0) {
		if !confirm(w.hwnd, "Quét lại từ đầu", "Thao tác này sẽ xóa checkpoint/kết quả OCR hiện tại của cấu hình này và quét lại từ đầu. Tiếp tục?") {
			return
		}
	}
	req := w.scanRequest()
	if restart {
		_ = w.app.RemoveOCRCheckpoint(req)
		w.ocrHasCheckpoint = false
		setText(w.ocrStart, "Bắt đầu quét")
		w.ocrCues = nil
		w.ocrTotalCues = 0
		w.renderCues()
	}
	id, e := w.app.StartOCRScan(req)
	if e != nil {
		w.setStatus(e.Error())
		return
	}
	if w.ocrPlayer != nil {
		w.ocrPlayer.Pause()
	}
	progressSet(w.ocrProgress, 0)
	w.bindJob(id, pageOCR)
	w.setWorkflowState(pageOCR, "[Đang quét] Đang xử lý video. Preview theo mốc liên tục an toàn; Pause sẽ lưu checkpoint trước khi dừng.")
	w.setStatus("Đang quét OCR...")
	w.syncControls()
}

func (w *window) refreshOCRCheckpoint() {
	req := w.scanRequest()
	w.asyncDo(func() {
		cp, err := w.app.InspectOCRCheckpoint(req)
		w.post(func() {
			if err != nil {
				setText(w.ocrStatus, "Checkpoint lỗi: "+err.Error())
				return
			}
			w.ocrHasCheckpoint = cp.Exists
			if !cp.Exists {
				progressSet(w.ocrProgress, 0)
				setText(w.ocrStart, "Bắt đầu quét")
				w.ocrCues = nil
				w.ocrTotalCues = 0
				w.renderCues()
				setText(w.ocrMetrics, "Checkpoint: không có tiến độ đã lưu")
				w.syncControls()
				return
			}
			progressSet(w.ocrProgress, cp.ProgressPercent)
			setText(w.ocrStart, "Tiếp tục quét")
			w.ocrCues = append([]ocr.Cue(nil), cp.RecentCues...)
			w.ocrTotalCues = cp.CueCount
			w.renderCues()
			setText(w.ocrStatus, fmt.Sprintf("Đã lưu %.1f%% tổng công việc · mốc liên tục %s · %d câu · %d/%d lane hoàn tất · %d luồng",
				cp.ProgressPercent, clock(cp.MediaSeconds), cp.CueCount, cp.CompletedLanes, cp.TotalLanes, cp.ParallelismSelected))
			setText(w.ocrMetrics, formatOCRTelemetry(telemetryFromCheckpoint(cp, len(w.ocrCues))))
			w.syncControls()
			if w.ocrPlayer != nil && cp.MediaSeconds > 0 {
				_ = w.ocrPlayer.Seek(cp.MediaSeconds)
				trackSet(w.ocrTimeline, int(cp.MediaSeconds/math.Max(.001, w.ocrPlayer.Duration())*10000))
			}
		})
	})
}

func (w *window) pauseOCR() {
	if w.active == nil || w.active.kind != pageOCR {
		return
	}
	id := w.active.id
	w.setWorkflowState(pageOCR, "[Đang Pause] Đang chờ các lane tới safe boundary và fsync checkpoint...")
	w.setStatus("Đang lưu checkpoint an toàn...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, c := context.WithTimeout(context.Background(), 95*time.Second)
		defer c()
		snap, e := w.app.PauseJob(ctx, id)
		w.post(func() {
			if e != nil {
				w.setWorkflowState(pageOCR, "[Lỗi Pause] "+e.Error())
				w.setStatus(e.Error())
				w.syncControls()
				return
			}
			w.setWorkflowState(pageOCR, "[Đã tạm dừng] Checkpoint an toàn đã được lưu. Có thể đóng app hoặc Tiếp tục quét.")
			w.setStatus(snap.Message)
			w.refreshOCRCheckpoint()
		})
	})
}
func (w *window) exportOCR() {
	if w.ocrHasCheckpoint {
		w.setWorkflowState(pageOCR, "[Chưa thể xuất] Scan đang tạm dừng ở checkpoint. Hãy Tiếp tục quét đến khi hoàn tất để xuất toàn bộ SRT.")
		w.setStatus("Chưa thể xuất SRT từ danh sách recent checkpoint")
		return
	}
	if len(w.ocrCues) == 0 {
		w.setStatus("Chưa có phụ đề để xuất")
		return
	}
	cues := make([]application.OCRCue, 0, len(w.ocrCues))
	for _, c := range w.ocrCues {
		cues = append(cues, application.OCRCue{Start: c.Start, End: c.End, Text: c.Text, Conf: c.Conf})
	}
	p, n, e := w.app.ExportOCR(cues, text(w.ocrOut), "BiliSub_OCR_Chinese.srt")
	if e != nil {
		w.setStatus(e.Error())
		return
	}
	w.setWorkflowState(pageOCR, fmt.Sprintf("[Đã xuất] %d câu SRT · %s", n, p))
	w.setStatus(fmt.Sprintf("Đã xuất %d câu: %s", n, p))
}
func (w *window) renderCues() {
	listReset(w.ocrCueList)
	for _, c := range w.ocrCues {
		listAdd(w.ocrCueList, fmt.Sprintf("%s  %s  %.0f%%", clock(c.Start), c.Text, c.Conf*100))
	}
	total := maxInt(len(w.ocrCues), w.ocrTotalCues)
	if total == 0 {
		setText(w.ocrCueSummary, "Danh sách phụ đề: chưa có cue · Test OCR hoặc bắt đầu quét để tạo kết quả")
	} else if w.ocrHasCheckpoint {
		setText(w.ocrCueSummary, fmt.Sprintf("Phụ đề gần mốc preview: %d / %d câu tổng · checkpoint đang được giữ", len(w.ocrCues), total))
	} else {
		setText(w.ocrCueSummary, fmt.Sprintf("Danh sách phụ đề: %d / %d câu", len(w.ocrCues), total))
	}
}
func (w *window) syncCueToTime(at float64) {
	if len(w.ocrCues) == 0 {
		return
	}
	lo, hi := 0, len(w.ocrCues)
	for lo < hi {
		m := (lo + hi) / 2
		if w.ocrCues[m].Start < at {
			lo = m + 1
		} else {
			hi = m
		}
	}
	idx := lo
	if idx >= len(w.ocrCues) {
		idx = len(w.ocrCues) - 1
	} else if idx > 0 && math.Abs(w.ocrCues[idx-1].Start-at) < math.Abs(w.ocrCues[idx].Start-at) {
		idx--
	}
	listSelect(w.ocrCueList, idx)
}

func (w *window) seekSelectedCue() {
	idx := listIndex(w.ocrCueList)
	if idx < 0 || idx >= len(w.ocrCues) || w.ocrPlayer == nil {
		return
	}
	at := w.ocrCues[idx].Start
	if err := w.ocrPlayer.Seek(at); err != nil {
		w.setStatus(err.Error())
		return
	}
	d := w.ocrPlayer.Duration()
	if d > 0 {
		trackSet(w.ocrTimeline, int(at/d*10000))
		setText(w.ocrTime, clock(at)+" / "+clock(d))
	}
	w.setStatus("Đã nhảy tới phụ đề " + clock(at))
}

func (w *window) editorInputFromControls() editorInput {
	return editorInput{
		X: text(w.editorX), Y: text(w.editorY), W: text(w.editorW), H: text(w.editorH),
		Strength: text(w.editorStrength), Whole: checkGet(w.editorWhole),
		Start: text(w.editorStart), End: text(w.editorEnd), Duration: w.editorDuration,
	}
}

func (w *window) validateEditorSelection() error {
	if w.editorModel.selected < 0 || w.editorModel.selected >= len(w.editorModel.regions) {
		return fmt.Errorf("hãy chọn một vùng trong danh sách")
	}
	return validateEditorInput(w.editorInputFromControls())
}

func (w *window) setEditorValidationError(err error) {
	if err == nil {
		w.clearEditorValidationError()
		return
	}
	w.editorValidationErr = err.Error()
	w.setWorkflowState(pageEditor, "[Dữ liệu vùng chưa hợp lệ] "+err.Error()+" · sửa giá trị trước khi xuất.")
}

func (w *window) clearEditorValidationError() {
	if w.editorValidationErr != "" {
		w.setWorkflowState(pageEditor, fmt.Sprintf("[Vùng hợp lệ] %d vùng · có thể tiếp tục chỉnh hoặc xuất video.", len(w.editorModel.regions)))
	}
	w.editorValidationErr = ""
}

func (w *window) validateEditorExport() error {
	if w.editorPlayer == nil || !nonEmpty(text(w.editorPath)) {
		return fmt.Errorf("hãy chọn video nguồn trước")
	}
	if len(w.editorModel.regions) == 0 {
		return fmt.Errorf("hãy tạo ít nhất một vùng cần xử lý")
	}
	if !nonEmpty(text(w.editorOut)) {
		return fmt.Errorf("hãy chọn thư mục xuất")
	}
	if !nonEmpty(text(w.editorName)) {
		return fmt.Errorf("hãy nhập tên file xuất")
	}
	if w.editorModel.selected >= 0 {
		if err := w.validateEditorSelection(); err != nil {
			return err
		}
	}
	return nil
}

func (w *window) editorRegionFromControls() videoedit.Region {
	x := parsePct(w.editorX, 5) / 100
	y := parsePct(w.editorY, 70) / 100
	ww := parsePct(w.editorW, 90) / 100
	hh := parsePct(w.editorH, 20) / 100
	effect := []string{"blur", "mosaic", "cover"}[maxInt(0, minInt(2, comboIndex(w.editorEffect)))]
	strength, _ := strconv.Atoi(strings.TrimSpace(text(w.editorStrength)))
	start, _ := strconv.ParseFloat(strings.TrimSpace(text(w.editorStart)), 64)
	end, _ := strconv.ParseFloat(strings.TrimSpace(text(w.editorEnd)), 64)
	if end <= 0 {
		end = w.editorDuration
	}
	return normalizeEditorRegion(videoedit.Region{X: x, Y: y, W: ww, H: hh, Effect: effect, Strength: strength, Whole: checkGet(w.editorWhole), Start: start, End: end})
}

func effectIndex(effect string) int {
	switch effect {
	case "mosaic":
		return 1
	case "cover":
		return 2
	default:
		return 0
	}
}

func effectLabel(effect string) string {
	switch effect {
	case "mosaic":
		return "Mosaic"
	case "cover":
		return "Che đen"
	default:
		return "Làm mờ"
	}
}

func (w *window) syncEditorControls() {
	w.syncingEditor = true
	defer func() { w.syncingEditor = false }()
	listReset(w.editorRegionList)
	for i, r := range w.editorModel.regions {
		scope := "toàn video"
		if !r.Whole {
			scope = clock(r.Start) + "–" + clock(r.End)
		}
		listAdd(w.editorRegionList, fmt.Sprintf("Vùng %d · %s · %s", i+1, effectLabel(r.Effect), scope))
	}
	if w.editorModel.selected >= 0 && w.editorModel.selected < len(w.editorModel.regions) {
		listSelect(w.editorRegionList, w.editorModel.selected)
		r := w.editorModel.regions[w.editorModel.selected]
		setText(w.editorX, fmt.Sprintf("%.1f", r.X*100))
		setText(w.editorY, fmt.Sprintf("%.1f", r.Y*100))
		setText(w.editorW, fmt.Sprintf("%.1f", r.W*100))
		setText(w.editorH, fmt.Sprintf("%.1f", r.H*100))
		comboSet(w.editorEffect, effectIndex(r.Effect))
		setText(w.editorStrength, strconv.Itoa(r.Strength))
		checkSet(w.editorWhole, r.Whole)
		setText(w.editorStart, fmt.Sprintf("%.3f", r.Start))
		setText(w.editorEnd, fmt.Sprintf("%.3f", r.End))
	}
	hasSel := w.editorModel.selected >= 0 && w.editorModel.selected < len(w.editorModel.regions)
	if hasSel {
		if err := w.validateEditorSelection(); err != nil {
			w.editorValidationErr = err.Error()
		} else {
			w.editorValidationErr = ""
		}
	} else {
		w.editorValidationErr = ""
	}
	setText(w.editorStatus, fmt.Sprintf("%d vùng · %s", len(w.editorModel.regions), func() string {
		if hasSel {
			return fmt.Sprintf("đang chọn Vùng %d", w.editorModel.selected+1)
		}
		return "kéo trên preview hoặc dùng preset để tạo vùng"
	}()))
	w.syncControls()
	invalidate(w.hwnd)
}

func (w *window) editorSelectRegion(idx int) {
	if idx < 0 || idx >= len(w.editorModel.regions) {
		return
	}
	w.editorModel.selected = idx
	w.syncEditorControls()
}

func (w *window) editorAddPreset(kind string) {
	if w.editorPlayer == nil {
		w.setStatus("Hãy chọn video trước")
		return
	}
	if w.editorModel.selected >= 0 {
		if err := w.validateEditorSelection(); err != nil {
			w.setEditorValidationError(err)
			w.syncControls()
			return
		}
		w.editorCommitSelected(false)
	}
	w.editorModel.addPreset(kind, w.editorDuration)
	w.syncEditorControls()
	name := map[bool]string{true: "watermark", false: "phụ đề"}[kind == "watermark"]
	w.setWorkflowState(pageEditor, "[Đã thêm] Vùng "+name+". Có thể kéo preview hoặc chỉnh X/Y/Rộng/Cao để tinh chỉnh.")
	w.setStatus("Đã thêm vùng " + name)
	w.syncControls()
}

func (w *window) editorDeleteSelected() {
	if w.editorModel.deleteSelected() {
		w.syncEditorControls()
		w.setWorkflowState(pageEditor, "[Đã xóa] Vùng đã được xóa. Có thể Hoàn tác nếu cần.")
		w.setStatus("Đã xóa vùng")
		w.syncControls()
	}
}

func (w *window) editorUndoLast() {
	if w.editorModel.undoLast() {
		w.syncEditorControls()
		w.setWorkflowState(pageEditor, "[Đã hoàn tác] Khôi phục trạng thái vùng trước đó.")
		w.setStatus("Đã hoàn tác")
		w.syncControls()
	}
}

func (w *window) editorCommitSelected(saveUndo bool) {
	if w.editorModel.selected < 0 || w.editorModel.selected >= len(w.editorModel.regions) {
		return
	}
	w.editorModel.replaceSelected(w.editorRegionFromControls(), saveUndo)
}

func (w *window) editorSetBoundary(startBoundary bool) {
	if w.editorPlayer == nil {
		w.setWorkflowState(pageEditor, "[Cần thao tác] Hãy chọn video trước.")
		return
	}
	if err := validateEditorGeometryInput(w.editorInputFromControls()); err != nil {
		w.setEditorValidationError(err)
		w.syncControls()
		return
	}
	w.editorCommitSelected(false)
	r, ok := w.editorModel.selectedRegion()
	if !ok {
		return
	}
	w.editorModel.snapshot()
	at := w.editorPlayer.Position()
	r.Whole = false
	if startBoundary {
		r.Start = math.Min(at, math.Max(0, r.End-.05))
	} else {
		r.End = math.Max(r.Start+.05, at)
	}
	w.editorModel.replaceSelected(r, false)
	w.syncEditorControls()
}

func (w *window) exportEditor() {
	if !w.canStartJob() {
		return
	}
	if err := w.validateEditorExport(); err != nil {
		w.setWorkflowState(pageEditor, "[Cần thao tác] "+err.Error())
		w.setStatus("Chưa thể xuất video: " + err.Error())
		return
	}
	w.editorCommitSelected(false)
	req := videoedit.Request{
		InputPath: text(w.editorPath), OutputDir: text(w.editorOut), FileName: text(w.editorName),
		SourceWidth: w.editorInfoW, SourceHeight: w.editorInfoH, Duration: w.editorDuration,
		Regions: cloneRegions(w.editorModel.regions),
	}
	progressSet(w.editorProgress, 0)
	setText(w.editorLog, "")
	w.setWorkflowState(pageEditor, fmt.Sprintf("[Đang xuất] Đang áp dụng %d vùng và mã hóa video...", len(w.editorModel.regions)))
	w.bindJob(w.app.StartEditor(req), pageEditor)
	w.setStatus("Đang xuất video...")
	w.syncControls()
}

func (w *window) saveCookie() {
	raw := strings.TrimSpace(text(w.settingsCookie))
	if raw == "" {
		w.setWorkflowState(pageSettings, "[Cần thao tác] Hãy nhập Cookie/SESSDATA hoặc dùng đăng nhập QR.")
		focused(w.settingsCookie)
		return
	}
	w.cookieBusy = true
	w.setWorkflowState(pageSettings, "[Đang đăng nhập] Đang xác minh Cookie với Bilibili...")
	w.setStatus("Đang kiểm tra đăng nhập...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, c := context.WithTimeout(context.Background(), 15*time.Second)
		defer c()
		user, e := w.app.SetCookie(ctx, raw)
		w.post(func() {
			w.cookieBusy = false
			if e != nil {
				w.setWorkflowState(pageSettings, "[Lỗi đăng nhập] "+e.Error())
				w.setStatus("Đăng nhập thất bại: " + e.Error())
				w.syncControls()
				return
			}
			w.cookieSaved = true
			setText(w.settingsCookie, "")
			w.setWorkflowState(pageSettings, "[Đăng nhập thành công] "+user)
			w.setStatus("Đăng nhập thành công " + user)
			w.refreshStatus(false)
			w.syncControls()
		})
	})
}

func (w *window) deleteCookie() {
	if !confirm(w.hwnd, "Xóa đăng nhập Bilibili", "Xóa Cookie/SESSDATA đang lưu trong BiliSub Studio?") {
		return
	}
	if e := w.app.DeleteCookie(); e != nil {
		w.setStatus(e.Error())
	} else {
		w.cookieSaved = false
		setText(w.settingsCookieState, "Cookie: Chưa đăng nhập")
		w.setWorkflowState(pageSettings, "[Đã đăng xuất] Cookie/SESSDATA lưu trong BiliSub đã được xóa.")
		w.setStatus("Đã xóa đăng nhập")
		w.syncControls()
	}
}
func (w *window) startQR() {
	if w.qrBusy {
		w.qrBusy = false
		w.qrKey = ""
		w.qrCode = qrcode.Matrix{}
		setText(w.qrBtn, "Đăng nhập QR")
		setText(w.qrState, "QR: đã hủy")
		w.setWorkflowState(pageSettings, "[Đã hủy QR] Có thể tạo mã mới bất cứ lúc nào.")
		invalidate(w.hwnd)
		w.syncControls()
		return
	}
	w.qrBusy = true
	setText(w.qrBtn, "Hủy QR")
	setText(w.qrState, "QR: đang tạo...")
	w.setWorkflowState(pageSettings, "[Đăng nhập QR] Đang yêu cầu mã QR từ Bilibili...")
	w.setStatus("Đang tạo QR...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, c := context.WithTimeout(context.Background(), 15*time.Second)
		defer c()
		q, e := w.app.QRStart(ctx)
		w.post(func() {
			if e != nil {
				w.qrBusy = false
				setText(w.qrBtn, "Đăng nhập QR")
				setText(w.qrState, "QR: lỗi tạo mã")
				w.setWorkflowState(pageSettings, "[Lỗi QR] "+e.Error())
				w.setStatus(e.Error())
				w.syncControls()
				return
			}
			matrix, qrErr := qrcode.Encode(q.URL)
			if qrErr != nil {
				w.qrBusy = false
				setText(w.qrBtn, "Đăng nhập QR")
				setText(w.qrState, "QR: lỗi render")
				w.setWorkflowState(pageSettings, "[Lỗi QR] Không render được QR native: "+qrErr.Error())
				w.setStatus("Không tạo được QR native: " + qrErr.Error())
				w.syncControls()
				return
			}
			w.qrCode = matrix
			w.qrKey = q.Key
			w.qrTimer = time.Now()
			setText(w.qrState, "QR: đang chờ quét/xác nhận trên điện thoại")
			setText(w.settingsStatus, "Mã QR đang hiển thị trực tiếp trong BiliSub Studio.\r\n1. Mở ứng dụng Bilibili trên điện thoại.\r\n2. Quét mã QR bên phải.\r\n3. Xác nhận đăng nhập trên điện thoại.\r\nBiliSub sẽ tự cập nhật trạng thái.")
			w.setWorkflowState(pageSettings, "[QR sẵn sàng] Hãy quét mã và xác nhận trên điện thoại.")
			w.setStatus("QR đã tạo · hãy quét mã trong cửa sổ BiliSub")
			w.syncControls()
			invalidate(w.hwnd)
		})
	})
}
func (w *window) pollQR() {
	if w.qrKey == "" || time.Since(w.qrTimer) < 1500*time.Millisecond {
		return
	}
	key := w.qrKey
	w.qrTimer = time.Now()
	w.asyncDo(func() {
		ctx, c := context.WithTimeout(context.Background(), 15*time.Second)
		defer c()
		r, e := w.app.QRPoll(ctx, key)
		w.post(func() {
			if e != nil {
				w.setStatus(e.Error())
				return
			}
			w.setStatus(r.Message)
			setText(w.qrState, "QR: "+r.Message)
			w.setWorkflowState(pageSettings, "[QR] "+r.Message)
			if r.LoggedIn || r.Expired {
				w.qrBusy = false
				setText(w.qrBtn, "Đăng nhập QR")
				w.qrKey = ""
				w.qrCode = qrcode.Matrix{}
				invalidate(w.hwnd)
			}
			if r.LoggedIn {
				w.cookieSaved = true
				setText(w.qrState, "QR: đăng nhập thành công")
				w.refreshStatus(false)
			} else if r.Expired {
				setText(w.qrState, "QR: đã hết hạn · bấm Đăng nhập QR để tạo mã mới")
			}
			w.syncControls()
		})
	})
}
func (w *window) doCheckUpdate() {
	if w.updateBusy {
		return
	}
	w.updateBusy = true
	w.setWorkflowState(pageSettings, "[Đang kiểm tra] Đang kiểm tra phiên bản mới...")
	w.setStatus("Đang kiểm tra cập nhật...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, c := context.WithTimeout(context.Background(), 15*time.Second)
		defer c()
		u, e := w.app.CheckUpdate(ctx)
		w.post(func() {
			w.updateBusy = false
			if e != nil {
				w.updateAvailable = false
				w.setWorkflowState(pageSettings, "[Lỗi cập nhật] "+e.Error())
				w.setStatus("Không kiểm tra được cập nhật: " + e.Error())
				w.syncControls()
				return
			}
			w.updateAvailable = u.Available
			setText(w.settingsStatus, fmt.Sprintf("Hiện tại: %s\r\nMới nhất: %s\r\nCó bản mới: %v\r\n%s", u.Current, u.Latest, u.Available, strings.Join(u.Notes, "\r\n")))
			if u.Available {
				w.setWorkflowState(pageSettings, "[Có bản mới] "+u.Latest+" · bấm Cập nhật ngay để tải và áp dụng.")
			} else {
				w.setWorkflowState(pageSettings, "[Đã mới nhất] Không có bản cập nhật mới.")
			}
			w.setStatus("Kiểm tra cập nhật hoàn tất")
			w.syncControls()
		})
	})
}

func (w *window) doApplyUpdate() {
	if w.updateBusy {
		return
	}
	if !w.updateAvailable {
		w.setWorkflowState(pageSettings, "[Cần thao tác] Hãy bấm Kiểm tra cập nhật trước; hiện chưa có bản mới đã xác nhận.")
		return
	}
	if !confirm(w.hwnd, "Cập nhật BiliSub Studio", "Tải bản cập nhật, xác minh gói rồi đóng ứng dụng để thay EXE? OCR đang chạy phải được Pause/checkpoint trước khi đóng.") {
		return
	}
	w.updateBusy = true
	w.setWorkflowState(pageSettings, "[Đang cập nhật] Đang tải và xác minh gói cập nhật...")
	w.setStatus("Đang tải cập nhật...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, c := context.WithTimeout(context.Background(), 3*time.Minute)
		defer c()
		path, ver, e := w.app.PrepareUpdate(ctx)
		w.post(func() {
			if e != nil {
				w.updateBusy = false
				w.setWorkflowState(pageSettings, "[Lỗi cập nhật] "+e.Error())
				w.setStatus("Cập nhật thất bại: " + e.Error())
				w.syncControls()
				return
			}
			w.result.UpdatePath = path
			w.setWorkflowState(pageSettings, "[Đã tải] Bản "+ver+" đã được xác minh. BiliSub sẽ đóng an toàn và khởi động updater.")
			w.setStatus("Đã tải v" + ver + " · đang khởi động updater")
			postMessageW.Call(w.hwnd, wmClose, 0, 0)
		})
	})
}

func (w *window) doCleanup() {
	if e := w.app.CleanupStorage(); e != nil {
		w.setStatus(e.Error())
	} else {
		w.setWorkflowState(pageSettings, "[Đã dọn] Temp/Cache đã được dọn. File output của người dùng không bị xóa.")
		w.setStatus("Đã dọn Temp/Cache")
		w.refreshStatus(false)
	}
}
func (w *window) doResetTools() {
	if !confirm(w.hwnd, "Đặt lại Tools", "Xóa các tool do BiliSub quản lý để ứng dụng chuẩn bị lại khi cần? File output không bị xóa.") {
		return
	}
	if e := w.app.ResetTools(); e != nil {
		w.setStatus(e.Error())
	} else {
		w.setWorkflowState(pageSettings, "[Đã đặt lại] Tools sẽ được BiliSub chuẩn bị lại khi chức năng cần dùng.")
		w.setStatus("Đã đặt lại Tools")
		w.refreshStatus(false)
	}
}
func (w *window) doRemoveOCR() {
	if !confirm(w.hwnd, "Xóa bộ OCR", "Xóa runtime/model OCR do BiliSub quản lý? Lần dùng OCR sau sẽ phải chuẩn bị lại.") {
		return
	}
	if e := w.app.RemoveOCR(); e != nil {
		w.setStatus(e.Error())
	} else {
		w.setWorkflowState(pageSettings, "[Đã xóa] Bộ OCR đã được xóa. Bấm Chuẩn bị bộ nhận diện ở tab OCR để cài lại.")
		w.setStatus("Đã xóa bộ OCR")
		w.refreshStatus(false)
	}
}

func (w *window) sendBugReport() {
	note := strings.TrimSpace(text(w.bugNote))
	if note == "" {
		w.setStatus("Hãy mô tả lỗi trước khi gửi")
		return
	}
	id := application.NewBugID(time.Now())
	pages := []string{"subtitle", "video", "ocr", "editor", "settings"}
	page := "unknown"
	if w.page >= 0 && w.page < len(pages) {
		page = pages[w.page]
	}
	r := application.BugReport{
		ID: id, Page: page, Note: note,
		Video: map[string]string{
			"url": text(w.videoURL), "quality": comboText(w.videoQuality), "mode": comboText(w.videoMode),
			"container": comboText(w.videoContainer), "speed": comboText(w.videoSpeed), "meta": text(w.videoMeta),
		},
		Logs: map[string]string{"video": text(w.videoLog), "subtitle": text(w.subLog), "ocr": text(w.ocrStatus), "editor": text(w.editorLog)},
	}
	w.bugBusy = true
	w.setWorkflowState(pageSettings, "[Đang gửi báo lỗi] Mã "+id+" · đang gửi log đã được sanitizer...")
	w.setStatus("Đang gửi " + id + "...")
	w.syncControls()
	w.asyncDo(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
		defer cancel()
		err := w.app.SendBugReport(ctx, r)
		w.post(func() {
			w.bugBusy = false
			if err != nil {
				w.setWorkflowState(pageSettings, "[Lỗi gửi báo cáo] "+err.Error())
				w.setStatus("Không gửi được báo lỗi: " + err.Error())
				w.syncControls()
				return
			}
			setText(w.bugNote, "")
			w.setWorkflowState(pageSettings, "[Đã gửi] Mã báo lỗi "+id+" · hãy giữ mã này để tra cứu.")
			w.setStatus("Đã gửi " + id + " · hãy giữ mã này để tra lỗi")
			w.syncControls()
		})
	})
}

func (w *window) requestClose() {
	if w.closing || w.closed {
		return
	}
	w.closing = true
	w.setStatus("Đang đóng an toàn · lưu checkpoint OCR nếu cần...")
	w.asyncDo(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 100*time.Second)
		defer cancel()
		err := w.app.PrepareShutdown(ctx)
		w.post(func() {
			if err != nil {
				w.closing = false
				w.setStatus("Không thể đóng an toàn: " + err.Error())
				return
			}
			postMessageW.Call(w.hwnd, wmAppCloseReady, 0, 0)
		})
	})
}

func (w *window) finishClose() {
	if w.closed {
		return
	}
	w.closed = true
	if w.ocrPlayer != nil {
		w.ocrPlayer.Close()
	}
	if w.editorPlayer != nil {
		w.editorPlayer.Close()
	}
	destroyWindow.Call(w.hwnd)
}

func (w *window) tick() {
	defer w.syncControls()
	w.pollQR()
	if w.ocrPlayer != nil {
		p := w.ocrPlayer.Position()
		d := w.ocrPlayer.Duration()
		if d > 0 {
			trackSet(w.ocrTimeline, int(p/d*10000))
			setText(w.ocrTime, clock(p)+" / "+clock(d))
		}
	}
	if w.editorPlayer != nil {
		p := w.editorPlayer.Position()
		d := w.editorPlayer.Duration()
		if d > 0 {
			trackSet(w.editorTimeline, int(p/d*10000))
			setText(w.editorTime, clock(p)+" / "+clock(d))
		}
	}
	if w.active == nil {
		return
	}
	binding := w.active
	snap, ok := w.app.JobSnapshot(binding.id, binding.after)
	if !ok {
		return
	}
	for _, l := range snap.Logs {
		binding.logs = append(binding.logs, l)
		if binding.kind == pageSubtitle {
			w.appendLog(w.subLog, l)
		} else if binding.kind == pageVideo {
			w.appendLog(w.videoLog, l)
		} else if binding.kind == pageEditor {
			w.appendLog(w.editorLog, l)
		}
	}
	binding.after = snap.LogNext
	w.setProgressForKind(binding.kind, snap.Progress)
	msg := strings.TrimSpace(snap.Message)
	if msg == "" {
		msg = "Đang xử lý"
	}
	w.setStatus(fmt.Sprintf("%s · %.1f%%", msg, snap.Progress))
	if !snap.Done {
		switch binding.kind {
		case pageSubtitle:
			w.setWorkflowState(pageSubtitle, fmt.Sprintf("[Đang tải] %s · %.1f%%", msg, snap.Progress))
		case pageVideo:
			w.setWorkflowState(pageVideo, fmt.Sprintf("[Đang tải] %s · %.1f%%", msg, snap.Progress))
		case pageOCR:
			w.setWorkflowState(pageOCR, fmt.Sprintf("[Đang quét] %s · %.1f%%", msg, snap.Progress))
		case pageEditor:
			w.setWorkflowState(pageEditor, fmt.Sprintf("[Đang xuất] %s · %.1f%%", msg, snap.Progress))
		}
	}
	if binding.kind == pageOCR && !(snap.Done && snap.Status == "paused") {
		w.applyOCRResult(snap.Result)
	}
	if !snap.Done {
		return
	}
	pausedOCR := binding.kind == pageOCR && snap.Status == "paused"
	if snap.Error != "" {
		w.setStatus("Lỗi: " + snap.Error)
		w.setWorkflowState(binding.kind, "[Lỗi] "+snap.Error)
	} else if snap.Status == "cancelled" {
		w.setStatus("Đã hủy tác vụ")
		w.setWorkflowState(binding.kind, "[Đã hủy] Tác vụ đã dừng theo yêu cầu.")
	} else if pausedOCR {
		w.setWorkflowState(pageOCR, "[Đã tạm dừng] Checkpoint an toàn đã được lưu. Có thể đóng app hoặc Tiếp tục quét.")
	} else {
		w.setProgressForKind(binding.kind, 100)
		switch binding.kind {
		case pageSubtitle:
			w.setWorkflowState(pageSubtitle, "[Hoàn tất] Phụ đề đã được tải. Xem log để biết đường dẫn file.")
		case pageVideo:
			w.setWorkflowState(pageVideo, "[Hoàn tất] Video đã tải/xử lý xong. Có thể bấm Mở để xem thư mục output.")
		case pageOCR:
			w.setWorkflowState(pageOCR, "[Hoàn tất] OCR đã quét xong. Kiểm tra cue/timeline rồi Xuất SRT.")
		case pageEditor:
			w.setWorkflowState(pageEditor, "[Hoàn tất] Video đã xuất xong. Có thể mở thư mục output để kiểm tra.")
		}
	}
	if binding.kind == pageOCR && !pausedOCR {
		w.applyOCRResult(snap.Result)
	}
	w.active = nil
	if pausedOCR {
		w.refreshOCRCheckpoint()
	}
	w.syncEditorControls()
	w.refreshStatus(false)
}

func (w *window) applyOCRResult(v any) {
	switch r := v.(type) {
	case ocr.ScanResult:
		w.ocrCues = append([]ocr.Cue(nil), r.Cues...)
		w.ocrTotalCues = len(r.Cues)
		w.ocrHasCheckpoint = false
		w.renderCues()
		setText(w.ocrMetrics, formatOCRTelemetry(telemetryFromScanResult(r)))
		setText(w.ocrStart, "Bắt đầu quét")
		if w.ocrPlayer != nil && r.MediaSeconds > 0 {
			_ = w.ocrPlayer.Seek(r.MediaSeconds)
		}
	case map[string]any:
		if x := intAny(r["cue_count"]); x > 0 || r["cue_count"] != nil {
			w.ocrTotalCues = x
		}
		if cues, ok := r["recent_cues"].([]ocr.Cue); ok {
			w.ocrCues = append([]ocr.Cue(nil), cues...)
			w.renderCues()
		}
		if ms, ok := numAny(r["media_seconds"]); ok && w.ocrPlayer != nil {
			_ = w.ocrPlayer.Seek(ms)
		}
		t := telemetryFromLiveMap(r, len(w.ocrCues))
		setText(w.ocrMetrics, formatOCRTelemetry(t))
	}
}
func numAny(v any) (float64, bool) {
	switch x := v.(type) {
	case float64:
		return x, true
	case int:
		return float64(x), true
	case int64:
		return float64(x), true
	}
	return 0, false
}
func clock(sec float64) string {
	if sec < 0 {
		sec = 0
	}
	t := int(sec + .5)
	h := t / 3600
	m := (t % 3600) / 60
	s := t % 60
	if h > 0 {
		return fmt.Sprintf("%02d:%02d:%02d", h, m, s)
	}
	return fmt.Sprintf("%02d:%02d", m, s)
}

func (w *window) paint() {
	var ps paintStruct
	hdc, _, _ := beginPaint.Call(w.hwnd, uintptr(unsafe.Pointer(&ps)))
	defer endPaint.Call(w.hwnd, uintptr(unsafe.Pointer(&ps)))
	if w.page == pageSettings && w.qrCode.Size > 0 && w.qrRect.Right > w.qrRect.Left {
		w.paintQRCode(hdc)
	}
	if w.page == pageOCR || w.page == pageEditor || w.fullscreen {
		var f nativeplayer.Frame
		if w.page == pageEditor {
			w.editorFrame.mu.Lock()
			f = w.editorFrame.frame
			w.editorFrame.mu.Unlock()
			f = editorPreviewFrame(f, w.editorModel.regions)
		} else {
			w.ocrFrame.mu.Lock()
			f = w.ocrFrame.frame
			w.ocrFrame.mu.Unlock()
		}
		if len(f.BGRA) > 0 {
			drawBGRA(hdc, w.previewRect, f.Width, f.Height, f.BGRA)
		}
		w.paintRegion(hdc)
	}
}
func (w *window) paintQRCode(hdc uintptr) {
	if w.qrCode.Size <= 0 {
		return
	}
	white, _, _ := createSolidBrush.Call(0x00FFFFFF)
	black, _, _ := createSolidBrush.Call(0x00000000)
	defer deleteObject.Call(white)
	defer deleteObject.Call(black)
	fillRect.Call(hdc, uintptr(unsafe.Pointer(&w.qrRect)), white)
	quiet := 4
	total := w.qrCode.Size + quiet*2
	width := int(w.qrRect.Right - w.qrRect.Left)
	height := int(w.qrRect.Bottom - w.qrRect.Top)
	module := minInt(width, height) / total
	if module < 1 {
		return
	}
	drawW := module * total
	ox := int(w.qrRect.Left) + (width-drawW)/2 + quiet*module
	oy := int(w.qrRect.Top) + (height-drawW)/2 + quiet*module
	for y := 0; y < w.qrCode.Size; y++ {
		for x := 0; x < w.qrCode.Size; x++ {
			if !w.qrCode.At(x, y) {
				continue
			}
			r := rect{Left: int32(ox + x*module), Top: int32(oy + y*module), Right: int32(ox + (x+1)*module), Bottom: int32(oy + (y+1)*module)}
			fillRect.Call(hdc, uintptr(unsafe.Pointer(&r)), black)
		}
	}
}

func (w *window) paintRegion(hdc uintptr) {
	if w.previewRect.Right <= w.previewRect.Left {
		return
	}
	if w.page == pageEditor && len(w.editorModel.regions) > 0 {
		brush, _, _ := createSolidBrush.Call(0x0000A5FF)
		defer deleteObject.Call(brush)
		for _, r := range w.editorModel.regions {
			rr := w.regionRect(r.X, r.Y, r.W, r.H)
			fillFrameRect(hdc, rr, brush)
		}
		return
	}
	if w.page == pageEditor && !w.dragging {
		return
	}
	var x, y, ww, hh float64
	if w.page == pageEditor {
		x = parsePct(w.editorX, 5) / 100
		y = parsePct(w.editorY, 70) / 100
		ww = parsePct(w.editorW, 90) / 100
		hh = parsePct(w.editorH, 20) / 100
	} else {
		r := w.ocrRegion()
		x, y, ww, hh = r.X, r.Y, r.W, r.H
	}
	rr := w.regionRect(x, y, ww, hh)
	brush, _, _ := createSolidBrush.Call(0x0000A5FF)
	fillFrameRect(hdc, rr, brush)
	deleteObject.Call(brush)
}
func (w *window) regionRect(x, y, ww, hh float64) rect {
	return rect{Left: w.previewRect.Left + int32(x*float64(w.previewRect.Right-w.previewRect.Left)), Top: w.previewRect.Top + int32(y*float64(w.previewRect.Bottom-w.previewRect.Top)), Right: w.previewRect.Left + int32((x+ww)*float64(w.previewRect.Right-w.previewRect.Left)), Bottom: w.previewRect.Top + int32((y+hh)*float64(w.previewRect.Bottom-w.previewRect.Top))}
}
func fillFrameRect(hdc uintptr, r rect, brush uintptr) { // 2px border using FillRect
	a := rect{r.Left, r.Top, r.Right, r.Top + 2}
	b := rect{r.Left, r.Bottom - 2, r.Right, r.Bottom}
	c := rect{r.Left, r.Top, r.Left + 2, r.Bottom}
	d := rect{r.Right - 2, r.Top, r.Right, r.Bottom}
	for _, q := range []rect{a, b, c, d} {
		fillRect.Call(hdc, uintptr(unsafe.Pointer(&q)), brush)
	}
}
func pointFromLP(lp uintptr) point {
	return point{X: int32(int16(lp & 0xffff)), Y: int32(int16((lp >> 16) & 0xffff))}
}
func (w *window) inPreview(p point) bool {
	return p.X >= w.previewRect.Left && p.X <= w.previewRect.Right && p.Y >= w.previewRect.Top && p.Y <= w.previewRect.Bottom
}
func (w *window) mouseDown(lp uintptr) {
	if w.page != pageOCR && w.page != pageEditor {
		return
	}
	p := pointFromLP(lp)
	if !w.inPreview(p) {
		return
	}
	w.dragging = true
	w.dragStart = p
}
func (w *window) mouseMove(lp uintptr) {
	if !w.dragging {
		return
	}
	w.applyDrag(w.dragStart, pointFromLP(lp))
	invalidate(w.hwnd)
}
func (w *window) mouseUp(lp uintptr) {
	if !w.dragging {
		return
	}
	w.dragging = false
	end := pointFromLP(lp)
	w.applyDrag(w.dragStart, end)
	if w.page == pageEditor && (absInt32(end.X-w.dragStart.X) >= 4 || absInt32(end.Y-w.dragStart.Y) >= 4) {
		r := w.editorRegionFromControls()
		r.Effect, r.Strength, r.Whole, r.Start, r.End = "blur", 18, true, 0, w.editorDuration
		w.editorModel.add(r)
		w.syncEditorControls()
	}
	invalidate(w.hwnd)
}
func absInt32(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}
func (w *window) applyDrag(a, b point) {
	pw := float64(w.previewRect.Right - w.previewRect.Left)
	ph := float64(w.previewRect.Bottom - w.previewRect.Top)
	if pw <= 0 || ph <= 0 {
		return
	}
	x1 := math.Max(0, math.Min(1, float64(a.X-w.previewRect.Left)/pw))
	x2 := math.Max(0, math.Min(1, float64(b.X-w.previewRect.Left)/pw))
	y1 := math.Max(0, math.Min(1, float64(a.Y-w.previewRect.Top)/ph))
	y2 := math.Max(0, math.Min(1, float64(b.Y-w.previewRect.Top)/ph))
	x := math.Min(x1, x2)
	y := math.Min(y1, y2)
	ww := math.Max(.002, math.Abs(x2-x1))
	hh := math.Max(.002, math.Abs(y2-y1))
	if w.page == pageOCR {
		setText(w.ocrLeft, fmt.Sprintf("%.1f", x*100))
		setText(w.ocrRight, fmt.Sprintf("%.1f", (x+ww)*100))
		setText(w.ocrTop, fmt.Sprintf("%.1f", y*100))
		setText(w.ocrBottom, fmt.Sprintf("%.1f", (y+hh)*100))
	} else {
		setText(w.editorX, fmt.Sprintf("%.1f", x*100))
		setText(w.editorY, fmt.Sprintf("%.1f", y*100))
		setText(w.editorW, fmt.Sprintf("%.1f", ww*100))
		setText(w.editorH, fmt.Sprintf("%.1f", hh*100))
	}
}
func (w *window) toggleFullscreen() {
	if w.hwnd == 0 || ((w.page != pageOCR && w.page != pageEditor) && !w.fullscreen) {
		return
	}
	if !w.fullscreen {
		w.normalRect = windowRect(w.hwnd)
		w.normalStyle = windowStyle(w.hwnd)
		w.fullscreen = true
		setWindowStyle(w.hwnd, wsPopup|wsVisible)
		m := monitorRectForWindow(w.hwnd)
		setWindowPos.Call(w.hwnd, 0, uintptr(m.Left), uintptr(m.Top), uintptr(m.Right-m.Left), uintptr(m.Bottom-m.Top), swpNoZOrder|swpFrameChanged)
	} else {
		w.fullscreen = false
		style := w.normalStyle
		if style == 0 {
			style = wsOverlappedWindow | wsVisible
		}
		setWindowStyle(w.hwnd, style)
		r := w.normalRect
		if r.Right <= r.Left || r.Bottom <= r.Top {
			r = rect{Left: 100, Top: 70, Right: 1500, Bottom: 950}
		}
		setWindowPos.Call(w.hwnd, 0, uintptr(r.Left), uintptr(r.Top), uintptr(r.Right-r.Left), uintptr(r.Bottom-r.Top), swpNoZOrder|swpFrameChanged)
		for _, h := range w.nav {
			show(h, true)
		}
		show(w.version, true)
		show(w.navHelp, true)
		show(w.status, true)
		w.selectPage(w.page)
	}
	w.layout()
	invalidate(w.hwnd)
}
