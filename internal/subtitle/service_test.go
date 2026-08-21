package subtitle

import "testing"

func TestParseBiliSubtitle(t *testing.T) {
	raw := []byte(`{"body":[{"from":1.2,"to":2.4,"content":"你好"},{"from":2.5,"to":3.1,"content":"world"}]}`)
	c, err := parseCues(raw)
	if err != nil || len(c) != 2 {
		t.Fatalf("c=%+v err=%v", c, err)
	}
	if got := renderSRT(c); got == "" || got[:1] != "1" {
		t.Fatalf("bad SRT %q", got)
	}
}

func TestParseJSON3Subtitle(t *testing.T) {
	raw := []byte(`{"events":[{"tStartMs":1000,"dDurationMs":1200,"segs":[{"utf8":"hello "},{"utf8":"world"}]}]}`)
	c, err := parseCues(raw)
	if err != nil || len(c) != 1 || c[0].Text != "hello world" {
		t.Fatalf("c=%+v err=%v", c, err)
	}
}
