# BiliSub Studio — C# / .NET 10 / WinUI 3

The current application lane is the native Windows x64 C# project under [`csharp/`](csharp/README.md). It uses .NET 10 and WinUI 3 directly, with no external browser, localhost UI, WebView or second BiliSub backend.

Start with [`ARCHITECTURE.md`](ARCHITECTURE.md). It records which component owns every part of the app and the exact video/OCR call graph so future fixes do not patch the wrong layer.

Current beta: `4.0.0-beta.12`.

The root `cmd/`, `internal/`, `web/` and Go module files are a frozen legacy reference while migration parity is being verified. They are not compiled into or called by the C# installer. After one exact C# installer passes the Windows runtime, visual and field matrix, that reference is moved to a frozen legacy tag/branch and removed from the production branch.

Validation details: see [`TEST_REPORT.md`](TEST_REPORT.md).

Release validation includes full native parity/application/dependency audits, schema-3/schema-4 OCR checkpoint inspection, full OCR telemetry regression, legacy browser parity oracle, and Windows x64 cross-build/static release validation.
