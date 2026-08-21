package appstate

import "testing"

func TestNormalizeCookieAcceptsBareSESSDATA(t *testing.T) {
	got := normalizeCookie("abc%2Cdef")
	if got != "SESSDATA=abc%2Cdef" {
		t.Fatalf("got %q", got)
	}
}

func TestNormalizeCookieHeaderAndDeduplicates(t *testing.T) {
	got := normalizeCookie("Cookie: SESSDATA=abc; bili_jct=csrf; SESSDATA=old")
	if got != "SESSDATA=abc; bili_jct=csrf" {
		t.Fatalf("got %q", got)
	}
}

func TestThemeDefaultsDarkAndPersists(t *testing.T) {
	root := t.TempDir()
	st, err := New(root, "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	if got := st.SnapshotConfig().Theme; got != "dark" {
		t.Fatalf("default theme=%q want dark", got)
	}
	if err := st.UpdateConfig(func(c *Config) { c.Theme = "light" }); err != nil {
		t.Fatal(err)
	}
	st2, err := New(root, "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	if got := st2.SnapshotConfig().Theme; got != "light" {
		t.Fatalf("persisted theme=%q want light", got)
	}
}
