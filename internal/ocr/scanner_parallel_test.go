package ocr

import (
	"context"
	"errors"
	"math"
	"reflect"
	"strings"
	"testing"
	"time"
)

type blockingScanPool struct{}

func (blockingScanPool) ConfigureScanWorkers(ctx context.Context, _ int) (int, error) {
	<-ctx.Done()
	return 0, ctx.Err()
}

func (blockingScanPool) ActiveScanWorkers() int { return 0 }

type recoveringScanPool struct {
	resetTarget int
}

func (p *recoveringScanPool) ConfigureScanWorkers(ctx context.Context, _ int) (int, error) {
	<-ctx.Done()
	return 0, ctx.Err()
}

func (p *recoveringScanPool) ActiveScanWorkers() int { return 0 }

func (p *recoveringScanPool) ResetScanWorkers(_ context.Context, target int) (int, error) {
	p.resetTarget = target
	return target, nil
}

func TestNormalizeScanParallelism(t *testing.T) {
	for _, tc := range []struct {
		in, want string
	}{
		{"", "auto"}, {"AUTO", "auto"}, {"1", "1"}, {"2", "2"},
		{"4", "4"}, {"8", "8"}, {"16", "16"}, {"3", "3"},
	} {
		got, err := normalizeScanParallelism(tc.in)
		if err != nil || got != tc.want {
			t.Fatalf("normalize(%q)=%q,%v want %q", tc.in, got, err, tc.want)
		}
	}
	for _, bad := range []string{"0", "17", "-1", "abc"} {
		if _, err := normalizeScanParallelism(bad); err == nil {
			t.Fatalf("normalize(%q) accepted invalid value", bad)
		}
	}
}

func TestBuildScanSegmentsHasExactCoreCoverageAndBoundedOverlap(t *testing.T) {
	for _, n := range []int{1, 2, 4, 8, 16} {
		segments, err := buildScanSegments(7200, n, 8)
		if err != nil {
			t.Fatal(err)
		}
		if len(segments) != n {
			t.Fatalf("n=%d len=%d", n, len(segments))
		}
		for i, seg := range segments {
			if seg.Index != i || seg.ScanStart < 0 || seg.ScanEnd > 7200 || seg.ScanStart > seg.CoreStart || seg.ScanEnd < seg.CoreEnd {
				t.Fatalf("n=%d segment[%d]=%+v", n, i, seg)
			}
			if i == 0 && seg.CoreStart != 0 {
				t.Fatalf("first core starts at %v", seg.CoreStart)
			}
			if i > 0 && math.Abs(segments[i-1].CoreEnd-seg.CoreStart) > 1e-9 {
				t.Fatalf("gap/overlap core n=%d prev=%+v cur=%+v", n, segments[i-1], seg)
			}
		}
		if math.Abs(segments[len(segments)-1].CoreEnd-7200) > 1e-9 {
			t.Fatalf("last core end=%v", segments[len(segments)-1].CoreEnd)
		}
	}
}

func TestBuildScanSegmentsCapsShortVideoParallelism(t *testing.T) {
	segments, err := buildScanSegments(240, 16, 8)
	if err != nil {
		t.Fatal(err)
	}
	if len(segments) != 2 {
		t.Fatalf("4-minute video should cap at 2 lanes, got %d", len(segments))
	}
}

func TestCueOwnershipUsesStartAndEndExclusiveCore(t *testing.T) {
	segments, err := buildScanSegments(1200, 2, 8)
	if err != nil {
		t.Fatal(err)
	}
	first, second := segments[0], segments[1]
	spanning := Cue{Start: 599.2, End: 602.5, Text: "跨界"}
	if !cueOwnedBySegment(spanning, first, false) || cueOwnedBySegment(spanning, second, true) {
		t.Fatalf("spanning cue ownership wrong first=%v second=%v", cueOwnedBySegment(spanning, first, false), cueOwnedBySegment(spanning, second, true))
	}
	atBoundary := Cue{Start: 600, End: 602, Text: "新句"}
	if cueOwnedBySegment(atBoundary, first, false) || !cueOwnedBySegment(atBoundary, second, true) {
		t.Fatalf("boundary cue ownership wrong")
	}
}

func TestScanFFmpegArgsRangeBoundsDecoderToSegment(t *testing.T) {
	args := strings.Join(scanFFmpegArgsRange("movie.mp4", 592, 608, "fps=2.5,format=rgb24", true), " ")
	if !strings.Contains(args, "-ss 592.000") || !strings.Contains(args, "-t 16.000") {
		t.Fatalf("range args missing bounded seek/duration: %s", args)
	}
	if !strings.Contains(args, "-hwaccel cuda") {
		t.Fatalf("range args lost NVDEC: %s", args)
	}
}

func TestReconcileSegmentCuesDropsLegacyForeignGarbage(t *testing.T) {
	segments, err := buildScanSegments(1200, 2, scanDefaultOverlapSeconds)
	if err != nil {
		t.Fatal(err)
	}
	lane0 := []Cue{
		{Start: 100, End: 101, Text: "ILLC", Conf: .99},
		{Start: 110, End: 112, Text: "真正字幕", Conf: .93},
	}
	lane1 := []Cue{
		{Start: 700, End: 701, Text: "铺 U 碎", Conf: .98},
		{Start: 710, End: 712, Text: "AI时代", Conf: .92},
		{Start: 720, End: 722, Text: "第3集", Conf: .95},
	}
	got, _ := reconcileSegmentCues([][]Cue{lane0, lane1}, segments, 1200)
	if len(got) != 2 || got[0].Text != "真正字幕" || got[1].Text != "第3集" {
		t.Fatalf("legacy foreign garbage leaked through reconciler: %#v", got)
	}
}

func TestReconcileSegmentCuesPreservesBoundaryOwnershipAndDeterminism(t *testing.T) {
	segments, err := buildScanSegments(1200, 2, 8)
	if err != nil {
		t.Fatal(err)
	}
	lane0 := []Cue{{Start: 599.2, End: 602.5, Text: "跨界", Conf: .95}, {Start: 600.0, End: 601, Text: "下一句", Conf: .94}}
	lane1 := []Cue{{Start: 592.0, End: 602.4, Text: "跨界", Conf: .93}, {Start: 600.0, End: 601, Text: "下一句", Conf: .96}}
	got, _ := reconcileSegmentCues([][]Cue{lane0, lane1}, segments, 1200)
	if len(got) != 2 || got[0].Text != "跨界" || got[1].Text != "下一句" {
		t.Fatalf("reconciled=%+v", got)
	}
	if got[0].Start != 599.2 {
		t.Fatalf("pre-roll lane stole spanning cue start: %+v", got[0])
	}
	got2, _ := reconcileSegmentCues([][]Cue{lane0, lane1}, segments, 1200)
	if !reflect.DeepEqual(got, got2) {
		t.Fatalf("reconcile not deterministic: %+v vs %+v", got, got2)
	}
}

func TestMaxParallelismForDurationAllowsSixteenOnLongVideo(t *testing.T) {
	if got := maxParallelismForDuration(7200); got != 16 {
		t.Fatalf("2-hour video max parallelism=%d want 16", got)
	}
	if got := maxParallelismForDuration(239); got != 1 {
		t.Fatalf("239-second video max parallelism=%d want 1", got)
	}
}

func TestCollectParallelBenchmarkOutcomesReturnsWhenOneLaneNeverReports(t *testing.T) {
	out := make(chan parallelBenchmarkOutcome, 2)
	out <- parallelBenchmarkOutcome{images: 3, scanned: 4}
	ctx, cancel := context.WithTimeout(context.Background(), 40*time.Millisecond)
	defer cancel()
	started := time.Now()
	images, scanned, err := collectParallelBenchmarkOutcomes(ctx, 2, out)
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("missing lane should return deadline, got %v", err)
	}
	if images != 3 || scanned != 4 {
		t.Fatalf("partial telemetry lost images=%d scanned=%v", images, scanned)
	}
	if elapsed := time.Since(started); elapsed > 500*time.Millisecond {
		t.Fatalf("collector remained blocked too long after deadline: %v", elapsed)
	}
}

func TestRestoreAutoWorkerPoolIsBoundedByItsOwnTimeout(t *testing.T) {
	started := time.Now()
	err := restoreAutoWorkerPool(context.Background(), blockingScanPool{}, 4, 40*time.Millisecond)
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("bounded restore should return deadline, got %v", err)
	}
	if elapsed := time.Since(started); elapsed > 500*time.Millisecond {
		t.Fatalf("pool restore remained blocked too long: %v", elapsed)
	}
}

func TestRestoreAutoWorkerPoolHardResetsAfterBusyPoolTimeout(t *testing.T) {
	pool := &recoveringScanPool{}
	err := restoreAutoWorkerPool(context.Background(), pool, 4, 40*time.Millisecond)
	if err != nil {
		t.Fatalf("hard reset fallback failed: %v", err)
	}
	if pool.resetTarget != 4 {
		t.Fatalf("hard reset target=%d want 4", pool.resetTarget)
	}
}

func TestAutoResourceGateStopsBeforeCPUOverload(t *testing.T) {
	baseline := autoResourceSnapshot{TotalRAM: 32 << 30, AvailableRAM: 24 << 30, RAMValid: true}
	current := autoResourceSnapshot{TotalRAM: 32 << 30, AvailableRAM: 20 << 30, RAMValid: true, CPUPercent: 91, CPUValid: true}
	got := evaluateAutoResourceGate(baseline, current, 4, 8)
	if got.Allow || got.Reason != "cpu_pressure" {
		t.Fatalf("CPU pressure should stop before spawning next level: %+v", got)
	}
}

func TestAutoResourceGateStopsBeforeGPUOverload(t *testing.T) {
	baseline := autoResourceSnapshot{TotalVRAM: 4 << 30, UsedVRAM: 1 << 30, VRAMValid: true}
	current := autoResourceSnapshot{TotalVRAM: 4 << 30, UsedVRAM: 2 << 30, VRAMValid: true, GPUPercent: 96, GPUValid: true}
	got := evaluateAutoResourceGate(baseline, current, 4, 8)
	if got.Allow || got.Reason != "gpu_pressure" {
		t.Fatalf("GPU pressure should stop before spawning next level: %+v", got)
	}
}

func TestAutoResourceGatePredictsVRAMBeforeSpawningNextLevel(t *testing.T) {
	// Baseline is 1.2 GiB used. Four lanes peak at 2.5 GiB used. Scaling the
	// measured workload delta to eight lanes predicts more than the 4 GiB card,
	// so Auto must reject 8 before creating the extra workers.
	baseline := autoResourceSnapshot{TotalVRAM: 4 << 30, UsedVRAM: uint64(6 * (uint64(1) << 30) / 5), VRAMValid: true}
	current := autoResourceSnapshot{TotalVRAM: 4 << 30, UsedVRAM: uint64(5 * (uint64(1) << 30) / 2), VRAMValid: true}
	got := evaluateAutoResourceGate(baseline, current, 4, 8)
	if got.Allow || got.Reason != "vram_guard" {
		t.Fatalf("VRAM predictor should stop before 8 workers: %+v", got)
	}
}

func TestAutoResourceGatePredictsRAMBeforeSpawningNextLevel(t *testing.T) {
	baseline := autoResourceSnapshot{TotalRAM: 16 << 30, AvailableRAM: 10 << 30, RAMValid: true}
	current := autoResourceSnapshot{TotalRAM: 16 << 30, AvailableRAM: 5 << 30, RAMValid: true}
	got := evaluateAutoResourceGate(baseline, current, 4, 8)
	if got.Allow || got.Reason != "ram_guard" {
		t.Fatalf("RAM predictor should stop before next level: %+v", got)
	}
}

func TestAutoResourceGateAllowsHealthyHeadroom(t *testing.T) {
	baseline := autoResourceSnapshot{
		TotalRAM: 32 << 30, AvailableRAM: 26 << 30, RAMValid: true,
		TotalVRAM: 8 << 30, UsedVRAM: 1 << 30, VRAMValid: true,
	}
	current := autoResourceSnapshot{
		TotalRAM: 32 << 30, AvailableRAM: 23 << 30, RAMValid: true,
		TotalVRAM: 8 << 30, UsedVRAM: 2 << 30, VRAMValid: true,
		CPUPercent: 54, CPUValid: true, GPUPercent: 72, GPUValid: true,
	}
	got := evaluateAutoResourceGate(baseline, current, 4, 8)
	if !got.Allow || got.Reason != "resource_ok" {
		t.Fatalf("healthy machine should be allowed to probe next level: %+v", got)
	}
}

func TestAutoResourceGateUnknownTelemetryKeepsWatchdogFallback(t *testing.T) {
	got := evaluateAutoResourceGate(autoResourceSnapshot{}, autoResourceSnapshot{}, 4, 8)
	if !got.Allow || got.Reason != "resource_unknown" {
		t.Fatalf("unknown telemetry should retain bounded benchmark fallback: %+v", got)
	}
}

func TestMergeAutoResourcePeakKeepsWorstObservedHeadroom(t *testing.T) {
	peak := mergeAutoResourcePeak(autoResourceSnapshot{}, autoResourceSnapshot{
		TotalRAM: 16 << 30, AvailableRAM: 9 << 30, RAMValid: true,
		TotalVRAM: 4 << 30, UsedVRAM: 1 << 30, VRAMValid: true,
		CPUPercent: 40, CPUValid: true, GPUPercent: 60, GPUValid: true,
	})
	peak = mergeAutoResourcePeak(peak, autoResourceSnapshot{
		TotalRAM: 16 << 30, AvailableRAM: 7 << 30, RAMValid: true,
		TotalVRAM: 4 << 30, UsedVRAM: 2 << 30, VRAMValid: true,
		CPUPercent: 78, CPUValid: true, GPUPercent: 83, GPUValid: true,
	})
	if peak.AvailableRAM != 7<<30 || peak.UsedVRAM != 2<<30 || peak.CPUPercent != 78 || peak.GPUPercent != 83 {
		t.Fatalf("peak sampler lost worst-case measurement: %+v", peak)
	}
}

func TestRecentCuesAtOrBeforeAlignsLiveListWithContiguousFrontier(t *testing.T) {
	cues := []Cue{
		{Start: 100, End: 102, Text: "前线字幕", Conf: .95},
		{Start: 900, End: 902, Text: "远端字幕", Conf: .96},
	}
	got := recentCuesAtOrBefore(cues, 120, 120)
	if len(got) != 1 || got[0].Text != "前线字幕" {
		t.Fatalf("frontier-aligned cues=%+v", got)
	}
}
