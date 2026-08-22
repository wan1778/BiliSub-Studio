package application

import (
	"strings"
	"testing"
	"time"
)

func TestSanitizeDiagnosticTextRemovesSecretsAndUserPath(t *testing.T) {
	in := `SESSDATA=secret token=abc C:\Users\Alice\Videos\x.mp4 https://x.test/?auth=z`
	out := SanitizeDiagnosticText(in)
	for _, bad := range []string{"secret", "Alice", "auth=z", "token=abc"} {
		if strings.Contains(out, bad) {
			t.Fatalf("secret leaked %q in %q", bad, out)
		}
	}
}

func TestNewBugIDStableShape(t *testing.T) {
	id := NewBugID(time.Date(2026, 8, 21, 2, 3, 4, 123456789, time.UTC))
	if !strings.HasPrefix(id, "BUG-20260821-020304-") {
		t.Fatalf("id=%q", id)
	}
}
