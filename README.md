# BiliSub Studio v4 native Windows app

This is the actual Go source for the native Windows x64 BiliSub Studio app, not a binary patch/recovery package. Production opens a Win32 window directly and does not use an external browser, localhost UI, WebView or WebView2.

Start with [`ARCHITECTURE.md`](ARCHITECTURE.md). It records which component owns every part of the app and the exact video/OCR call graph so future fixes do not patch the wrong layer.

Current beta: `4.0.0-beta.12`.

The frozen Go + Win32 tree remains the current executable reference. The C#/.NET 10 + WinUI 3 lane under [`csharp/`](csharp/README.md) is source-complete for the planned production owners, but it is not a release replacement until the exact Windows build, runtime parity, visual QA and field matrix pass.

Validation details: see [`TEST_REPORT.md`](TEST_REPORT.md).

Release validation includes full native parity/application/dependency audits, schema-3/schema-4 OCR checkpoint inspection, full OCR telemetry regression, legacy browser parity oracle, and Windows x64 cross-build/static release validation.
