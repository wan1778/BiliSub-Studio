package application

import (
	"context"
	"testing"
	"time"

	"bilisubstudio/internal/jobs"
)

func TestPrepareShutdownWaitsForPausableJobBeforeCancellingOthers(t *testing.T) {
	a := &App{Jobs: jobs.NewManager()}
	ocrJob := jobs.NewPausable("ocr-test")
	other := jobs.New("video-test")
	a.Jobs.Add(ocrJob)
	a.Jobs.Add(other)

	go func() {
		deadline := time.Now().Add(time.Second)
		for time.Now().Before(deadline) {
			if ocrJob.PauseRequested() {
				ocrJob.PauseComplete("checkpoint fsynced")
				return
			}
			time.Sleep(time.Millisecond)
		}
	}()

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if err := a.PrepareShutdown(ctx); err != nil {
		t.Fatal(err)
	}
	if got := ocrJob.Snapshot(0).Status; got != "paused" {
		t.Fatalf("OCR status=%q, want paused", got)
	}
	if got := other.Snapshot(0).Status; got != "cancelled" {
		t.Fatalf("other status=%q, want cancelled", got)
	}
}

func TestPrepareShutdownRefusesUnsafeCloseWhenPauseCannotFinish(t *testing.T) {
	a := &App{Jobs: jobs.NewManager()}
	ocrJob := jobs.NewPausable("ocr-stuck")
	other := jobs.New("video-test")
	a.Jobs.Add(ocrJob)
	a.Jobs.Add(other)

	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	if err := a.PrepareShutdown(ctx); err == nil {
		t.Fatal("expected safe-close failure")
	}
	if got := other.Snapshot(0).Status; got == "cancelled" {
		t.Fatalf("non-OCR job cancelled even though OCR checkpoint was not safe")
	}
}
