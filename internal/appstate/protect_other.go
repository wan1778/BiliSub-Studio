//go:build !windows

package appstate

func protect(b []byte) ([]byte, error) { return append([]byte("PLAIN\x00"), b...), nil }
func unprotect(b []byte) ([]byte, error) {
	if len(b) >= 6 && string(b[:6]) == "PLAIN\x00" {
		return b[6:], nil
	}
	return b, nil
}
