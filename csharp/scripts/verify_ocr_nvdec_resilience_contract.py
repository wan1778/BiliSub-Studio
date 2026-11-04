from __future__ import annotations

import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[2]
SCANNER = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrScanner.cs"
CHECKPOINT = ROOT / "csharp" / "src" / "BiliSubStudio.Core" / "Ocr" / "OcrCheckpointStore.cs"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("FAIL OCR NVDEC resilience contract: " + message)


def main() -> int:
    scanner = SCANNER.read_text(encoding="utf-8")
    checkpoint = CHECKPOINT.read_text(encoding="utf-8")

    # Layer 1 — ffmpeg decode hardening: ffmpeg must swallow broken frames
    # instead of killing the stream (ExitCode != 0 used to drop 4/8 lanes
    # permanently to software).
    lane_args = scanner.split("private static IReadOnlyList<string> BuildLaneArguments", 1)[1]
    lane_args = lane_args.split("private static double Similarity", 1)[0]
    require('args.AddRange(["-err_detect", "ignore_err", "-ec", "3", "-fflags", "+genpts+discardcorrupt"]);' in lane_args,
            "lane ffmpeg args are missing the decode-robustness flags (err_detect/ec/fflags)")
    input_index = lane_args.index('args.AddRange(["-i", source]);')
    flags_index = lane_args.index('-fflags", "+genpts+discardcorrupt')
    require(flags_index < input_index, "decode-robustness flags must be input options placed before -i")
    require('"-loglevel", "info"' in lane_args,
            "lane ffmpeg loglevel changed without contract update")

    # Layer 2 — the NVDEC retry ladder in RunLaneWithFallbackAsync (resume from
    # the checkpoint, exponential backoff, only fall back to software after
    # MaxNvdecLaneAttempts NVDEC-linked attempts).
    ladder_start = scanner.find("private async Task<OcrLaneCheckpoint> RunLaneWithFallbackAsync")
    ladder_end = scanner.find("private async Task<OcrLaneCheckpoint> RunLaneAsync", ladder_start)
    require(ladder_start >= 0 and ladder_end > ladder_start, "missing RunLaneWithFallbackAsync ladder bounds")
    ladder = scanner[ladder_start:ladder_end]
    require("private const int MaxNvdecLaneAttempts = 3;" in scanner,
            "NVDEC retry ladder count is not pinned to 3 attempts")
    require("private const double NvdecRetryBackoffSeconds = 1.5;" in scanner and
            "NvdecRetryBackoffSeconds * Math.Pow(2, attempt - 1)" in ladder,
            "NVDEC retry backoff is not exponential with a pinned base")
    require("for (var attempt = 1; attempt <= MaxNvdecLaneAttempts; attempt++)" in ladder and
            "RunLaneAsync(ffmpeg, source, request, mode, lane, nvdec: true" in ladder and
            "Task.Delay(TimeSpan.FromSeconds(backoff), cancellationToken)" in ladder,
            "NVDEC ladder does not retry with in-process NVDEC and a cancellable backoff")
    require("Interlocked.Increment(ref telemetry.NvdecRetries);" in ladder and
            "Interlocked.Increment(ref telemetry.NvdecFailures);" in ladder and
            "Interlocked.Increment(ref telemetry.SoftwareFallbacks);" in ladder,
            "NVDEC ladder does not count retry/fail/software-fallback into telemetry")
    require("RunLaneAsync(ffmpeg, source, request, mode, lane, nvdec: false" in ladder,
            "exhausted NVDEC ladder does not have a software re-run")
    require("catch (OperationCanceledException) { throw; }" in ladder,
            "NVDEC ladder can swallow user cancellation")
    require("if (decoder != \"nvdec\")" in ladder,
            "software-decided boot must stay on the legacy path without a fake NVDEC ladder")

    # Layer 3 — real decode errors must go into an exception instead of the junk
    # showinfo tail.
    reader_start = scanner.find("private sealed class FrameTimestampReader")
    reader_end = scanner.find("private sealed class JpegStreamReader", reader_start)
    require(reader_start >= 0 and reader_end > reader_start, "missing FrameTimestampReader bounds")
    reader = scanner[reader_start:reader_end]
    require("TryCaptureDecoderErrorLine" in reader and "DecoderErrorKeywords" in reader and
            "_decoderErrors" in reader and "BuildDiagnostics" in reader,
            "FrameTimestampReader does not keep a dedicated decoder-error buffer")
    require("DiagnosticTailCapacity" in reader and "_diagnostic.Remove(0, _diagnostic.Length - DiagnosticTailCapacity);" in reader,
            "stderr diagnostic is not a rolling tail (head-keeping once hid real errors)")
    composed = reader[reader.index("private string BuildDiagnostics()"):]
    require('[stderr tail]' in composed and '[decoder errors]' in composed and
            composed.index('[decoder errors]') < composed.index(': _diagnostic.ToString();'),
            "decoder errors must be appended AFTER the tail so Compact() surfaces real causes")

    # Layer 4 — staggered spawn guards against NVDEC session races at
    # start/resume.
    guarded_start = scanner.find("async Task<OcrLaneCheckpoint> RunGuardedLaneAsync(OcrLaneCheckpoint lane)")
    guarded_window = scanner[guarded_start:guarded_start + 900]
    require("LaneSpawnStaggerMs * lane.Segment.Index" in guarded_window and
            "Task.Delay(launchDelay, laneCancellation.Token)" in guarded_window,
            "OCR lanes still spawn simultaneously instead of a pinned-stagger launch")
    require("private const int LaneSpawnStaggerMs = 150;" in scanner,
            "lane spawn stagger is not pinned to 150ms per lane index")

    # Layer 5 — the telemetry summary line must expose NVDEC health.
    require("nvdec_retries={telemetry.NvdecRetries}" in scanner and
            "nvdec_fail={telemetry.NvdecFailures}" in scanner and
            "soft_fallback={telemetry.SoftwareFallbacks}" in scanner,
            "telemetry summary line does not expose nvdec_retries/fail/soft_fallback")
    require("public long NvdecRetries;" in scanner and "public long NvdecFailures;" in scanner and
            "public long SoftwareFallbacks;" in scanner,
            "telemetry counters missing from OcrScanTelemetryCounters")

    # The checkpoint key is untouchable: the fix only touches decode/retry — the
    # checkpoint identity must not change, otherwise resuming a
    # half-finished checkpoint becomes invalid.
    require("private const int Schema = 19;" in checkpoint and
            "OcrModelTier.DetectionModel(request.Mode), OcrModelTier.RecognitionModel(request.Mode)" in checkpoint,
            "NVDEC fix must not touch checkpoint identity; existing progress must stay resumable")

    print("PASS OCR NVDEC resilience: decode-hardening flags before -i, 3-attempt NVDEC ladder with exponential backoff and telemetry, real decode-error capture appended after diagnostics tail, 150ms staggered lane launch, checkpoint identity untouched")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
