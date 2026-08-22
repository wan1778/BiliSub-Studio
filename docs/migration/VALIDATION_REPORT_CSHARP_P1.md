# Validation report — CSharp-P1-Settings

Date: 2026-08-21 (Asia/Ho_Chi_Minh)  
Baseline source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`

## Completed checks

| Check | Result | Evidence |
|---|---|---|
| Exact source archive SHA-256 | PASS | `d4acd9ed2b9237f5b20f187e750059d17802d00fc5d3cd81fe6a4387e458d4da` |
| Archive identity comment | PASS | `9be4abd8184d2d7d24159dd736b6accfbe1cda90` |
| C# migration static contract | PASS | `python3 csharp/scripts/validate_csharp_migration.py` |
| XAML/csproj/props XML parse | PASS | Included in static contract |
| Legacy JSON field set/order | PASS | 12 fields match `appstate.Config` JSON tags |
| Forbidden production markers | PASS | No localhost URL, WebView2, `/api/` route, or `BiliSubStudioCore.exe` in C# app source |
| Core ownership boundary | PASS | Core contains no `Microsoft.UI` or `Windows.Storage` reference |
| Deferred action safety | PASS | Login/Update/Bug/Maintenance actions remain visibly disabled |
| Go production containment | PASS | Recursive diff of `cmd`, `internal`, `scripts`, and `web` against the exact archive produced no difference |

## Authored Windows checks

The package-free runner under `csharp/tests/BiliSubStudio.Core.ContractTests` covers:

1. Go-compatible defaults.
2. Legacy normalization behavior.
3. Partial JSON/default merging.
4. Atomic persistence/reopen.
5. Application-boundary validation.
6. Serialized concurrent writes.
7. Invalid JSON fallback with an explicit warning.

Run all Windows checks with:

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1
```

## Gates not executed in this runtime

| Gate | Status | Reason |
|---|---|---|
| `dotnet restore/build/run/publish` | NOT RUN | `.NET SDK` is absent in the current Linux work runtime |
| Real WinUI launch | NOT RUN | Requires Windows 10/11 x64 |
| Native visual QA | NOT RUN | Requires the real published WinUI window |
| FolderPicker interaction | NOT RUN | Requires Windows HWND/WinRT |
| DPI matrix | NOT RUN | Requires Windows at 100/125/150/200% |
| New EXE hash/PE validation | NOT APPLICABLE | No EXE was produced; this is a source checkpoint |

This report does not promote a release candidate. The next valid gate is a real Windows build and Settings visual field test before Media preview migration begins.
