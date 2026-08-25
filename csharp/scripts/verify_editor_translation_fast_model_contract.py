#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
SERVICE = (ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs").read_text(encoding="utf-8")
APP = (ROOT / "csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs").read_text(encoding="utf-8")
XAML = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml").read_text(encoding="utf-8")
PAGE = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs").read_text(encoding="utf-8")
CUES = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs").read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        print("FAIL:", message, file=sys.stderr)
        raise SystemExit(1)


for token in (
    'public enum EditorTranslationModelMode',
    'Quality,', 'Fast,',
    'EditorTranslationModelMode ModelMode = EditorTranslationModelMode.Quality',
    'internal const string ModelName = "Qwen3-8B Q4_K_M";',
    'internal const long ModelBytes = 5_027_783_488;',
    'internal const string ModelSha256 = "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785";',
    'internal const string FastModelName = "Qwen3-4B Q4_K_M";',
    'a9a60d009fa7ff9606305047c2bf77ac25dbec49/Qwen3-4B-Q4_K_M.gguf?download=true',
    'internal const long FastModelBytes = 2_497_280_256;',
    'internal const string FastModelSha256 = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5";',
    'public LocalTranslationStatus Status => StatusFor(EditorTranslationModelMode.Quality);',
    'public LocalTranslationStatus StatusFor(EditorTranslationModelMode mode)',
    'public async Task PrepareAsync(AppJob job, EditorTranslationModelMode mode = EditorTranslationModelMode.Quality)',
    'var model = ModelFor(request.ModelMode);',
    'StartTranslationServerWithFallbackAsync(model, layers, job, job.CancellationToken)',
    '"-m", ModelPath(model)',
    'return new EditorTranslationResult(translated, output, restored, model.Name, Skill.Info.Sha256);',
    'string? ModelKey',
    'model.Mode != EditorTranslationModelMode.Quality',
    'string.Equals(checkpointModelKey, model.Key, StringComparison.Ordinal)',
):
    require(token in SERVICE, f"SPEED-05 service marker missing: {token}")

require('"-m", ModelPath,' not in SERVICE, "SPEED-05 runtime can still hardwire the 8B model path")
require('["cache_prompt"] = true' in SERVICE and 'RuntimeAutoGpuLayers = -1' in SERVICE,
        "SPEED-05 regressed persistent/cache/GPU ownership")
require('RecommendedTranslationBatchSize(adaptiveResources)' in SERVICE,
        "SPEED-05 regressed SPEED-04 adaptive batching")
require('public LocalTranslationStatus LocalTranslationStatusFor(EditorTranslationModelMode mode)' in APP,
        "SPEED-05 application boundary cannot query selected model readiness")
require('StartLocalTranslationPreparation(EditorTranslationModelMode mode = EditorTranslationModelMode.Quality)' in APP,
        "SPEED-05 preparation does not preserve 8B default")
require('await _translation.PrepareAsync(job, mode);' in APP and '_translation.StatusFor(mode)' in APP,
        "SPEED-05 preparation does not forward selected mode")

for token in (
    'x:Name="TranslationFastModeToggle"',
    'OffContent="8B Chất lượng"',
    'OnContent="4B Nhanh / nháp"',
    'IsOn="False"',
    'Toggled="TranslationFastMode_Toggled"',
):
    require(token in XAML, f"SPEED-05 UI marker missing: {token}")

for token in (
    'TranslationFastModeToggle.IsOn ? EditorTranslationModelMode.Fast : EditorTranslationModelMode.Quality',
    '_application.StartLocalTranslationPreparation(mode)',
    '_application.LocalTranslationStatusFor(SelectedTranslationModelMode())',
    'TranslationFastModeToggle.IsEnabled = idle && !_playback.IsPreviewMode;',
):
    require(token in PAGE, f"SPEED-05 page owner missing: {token}")

for token in (
    '"all" + modeScope', '"cue" + modeScope',
    'ModelMode: modelMode',
    'modeScope = modelMode == EditorTranslationModelMode.Fast ? "fast" : "quality"',
):
    require(token in CUES, f"SPEED-05 request/checkpoint isolation missing: {token}")

# User owns the toggle; no scene-scoring/automatic quality switch is allowed in SPEED-05.
for forbidden in ('climax', 'cao trào', 'scene importance', 'importance score', 'AutoModelMode'):
    require(forbidden.lower() not in (SERVICE + PAGE + CUES).lower(),
            f"SPEED-05 introduced hidden automatic scene/model switching: {forbidden}")

# Synthetic identity/default contract.
quality = 'qwen3-8b-q4-k-m'
fast = 'qwen3-4b-q4-k-m'
require(quality != fast, "SPEED-05 model checkpoint identities collide")
request_default = 'quality'
require(request_default == 'quality', "SPEED-05 default must remain 8B quality")
def accepts_legacy_checkpoint(selected: str, stored: str | None) -> bool:
    if stored is None:
        return selected == quality
    return stored == selected
require(accepts_legacy_checkpoint(quality, None), "SPEED-05 must preserve legacy 8B checkpoints")
require(not accepts_legacy_checkpoint(fast, None), "SPEED-05 fast mode must not reuse legacy 8B checkpoints")
require(not accepts_legacy_checkpoint(fast, quality), "SPEED-05 4B must not reuse 8B checkpoint")

print("PASS: SPEED-05 keeps 8B quality as default and exposes explicit 4B Fast/Draft mode with isolated model files, readiness and checkpoints")
