#!/usr/bin/env python3
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import hashlib
import sys

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
FINGERPRINT = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorSubtitleSourceFingerprint.cs"
STORE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs"
EDITOR = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs"
CUE = ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs"


def fail(message: str) -> None:
    print("FAIL:", message, file=sys.stderr)
    raise SystemExit(1)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def method_body(source: str, signature: str) -> str:
    start = source.find(signature)
    require(start >= 0, f"PROJECT-06 missing method: {signature}")
    brace = source.find("{", start)
    require(brace >= 0, f"PROJECT-06 method has no body: {signature}")
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace:index + 1]
    fail(f"PROJECT-06 unterminated method: {signature}")
    return ""


def verify_source(fingerprint: str, store: str, editor: str, cue: str) -> None:
    for token in (
        "SourceFingerprintMatchesCurrent(EditorSubtitleSource? source)",
        "source.Path, source.Size, source.LastWriteUtcTicks, source.Sha256",
        "before.Length != expectedSize",
        "before.LastWriteTimeUtc.Ticks != expectedLastWriteUtcTicks",
        "SHA256.HashData(stream)",
        "after.Length == expectedSize",
        "after.LastWriteTimeUtc.Ticks == expectedLastWriteUtcTicks",
        "string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)",
    ):
        require(token in fingerprint, f"PROJECT-06 fingerprint contract lost: {token}")

    normalize = method_body(store, "private static EditorSubtitleProject? NormalizeSubtitle(")
    require("EditorSubtitleDocument.SourceFingerprintMatchesCurrent" in normalize,
            "PROJECT-06 persisted Subtitle must use the authoritative SRT fingerprint check")
    require("SourceSize" in normalize and "SourceLastWriteUtcTicks" in normalize and "SourceSha256" in normalize,
            "PROJECT-06 persisted Subtitle fingerprint must include size/mtime/SHA")
    require(store.count("VoiceOverrides = subtitle is null ? null : NormalizeVoiceOverrides") == 2,
            "PROJECT-06 load/save must both clear cue-keyed VoiceOverrides when Subtitle is invalid")
    require(store.count("Tts = subtitle is null ? null : NormalizeTts") >= 2,
            "PROJECT-06 load/save must continue clearing TTS when Subtitle is invalid")

    require("private bool CurrentSubtitleFingerprintMatches()" in editor,
            "PROJECT-06 Editor must have one current-SRT fingerprint owner")
    ensure = method_body(editor, "private void EnsureCurrentSubtitleFingerprint()")
    require("CurrentSubtitleFingerprintMatches()" in ensure and "SRT nguồn đã thay đổi ngoài ứng dụng" in ensure,
            "PROJECT-06 stale SRT guard must explain that the source changed")

    request = method_body(editor, "private VideoEditRequest CurrentEditRequest(")
    require("if (subtitle is not null || _voiceTrack is not null) EnsureCurrentSubtitleFingerprint();" in request,
            "PROJECT-06 processed Preview/Export using subtitle or voice must reject stale SRT")

    imported = method_body(editor, "private async Task ImportSubtitleAsync()")
    require("var subtitleSourceChanged = _subtitleSource is not null" in imported,
            "PROJECT-06 reimport must identify a replacement SRT")
    require("VoiceOverrides = subtitleSourceChanged ? null : _project.VoiceOverrides" in imported,
            "PROJECT-06 replacement SRT must invalidate cue-keyed voice overrides")
    require("_voiceTrack = null;" in imported and "Tts = null" in imported,
            "PROJECT-06 replacement/reimport must invalidate rendered voice/TTS")

    generate_tts = method_body(editor, "private async void GenerateTts_Click(")
    require("if (!CurrentSubtitleFingerprintMatches())" in generate_tts,
            "PROJECT-06 TTS must be blocked before starting from a stale SRT")
    poll_tts = method_body(editor, "private async Task PollTtsJobAsync()")
    result_index = poll_tts.find("if (snapshot.Result is EditorTtsResult result")
    guard_index = poll_tts.find("EnsureCurrentSubtitleFingerprint();", result_index)
    apply_index = poll_tts.find("_voiceTrack = result.VoiceTrack;", result_index)
    require(0 <= result_index < guard_index < apply_index,
            "PROJECT-06 TTS completion must recheck SRT before applying a long-running result")

    karaoke = method_body(editor, "private async void SaveKaraokeAss_Click(")
    require("if (!CurrentSubtitleFingerprintMatches())" in karaoke,
            "PROJECT-06 karaoke output must not be written from stale SRT state")
    open_output = method_body(editor, "private async void OpenTranslatedSrt_Click(")
    require("!CurrentSubtitleFingerprintMatches()" in open_output,
            "PROJECT-06 stale translated output must be locked until SRT is reselected")

    save_cue = method_body(cue, "private async Task SaveCurrentSubtitleCueAsync()")
    require("EnsureCurrentSubtitleFingerprint();" in save_cue,
            "PROJECT-06 manual cue save must refuse a stale SRT source")

    translate_all = method_body(cue, "private async Task TranslateAllWithManualStateAsync()")
    completed = translate_all.find("if (snapshot.Result is not EditorTranslationResult result)")
    guard = translate_all.find("EnsureCurrentSubtitleFingerprint();", completed)
    merge = translate_all.find("var merged = result.Cues.Select", completed)
    require(0 <= completed < guard < merge,
            "PROJECT-06 full translation completion must recheck SRT before merging result")

    retranslate = method_body(cue, "private async Task RetranslateSelectedCueAsync()")
    completed = retranslate.find("if (snapshot.Result is not EditorTranslationResult result")
    guard = retranslate.find("EnsureCurrentSubtitleFingerprint();", completed)
    apply = retranslate.find("var translated = result.Cues[0].VietnameseText;", completed)
    require(0 <= completed < guard < apply,
            "PROJECT-06 single-cue translation completion must recheck SRT before applying result")


@dataclass(frozen=True)
class SrtFingerprint:
    path: str
    size: int
    ticks: int
    sha256: str


def fingerprint(path: str, payload: bytes, ticks: int) -> SrtFingerprint:
    return SrtFingerprint(path, len(payload), ticks, hashlib.sha256(payload).hexdigest())


def same(left: SrtFingerprint, right: SrtFingerprint) -> bool:
    return (
        left.path.upper() == right.path.upper()
        and left.size == right.size
        and left.ticks == right.ticks
        and left.sha256.lower() == right.sha256.lower()
    )


def verify_fixture() -> None:
    old = fingerprint("C:/subs/movie.srt", b"1\n00:00:00,000 --> 00:00:01,000\nold\n", 100)
    unchanged = fingerprint("c:/SUBS/movie.srt", b"1\n00:00:00,000 --> 00:00:01,000\nold\n", 100)
    changed_text = fingerprint("C:/subs/movie.srt", b"1\n00:00:00,000 --> 00:00:01,000\nnew\n", 101)
    same_size_changed = fingerprint("C:/subs/movie.srt", b"1\n00:00:00,000 --> 00:00:01,000\nNEW\n", 102)

    require(same(old, unchanged), "PROJECT-06 unchanged SRT must keep its translation/voice state")
    require(not same(old, changed_text), "PROJECT-06 changed SRT content must invalidate the old state")
    require(not same(old, same_size_changed), "PROJECT-06 same-size replacement must still be detected by SHA/mtime")

    state = {
        "blur": "keep",
        "audio": "keep",
        "images": "keep",
        "speech": "keep",
        "subtitle": "old-vietsub",
        "tts": "old-voice",
        "voice_overrides": "old-cue-map",
    }
    if not same(old, changed_text):
        state["subtitle"] = None
        state["tts"] = None
        state["voice_overrides"] = None
    require(state["blur"] == "keep" and state["audio"] == "keep" and state["images"] == "keep" and state["speech"] == "keep",
            "PROJECT-06 must not reset video-derived state when only SRT changes")
    require(state["subtitle"] is None and state["tts"] is None and state["voice_overrides"] is None,
            "PROJECT-06 must invalidate SRT-derived translation/voice state")

    # Long job starts on old source, source changes before completion: result must not apply.
    job_started_from = old
    source_at_completion = changed_text
    applied = same(job_started_from, source_at_completion)
    require(not applied, "PROJECT-06 translation/TTS result must be discarded when SRT changes mid-job")


if all(path.exists() for path in (FINGERPRINT, STORE, EDITOR, CUE)):
    verify_source(
        FINGERPRINT.read_text(encoding="utf-8"),
        STORE.read_text(encoding="utf-8"),
        EDITOR.read_text(encoding="utf-8"),
        CUE.read_text(encoding="utf-8"),
    )

verify_fixture()
print("PASS: PROJECT-06 external SRT source replacement contract is locked")
