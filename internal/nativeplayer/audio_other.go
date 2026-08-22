//go:build !windows

package nativeplayer

import "errors"

type audioSink interface {
	Write([]byte) error
	SetMuted(bool)
	Close()
}

func newAudioSink(int, int) (audioSink, error) {
	return nil, errors.New("native audio chỉ khả dụng trên Windows")
}
