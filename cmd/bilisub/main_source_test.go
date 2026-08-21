package main

import (
	"os"
	"strings"
	"testing"
)

func TestProductionEntrypointIsNativeOnly(t *testing.T) {
	b, err := os.ReadFile("main.go")
	if err != nil {
		t.Fatal(err)
	}
	s := string(b)
	for _, forbidden := range []string{"net.Listen", "127.0.0.1", "http.Server", "internal/api", ".Launch("} {
		if strings.Contains(s, forbidden) {
			t.Fatalf("production entrypoint still contains browser/HTTP runtime marker %q", forbidden)
		}
	}
	for _, required := range []string{"application.New", "nativeui.Run", "proc.EnableContainment", "proc.Breakaway"} {
		if !strings.Contains(s, required) {
			t.Fatalf("production entrypoint missing native marker %q", required)
		}
	}
}
