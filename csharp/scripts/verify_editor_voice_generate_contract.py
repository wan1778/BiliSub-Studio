#!/usr/bin/env python3
"""Source contract for the VieNeu whole-cue route; real audio is tested separately."""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
def read(path):
    return (ROOT / path).read_text(encoding="utf-8")

installer = read("csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs")
catalog = read("csharp/src/BiliSubStudio.Core/Editor/TtsVoiceCatalog.cs")
service = read("csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs")
timing = read("csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.Timing.cs")
worker = read("internal/vieneu/worker.py")
editor = read("csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs")
xaml = read("csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml")
sample = read("csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs")
application = read("csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs")

def require(condition, message):
    if not condition:
        raise SystemExit("FAIL: " + message)

for forbidden in ("Kokoro", "synth_sine", "DummyModelBytes", "MirrorModelUrl", "nghi-tts", "nghimestudio/nghitts"):
    require(forbidden not in installer + service + worker, "retired/unverified synthesis returned: " + forbidden)
require('Engine = "vieneu-tts"' in installer
        and 'EngineVersion = "vieneu-v3-turbo-1"' in installer
        and 'TimingAlgorithm = "whole-cue-vieneu-natural-first-v1"' in installer
        and "Voice = TtsVoiceCatalog.DefaultVoice" in installer,
        "installer must pin the VieNeu engine tuple and catalog default voice")
require("PrepareAsync(AppJob job, string voice, double progressCeiling = 98)" in installer
        and "EnsureVieneuModelsAsync(voice, job, Progress)" in installer
        and "manifest.EngineVersion == EngineVersion" in installer,
        "installer must prepare the pinned VieNeu runtime and backbone")
require('DefaultVoice = "vieneu_ngoc_huyen"' in catalog and "VieneuPresetLabels" in catalog
        and len(re.findall(r'"[^"]+"', catalog.split("VieneuPresetLabels", 1)[1].split("});", 1)[0])) == 25,
        "voice catalog must expose exactly 25 reviewed VieNeu presets")
dropdown = re.search(r'<ComboBox x:Name="VoiceModelBox"[^>]*?(/>|>)', xaml, re.S).group()
require('x:Name="VoiceModelBox"' in dropdown and re.findall(r'Tag="([^"]+)"', dropdown) == [],
        "voice dropdown must not hardcode catalog items")
require("TtsVoiceCatalog.VoiceOptions" in sample
        and "VoiceModelBox.Items.Add(new ComboBoxItem { Tag = tag, Content = content });" in sample
        and "EnsureVoiceModelItems" in sample,
        "dropdown contents must come from the single catalog source")
require("TtsVoiceCatalog.DefaultVoice" in sample and "VoiceModelBox.SelectedIndex = defaultIndex" in sample,
        "dropdown default must be the reviewed catalog default voice")
require('VoiceEngineChipText.Text = "VieNeu-TTS";' in sample
        and 'VoiceRuntimeChipText.Text = "GPU";' in sample,
        "voice chips must describe the VieNeu GPU engine only")

for marker in ('ENGINE = "vieneu-tts"', 'ENGINE_VERSION = "vieneu-v3-turbo-1"',
               'TIMING_ALGORITHM = "whole-cue-vieneu-natural-first-v1"',
               'FIT_METHOD = "vieneu-natural"', 'ATEMPO_METHOD = "vieneu-atempo"',
               "class VieneuVoice", "def synthesize_cue(", "def synthesize_cue_batch(",
               "def cache_identity(", "def validate_manifest(", "def build_master(",
               "voice-master.flac", "overflow_budget_frames", "clip_overlap_pairs",
               "def normalize_vietnamese_text(", "def sentence_groups("):
    require(marker in worker, "VieNeu worker contract missing " + marker)
require("atempo=" in worker and "MAX_TEMPO_ATTEMPTS = 6" in worker
        and all(value not in worker for value in ("asetrate=", "rubberband=", "atrim=")),
        "worker must use bounded pitch-preserving tempo and never cut speech")

require("EditorVoiceCuePlanner.Build(request.Subtitle.Cues)" in service,
        "proven OCR flicker duplicates must be removed from the voice plan")
require("EditorVoiceCuePlanner.RemoveUnspeakable(" in service
        and "VietnameseTtsTextNormalizer.HasSpeakableUnits" in service,
        "punctuation, symbols and emoji-only cues must be removed from voice without changing SRT")
require("RemoveUnvoicedFlicker" not in service
        and "var cues = voiceCues.Select(cue => BuildWholeCue" in service,
        "ASR must not remove speakable SRT cues from the voice plan")
whole = service.split("internal static TtsCueManifest BuildWholeCue", 1)[1].split("private async Task<EditorTtsResult> GenerateCuesAsync", 1)[0]
require('cue.Start, cue.End, voice, text, cue.Start, cue.End, "srt-fallback"' in whole
        and "timing.SpeechStart" not in whole and "timing.SpeechEnd" not in whole and "Pauses" not in whole,
        "C# manifest must keep SRT as the only timing owner and retain complete text")
require("GenerateCuesAsync" in service and "GenerateSampleAsync" in service,
        "sample and project generation must share the production worker")
require("_application.StartEditorTtsSample(selectedVoice)" in sample and "voice-sample-analysis" not in sample,
        "sample must not fabricate a Whisper analysis/source")
require('Jobs.Create("editor-tts", cleanupAwareCancel: true)' in application,
        "TTS jobs must wait for cleanup")
require('"-I", "-X", "utf8", runtime.Worker' in service, "isolated Python requires explicit UTF-8")
for marker in ("SamePath(reportedResult, resultPath)", "SamePath(result.Master.Path, expectedMaster)",
               "result.Cues.Count != expectedCues.Count", "result.VoiceRevision != LocalTtsInstaller.VoiceRevision",
               "await HashAsync(expectedMaster, cancellationToken) != result.Master.Sha256",
               'GetInt(audioStreams[0], "channels") != 1', "Math.Abs(actualDuration - duration) > .05"):
    require(marker in service, "result validation missing " + marker)
require(("_voiceTrack = result.VoiceTrack;" in editor
        or "_voiceTrack = result.VoiceTrack with { Gain = _voiceGain };" in editor)
        and "QueuePreviewRefresh();" in editor,
        "validated master must reach processed preview")
require("ValidateNativeSynthesis(cue, targetFrames, naturalSample, expectedSampleRate, index)" in service
        and "ValidateSentenceGroupWindows(result.Cues, expectedCues, expectedSampleRate)" in service,
        "result validation must apply the VieNeu timing validator")
for marker in (
               'cue.FitMethod is not ("vieneu-natural" or "vieneu-atempo")',
               "VieneuMicroMaxTempoFactor = 3.01", "VieneuMinTempoFactor = 1.0 / 1.4"):
    require(marker in timing, "VieNeu timing validator missing " + marker)
progress_panel = xaml.split('x:Name="VoiceProgressContainer"', 1)[1].split("</StackPanel>", 1)[0]
progress_row = re.search(r'<Grid ColumnSpacing="8">.*?</Grid>', progress_panel, re.S)
require(progress_row is not None and 'x:Name="VoiceProgressPercent"' in progress_row.group()
        and 'x:Name="VoiceProgress"' in progress_row.group() and 'Text="0%"' in progress_row.group()
        and 'Grid.Column="1"' in progress_row.group(),
        "Voice/ASR needs a visible percentage alongside the existing progress bar")
require("VoiceProgress.RegisterPropertyChangedCallback(ProgressBar.ValueProperty, (_, _) => UpdateVoiceProgressPercent());" in editor,
        "percentage must follow the shared bar for ASR/TTS/sample, including resets")
progress_formatter = editor.split("private void UpdateVoiceProgressPercent()", 1)[1].split("private void SetInspectorMode", 1)[0]
require("double.IsFinite(VoiceProgress.Value)" in progress_formatter
        and "Math.Clamp(VoiceProgress.Value, 0, 100)" in progress_formatter
        and 'VoiceProgressPercent.Text = $"{Math.Floor(value * 10) / 10:0.#}%";' in progress_formatter,
        "percentage must be bounded and must not round incomplete progress up to 100%")
require("VoiceProgress.Value = snapshot.Progress;" in editor and "VoiceProgress.Value = snapshot.Progress;" in sample,
        "percent must remain based on actual job progress, not a synthetic timer")
print("PASS: VieNeu real-model whole-cue source contract (not a listening/quality result)")
