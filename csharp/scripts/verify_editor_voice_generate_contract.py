#!/usr/bin/env python3
"""Source contract for the NGHI whole-cue route; real audio is tested separately."""
import ast
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
def read(path):
    return (ROOT / path).read_text(encoding="utf-8")

installer = read("csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs")
service = read("csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs")
worker = read("internal/tts/worker.py")
editor = read("csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs")
xaml = read("csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml")
sample = read("csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs")
application = read("csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs")

def require(condition, message):
    if not condition:
        raise SystemExit("FAIL: " + message)

for forbidden in ("Kokoro", "synth_sine", "DummyModelBytes", "MirrorModelUrl"):
    require(forbidden not in installer + service + worker, "unverified synthesis/fallback: " + forbidden)
require('Voice = "ngoc_huyen"' in installer, "default must be verified Ngọc Huyền")
dropdown = re.search(r'<ComboBox x:Name="VoiceModelBox".*?</ComboBox>', xaml, re.S).group()
require('SelectedIndex="0"' in dropdown and re.findall(r'Tag="([^"]+)"', dropdown) == ["ngoc_huyen"],
        "dropdown may expose only a reviewed model/config pair")
for digest in ("2140977786d76d834736c059dacfa553d4931dac2b2c7aaaea438bb2aa9da697",
               "971f57f8d504223fee5b40d664f503cf769baf7db21f7d2ae0554a75d07de2f8"):
    require(digest in installer and digest in worker, "model/config content pins drifted")
require("new FileInfo(path).Length == file.Size" in installer and
        "await HashAsync(path, cancellationToken) == file.Sha256" in installer,
        "file readiness must compare size and reviewed SHA, not stamp downloaded bytes blindly")
require("MatchesAsync(partial, file" in installer and
        installer.index("MatchesAsync(partial, file") < installer.index("File.Move(partial, destination"),
        "download must verify before promotion")
require('JsonSerializer.Deserialize<RuntimeInstallManifest>(File.ReadAllText(RuntimeManifest), Json)' in installer,
        "runtime manifest must read the same JSON naming policy it writes")

tree = ast.parse(worker)
calls = [node for node in ast.walk(tree) if isinstance(node, ast.Call)]
require(sum(isinstance(c.func, ast.Attribute) and isinstance(c.func.value, ast.Name)
            and c.func.value.id == "PiperVoice" and c.func.attr == "load" for c in calls) == 1,
        "worker must contain exactly one Piper model load")
require(sum(isinstance(c.func, ast.Attribute) and c.func.attr == "synthesize" for c in calls) == 1,
        "worker must use one whole-cue synthesize API call")
main = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "main")
loop = next(node for node in main.body if isinstance(node, ast.For) and ast.unparse(node.target) == "(index, cue)")
require(not any(isinstance(node, ast.Attribute) and node.attr == "load" for node in ast.walk(loop)),
        "model load must be outside the cue loop")
require('normalizer.normalize(cue["text"])' in worker and 'cue.get("groups")' not in worker,
        "worker must normalize and speak complete cue text")
whole = service.split("internal static TtsCueManifest BuildWholeCue", 1)[1].split("private static string ResolveVoice", 1)[0]
require("cue.Start, cue.End, voice, text" in whole and "Pauses" not in whole,
        "C# manifest must retain exact SRT interval and complete text")
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
require("_voiceTrack = result.VoiceTrack;" in editor and "QueuePreviewRefresh();" in editor,
        "validated master must reach processed preview")
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
print("PASS: NGHI real-model whole-cue source contract (not a listening/quality result)")
