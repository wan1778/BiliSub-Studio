# Build / Release

## Requirements

- Go 1.23+
- Windows target is amd64 and CGO-free.

## Test

```bash
go test -count=1 ./...
go vet ./...
go test -race -count=1 ./...
python scripts/audit_ui_contract.py
python scripts/audit_native_ui.py
python scripts/audit_feature_parity.py
python scripts/audit_standalone_gpu.py
python scripts/audit_application_boundary.py
python scripts/audit_dependency_process.py
python scripts/generate_code_map.py --check
go run scripts/generate_ocr_call_map.go --check
```

## Windows portable build

```bash
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 \
  go build -trimpath -ldflags='-s -w -H=windowsgui' \
  -o BiliSubStudio_v4.0.0-beta.12.exe ./cmd/bilisub
```

The EXE is a native Windows x64 GUI program. It opens its own Win32 window directly: no Chrome/Edge, localhost UI, WebView or WebView2. At runtime it creates/uses sibling folders:

```text
Data/
Tools/
  OCR/
Temp/
Cache/
Downloads/
```

Do not reintroduce a localhost/browser production UI, a second BiliSub backend, or a fixed core port. `Tools` and `Tools/OCR` are app-owned runtimes; production must not search system PATH for ffmpeg/yt-dlp/Python.

## C# + WinUI 3 migration checkpoint

On a Windows development/field machine with .NET 10 and the WinUI workload:

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1
```

This verifies the source-complete P2 checkpoint and publishes an unpackaged `win-x64` candidate. It is not permission to replace the Go candidate: the exact published SHA-256 must still pass the full Windows visual/runtime acceptance matrix before promotion.
