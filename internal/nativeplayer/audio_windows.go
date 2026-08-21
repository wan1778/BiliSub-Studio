//go:build windows

package nativeplayer

import (
	"errors"
	"runtime"
	"sync"
	"syscall"
	"time"
	"unsafe"
)

const (
	waveMapper    = 0xFFFFFFFF
	waveFormatPCM = 1
	callbackNull  = 0
	whdrDone      = 0x00000001
)

type waveFormatEx struct {
	FormatTag      uint16
	Channels       uint16
	SamplesPerSec  uint32
	AvgBytesPerSec uint32
	BlockAlign     uint16
	BitsPerSample  uint16
	Size           uint16
}

type waveHdr struct {
	Data          *byte
	BufferLength  uint32
	BytesRecorded uint32
	User          uintptr
	Flags         uint32
	Loops         uint32
	Next          uintptr
	Reserved      uintptr
}

type waveBuffer struct {
	data []byte
	hdr  waveHdr
}

type winAudioSink struct {
	mu      sync.Mutex
	handle  uintptr
	pending []*waveBuffer
	closed  bool
}

var (
	winmm                  = syscall.NewLazyDLL("winmm.dll")
	waveOutOpen            = winmm.NewProc("waveOutOpen")
	waveOutPrepareHeader   = winmm.NewProc("waveOutPrepareHeader")
	waveOutWrite           = winmm.NewProc("waveOutWrite")
	waveOutUnprepareHeader = winmm.NewProc("waveOutUnprepareHeader")
	waveOutReset           = winmm.NewProc("waveOutReset")
	waveOutClose           = winmm.NewProc("waveOutClose")
	waveOutSetVolume       = winmm.NewProc("waveOutSetVolume")
)

type audioSink interface {
	Write([]byte) error
	SetMuted(bool)
	Close()
}

func newAudioSink(sampleRate, channels int) (audioSink, error) {
	format := waveFormatEx{FormatTag: waveFormatPCM, Channels: uint16(channels), SamplesPerSec: uint32(sampleRate), BitsPerSample: 16}
	format.BlockAlign = format.Channels * format.BitsPerSample / 8
	format.AvgBytesPerSec = format.SamplesPerSec * uint32(format.BlockAlign)
	var handle uintptr
	ret, _, _ := waveOutOpen.Call(uintptr(unsafe.Pointer(&handle)), waveMapper, uintptr(unsafe.Pointer(&format)), 0, 0, callbackNull)
	if ret != 0 || handle == 0 {
		return nil, errors.New("không mở được Windows audio output")
	}
	return &winAudioSink{handle: handle}, nil
}

func (s *winAudioSink) Write(p []byte) error {
	if len(p) == 0 {
		return nil
	}
	data := append([]byte(nil), p...)
	b := &waveBuffer{data: data}
	b.hdr.Data = &b.data[0]
	b.hdr.BufferLength = uint32(len(b.data))
	s.mu.Lock()
	if s.closed {
		s.mu.Unlock()
		return errors.New("audio output đã đóng")
	}
	if len(s.pending) >= 4 {
		first := s.pending[0]
		s.mu.Unlock()
		if err := s.waitDone(first); err != nil {
			return err
		}
		s.mu.Lock()
		if len(s.pending) > 0 && s.pending[0] == first {
			s.pending = s.pending[1:]
		}
		s.mu.Unlock()
		s.unprepare(first)
		s.mu.Lock()
	}
	ret, _, _ := waveOutPrepareHeader.Call(s.handle, uintptr(unsafe.Pointer(&b.hdr)), unsafe.Sizeof(b.hdr))
	if ret != 0 {
		s.mu.Unlock()
		return errors.New("waveOutPrepareHeader thất bại")
	}
	ret, _, _ = waveOutWrite.Call(s.handle, uintptr(unsafe.Pointer(&b.hdr)), unsafe.Sizeof(b.hdr))
	if ret != 0 {
		s.mu.Unlock()
		s.unprepare(b)
		return errors.New("waveOutWrite thất bại")
	}
	s.pending = append(s.pending, b)
	s.mu.Unlock()
	return nil
}

func (s *winAudioSink) waitDone(b *waveBuffer) error {
	for i := 0; i < 200; i++ {
		if b.hdr.Flags&whdrDone != 0 {
			return nil
		}
		s.mu.Lock()
		closed := s.closed
		s.mu.Unlock()
		if closed {
			return nil
		}
		time.Sleep(5 * time.Millisecond)
	}
	return errors.New("Windows audio buffer timeout")
}
func (s *winAudioSink) unprepare(b *waveBuffer) {
	if b == nil {
		return
	}
	_, _, _ = waveOutUnprepareHeader.Call(s.handle, uintptr(unsafe.Pointer(&b.hdr)), unsafe.Sizeof(b.hdr))
	runtime.KeepAlive(b.data)
}
func (s *winAudioSink) SetMuted(m bool) {
	s.mu.Lock()
	h := s.handle
	closed := s.closed
	s.mu.Unlock()
	if closed || h == 0 {
		return
	}
	vol := uintptr(0xFFFFFFFF)
	if m {
		vol = 0
	}
	_, _, _ = waveOutSetVolume.Call(h, vol)
}
func (s *winAudioSink) Close() {
	s.mu.Lock()
	if s.closed {
		s.mu.Unlock()
		return
	}
	s.closed = true
	h := s.handle
	pending := append([]*waveBuffer(nil), s.pending...)
	s.pending = nil
	s.mu.Unlock()
	_, _, _ = waveOutReset.Call(h)
	for _, b := range pending {
		s.unprepare(b)
	}
	_, _, _ = waveOutClose.Call(h)
}
