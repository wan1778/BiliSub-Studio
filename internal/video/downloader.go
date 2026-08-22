package video

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"bilisubstudio/internal/jobs"
)

const DefaultChunkSize int64 = 32 << 20 // 32 MiB

const resumeManifestVersion = 1

type RefreshFunc func(context.Context, StreamKind, uint64) (*Stream, error)

type DownloadOptions struct {
	ChunkSize   int64
	Concurrency int
	MaxAttempts int
	Client      *http.Client
	Refresh     RefreshFunc
}

type Segment struct {
	Index      int
	Start, End int64
}

func (s Segment) Size() int64 { return s.End - s.Start + 1 }

type DownloadResult struct {
	Path string
	Size int64
}

// DownloadStream downloads one resolved Bilibili stream. Completed Range segments
// are durable resume checkpoints in a shared preallocated work file. A failed
// or incomplete segment is never committed to the resume manifest.
func DownloadStream(ctx context.Context, job *jobs.Job, stream *Stream, dir, base string, opts DownloadOptions) (DownloadResult, error) {
	if stream == nil || strings.TrimSpace(stream.URL) == "" {
		return DownloadResult{}, errors.New("stream URL rỗng")
	}
	if opts.ChunkSize <= 0 {
		opts.ChunkSize = DefaultChunkSize
	}
	if opts.Concurrency < 1 {
		opts.Concurrency = 1
	}
	if opts.MaxAttempts < 1 {
		opts.MaxAttempts = 16
	}
	if opts.Client == nil {
		opts.Client = defaultHTTPClient()
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return DownloadResult{}, err
	}

	// Probe every fresh job even when yt-dlp supplied filesize_approx. The CDN's
	// Content-Range is authoritative and also proves Range support.
	total, rangeOK, err := probe(ctx, opts.Client, stream)
	if err != nil {
		return DownloadResult{}, fmt.Errorf("probe %s: %w", stream.Kind, err)
	}
	out := filepath.Join(dir, base+".stream")
	if !rangeOK {
		job.Logf("%s CDN không hỗ trợ Range; chuyển sang single-stream", stream.Kind)
		return sequentialWithRetry(ctx, job, stream, out, opts)
	}

	// A completed stream may survive a later ffmpeg failure. Reuse it rather
	// than downloading the same resolved format again.
	if st, statErr := os.Stat(out); statErr == nil {
		if st.Size() == total {
			job.Logf("%s stream hoàn chỉnh đã có, dùng lại %d bytes", stream.Kind, total)
			return DownloadResult{Path: out, Size: total}, nil
		}
		_ = os.Remove(out)
	}

	segs := segments(total, opts.ChunkSize)
	workPath := out + ".partial"
	resumePath := out + ".resume.json"
	workFile, resume, err := openResumeWork(workPath, resumePath, total, opts.ChunkSize, len(segs))
	if err != nil {
		return DownloadResult{}, err
	}
	defer func() {
		if workFile != nil {
			_ = workFile.Close()
		}
	}()

	ctx, cancel := context.WithCancel(ctx)
	defer cancel()

	var current atomic.Pointer[Stream]
	current.Store(stream)
	var refreshMu sync.Mutex
	refresh := func(seen uint64) (*Stream, error) {
		refreshMu.Lock()
		defer refreshMu.Unlock()
		now := current.Load()
		if now != nil && now.Generation != seen {
			return now, nil
		}
		if opts.Refresh == nil {
			return now, errors.New("không có cơ chế làm mới CDN URL")
		}
		ns, err := opts.Refresh(ctx, stream.Kind, seen)
		if err != nil {
			return nil, err
		}
		if ns == nil || strings.TrimSpace(ns.URL) == "" {
			return nil, errors.New("URL mới rỗng")
		}
		current.Store(ns)
		return ns, nil
	}

	var committed atomic.Int64
	for _, seg := range segs {
		if resume.IsComplete(seg.Index) {
			committed.Add(seg.Size())
		}
	}
	pending := make(chan Segment)
	errCh := make(chan error, 1)
	var wg sync.WaitGroup

	worker := func(workerID int) {
		defer wg.Done()
		for seg := range pending {
			if ctx.Err() != nil {
				return
			}
			if resume.IsComplete(seg.Index) {
				continue
			}

			var lastErr error
			for attempt := 1; attempt <= opts.MaxAttempts; attempt++ {
				if ctx.Err() != nil {
					return
				}
				st := current.Load()
				if st == nil {
					lastErr = errors.New("stream URL rỗng")
					break
				}
				lastErr = downloadSegment(ctx, opts.Client, st, seg, total, workFile)
				if lastErr == nil {
					// Data becomes a durable resume checkpoint only after the shared
					// work file is synced and the completion manifest is atomically
					// replaced. Partial bytes from cancelled/failed attempts remain
					// uncommitted and are overwritten from seg.Start on retry.
					if commitErr := resume.Commit(workFile, seg.Index); commitErr != nil {
						lastErr = fmt.Errorf("checkpoint chunk %d: %w", seg.Index, commitErr)
						break
					}
					committed.Add(seg.Size())
					job.Logf("%s segment %d/%d xong", stream.Kind, seg.Index+1, len(segs))
					break
				}
				cdnCut := isCDNBodyFailure(lastErr)
				if cdnCut {
					job.Logf("%s segment %d CDN cắt sớm (%d/%d), đang thử lại: %v", stream.Kind, seg.Index+1, attempt, opts.MaxAttempts, lastErr)
				} else {
					job.Logf("%s segment %d lỗi (%d/%d): %v", stream.Kind, seg.Index+1, attempt, opts.MaxAttempts, lastErr)
				}

				// Stage A intentionally preserves beta.5 URL refresh behavior. CDN
				// candidate ranking/rotation belongs to the separately-tested Stage B.
				if attempt < opts.MaxAttempts && (cdnCut || attempt%2 == 0) {
					ns, refreshErr := refresh(st.Generation)
					if refreshErr != nil {
						job.Logf("Làm mới URL %s lỗi: %v", stream.Kind, refreshErr)
					} else if ns != nil {
						job.Logf("Đã làm mới URL %s (generation %d)", stream.Kind, ns.Generation)
					}
				}
				if !sleepContext(ctx, time.Duration(minInt(attempt, 5))*300*time.Millisecond) {
					return
				}
			}
			if lastErr != nil {
				select {
				case errCh <- fmt.Errorf("%s segment %d thất bại: %w", stream.Kind, seg.Index, lastErr):
					cancel()
				default:
				}
				return
			}
		}
	}

	wg.Add(opts.Concurrency)
	for i := 0; i < opts.Concurrency; i++ {
		go worker(i)
	}
	go func() {
		defer close(pending)
		for _, seg := range segs {
			select {
			case <-ctx.Done():
				return
			case pending <- seg:
			}
		}
	}()

	done := make(chan struct{})
	go func() {
		wg.Wait()
		close(done)
	}()
	ticker := time.NewTicker(500 * time.Millisecond)
	defer ticker.Stop()

	for {
		select {
		case err := <-errCh:
			if err != nil {
				cancel()
				<-done
				return DownloadResult{}, err
			}
		case <-ctx.Done():
			<-done
			if cause := context.Cause(ctx); cause != nil {
				return DownloadResult{}, cause
			}
			return DownloadResult{}, ctx.Err()
		case <-ticker.C:
			job.Set("downloading", -1, fmt.Sprintf("%s %.1f%%", stream.Kind, 100*float64(committed.Load())/float64(total)))
		case <-done:
			select {
			case err := <-errCh:
				if err != nil {
					return DownloadResult{}, err
				}
			default:
			}
			if !resume.AllComplete(len(segs)) {
				return DownloadResult{}, errors.New("resume manifest thiếu segment hoàn tất")
			}
			if err := workFile.Sync(); err != nil {
				return DownloadResult{}, err
			}
			if err := workFile.Close(); err != nil {
				return DownloadResult{}, err
			}
			workFile = nil
			st, err := os.Stat(workPath)
			if err != nil {
				return DownloadResult{}, err
			}
			if st.Size() != total {
				return DownloadResult{}, fmt.Errorf("sai kích thước file work: %d/%d", st.Size(), total)
			}
			if err := os.Rename(workPath, out); err != nil {
				return DownloadResult{}, err
			}
			_ = os.Remove(resumePath)
			return DownloadResult{Path: out, Size: total}, nil
		}
	}
}

func defaultHTTPClient() *http.Client {
	return &http.Client{
		Timeout: 120 * time.Second,
		Transport: &http.Transport{
			MaxIdleConns:        32,
			MaxIdleConnsPerHost: 16,
			IdleConnTimeout:     90 * time.Second,
		},
	}
}

func segments(total, chunk int64) []Segment {
	var out []Segment
	for start := int64(0); start < total; start += chunk {
		end := start + chunk - 1
		if end >= total {
			end = total - 1
		}
		out = append(out, Segment{Index: len(out), Start: start, End: end})
	}
	return out
}

type resumeManifest struct {
	Version   int   `json:"version"`
	Total     int64 `json:"total"`
	ChunkSize int64 `json:"chunk_size"`
	Completed []int `json:"completed"`
}

type resumeState struct {
	mu        sync.Mutex
	path      string
	total     int64
	chunkSize int64
	completed map[int]bool
}

func openResumeWork(workPath, resumePath string, total, chunkSize int64, segmentCount int) (*os.File, *resumeState, error) {
	state, valid := loadResumeState(resumePath, total, chunkSize, segmentCount)
	f, err := os.OpenFile(workPath, os.O_CREATE|os.O_RDWR, 0o644)
	if err != nil {
		return nil, nil, err
	}
	st, statErr := f.Stat()
	if statErr != nil {
		_ = f.Close()
		return nil, nil, statErr
	}
	if st.Size() != total {
		valid = false
	}
	if !valid {
		state = &resumeState{path: resumePath, total: total, chunkSize: chunkSize, completed: map[int]bool{}}
		if err := f.Truncate(total); err != nil {
			_ = f.Close()
			return nil, nil, err
		}
		if err := state.saveLocked(); err != nil {
			_ = f.Close()
			return nil, nil, err
		}
	}
	return f, state, nil
}

func loadResumeState(path string, total, chunkSize int64, segmentCount int) (*resumeState, bool) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, false
	}
	var m resumeManifest
	if json.Unmarshal(b, &m) != nil || m.Version != resumeManifestVersion || m.Total != total || m.ChunkSize != chunkSize {
		return nil, false
	}
	completed := make(map[int]bool, len(m.Completed))
	for _, idx := range m.Completed {
		if idx < 0 || idx >= segmentCount {
			return nil, false
		}
		completed[idx] = true
	}
	return &resumeState{path: path, total: total, chunkSize: chunkSize, completed: completed}, true
}

func (r *resumeState) IsComplete(index int) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.completed[index]
}

func (r *resumeState) AllComplete(segmentCount int) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.completed) == segmentCount
}

func (r *resumeState) Commit(f *os.File, index int) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.completed[index] {
		return nil
	}
	// Durability order is data first, manifest second. A crash before the
	// manifest rename merely causes this range to be downloaded again.
	if err := f.Sync(); err != nil {
		return err
	}
	r.completed[index] = true
	if err := r.saveLocked(); err != nil {
		delete(r.completed, index)
		return err
	}
	return nil
}

func (r *resumeState) saveLocked() error {
	completed := make([]int, 0, len(r.completed))
	for idx := range r.completed {
		completed = append(completed, idx)
	}
	sort.Ints(completed)
	m := resumeManifest{Version: resumeManifestVersion, Total: r.total, ChunkSize: r.chunkSize, Completed: completed}
	b, err := json.Marshal(m)
	if err != nil {
		return err
	}
	tmp := r.path + ".tmp"
	f, err := os.OpenFile(tmp, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644)
	if err != nil {
		return err
	}
	if _, err = f.Write(b); err == nil {
		err = f.Sync()
	}
	if closeErr := f.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		_ = os.Remove(tmp)
		return err
	}
	if err := os.Rename(tmp, r.path); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	return nil
}

func probe(ctx context.Context, client *http.Client, stream *Stream) (int64, bool, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, stream.URL, nil)
	if err != nil {
		return 0, false, err
	}
	applyHeaders(req, stream.Headers)
	req.Header.Set("Range", "bytes=0-0")
	req.Header.Set("Accept-Encoding", "identity")
	resp, err := client.Do(req)
	if err != nil {
		return 0, false, err
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusPartialContent {
		start, end, total, err := parseContentRange(resp.Header.Get("Content-Range"))
		if err != nil {
			return 0, false, err
		}
		if start != 0 || end != 0 || total <= 0 {
			return 0, false, fmt.Errorf("probe Content-Range sai: %q", resp.Header.Get("Content-Range"))
		}
		return total, true, nil
	}
	if resp.StatusCode >= 200 && resp.StatusCode < 300 && resp.ContentLength > 0 {
		return resp.ContentLength, false, nil
	}
	return 0, false, fmt.Errorf("HTTP %d", resp.StatusCode)
}

func downloadSegment(ctx context.Context, client *http.Client, stream *Stream, seg Segment, expectedTotal int64, dst *os.File) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, stream.URL, nil)
	if err != nil {
		return err
	}
	applyHeaders(req, stream.Headers)
	req.Header.Set("Range", fmt.Sprintf("bytes=%d-%d", seg.Start, seg.End))
	req.Header.Set("Accept-Encoding", "identity")
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusPartialContent {
		return fmt.Errorf("HTTP %d, cần 206", resp.StatusCode)
	}
	start, end, total, err := parseContentRange(resp.Header.Get("Content-Range"))
	if err != nil {
		return err
	}
	if start != seg.Start || end != seg.End || (expectedTotal > 0 && total != expectedTotal) {
		return fmt.Errorf("Content-Range sai: %q", resp.Header.Get("Content-Range"))
	}
	if resp.ContentLength >= 0 && resp.ContentLength != seg.Size() {
		return fmt.Errorf("Content-Length sai: %d/%d", resp.ContentLength, seg.Size())
	}
	if badMediaContentType(resp.Header.Get("Content-Type")) {
		return fmt.Errorf("phản hồi không phải media: %s", resp.Header.Get("Content-Type"))
	}
	if dst == nil {
		return errors.New("file work nil")
	}

	// Workers own disjoint byte ranges in one preallocated file. OffsetWriter
	// uses WriteAt semantics, so a retry starts at seg.Start and overwrites any
	// uncommitted partial bytes left by the previous attempt.
	ow := io.NewOffsetWriter(dst, seg.Start)
	bw := bufio.NewWriterSize(ow, 256<<10)
	n, copyErr := io.CopyN(bw, resp.Body, seg.Size())
	flushErr := bw.Flush()
	if copyErr != nil {
		return fmt.Errorf("short body %d/%d: %w", n, seg.Size(), copyErr)
	}
	if flushErr != nil {
		return flushErr
	}
	if n != seg.Size() {
		return fmt.Errorf("short body %d/%d", n, seg.Size())
	}
	// When Content-Length is absent (chunked response), verify the server did
	// not send bytes beyond the requested inclusive Range.
	if resp.ContentLength < 0 {
		var extra [1]byte
		extraN, extraErr := resp.Body.Read(extra[:])
		if extraN > 0 {
			return fmt.Errorf("over body: nhận quá %d bytes", seg.Size())
		}
		if extraErr != nil && !errors.Is(extraErr, io.EOF) {
			return fmt.Errorf("kiểm tra cuối body: %w", extraErr)
		}
	}
	return nil
}

func parseContentRange(v string) (start, end, total int64, err error) {
	v = strings.TrimSpace(v)
	if !strings.HasPrefix(strings.ToLower(v), "bytes ") {
		return 0, 0, 0, fmt.Errorf("Content-Range thiếu/sai: %q", v)
	}
	body := strings.TrimSpace(v[len("bytes "):])
	parts := strings.Split(body, "/")
	if len(parts) != 2 || parts[1] == "*" {
		return 0, 0, 0, fmt.Errorf("Content-Range sai: %q", v)
	}
	rangeParts := strings.Split(parts[0], "-")
	if len(rangeParts) != 2 {
		return 0, 0, 0, fmt.Errorf("Content-Range sai: %q", v)
	}
	start, err = strconv.ParseInt(strings.TrimSpace(rangeParts[0]), 10, 64)
	if err != nil {
		return 0, 0, 0, fmt.Errorf("Content-Range start: %w", err)
	}
	end, err = strconv.ParseInt(strings.TrimSpace(rangeParts[1]), 10, 64)
	if err != nil {
		return 0, 0, 0, fmt.Errorf("Content-Range end: %w", err)
	}
	total, err = strconv.ParseInt(strings.TrimSpace(parts[1]), 10, 64)
	if err != nil {
		return 0, 0, 0, fmt.Errorf("Content-Range total: %w", err)
	}
	if start < 0 || end < start || total <= end {
		return 0, 0, 0, fmt.Errorf("Content-Range không hợp lệ: %q", v)
	}
	return start, end, total, nil
}

func sequentialWithRetry(ctx context.Context, job *jobs.Job, initial *Stream, path string, opts DownloadOptions) (DownloadResult, error) {
	current := initial
	var lastErr error
	for attempt := 1; attempt <= opts.MaxAttempts; attempt++ {
		if ctx.Err() != nil {
			return DownloadResult{}, ctx.Err()
		}
		result, err := sequentialOnce(ctx, current, path, opts.Client)
		if err == nil {
			job.Logf("%s single-stream hoàn tất: %d bytes", current.Kind, result.Size)
			return result, nil
		}
		lastErr = err
		_ = os.Remove(path)
		job.Logf("%s single-stream lỗi (%d/%d): %v", current.Kind, attempt, opts.MaxAttempts, err)
		if attempt%2 == 0 && attempt < opts.MaxAttempts && opts.Refresh != nil {
			ns, refreshErr := opts.Refresh(ctx, current.Kind, current.Generation)
			if refreshErr == nil && ns != nil {
				current = ns
				job.Logf("Đã làm mới URL %s (generation %d)", current.Kind, current.Generation)
			} else if refreshErr != nil {
				job.Logf("Làm mới URL %s lỗi: %v", current.Kind, refreshErr)
			}
		}
		if !sleepContext(ctx, time.Duration(minInt(attempt, 5))*300*time.Millisecond) {
			return DownloadResult{}, ctx.Err()
		}
	}
	return DownloadResult{}, fmt.Errorf("%s single-stream thất bại: %w", initial.Kind, lastErr)
}

func sequentialOnce(ctx context.Context, stream *Stream, path string, client *http.Client) (DownloadResult, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, stream.URL, nil)
	if err != nil {
		return DownloadResult{}, err
	}
	applyHeaders(req, stream.Headers)
	req.Header.Set("Accept-Encoding", "identity")
	resp, err := client.Do(req)
	if err != nil {
		return DownloadResult{}, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return DownloadResult{}, fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	if badMediaContentType(resp.Header.Get("Content-Type")) {
		return DownloadResult{}, fmt.Errorf("phản hồi không phải media: %s", resp.Header.Get("Content-Type"))
	}

	tmp := path + ".tmp"
	f, err := os.Create(tmp)
	if err != nil {
		return DownloadResult{}, err
	}
	n, copyErr := io.Copy(f, resp.Body)
	syncErr := f.Sync()
	closeErr := f.Close()
	if copyErr != nil {
		_ = os.Remove(tmp)
		return DownloadResult{}, copyErr
	}
	if syncErr != nil {
		_ = os.Remove(tmp)
		return DownloadResult{}, syncErr
	}
	if closeErr != nil {
		_ = os.Remove(tmp)
		return DownloadResult{}, closeErr
	}
	if resp.ContentLength >= 0 && n != resp.ContentLength {
		_ = os.Remove(tmp)
		return DownloadResult{}, fmt.Errorf("short body %d/%d", n, resp.ContentLength)
	}
	if err := os.Rename(tmp, path); err != nil {
		_ = os.Remove(tmp)
		return DownloadResult{}, err
	}
	return DownloadResult{Path: path, Size: n}, nil
}

func applyHeaders(req *http.Request, h map[string]string) {
	for k, v := range h {
		if strings.EqualFold(k, "Accept-Encoding") || strings.EqualFold(k, "Range") {
			continue
		}
		req.Header.Set(k, v)
	}
	if req.Header.Get("Referer") == "" {
		req.Header.Set("Referer", "https://www.bilibili.com/")
	}
	if req.Header.Get("User-Agent") == "" {
		req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36")
	}
}

func isCDNBodyFailure(err error) bool {
	if err == nil {
		return false
	}
	m := strings.ToLower(err.Error())
	return strings.Contains(m, "short body") ||
		strings.Contains(m, "unexpected eof") ||
		strings.Contains(m, "over body") ||
		strings.Contains(m, "content-range") ||
		strings.Contains(m, "content-length") ||
		strings.Contains(m, "http 403") || strings.Contains(m, "http 412") || strings.Contains(m, "http 416")
}

func badMediaContentType(v string) bool {
	v = strings.ToLower(strings.TrimSpace(strings.Split(v, ";")[0]))
	return strings.Contains(v, "json") || strings.HasPrefix(v, "text/") || strings.Contains(v, "html") || strings.Contains(v, "xml")
}

func sleepContext(ctx context.Context, d time.Duration) bool {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-t.C:
		return true
	}
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}
