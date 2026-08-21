package video

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"testing"

	"bilisubstudio/internal/jobs"
)

func TestDownloadStreamShortReadRetriesOnlyFailedSegment(t *testing.T) {
	data := []byte("abcdefghij")
	var mu sync.Mutex
	counts := map[string]int{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rg := r.Header.Get("Range")
		mu.Lock()
		counts[rg]++
		n := counts[rg]
		mu.Unlock()
		if rg == "bytes=0-0" {
			serveRange(w, data, 0, 0, false)
			return
		}
		start, end := mustRange(t, rg)
		if rg == "bytes=4-7" && n == 1 {
			serveRange(w, data, start, end, true)
			return
		}
		serveRange(w, data, start, end, false)
	}))
	defer srv.Close()

	dir := t.TempDir()
	job := jobs.New("t1")
	res, err := DownloadStream(context.Background(), job, &Stream{Kind: StreamVideo, URL: srv.URL, Generation: 1}, dir, "video", DownloadOptions{ChunkSize: 4, Concurrency: 1, MaxAttempts: 4})
	if err != nil {
		t.Fatalf("DownloadStream: %v", err)
	}
	got, _ := os.ReadFile(res.Path)
	if string(got) != string(data) {
		t.Fatalf("output=%q want=%q", got, data)
	}
	mu.Lock()
	defer mu.Unlock()
	if counts["bytes=0-3"] != 1 || counts["bytes=8-9"] != 1 {
		t.Fatalf("completed segments unexpectedly retried: %#v", counts)
	}
	if counts["bytes=4-7"] != 2 {
		t.Fatalf("short segment attempts=%d want=2, all=%#v", counts["bytes=4-7"], counts)
	}
}

func TestDownloadStreamUsesExistingCompleteSegments(t *testing.T) {
	data := []byte("abcdefghij")
	var mu sync.Mutex
	counts := map[string]int{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rg := r.Header.Get("Range")
		mu.Lock()
		counts[rg]++
		mu.Unlock()
		start, end := mustRange(t, rg)
		serveRange(w, data, start, end, false)
	}))
	defer srv.Close()

	dir := t.TempDir()
	workPath := filepath.Join(dir, "video.stream.partial")
	resumePath := filepath.Join(dir, "video.stream.resume.json")
	f, resume, err := openResumeWork(workPath, resumePath, int64(len(data)), 4, 3)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := f.WriteAt(data[:4], 0); err != nil {
		t.Fatal(err)
	}
	if err := resume.Commit(f, 0); err != nil {
		t.Fatal(err)
	}
	if err := f.Close(); err != nil {
		t.Fatal(err)
	}

	job := jobs.New("t2")
	_, err = DownloadStream(context.Background(), job, &Stream{Kind: StreamVideo, URL: srv.URL, Generation: 1}, dir, "video", DownloadOptions{ChunkSize: 4, Concurrency: 2, MaxAttempts: 3})
	if err != nil {
		t.Fatal(err)
	}
	mu.Lock()
	defer mu.Unlock()
	// bytes=0-0 is the mandatory probe. A bytes=0-3 request would mean the
	// resume checkpoint was ignored.
	if counts["bytes=0-3"] != 0 {
		t.Fatalf("resume segment was re-downloaded: %#v", counts)
	}
}

func TestDownloadStreamRefreshesURLByGeneration(t *testing.T) {
	data := []byte("abcdefgh")
	var mu sync.Mutex
	oldSegments := 0
	newSegments := 0
	old := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start, end := mustRange(t, r.Header.Get("Range"))
		if start == 0 && end == 0 {
			serveRange(w, data, start, end, false)
			return
		}
		mu.Lock()
		oldSegments++
		mu.Unlock()
		serveRange(w, data, start, end, true)
	}))
	defer old.Close()
	newServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start, end := mustRange(t, r.Header.Get("Range"))
		mu.Lock()
		newSegments++
		mu.Unlock()
		serveRange(w, data, start, end, false)
	}))
	defer newServer.Close()

	refreshCount := 0
	refresh := func(ctx context.Context, kind StreamKind, seen uint64) (*Stream, error) {
		refreshCount++
		if seen != 1 {
			t.Fatalf("seen generation=%d want=1", seen)
		}
		return &Stream{Kind: kind, URL: newServer.URL, Generation: 2}, nil
	}
	job := jobs.New("t3")
	_, err := DownloadStream(context.Background(), job, &Stream{Kind: StreamVideo, URL: old.URL, Generation: 1}, t.TempDir(), "video", DownloadOptions{ChunkSize: 4, Concurrency: 1, MaxAttempts: 5, Refresh: refresh})
	if err != nil {
		t.Fatal(err)
	}
	if refreshCount != 1 {
		t.Fatalf("refreshCount=%d want=1", refreshCount)
	}
	mu.Lock()
	defer mu.Unlock()
	if oldSegments != 1 {
		t.Fatalf("old segment attempts=%d want=1 after immediate short-read refresh", oldSegments)
	}
	if newSegments == 0 {
		t.Fatal("new URL was never used")
	}
}

func TestDownloadStreamRangeUnsupportedFallsBack(t *testing.T) {
	data := []byte("single-stream-data")
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/octet-stream")
		w.Header().Set("Content-Length", strconv.Itoa(len(data)))
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write(data)
	}))
	defer srv.Close()
	job := jobs.New("t4")
	res, err := DownloadStream(context.Background(), job, &Stream{Kind: StreamAudio, URL: srv.URL, Generation: 1}, t.TempDir(), "audio", DownloadOptions{MaxAttempts: 2})
	if err != nil {
		t.Fatal(err)
	}
	got, _ := os.ReadFile(res.Path)
	if string(got) != string(data) {
		t.Fatalf("got=%q", got)
	}
}

func TestParseContentRangeRejectsMismatch(t *testing.T) {
	cases := []string{"", "bytes 0-0/*", "bytes 3-2/10", "items 0-0/10", "bytes a-b/10", "bytes 0-10/10"}
	for _, c := range cases {
		if _, _, _, err := parseContentRange(c); err == nil {
			t.Fatalf("expected error for %q", c)
		}
	}
}

func serveRange(w http.ResponseWriter, data []byte, start, end int64, truncate bool) {
	w.Header().Set("Content-Type", "application/octet-stream")
	w.Header().Set("Content-Range", fmt.Sprintf("bytes %d-%d/%d", start, end, len(data)))
	want := end - start + 1
	w.Header().Set("Content-Length", strconv.FormatInt(want, 10))
	w.WriteHeader(http.StatusPartialContent)
	body := data[start : end+1]
	if truncate && len(body) > 1 {
		body = body[:len(body)-1]
	}
	_, _ = w.Write(body)
}

func mustRange(t *testing.T, value string) (int64, int64) {
	t.Helper()
	if !strings.HasPrefix(value, "bytes=") {
		t.Fatalf("bad Range %q", value)
	}
	parts := strings.Split(strings.TrimPrefix(value, "bytes="), "-")
	if len(parts) != 2 {
		t.Fatalf("bad Range %q", value)
	}
	a, err := strconv.ParseInt(parts[0], 10, 64)
	if err != nil {
		t.Fatal(err)
	}
	b, err := strconv.ParseInt(parts[1], 10, 64)
	if err != nil {
		t.Fatal(err)
	}
	return a, b
}

func TestDownloadSegmentRejectsOversizedChunkedBody(t *testing.T) {
	data := []byte("abcde")
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start, end := mustRange(t, r.Header.Get("Range"))
		w.Header().Set("Content-Type", "application/octet-stream")
		w.Header().Set("Content-Range", fmt.Sprintf("bytes %d-%d/%d", start, end, len(data)))
		w.WriteHeader(http.StatusPartialContent)
		// Flush headers before writing so net/http cannot synthesize a
		// Content-Length; this exercises the chunked-response over-body guard.
		if f, ok := w.(http.Flusher); ok {
			f.Flush()
		}
		_, _ = w.Write(data[start : end+1])
		_, _ = w.Write([]byte("X"))
	}))
	defer srv.Close()

	seg := Segment{Index: 0, Start: 0, End: 3}
	f, err := os.Create(filepath.Join(t.TempDir(), "x.partial"))
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	if err := f.Truncate(int64(len(data))); err != nil {
		t.Fatal(err)
	}
	err = downloadSegment(context.Background(), defaultHTTPClient(), &Stream{Kind: StreamVideo, URL: srv.URL}, seg, int64(len(data)), f)
	if err == nil || !strings.Contains(err.Error(), "over body") {
		t.Fatalf("expected oversized body rejection, got %v", err)
	}
}

func TestDownloadStreamCancelThenResumeKeepsCompletedSegment(t *testing.T) {
	data := []byte("abcdefghijkl")
	var mu sync.Mutex
	counts := map[string]int{}
	secondStarted := make(chan struct{}, 1)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rg := r.Header.Get("Range")
		mu.Lock()
		counts[rg]++
		mu.Unlock()
		start, end := mustRange(t, rg)
		if rg == "bytes=4-7" {
			select {
			case secondStarted <- struct{}{}:
			default:
			}
			<-r.Context().Done()
			return
		}
		serveRange(w, data, start, end, false)
	}))

	dir := t.TempDir()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() {
		_, err := DownloadStream(ctx, jobs.New("cancel-resume-1"), &Stream{Kind: StreamVideo, URL: srv.URL, Generation: 1}, dir, "video", DownloadOptions{ChunkSize: 4, Concurrency: 1, MaxAttempts: 2})
		done <- err
	}()
	<-secondStarted
	cancel()
	if err := <-done; err == nil {
		t.Fatal("expected cancellation")
	}
	srv.Close()

	// A fresh server serves all remaining ranges. The first completed segment must
	// be reused rather than fetched again.
	srv2 := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rg := r.Header.Get("Range")
		mu.Lock()
		counts["retry:"+rg]++
		mu.Unlock()
		start, end := mustRange(t, rg)
		serveRange(w, data, start, end, false)
	}))
	defer srv2.Close()
	_, err := DownloadStream(context.Background(), jobs.New("cancel-resume-2"), &Stream{Kind: StreamVideo, URL: srv2.URL, Generation: 2}, dir, "video", DownloadOptions{ChunkSize: 4, Concurrency: 1, MaxAttempts: 2})
	if err != nil {
		t.Fatal(err)
	}
	mu.Lock()
	defer mu.Unlock()
	if counts["retry:bytes=0-3"] != 0 {
		t.Fatalf("completed segment was re-downloaded after cancellation: %#v", counts)
	}
}

func TestDefaultChunkSizeIs32MiBAndFinalSegmentIsExact(t *testing.T) {
	if DefaultChunkSize != 32<<20 {
		t.Fatalf("DefaultChunkSize=%d want=%d", DefaultChunkSize, int64(32<<20))
	}
	total := 2*DefaultChunkSize + 123
	segs := segments(total, DefaultChunkSize)
	if len(segs) != 3 {
		t.Fatalf("segments=%d want=3", len(segs))
	}
	if got := segs[2].Size(); got != 123 {
		t.Fatalf("final segment size=%d want=123", got)
	}
	if segs[2].Start != 2*DefaultChunkSize || segs[2].End != total-1 {
		t.Fatalf("final range=%d-%d total=%d", segs[2].Start, segs[2].End, total)
	}
}

func TestCompletedStreamIsReusedWithoutSegmentDownload(t *testing.T) {
	data := []byte("already-complete")
	var mu sync.Mutex
	segmentRequests := 0
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start, end := mustRange(t, r.Header.Get("Range"))
		if !(start == 0 && end == 0) {
			mu.Lock()
			segmentRequests++
			mu.Unlock()
		}
		serveRange(w, data, start, end, false)
	}))
	defer srv.Close()

	dir := t.TempDir()
	out := filepath.Join(dir, "video.stream")
	if err := os.WriteFile(out, data, 0o644); err != nil {
		t.Fatal(err)
	}
	res, err := DownloadStream(context.Background(), jobs.New("reuse-final"), &Stream{Kind: StreamVideo, URL: srv.URL, Generation: 1}, dir, "video", DownloadOptions{ChunkSize: 4, Concurrency: 2})
	if err != nil {
		t.Fatal(err)
	}
	if res.Path != out || res.Size != int64(len(data)) {
		t.Fatalf("result=%+v", res)
	}
	mu.Lock()
	defer mu.Unlock()
	if segmentRequests != 0 {
		t.Fatalf("completed stream was downloaded again: %d segment requests", segmentRequests)
	}
}

func TestResumeManifestMismatchDoesNotTrustOldCheckpoint(t *testing.T) {
	dir := t.TempDir()
	workPath := filepath.Join(dir, "video.stream.partial")
	resumePath := filepath.Join(dir, "video.stream.resume.json")
	if err := os.WriteFile(workPath, make([]byte, 10), 0o644); err != nil {
		t.Fatal(err)
	}
	// Old/different chunk geometry must never mark ranges complete in the new job.
	if err := os.WriteFile(resumePath, []byte(`{"version":1,"total":10,"chunk_size":8,"completed":[0]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	f, resume, err := openResumeWork(workPath, resumePath, 10, 4, 3)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	if resume.IsComplete(0) {
		t.Fatal("mismatched resume manifest was trusted")
	}
}

func TestInvalidResumeManifestForSameSizedWorkRedownloadsEveryRange(t *testing.T) {
	data := []byte("abcdefghij")
	var mu sync.Mutex
	counts := map[string]int{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rg := r.Header.Get("Range")
		mu.Lock()
		counts[rg]++
		mu.Unlock()
		start, end := mustRange(t, rg)
		serveRange(w, data, start, end, false)
	}))
	defer srv.Close()

	dir := t.TempDir()
	workPath := filepath.Join(dir, "video.stream.partial")
	resumePath := filepath.Join(dir, "video.stream.resume.json")
	// Same-sized stale bytes are deliberately left in place. The mismatched
	// geometry must reset completion state, forcing every segment to overwrite
	// the stale content before the work file can be promoted.
	if err := os.WriteFile(workPath, []byte("XXXXXXXXXX"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(resumePath, []byte(`{"version":1,"total":10,"chunk_size":8,"completed":[0,1]}`), 0o644); err != nil {
		t.Fatal(err)
	}

	res, err := DownloadStream(context.Background(), jobs.New("stale-resume"), &Stream{Kind: StreamVideo, URL: srv.URL, Generation: 1}, dir, "video", DownloadOptions{ChunkSize: 4, Concurrency: 2, MaxAttempts: 2})
	if err != nil {
		t.Fatal(err)
	}
	got, err := os.ReadFile(res.Path)
	if err != nil {
		t.Fatal(err)
	}
	if string(got) != string(data) {
		t.Fatalf("output=%q want=%q", got, data)
	}
	mu.Lock()
	defer mu.Unlock()
	for _, rg := range []string{"bytes=0-3", "bytes=4-7", "bytes=8-9"} {
		if counts[rg] != 1 {
			t.Fatalf("range %s requests=%d want=1; all=%#v", rg, counts[rg], counts)
		}
	}
}
