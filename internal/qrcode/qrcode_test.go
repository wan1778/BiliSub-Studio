package qrcode

import "testing"

func TestEncodeVersion10L(t *testing.T) {
	m, err := Encode("https://passport.bilibili.com/qrcode/test?key=fixture")
	if err != nil {
		t.Fatal(err)
	}
	if m.Size != 57 {
		t.Fatalf("size=%d", m.Size)
	}
	// Finder pattern centers are dark and their white rings are light.
	for _, p := range [][2]int{{3, 3}, {53, 3}, {3, 53}} {
		if !m.At(p[0], p[1]) {
			t.Fatalf("finder center not dark at %v", p)
		}
	}
	if !m.At(0, 0) || m.At(1, 1) {
		t.Fatalf("finder rings are malformed")
	}
}
func TestEncodeRejectsTooLong(t *testing.T) {
	b := make([]byte, 272)
	for i := range b {
		b[i] = 'a'
	}
	if _, err := Encode(string(b)); err == nil {
		t.Fatal("expected capacity error")
	}
}
