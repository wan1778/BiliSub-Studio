//go:build windows

package appstate

import (
	"fmt"
	"syscall"
	"unsafe"
)

type dataBlob struct {
	cbData uint32
	pbData *byte
}

var (
	crypt32              = syscall.NewLazyDLL("crypt32.dll")
	kernel32             = syscall.NewLazyDLL("kernel32.dll")
	procCryptProtectData = crypt32.NewProc("CryptProtectData")
	procCryptUnprotect   = crypt32.NewProc("CryptUnprotectData")
	procLocalFree        = kernel32.NewProc("LocalFree")
)

func blobFromBytes(b []byte) dataBlob {
	if len(b) == 0 {
		return dataBlob{}
	}
	return dataBlob{cbData: uint32(len(b)), pbData: &b[0]}
}

func blobBytes(b dataBlob) []byte {
	if b.cbData == 0 || b.pbData == nil {
		return nil
	}
	return append([]byte(nil), unsafe.Slice(b.pbData, b.cbData)...)
}

func protect(in []byte) ([]byte, error) {
	ib := blobFromBytes(in)
	var ob dataBlob
	r, _, e := procCryptProtectData.Call(uintptr(unsafe.Pointer(&ib)), 0, 0, 0, 0, 0x1, uintptr(unsafe.Pointer(&ob))) // CRYPTPROTECT_UI_FORBIDDEN
	if r == 0 {
		return nil, fmt.Errorf("CryptProtectData: %w", e)
	}
	defer procLocalFree.Call(uintptr(unsafe.Pointer(ob.pbData)))
	return blobBytes(ob), nil
}

func unprotect(in []byte) ([]byte, error) {
	ib := blobFromBytes(in)
	var ob dataBlob
	r, _, e := procCryptUnprotect.Call(uintptr(unsafe.Pointer(&ib)), 0, 0, 0, 0, 0x1, uintptr(unsafe.Pointer(&ob)))
	if r == 0 {
		return nil, fmt.Errorf("CryptUnprotectData: %w", e)
	}
	defer procLocalFree.Call(uintptr(unsafe.Pointer(ob.pbData)))
	return blobBytes(ob), nil
}
