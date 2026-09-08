---
name: gitnexus-area-tts
description: "Skill for the Tts area of BiliSub-Studio. 32 symbols across 1 files."
---

# Tts

32 symbols | 1 files | Cohesion: 75%

## When to Use

- Working with code in `internal/`
- Understanding how atomic_json, build_master, cache_identity work
- Modifying tts-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `internal/tts/worker.py` | atomic_json, build_master, cache_identity, cue_window, emit (+27) |

## Entry Points

Start here when exploring this area:

- **`atomic_json`** (Function) — `internal/tts/worker.py:95`
- **`build_master`** (Function) — `internal/tts/worker.py:672`
- **`cache_identity`** (Function) — `internal/tts/worker.py:124`
- **`cue_window`** (Function) — `internal/tts/worker.py:130`
- **`emit`** (Function) — `internal/tts/worker.py:56`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `atomic_json` | Function | `internal/tts/worker.py` | 95 |
| `build_master` | Function | `internal/tts/worker.py` | 672 |
| `cache_identity` | Function | `internal/tts/worker.py` | 124 |
| `cue_window` | Function | `internal/tts/worker.py` | 130 |
| `emit` | Function | `internal/tts/worker.py` | 56 |
| `group_plan_identity` | Function | `internal/tts/worker.py` | 387 |
| `load_clip` | Function | `internal/tts/worker.py` | 208 |
| `load_group_plan` | Function | `internal/tts/worker.py` | 393 |
| `main` | Function | `internal/tts/worker.py` | 725 |
| `emit_attempt` | Function | `internal/tts/worker.py` | 765 |
| `on_group_attempt` | Function | `internal/tts/worker.py` | 841 |
| `resolve_individual` | Function | `internal/tts/worker.py` | 770 |
| `on_attempt` | Function | `internal/tts/worker.py` | 791 |
| `minimum_speech_frames` | Function | `internal/tts/worker.py` | 74 |
| `normalize_vietnamese_text` | Function | `internal/tts/worker.py` | 60 |
| `sentence_groups` | Function | `internal/tts/worker.py` | 365 |
| `sha256` | Function | `internal/tts/worker.py` | 87 |
| `srt_fallback_cue` | Function | `internal/tts/worker.py` | 627 |
| `synthesize_sentence_group` | Function | `internal/tts/worker.py` | 419 |
| `validate_manifest` | Function | `internal/tts/worker.py` | 639 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `Resolve_individual → Needs_rate_review` | cross_community | 4 |
| `Resolve_individual → Padding_budget` | cross_community | 4 |
| `Main → Cue_window` | intra_community | 3 |
| `Resolve_individual → Read_wav` | cross_community | 3 |
| `Resolve_individual → Sha256` | intra_community | 3 |
| `Resolve_individual → Cue_window` | intra_community | 3 |

## How to Explore

1. `context({name: "atomic_json"})` — see callers and callees
2. `query({search_query: "tts"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
