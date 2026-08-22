# C# P2 source-checkpoint validation

Checkpoint: `CSharp-P2-SourceComplete`  
Date: 2026-08-21 (Asia/Ho_Chi_Minh)

## Identity

- Frozen Go source identity: `9be4abd8184d2d7d24159dd736b6accfbe1cda90`.
- Frozen source archive SHA-256: `d4acd9ed2b9237f5b20f187e750059d17802d00fc5d3cd81fe6a4387e458d4da`.
- Frozen executable SHA-256: `849dcdeac778287d3004aafd7a661323f342e8bc552a950180f66306c7f7f5f8`.
- The supplied source archive has no `.git`; archive hash plus ZIP comment is the identity gate.

## Passed in the authoring runtime

- C# migration static contract.
- XML/XAML/project parsing and XAML event-handler resolution.
- Generated C# code-map freshness.
- Core/UI ownership and forbidden production marker scan.
- Lexical delimiter/string/comment balance across C# sources.
- Frozen Go production-directory byte comparison against the exact baseline archive.

## Authored but not executed here

The package-free Core runner covers legacy config parity, atomic/concurrent config writes, 1/8/16 connection budgets, real concurrent Range bodies and cleanup, media probe, JSON3/VTT/SRT subtitle normalization, editor filters, Chinese OCR filtering, QR encoding, cookie normalization and job cancellation.

This Linux runtime has no .NET SDK, Windows App SDK runtime, WinUI compositor or Windows field desktop. Therefore no C# compilation, contract-runner execution, publish, PE inspection, native visual QA, real FFmpeg/yt-dlp/PaddleOCR run or Windows field test is claimed.

## Mandatory next gate

Run from repository root on the Windows field machine:

```powershell
powershell -ExecutionPolicy Bypass -File csharp/scripts/verify.ps1
```

Any defect reopens P2. Only the exact published SHA-256 that passes the full matrix may become a release candidate.
