# NGHI/Piper Voice task — 2026-08-28

Baseline: main, 6b976bf4ef20bbf9f3c17ed0a867adb414029648 (clean at task start).
Scope: Voice/TTS, extended by the user to remove in-app Editor translation and accept Vietnamese SRT directly. No release, version bump, PR, merge, or push.

## Reviewed artifacts

The [NGHI repository](https://github.com/nghimestudio/nghitts/tree/46d160da32041f7e176607203b958069265df7da)
links its [official model folder](https://drive.google.com/drive/folders/1f_pCpvgqfvO4fdNKM7WS4zTuXC0HBskL).
Its folder listing identifies the following exact Ngọc Huyền files, independently
re-downloaded by the production installer during this task:

| Artifact | Drive ID | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| ngochuyen.onnx | 12HNgJmBY3GiNCcFBRpHxYFv-qbE-jEv7 | 63516050 | 2140977786d76d834736c059dacfa553d4931dac2b2c7aaaea438bb2aa9da697 |
| ngochuyen.onnx.json | 1p-oDIiuhecInjgys4bqsaeOf794OFcHC | 4855 | 971f57f8d504223fee5b40d664f503cf769baf7db21f7d2ae0554a75d07de2f8 |

Drive URLs are mutable; the reviewed bytes and SHA pins are authoritative.
The model is mono 22050 Hz, Vietnamese espeak phonemes, one speaker.
Canonical/default voice: ngoc_huyen. Unverified dropdown voices were removed,
including the former fallback that served VAIS bytes under unrelated NGHI labels.
Kokoro and synthetic substitute models are absent from the production TTS path.

Runtime pins: piper-tts 1.7.0, onnxruntime 1.22.1, vietnormalizer 0.2.3,
numpy 2.5.2, gdown 6.1.0. Runtime manifests use symmetric snake-case
serialization/deserialization and verify the bundled worker identity.

## Runtime contract

One PiperVoice.load before the cue loop; one synthesize(text) call per uncached
whole cue. Piper's internal audio chunks are streamed into one cue WAV.
The text-only sample uses the same service and worker without fake source files
or fabricated Whisper timing. Full-project Whisper provenance is still checked,
but its word/pause data does not split synthesis.

Natural duration is measured. FFmpeg atempo is bounded to 0.92–1.08 with no second
TTS pass. fit/review reflects measured duration, including cache hits. Full audio
is preserved in the cue cache; master playback is bounded by each SRT cue and
overlong cues are explicitly flagged for review. No SRT timecode is rewritten.

Cache keys include model/config, worker, package and algorithm identities, cue ID,
time interval, and normalized text. Cached WAV hashes and decoded format are
checked before reuse. Separate per-run paths keep previous masters/results safe.
Cancellation stops the owned process tree, then waits boundedly for Windows file
handles before deleting only the current run directory. Completed cue cache stays.

Master PCM is assembled in 30-second blocks and streamed to FLAC. C# verifies
result identity, cue order/count, review count, master SHA, decoded rate/channels
and duration before promotion. Project reopen rejects older TTS manifest revisions
and changed master bytes. Preview/Export continue consuming the same voice graph.

## External Vietnamese SRT workflow

The user explicitly retired in-app translation. Editor now offers one Vietnamese
SRT import, Vietnamese-only cue editing and separate SRT export. Import fills
spoken text immediately, preserving original bytes, cue identity and timecodes.
No Chinese SRT, AI preparation or translation step is required. Dirty state compares
actual text rather than treating programmatic TextChanged events as user edits.
The Voice action explains missing video/SRT, pending edits and active-job blockers.
Voice selection/sample stays available without video. Whisper still runs internally
for full-project timing if no valid cache exists; it does not split cue synthesis.

## Verification and limits

The opt-in real integration runs the actual installer, model and service:

    dotnet run --project csharp/tests/BiliSubStudio.Core.ContractTests -- --nghi-tts-runtime <isolated-root> <real-video>

It retains four cue WAVs, a master, processed preview and runtime-checks.json.
It checks cold/warm cache, same-size corruption, inference cancel/retry, cancellation
during master encoding, zero remaining child processes, sample API, decoded preview,
and project reopen. It does not use fake model bytes or an oscillator.

Four spoken test texts:

1. Xin chào, tôi đang kiểm tra giọng đọc tiếng Việt của Ngọc Huyền.
2. Hôm nay trời trong xanh, những hàng cây khẽ đung đưa trước gió.
3. Đạo hữu hãy bình tĩnh, chúng ta vẫn còn cơ hội trở về nhà.
4. Cảm ơn bạn đã lắng nghe. Chúc bạn một ngày thật bình an.

Automated real integration checks: completed successfully on Windows after fixing
the demonstrated FFmpeg file-handle cleanup race. Core contracts: 75/75, including direct Vietnamese import, readiness and edit/reopen.
Signal integrity and successful decoding do not establish intelligible speech.

Technical/runtime speech gate: user confirmed audible speech from the real Ngọc Huyền sample on 2026-08-28 ("ok nghe được rồi"). Automated checks are not a listening assessment. Full-video timing/quality remains a user field test.
Voice quality: WAITING FOR USER FIELD TEST.
This task does not authorize release or model-license clearance for publication.
