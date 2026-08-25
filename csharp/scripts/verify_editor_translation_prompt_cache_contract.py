from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SERVICE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs"
SKILL = ROOT / "csharp/src/BiliSubStudio.Core/Editor/TranslationSkillBundle.cs"
service = SERVICE.read_text(encoding="utf-8")
skill = SKILL.read_text(encoding="utf-8")

start = service.index('private string BuildTranslationPrompt')
end = service.index('private static void ValidateTranslationText', start)
prompt = service[start:end]
translate_start = service.index('public async Task<EditorTranslationResult> TranslateAsync')
translate_end = service.index('internal static int RecommendedGpuLayers', translate_start)
translate = service[translate_start:translate_end]

required_service = [
    '["cache_prompt"] = true',
    'var relevantSkill = Skill.BuildReferenceInstructions(context.Select(x => x.SourceText), 2_000);',
    'if (compactBible.Length > 1_600) compactBible = compactBible[..1_600];',
    '{{relevantSkill}}', '{{contextJson}}', '{{targetJson}}',
    'Dịch phụ đề Trung → Việt. Chỉ dịch TARGET và chỉ trả JSON.',
    'source.Skip(Math.Max(0, firstIndex - 2)).Take(batch.Length + 4).ToArray()',
    'TranslationSchema, 1024, job.CancellationToken, job',
]
for marker in required_service:
    if marker not in service:
        raise SystemExit(f"FAIL: LIVE-SRT-02 compact prompt marker missing: {marker}")

for forbidden in (
    'var coreSkill = Skill.BuildCoreInstructions();',
    '34_000',
    'QUY TẮC DỊCH BẮT BUỘC CHO TỪNG CUE:',
    'TỰ KIỂM TRA THẦM TRƯỚC KHI TRẢ JSON:',
    'Ví dụ phong cách ngắn:',
):
    if forbidden in prompt:
        raise SystemExit(f"FAIL: LIVE-SRT-02 translation prompt is still verbose: {forbidden}")

if 'const int analysisPages = 0;' not in translate:
    raise SystemExit('FAIL: LIVE-SRT-02 whole-SRT bible analysis still runs before direct translation')
if 'checkpoint = checkpoint with { Bible = string.Empty, AnalysisPagesCompleted = 0, Translations = recovered };' not in translate:
    raise SystemExit('FAIL: LIVE-SRT-02 direct runtime must not carry a large pre-analysis bible into every cue prompt')

required_skill = [
    'public string BuildCoreInstructions()',
    'public string BuildReferenceInstructions(IEnumerable<string> sourceTexts, int maxCharacters, int initialCharacters = 0)',
    'if (initialCharacters + selected.Length + section.Length + 80 > maxCharacters) break;',
]
for marker in required_skill:
    if marker not in skill:
        raise SystemExit(f"FAIL: LIVE-SRT-02 reviewed skill reader marker missing: {marker}")

# The runtime prompt still consults the reviewed skill bundle, but only the cue-relevant
# reference slice is attached. The full SKILL.md/full-film bible are no longer repeated.
if 'Skill.BuildReferenceInstructions(context.Select(x => x.SourceText), 2_000)' not in prompt:
    raise SystemExit('FAIL: LIVE-SRT-02 compact prompt stopped consulting the reviewed translation skill')

print("PASS: LIVE-SRT-02 keeps per-cue translation prompts compact while retaining a bounded relevant slice of the reviewed skill")
