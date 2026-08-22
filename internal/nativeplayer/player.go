package nativeplayer

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os/exec"
	"strconv"
	"sync"
	"time"

	"bilisubstudio/internal/proc"
)

type Frame struct {
	Width  int
	Height int
	Time   float64
	BGRA   []byte
}

type Media struct {
	Path     string
	Width    int
	Height   int
	Duration float64
}

type Player struct {
	mu         sync.Mutex
	ffmpeg     string
	media      Media
	frameW     int
	frameH     int
	position   float64
	playing    bool
	muted      bool
	startedAt  time.Time
	startPos   float64
	cancel     context.CancelFunc
	generation uint64
	onFrame    func(Frame)
	onState    func()
	audio      audioSink
}

func New(ffmpeg string) *Player { return &Player{ffmpeg: ffmpeg} }

func (p *Player) SetFrameCallback(fn func(Frame)) { p.mu.Lock(); p.onFrame = fn; p.mu.Unlock() }
func (p *Player) SetStateCallback(fn func())      { p.mu.Lock(); p.onState = fn; p.mu.Unlock() }

func (p *Player) Open(media Media) error {
	if media.Path == "" || media.Width <= 0 || media.Height <= 0 || media.Duration <= 0 {
		return errors.New("media preview không hợp lệ")
	}
	p.ClosePlayback()
	w, h := targetDimensions(media.Width, media.Height, 960, 540)
	p.mu.Lock()
	p.media = media
	p.frameW, p.frameH = w, h
	p.position = 0
	p.mu.Unlock()
	return p.RenderAt(0)
}

func (p *Player) Media() Media { p.mu.Lock(); defer p.mu.Unlock(); return p.media }

func (p *Player) Play() error {
	p.mu.Lock()
	if p.playing {
		p.mu.Unlock()
		return nil
	}
	if p.media.Path == "" {
		p.mu.Unlock()
		return errors.New("chưa mở video")
	}
	start := p.position
	if start >= p.media.Duration-.05 {
		start = 0
		p.position = 0
	}
	ctx, cancel := context.WithCancel(context.Background())
	p.cancel = cancel
	p.playing = true
	p.startPos = start
	p.startedAt = time.Now()
	p.generation++
	gen := p.generation
	media, w, h, muted := p.media, p.frameW, p.frameH, p.muted
	p.mu.Unlock()
	p.notifyState()
	go p.runVideo(ctx, gen, media, w, h, start)
	go p.runAudio(ctx, gen, media, start, muted)
	return nil
}

func (p *Player) Pause() {
	p.mu.Lock()
	if p.playing {
		p.position = p.currentPositionLocked()
	}
	p.playing = false
	p.generation++
	cancel := p.cancel
	p.cancel = nil
	audio := p.audio
	p.audio = nil
	p.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	if audio != nil {
		audio.Close()
	}
	p.notifyState()
}

func (p *Player) ClosePlayback() { p.Pause() }
func (p *Player) Close()         { p.Pause(); p.mu.Lock(); p.media = Media{}; p.position = 0; p.mu.Unlock() }

func (p *Player) Seek(seconds float64) error {
	p.mu.Lock()
	media := p.media
	wasPlaying := p.playing
	p.mu.Unlock()
	if media.Path == "" {
		return errors.New("chưa mở video")
	}
	if seconds < 0 {
		seconds = 0
	}
	if seconds > media.Duration {
		seconds = media.Duration
	}
	p.Pause()
	p.mu.Lock()
	p.position = seconds
	p.mu.Unlock()
	if err := p.RenderAt(seconds); err != nil {
		return err
	}
	if wasPlaying {
		return p.Play()
	}
	p.notifyState()
	return nil
}

func (p *Player) RenderAt(seconds float64) error {
	p.mu.Lock()
	media, w, h, gen := p.media, p.frameW, p.frameH, p.generation
	p.mu.Unlock()
	if media.Path == "" {
		return errors.New("chưa mở video")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	go func() {
		defer cancel()
		args := []string{"-hide_banner", "-loglevel", "error", "-ss", fmt.Sprintf("%.3f", seconds), "-i", media.Path, "-frames:v", "1", "-an", "-vf", fmt.Sprintf("scale=%d:%d", w, h), "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"}
		cmd := proc.Hide(exec.CommandContext(ctx, p.ffmpeg, args...))
		out, err := cmd.Output()
		if err != nil || len(out) < w*h*4 {
			return
		}
		frame := append([]byte(nil), out[:w*h*4]...)
		p.mu.Lock()
		if gen != p.generation {
			p.mu.Unlock()
			return
		}
		cb := p.onFrame
		p.mu.Unlock()
		if cb != nil {
			cb(Frame{Width: w, Height: h, Time: seconds, BGRA: frame})
		}
	}()
	return nil
}

func (p *Player) SetMuted(muted bool) {
	p.mu.Lock()
	p.muted = muted
	a := p.audio
	p.mu.Unlock()
	if a != nil {
		a.SetMuted(muted)
	}
	p.notifyState()
}

func (p *Player) Muted() bool       { p.mu.Lock(); defer p.mu.Unlock(); return p.muted }
func (p *Player) Playing() bool     { p.mu.Lock(); defer p.mu.Unlock(); return p.playing }
func (p *Player) Duration() float64 { p.mu.Lock(); defer p.mu.Unlock(); return p.media.Duration }
func (p *Player) Position() float64 {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.currentPositionLocked()
}

func (p *Player) currentPositionLocked() float64 {
	pos := p.position
	if p.playing {
		pos = p.startPos + time.Since(p.startedAt).Seconds()
		if p.media.Duration > 0 && pos > p.media.Duration {
			pos = p.media.Duration
		}
	}
	return pos
}

func (p *Player) runVideo(ctx context.Context, gen uint64, media Media, w, h int, start float64) {
	fps := 24
	args := []string{"-hide_banner", "-loglevel", "error", "-re", "-ss", fmt.Sprintf("%.3f", start), "-i", media.Path, "-an", "-vf", "fps=" + strconv.Itoa(fps) + ",scale=" + strconv.Itoa(w) + ":" + strconv.Itoa(h), "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"}
	cmd := proc.Hide(exec.CommandContext(ctx, p.ffmpeg, args...))
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		p.finishGeneration(gen)
		return
	}
	if err := cmd.Start(); err != nil {
		p.finishGeneration(gen)
		return
	}
	frameSize := w * h * 4
	buf := make([]byte, frameSize)
	index := 0
	for {
		if _, err := io.ReadFull(stdout, buf); err != nil {
			break
		}
		frame := append([]byte(nil), buf...)
		at := start + float64(index)/float64(fps)
		index++
		p.mu.Lock()
		if gen != p.generation {
			p.mu.Unlock()
			break
		}
		cb := p.onFrame
		p.position = at
		p.mu.Unlock()
		if cb != nil {
			cb(Frame{Width: w, Height: h, Time: at, BGRA: frame})
		}
	}
	_ = cmd.Wait()
	p.finishGeneration(gen)
}

func (p *Player) runAudio(ctx context.Context, gen uint64, media Media, start float64, muted bool) {
	args := []string{"-hide_banner", "-loglevel", "error", "-re", "-ss", fmt.Sprintf("%.3f", start), "-i", media.Path, "-vn", "-ac", "2", "-ar", "48000", "-f", "s16le", "pipe:1"}
	cmd := proc.Hide(exec.CommandContext(ctx, p.ffmpeg, args...))
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return
	}
	if err := cmd.Start(); err != nil {
		return
	}
	sink, err := newAudioSink(48000, 2)
	if err != nil {
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
		return
	}
	sink.SetMuted(muted)
	p.mu.Lock()
	if gen != p.generation {
		p.mu.Unlock()
		sink.Close()
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
		return
	}
	p.audio = sink
	p.mu.Unlock()
	buf := make([]byte, 48_000)
	for {
		n, readErr := stdout.Read(buf)
		if n > 0 {
			if err := sink.Write(buf[:n]); err != nil {
				break
			}
		}
		if readErr != nil {
			break
		}
		select {
		case <-ctx.Done():
			break
		default:
		}
	}
	sink.Close()
	p.mu.Lock()
	if p.audio == sink {
		p.audio = nil
	}
	p.mu.Unlock()
	_ = cmd.Wait()
}

func (p *Player) finishGeneration(gen uint64) {
	p.mu.Lock()
	if gen != p.generation {
		p.mu.Unlock()
		return
	}
	p.position = p.currentPositionLocked()
	p.playing = false
	p.cancel = nil
	p.mu.Unlock()
	p.notifyState()
}

func (p *Player) notifyState() {
	p.mu.Lock()
	cb := p.onState
	p.mu.Unlock()
	if cb != nil {
		cb()
	}
}

func targetDimensions(w, h, maxW, maxH int) (int, int) {
	if w <= 0 || h <= 0 {
		return 2, 2
	}
	scale := 1.0
	if w > maxW {
		scale = float64(maxW) / float64(w)
	}
	if float64(h)*scale > float64(maxH) {
		scale = float64(maxH) / float64(h)
	}
	outW := int(float64(w)*scale + .5)
	outH := int(float64(h)*scale + .5)
	if outW < 2 {
		outW = 2
	}
	if outH < 2 {
		outH = 2
	}
	if outW%2 != 0 {
		outW--
	}
	if outH%2 != 0 {
		outH--
	}
	return outW, outH
}
