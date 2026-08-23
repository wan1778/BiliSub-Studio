# NghiTTS integration audit

Reference: https://github.com/nghimestudio/nghitts
Audit date: 2026-08-23

## What is useful for BiliSub Studio

NghiTTS demonstrates a fully local Vietnamese TTS path based on Piper-compatible ONNX models. The repository documents:

- Vietnamese text normalization for numbers, dates, time, currency, percentages, decimals, phone numbers, ordinals, Roman numerals and ranges;
- Piper-compatible ONNX model + JSON configuration pairs;
- local inference;
- multi-speaker model support;
- adjustable speed;
- WAV output;
- chunked synthesis;
- about 5x realtime as the repository's own stated example performance.

Its current web implementation uses ONNX Runtime Web/WASM and a browser Web Worker. BiliSub Studio must not copy that runtime architecture into production because the app is native WinUI 3. The reusable parts are the reviewed preprocessing behavior, model/config format and model candidates.

## Initial generic model candidates

The repository README documents these generic candidates:

- `calmwoman3688` - female voice - roughly 60.6 MB ONNX plus JSON
- `deepman3909` - male voice - roughly 60.6 MB ONNX plus JSON

These are the preferred first evaluation pair because the product only needs a male-like/female-like routing choice.

The repository also mentions celebrity-named voices. Those are excluded from the default integration audit until separate model-weight/dataset/voice-likeness rights are established.

## Licensing boundary

The GitHub repository contains an Apache-2.0 LICENSE for the source code. The README also states that the project is free/open source and allowed for commercial use.

However, the TTS model files are described as separately downloadable from external storage. Therefore BiliSub Studio must not infer that every externally hosted model weight or training dataset automatically inherits the repository source-code license.

Before a model is distributed or downloaded by BiliSub Studio, record:

- source URL/repository
- model name/revision
- model/config sizes
- SHA-256 for every required file
- model/data license or explicit redistribution permission
- whether any real-person voice likeness is involved

## Production integration direction

Preferred native path:

```text
C# Editor
  -> Vietnamese text normalization port/reimplementation
  -> native/local ONNX inference owner
  -> reviewed Piper-compatible ONNX + JSON model
  -> WAV clip
  -> measured duration / timing fit
  -> shared preview/render FFmpeg mix
```

Do not require:

- Vue
- Vite
- Node.js on the user's machine
- browser/WebView
- Cloudflare R2 at inference time
- localhost API

A first-time app-managed model download is acceptable only when free, version-pinned and checksum-verified. Once downloaded, synthesis must run locally.

## Open items before production pin

1. Obtain the exact generic model files/configs.
2. Verify explicit model-weight license/provenance.
3. Record exact sizes and SHA-256.
4. Compare quality on actual translated Chinese-film dialogue.
5. Benchmark CPU/RAM and synthesis speed on Windows.
6. Validate pronunciation after BiliSub's Vietnamese text normalization.
7. Establish safe speed range for timing fit.
8. Confirm preview and final export consume the identical generated WAV clips.
