# G1 Classic contracts - frozen gate inventory

Scope: single-video autosave / reopen / preview / export for the classic Editor.
Source freeze: no classic Editor source was modified for this gate; only stale
static verifier tokens were refreshed to the current reviewed implementation.

Result (2026-09-06): 35/35 PASS with `python -B`.

## Autosave / project / reopen (10)

- `verify_editor_audio_reopen_contract.py`
- `verify_editor_image_reopen_project_contract.py`
- `verify_editor_project_app_reopen_contract.py`
- `verify_editor_project_autosave_owner_contract.py`
- `verify_editor_project_corrupt_contract.py`
- `verify_editor_project_debounce_contract.py`
- `verify_editor_project_output_target_contract.py`
- `verify_editor_project_source_change_contract.py`
- `verify_editor_project_srt_source_change_contract.py`
- `verify_editor_project_tab_reopen_contract.py`

## Preview (9)

- `verify_editor_audio_preview_export_contract.py`
- `verify_editor_image_preview_export_contract.py`
- `verify_editor_preview_duration_contract.py`
- `verify_editor_preview_unload_contract.py`
- `verify_editor_subtitle_frame_preview_contract.py`
- `verify_editor_voice_cue_preview_contract.py`
- `verify_editor_voice_preview_contract.py`
- `verify_editor_voice_preview_export_contract.py`
- `verify_editor_voice_reopen_contract.py`

Note: voice reopen is counted here because it locks restored-track preview state.

## Export (16)

- `verify_editor_export_audio_only_contract.py`
- `verify_editor_export_blur_only_contract.py`
- `verify_editor_export_cancel_temp_cleanup_contract.py`
- `verify_editor_export_dialog_codec_contract.py`
- `verify_editor_export_filename_validation_contract.py`
- `verify_editor_export_full_combo_contract.py`
- `verify_editor_export_image_only_contract.py`
- `verify_editor_export_no_source_overwrite_contract.py`
- `verify_editor_export_output_directory_contract.py`
- `verify_editor_export_progress_contract.py`
- `verify_editor_export_render_button_state_owner_contract.py`
- `verify_editor_export_single_entry_contract.py`
- `verify_editor_export_subtitle_blur_contract.py`
- `verify_editor_export_subtitle_only_contract.py`
- `verify_editor_export_subtitle_voice_audio_contract.py`
- `verify_editor_export_voice_only_contract.py`

The union above is 35 unique scripts. Six required refactor-aware token
refreshes without behavior weakening:

- configured FFmpeg builder (`BuildConfiguredRenderArguments`)
- sliced `renderRequest` ASS/temp ownership
- export-dialog immutable output target (`ShowExportDialogAsync`)
- strengthened `subtitleReady` + `cleanupPending`/`trimChanged` gating
- settings validation helper (`ValidateWritableDirectoryAsync`)
- image composer `graph.OutputLabel` mapping
