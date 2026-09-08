---
name: gitnexus-area-scripts
description: "Skill for the Scripts area of BiliSub-Studio. 229 symbols across 70 files."
---

# Scripts

229 symbols | 70 files | Cohesion: 99%

## When to Use

- Working with code in `csharp/`
- Understanding how reading, fail, method_body work
- Modifying scripts-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `csharp/scripts/verify_asr_hybrid_contract.py` | reading, test_adjacent_segment_jitter_merges_without_losing_words, test_deep_adjacent_segment_conflict_is_sorted_and_kept, test_jittered_anchor_is_kept_once, test_out_of_window_word_is_pinned_not_dropped (+4) |
| `csharp/scripts/verify_editor_project_debounce_contract.py` | verify_fixture, forced_flush, queue, tick, unload (+4) |
| `csharp/scripts/verify_editor_project_source_change_contract.py` | fail, method_body, require, same, verify_core (+3) |
| `csharp/scripts/verify_editor_project_app_reopen_contract.py` | fail, method_body, require, verify_source, verify_fixture (+3) |
| `csharp/scripts/verify_editor_project_autosave_owner_contract.py` | fail, method_body, require, verify_source, verify_fixture (+3) |
| `csharp/scripts/verify_editor_project_srt_source_change_contract.py` | fail, fingerprint, method_body, require, same (+2) |
| `csharp/scripts/verify_editor_project_tab_reopen_contract.py` | fail, method_body, require, verify_fixture, verify_source (+2) |
| `csharp/scripts/verify_ocr_worker_contract.py` | assert_rejected, assert_result, install_dependency_stubs, load_worker, main (+2) |
| `csharp/scripts/verify_editor_voice_duration_contract.py` | cue, test_absolute_rounding_prevents_one_sample_overrun, test_changed_whisper_window_invalidates_same_srt_cache, test_contiguous_subtitle_fragments_form_bounded_sentence_groups, test_failed_whisper_window_has_one_explicit_full_srt_fallback (+2) |
| `csharp/scripts/verify_editor_project_output_target_contract.py` | fail, method_body, require, validate_fixture, verify_fixture (+1) |

## Entry Points

Start here when exploring this area:

- **`reading`** (Function) — `csharp/scripts/verify_asr_hybrid_contract.py:14`
- **`fail`** (Function) — `csharp/scripts/verify_editor_project_source_change_contract.py:13`
- **`method_body`** (Function) — `csharp/scripts/verify_editor_project_source_change_contract.py:23`
- **`require`** (Function) — `csharp/scripts/verify_editor_project_source_change_contract.py:18`
- **`same`** (Function) — `csharp/scripts/verify_editor_project_source_change_contract.py:141`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `reading` | Function | `csharp/scripts/verify_asr_hybrid_contract.py` | 14 |
| `fail` | Function | `csharp/scripts/verify_editor_project_source_change_contract.py` | 13 |
| `method_body` | Function | `csharp/scripts/verify_editor_project_source_change_contract.py` | 23 |
| `require` | Function | `csharp/scripts/verify_editor_project_source_change_contract.py` | 18 |
| `same` | Function | `csharp/scripts/verify_editor_project_source_change_contract.py` | 141 |
| `verify_core` | Function | `csharp/scripts/verify_editor_project_source_change_contract.py` | 40 |
| `verify_editor` | Function | `csharp/scripts/verify_editor_project_source_change_contract.py` | 73 |
| `verify_fixture` | Function | `csharp/scripts/verify_editor_project_source_change_contract.py` | 152 |
| `verify_images` | Function | `csharp/scripts/verify_editor_project_source_change_contract.py` | 114 |
| `fail` | Function | `csharp/scripts/verify_editor_project_srt_source_change_contract.py` | 15 |
| `fingerprint` | Function | `csharp/scripts/verify_editor_project_srt_source_change_contract.py` | 113 |
| `method_body` | Function | `csharp/scripts/verify_editor_project_srt_source_change_contract.py` | 25 |
| `require` | Function | `csharp/scripts/verify_editor_project_srt_source_change_contract.py` | 20 |
| `same` | Function | `csharp/scripts/verify_editor_project_srt_source_change_contract.py` | 117 |
| `verify_fixture` | Function | `csharp/scripts/verify_editor_project_srt_source_change_contract.py` | 126 |
| `verify_source` | Function | `csharp/scripts/verify_editor_project_srt_source_change_contract.py` | 42 |
| `fail` | Function | `csharp/scripts/verify_editor_project_tab_reopen_contract.py` | 13 |
| `method_body` | Function | `csharp/scripts/verify_editor_project_tab_reopen_contract.py` | 23 |
| `require` | Function | `csharp/scripts/verify_editor_project_tab_reopen_contract.py` | 18 |
| `verify_fixture` | Function | `csharp/scripts/verify_editor_project_tab_reopen_contract.py` | 130 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `Main → Control_type` | intra_community | 3 |
| `Main → Extract_method` | intra_community | 3 |
| `Verify_fixture → Project_id` | intra_community | 3 |
| `Verify_fixture → Fail` | intra_community | 3 |
| `Main → Install_dependency_stubs` | intra_community | 3 |
| `Main → Assert_result` | intra_community | 3 |

## How to Explore

1. `context({name: "reading"})` — see callers and callees
2. `query({search_query: "scripts"})` — find related execution flows
3. Read key files listed above for implementation details
4. `explain({target: "<file or symbol>"})` — persisted taint findings (source→sink data flows), when indexed with `--pdg`
