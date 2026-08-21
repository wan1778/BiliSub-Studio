package jobs

import (
	"testing"
	"time"
)

func TestCancelMarksJobDone(t *testing.T) {
	j := New("x")
	j.Cancel()
	s := j.Snapshot(0)
	if !s.Done || s.Status != "cancelled" {
		t.Fatalf("snapshot=%+v", s)
	}
}

func TestPausableJobHandshake(t *testing.T) {
	j := NewPausable("ocr")
	done, err := j.RequestPause()
	if err != nil {
		t.Fatal(err)
	}
	if !j.PauseRequested() {
		t.Fatal("pause request not visible to scanner")
	}
	select {
	case <-done:
		t.Fatal("pause completed before scanner acknowledged checkpoint")
	default:
	}
	j.PauseComplete("paused safely")
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("pause waiter was not released")
	}
	s := j.Snapshot(0)
	if !s.Done || s.Status != "paused" || !s.PauseSupported || !s.PauseRequested {
		t.Fatalf("snapshot=%+v", s)
	}
}

func TestNonPausableJobRejectsPause(t *testing.T) {
	if _, err := New("download").RequestPause(); err == nil {
		t.Fatal("expected pause rejection")
	}
}
