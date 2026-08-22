# RC11 NVDEC long-video OCR optimization

Baseline: RC10 commit `136741143584c1e66a674102ea551fe0b425235e`.

## Target hardware

Primary field target supplied by the user:
- Ryzen 7 4800H
- NVIDIA RTX 3050 Laptop 4 GB
- 32 GB RAM

## Goal

Reduce the CPU bottleneck during 7–8 hour burned-in-subtitle scans without changing OCR models or subtitle semantics.

## Execution map

```text
/api/ocr/scan
-> Scanner.Run
-> Scanner.run
-> probeNVDEC(exact video + exact ROI/filter path)
   -> success: FFmpeg -hwaccel cuda -hwaccel_output_format cuda
      -> fps on CUDA frames
      -> hwdownload only sampled frames
      -> CPU crop/scale/RGB
   -> failure: software FFmpeg
      -> fps before CPU crop/scale/RGB
-> edge signature / visual confirmation
-> sparse PaddleOCR
-> subtitleTracker
-> checkpoint
```

Runtime NVDEC failure after a successful probe:

```text
NVDEC scan error
-> SaveNow checkpoint when safe
-> Scanner.run(force software reason)
-> checkpoint resume
-> software FFmpeg
```

## Safety constraints

- No change to PP-OCRv6 Small detector/recognizer.
- No forced GPU decode: every source is probed and has software fallback.
- No change to checkpoint schema/key semantics for decoder mode.
- No Google Drive promotion before Windows field evidence.
