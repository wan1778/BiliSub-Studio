---
name: gitnexus-area-asr
description: "Skill for the Asr area of BiliSub-Studio. 15 symbols across 1 files."
---

# Asr

15 symbols | 1 files | Cohesion: 88%

## When to Use

- Working with code in `internal/`
- Understanding how configure_cuda, emit, hybrid_plan work
- Modifying asr-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `internal/asr/worker.py` | configure_cuda, emit, hybrid_plan, hybrid_project, clip_word (+10) |

## Entry Points

Start here when exploring this area:

- **`configure_cuda`** (Function) — `internal/asr/worker.py:15`
- **`emit`** (Function) — `internal/asr/worker.py:38`
- **`hybrid_plan`** (Function) — `internal/asr/worker.py:82`
- **`hybrid_project`** (Function) — `internal/asr/worker.py:142`
- **`clip_word`** (Function) — `internal/asr/worker.py:147`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `configure_cuda` | Function | `internal/asr/worker.py` | 15 |
| `emit` | Function | `internal/asr/worker.py` | 38 |
| `hybrid_plan` | Function | `internal/asr/worker.py` | 82 |
| `hybrid_project` | Function | `internal/asr/worker.py` | 142 |
| `clip_word` | Function | `internal/asr/worker.py` | 147 |
| `main` | Function | `internal/asr/worker.py` | 277 |
| `parse_args` | Function | `internal/asr/worker.py` | 43 |
| `run_hybrid` | Function | `internal/asr/worker.py` | 191 |
| `hybrid_seam` | Function | `internal/asr/worker.py` | 119 |
| `key` | Function | `internal/asr/worker.py` | 124 |
| `midpoint` | Function | `internal/asr/worker.py` | 123 |
| `hybrid_words` | Function | `internal/asr/worker.py` | 115 |
| `recognition` | Function | `internal/asr/worker.py` | 58 |
| `process_chunk` | Function | `internal/asr/worker.py` | 209 |
| `segment_payload` | Function | `internal/asr/worker.py` | 66 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `Main → Configure_cuda` | intra_community | 3 |
| `Main → Emit` | intra_community | 3 |
| `Main → Hybrid_plan` | intra_community | 3 |
| `Main → Hybrid_words` | cross_community | 3 |

## How to Explore

1. `context({name: "configure_cuda"})` — see callers and callees
2. `query({search_query: "asr"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
