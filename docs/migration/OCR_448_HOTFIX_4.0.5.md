# OCR Windows error 448 hotfix — 4.0.5

## Field blocker

BiliSub Studio 4.0.4 can fail during **Chuẩn bị OCR** on Windows with:

- `Failed to create Python minor version link directory`
- `The path cannot be traversed because it contains an untrusted mount point`
- `os error 448`

The failure occurs in uv's managed-Python minor-version junction step. `--no-bin` does not remove that junction step.

## 4.0.5 hotfix contract

- uv/Python bootstrap lives under `%LOCALAPPDATA%\BiliSub Studio\OCRBootstrap` instead of the application install root.
- OCR discovers and validates an exact CPython 3.12 patch-version `python.exe` and builds the private venv from that exact executable.
- A complete interpreter left by a failed 4.0.4 preparation attempt may be reused.
- Paddle/PaddleOCR install steps use `--no-python-downloads` and cannot silently resolve another managed Python.
- Removing OCR also removes the LocalAppData bootstrap.
- Windows contract tests lock the recovery policy.

## Release gate

Public 4.0.5 must not be treated as OCR PASS until a real affected Windows machine completes:

1. Update/install 4.0.5.
2. Open **OCR phụ đề**.
3. Press **Chuẩn bị OCR**.
4. Confirm the old `os error 448` / `untrusted mount point` error does not recur.
5. Continue with **Test frame** before full-video OCR regression.

Source snapshot must pass Windows WinUI, OCR contracts, installer/package and artifact gates before publication.
