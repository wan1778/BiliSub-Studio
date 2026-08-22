package ocr

import (
	"fmt"
	"math"
	"os"
	"path/filepath"
	"testing"
)

func TestScanCheckpointKeyChangesWithSourceOrScanContract(t *testing.T) {
	d := t.TempDir()
	video := filepath.Join(d, "video.mp4")
	if err := os.WriteFile(video, []byte("video-a"), 0o644); err != nil {
		t.Fatal(err)
	}
	base := ScanRequest{
		Path: video, Region: ScanRegion{X: .1, Y: .7, W: .8, H: .2},
		Mode: "balanced", Sensitivity: 1, Duration: 3600,
	}
	key1, err := scanCheckpointKey(base)
	if err != nil {
		t.Fatal(err)
	}
	changedROI := base
	changedROI.Region.Y = .6
	key2, err := scanCheckpointKey(changedROI)
	if err != nil {
		t.Fatal(err)
	}
	if key1 == key2 {
		t.Fatal("checkpoint key ignored ROI change")
	}
	changedMode := base
	changedMode.Mode = "accurate"
	key3, err := scanCheckpointKey(changedMode)
	if err != nil {
		t.Fatal(err)
	}
	if key1 == key3 {
		t.Fatal("checkpoint key ignored scan-mode change")
	}
	if err := os.WriteFile(video, []byte("video-content-is-now-different"), 0o644); err != nil {
		t.Fatal(err)
	}
	key4, err := scanCheckpointKey(base)
	if err != nil {
		t.Fatal(err)
	}
	if key1 == key4 {
		t.Fatal("checkpoint key ignored source-file change")
	}
}

func TestScanCheckpointRoundTripRestoresConfirmedTrackerState(t *testing.T) {
	d := t.TempDir()
	video := filepath.Join(d, "video.mp4")
	if err := os.WriteFile(video, []byte("video"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{Path: video, Region: ScanRegion{X: .1, Y: .7, W: .8, H: .2}, Mode: "balanced", Sensitivity: 1}
	key, err := scanCheckpointKey(req)
	if err != nil {
		t.Fatal(err)
	}
	want := scanCheckpoint{
		Schema: checkpointSchema, Key: key, MediaSeconds: 600,
		Cues:   []Cue{{Start: 10, End: 12, Text: "第一句", Conf: .95}},
		Active: &Cue{Start: 599, End: 600, Text: "第二句", Conf: .94},
		Frames: 1500, OCRCalls: 80,
	}
	path := scanCheckpointFile(d, key)
	if err := writeScanCheckpoint(path, want); err != nil {
		t.Fatal(err)
	}
	got, ok, err := readScanCheckpoint(path, key)
	if err != nil {
		t.Fatal(err)
	}
	if !ok {
		t.Fatal("checkpoint was not loaded")
	}
	if got.MediaSeconds != want.MediaSeconds || got.Frames != want.Frames || got.OCRCalls != want.OCRCalls {
		t.Fatalf("checkpoint counters got=%+v want=%+v", got, want)
	}
	if len(got.Cues) != 1 || got.Cues[0].Text != "第一句" || got.Active == nil || got.Active.Text != "第二句" {
		t.Fatalf("checkpoint tracker state=%+v", got)
	}
}

func TestScanCheckpointRejectsWrongKeyAndCorruptJSON(t *testing.T) {
	d := t.TempDir()
	path := filepath.Join(d, "scan.json")
	if err := writeScanCheckpoint(path, scanCheckpoint{Schema: checkpointSchema, Key: "right", MediaSeconds: 10}); err != nil {
		t.Fatal(err)
	}
	if _, ok, err := readScanCheckpoint(path, "wrong"); err != nil || ok {
		t.Fatalf("wrong-key checkpoint accepted: ok=%v err=%v", ok, err)
	}
	if err := os.WriteFile(path, []byte("{broken"), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, ok, err := readScanCheckpoint(path, "right"); err == nil || ok {
		t.Fatalf("corrupt checkpoint not rejected: ok=%v err=%v", ok, err)
	}
}

func TestCheckpointSessionWritesOnlyStableTrackerState(t *testing.T) {
	d := t.TempDir()
	video := filepath.Join(d, "video.mp4")
	if err := os.WriteFile(video, []byte("video"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{Path: video, Region: ScanRegion{X: .1, Y: .7, W: .8, H: .2}, Mode: "balanced", Sensitivity: 1}
	session, _, _, err := newScanCheckpointSession(d, 10, req)
	if err != nil {
		t.Fatal(err)
	}
	tracker := newSubtitleTracker(scanModeFor("balanced", 1))
	tracker.Observe(9.9, Result{OK: true, Detected: true, Text: "字幕", Confidence: .95})
	if err := session.MaybeSave(10, tracker, 25, 4); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(session.path); !os.IsNotExist(err) {
		t.Fatalf("unstable candidate was checkpointed: err=%v", err)
	}
	tracker.Observe(10.1, Result{OK: true, Detected: true, Text: "字幕", Confidence: .96})
	if err := session.MaybeSave(10.1, tracker, 26, 5); err != nil {
		t.Fatal(err)
	}
	cp, ok, err := readScanCheckpoint(session.path, session.key)
	if err != nil || !ok {
		t.Fatalf("stable state not checkpointed: ok=%v err=%v", ok, err)
	}
	if cp.Active == nil || cp.Active.Text != "字幕" || cp.MediaSeconds != 10.1 {
		t.Fatalf("checkpoint=%+v", cp)
	}
}

func TestInspectAndRemoveCheckpointExposeResumeMetadata(t *testing.T) {
	d := t.TempDir()
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("video"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{Path: input, Region: ScanRegion{X: .05, Y: .65, W: .9, H: .3}, Mode: "balanced", Sensitivity: .75}
	key, err := scanCheckpointKey(req)
	if err != nil {
		t.Fatal(err)
	}
	active := &Cue{Start: 9, End: 10, Text: "đang chạy", Conf: .96}
	if err := writeScanCheckpoint(scanCheckpointFile(d, key), scanCheckpoint{
		Schema: checkpointSchema, Key: key, MediaSeconds: 10.5,
		Cues: []Cue{{Start: 1, End: 2, Text: "đã xong", Conf: .95}}, Active: active,
		Frames: 26, OCRCalls: 7, Stats: scanCheckpointStats{OCRImages: 7, OCRBatchCalls: 3, VisualSkips: 9, VisualConfirmations: 2, OCRRetries: 1, FramePipelineSeconds: 1.2, VisualSeconds: .3, EncodeSeconds: .2, OCRSeconds: .7},
	}); err != nil {
		t.Fatal(err)
	}
	info, err := InspectCheckpoint(d, req)
	if err != nil {
		t.Fatal(err)
	}
	if !info.Exists || info.MediaSeconds != 10.5 || info.CueCount != 2 || info.Frames != 26 || info.OCRCalls != 7 || info.OCRBatchCalls != 3 || info.VisualSkips != 9 || info.VisualConfirmations != 2 || info.OCRRetries != 1 {
		t.Fatalf("info=%+v", info)
	}
	if info.FramePipelineSeconds != 1.2 || info.VisualSeconds != .3 || info.EncodeSeconds != .2 || info.OCRSeconds != .7 {
		t.Fatalf("timing info=%+v", info)
	}
	if len(info.RecentCues) != 2 || info.RecentCues[1].Text != "đang chạy" {
		t.Fatalf("recent=%+v", info.RecentCues)
	}
	if err := RemoveCheckpoint(d, req); err != nil {
		t.Fatal(err)
	}
	info, err = InspectCheckpoint(d, req)
	if err != nil {
		t.Fatal(err)
	}
	if info.Exists {
		t.Fatalf("checkpoint still exists: %+v", info)
	}
}

func TestParallelCheckpointSchema4RoundTripAndInspect(t *testing.T) {
	d := t.TempDir()
	video := filepath.Join(d, "video.mp4")
	if err := os.WriteFile(video, []byte("video-parallel"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{Path: video, Region: ScanRegion{X: .1, Y: .7, W: .8, H: .2}, Mode: "balanced", Sensitivity: 1, Duration: 1200, Parallelism: "4"}
	segments, err := buildScanSegments(req.Duration, 4, scanDefaultOverlapSeconds)
	if err != nil {
		t.Fatal(err)
	}
	session, _, _, err := newParallelCheckpointSession(d, req)
	if err != nil {
		t.Fatal(err)
	}
	cp := scanParallelCheckpoint{Schema: parallelCheckpointSchema, RequestedParallelism: "4", SelectedParallelism: 4, Duration: req.Duration, Overlap: scanDefaultOverlapSeconds, Lanes: make([]scanParallelLaneCheckpoint, 4)}
	for i, seg := range segments {
		cp.Lanes[i] = scanParallelLaneCheckpoint{ID: fmt.Sprintf("lane-%03d", i), Segment: seg, Media: seg.ScanStart}
	}
	cp.Lanes[0].Completed = true
	cp.Lanes[0].Media = segments[0].ScanEnd
	cp.Lanes[0].Cues = []Cue{{Start: 10, End: 12, Text: "第一句", Conf: .95}}
	cp.Lanes[0].Stats = scanCheckpointStats{OCRImages: 7, OCRBatchCalls: 7}
	cp.Lanes[1].Media = 420
	cp.Lanes[1].Cues = []Cue{{Start: 330, End: 332, Text: "第二句", Conf: .94}}
	if err := session.Save(cp); err != nil {
		t.Fatal(err)
	}
	got, ok, err := readParallelScanCheckpoint(session.path, session.key)
	if err != nil || !ok {
		t.Fatalf("read ok=%v err=%v", ok, err)
	}
	if got.Schema != 4 || got.SelectedParallelism != 4 || !got.Lanes[0].Completed || got.Lanes[1].Media != 420 {
		t.Fatalf("checkpoint=%+v", got)
	}
	info, err := InspectCheckpoint(d, req)
	if err != nil {
		t.Fatal(err)
	}
	if !info.Exists || info.ParallelismSelected != 4 || info.TotalLanes != 4 || info.CompletedLanes != 1 || info.OCRCalls != 7 {
		t.Fatalf("info=%+v", info)
	}
	if err := RemoveCheckpoint(d, req); err != nil {
		t.Fatal(err)
	}
	info, err = InspectCheckpoint(d, req)
	if err != nil || info.Exists {
		t.Fatalf("after remove info=%+v err=%v", info, err)
	}
}

func TestSchema3KeyRemainsStableWhenParallelCheckpointSchemaChanges(t *testing.T) {
	d := t.TempDir()
	video := filepath.Join(d, "video.mp4")
	if err := os.WriteFile(video, []byte("video"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{Path: video, Region: ScanRegion{X: .1, Y: .7, W: .8, H: .2}, Mode: "balanced", Sensitivity: 1}
	legacy, err := scanCheckpointKey(req)
	if err != nil {
		t.Fatal(err)
	}
	schema3, err := scanCheckpointKeyForSchema(req, 3)
	if err != nil {
		t.Fatal(err)
	}
	schema4, err := scanParallelCheckpointKey(req)
	if err != nil {
		t.Fatal(err)
	}
	if legacy != schema3 || schema4 == schema3 {
		t.Fatalf("legacy=%s schema3=%s schema4=%s", legacy, schema3, schema4)
	}
}

func TestLegacyCheckpointAvailableOnlyWhenSchema4HasNotTakenOwnership(t *testing.T) {
	d := t.TempDir()
	video := filepath.Join(d, "video.mp4")
	if err := os.WriteFile(video, []byte("video"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{Path: video, Region: ScanRegion{X: .1, Y: .7, W: .8, H: .2}, Mode: "balanced", Sensitivity: 1, Duration: 600, Parallelism: "4"}
	legacyKey, err := scanCheckpointKey(req)
	if err != nil {
		t.Fatal(err)
	}
	if err := writeScanCheckpoint(scanCheckpointFile(d, legacyKey), scanCheckpoint{Schema: checkpointSchema, Key: legacyKey, MediaSeconds: 12, Frames: 30}); err != nil {
		t.Fatal(err)
	}
	ok, err := legacyCheckpointAvailable(d, req)
	if err != nil || !ok {
		t.Fatalf("legacy available=%v err=%v", ok, err)
	}

	segments, err := buildScanSegments(req.Duration, 4, scanDefaultOverlapSeconds)
	if err != nil {
		t.Fatal(err)
	}
	parallelSession, _, _, err := newParallelCheckpointSession(d, req)
	if err != nil {
		t.Fatal(err)
	}
	cp := scanParallelCheckpoint{Schema: parallelCheckpointSchema, RequestedParallelism: "4", SelectedParallelism: 4, Duration: req.Duration, Overlap: scanDefaultOverlapSeconds, Lanes: make([]scanParallelLaneCheckpoint, 4)}
	for i, seg := range segments {
		cp.Lanes[i] = scanParallelLaneCheckpoint{ID: fmt.Sprintf("lane-%03d", i), Segment: seg, Media: seg.ScanStart}
	}
	if err := parallelSession.Save(cp); err != nil {
		t.Fatal(err)
	}
	ok, err = legacyCheckpointAvailable(d, req)
	if err != nil || ok {
		t.Fatalf("schema4 must win: legacy available=%v err=%v", ok, err)
	}
}

func TestParallelCheckpointInfoReportsAggregateProgressAndPausedLaneState(t *testing.T) {
	d := t.TempDir()
	video := filepath.Join(d, "video-parallel-progress.mp4")
	if err := os.WriteFile(video, []byte("video-parallel-progress"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{Path: video, Region: ScanRegion{X: .1, Y: .7, W: .8, H: .2}, Mode: "balanced", Sensitivity: 1, Duration: 1200, Parallelism: "4"}
	segments, err := buildScanSegments(req.Duration, 4, scanDefaultOverlapSeconds)
	if err != nil {
		t.Fatal(err)
	}
	session, _, _, err := newParallelCheckpointSession(d, req)
	if err != nil {
		t.Fatal(err)
	}
	cp := scanParallelCheckpoint{Schema: parallelCheckpointSchema, RequestedParallelism: "4", SelectedParallelism: 4, Duration: req.Duration, Overlap: scanDefaultOverlapSeconds, Lanes: make([]scanParallelLaneCheckpoint, 4)}
	for i, seg := range segments {
		cp.Lanes[i] = scanParallelLaneCheckpoint{
			ID: fmt.Sprintf("lane-%03d", i), Segment: seg,
			Media: seg.CoreStart + 120,
			Stats: scanCheckpointStats{OCRImages: 10, OCRBatchCalls: 10},
		}
	}
	cp.Lanes[0].Cues = []Cue{{Start: 100, End: 102, Text: "前线字幕", Conf: .95}}
	cp.Lanes[3].Cues = []Cue{{Start: 1000, End: 1002, Text: "远端字幕", Conf: .96}}
	if err := session.Save(cp); err != nil {
		t.Fatal(err)
	}
	info, err := InspectCheckpoint(d, req)
	if err != nil {
		t.Fatal(err)
	}
	if info.ActiveLanes != 0 {
		t.Fatalf("paused checkpoint reported active lanes: %+v", info)
	}
	if math.Abs(info.MediaSeconds-120) > 1e-9 {
		t.Fatalf("contiguous frontier=%v want 120", info.MediaSeconds)
	}
	if math.Abs(info.ProgressPercent-40) > 1e-9 {
		t.Fatalf("aggregate progress=%v want 40", info.ProgressPercent)
	}
	if info.CueCount != 2 {
		t.Fatalf("total cue count=%d want 2", info.CueCount)
	}
	if len(info.RecentCues) != 1 || info.RecentCues[0].Text != "前线字幕" {
		t.Fatalf("checkpoint recent cues should follow preview frontier: %+v", info.RecentCues)
	}
}
