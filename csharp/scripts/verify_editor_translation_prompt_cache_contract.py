from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SERVICE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs"
MEMORY = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.TranslationMemory.cs"
SKILL = ROOT / "csharp/src/BiliSubStudio.Core/Editor/TranslationSkillBundle.cs"
service = SERVICE.read_text(encoding="utf-8")
memory = MEMORY.read_text(encoding="utf-8")
skill = SKILL.read_text(encoding="utf-8")

translate_start = service.index('public async Task<EditorTranslationResult> TranslateAsync')
translate_end = service.index('internal static int RecommendedGpuLayers', translate_start)
translate = service[translate_start:translate_end]

required_service = [
    '["cache_prompt"] = true',
    'source.Skip(Math.Max(0, firstIndex - 2)).Take(batch.Length + 4).ToArray()',
    'var promptMemory = BuildTranslationPromptMemory(batch, context, checkpoint);',
    'var prompt = BuildMemoryTranslationPrompt(batch, context, promptMemory);',
    'MemoryTranslationSchema, 1024, job.CancellationToken, job',
    'ValidateMemoryBatch(root, batch, context, promptMemory, checkpoint)',
    'MergeTranslationMemory(checkpoint, validated)',
]
for marker in required_service:
    if marker not in service:
        raise SystemExit(f"FAIL: LIVE-SRT locked-memory marker missing: {marker}")

required_memory = [
    'Skill.MatchLockedTerms(context.Select(x => x.SourceText), 24)',
    'Skill.MatchLockedTerms(target.Select(x => x.SourceText), 16)',
    'Skill.BuildReferenceInstructions(context.Select(x => x.SourceText), 900)',
    'Bắt buộc đúng TERMS, NAMES, RELATION',
    '陈长安=Trần Trường An',
    'TERMS: {{terms}}',
    'NAMES: {{names}}',
    'RELATION: {{relations}}',
    'Nếu CONTEXT/TARGET xác nhận tên riêng',
]
for marker in required_memory:
    if marker not in memory:
        raise SystemExit(f"FAIL: compact locked-memory prompt marker missing: {marker}")

for forbidden in (
    '34_000',
    'QUY TẮC DỊCH BẮT BUỘC CHO TỪNG CUE:',
    'TỰ KIỂM TRA THẦM TRƯỚC KHI TRẢ JSON:',
    'Ví dụ phong cách ngắn:',
):
    if forbidden in memory:
        raise SystemExit(f"FAIL: locked-memory translation prompt is verbose: {forbidden}")

if 'const int analysisPages = 0;' not in translate:
    raise SystemExit('FAIL: whole-SRT pre-analysis must stay disabled before direct translation')
if 'checkpoint = checkpoint with { Bible = string.Empty, AnalysisPagesCompleted = 0, Translations = recovered };' not in translate:
    raise SystemExit('FAIL: direct runtime must not carry a large pre-analysis bible into every cue prompt')
if 'BuildTranslationPrompt(batch, context, bible)' in translate:
    raise SystemExit('FAIL: runtime still calls the old generic per-cue prompt instead of locked memory prompt')

required_skill = [
    'public string BuildCoreInstructions()',
    'public string BuildReferenceInstructions(IEnumerable<string> sourceTexts, int maxCharacters, int initialCharacters = 0)',
    'private const string CompactCultivationProfile = "SKILL TU TIÊN:',
    '陈长安=Trần Trường An',
    'private static readonly (string Source, string Vietnamese)[] LockedCultivationTerms',
    'public IReadOnlyList<KeyValuePair<string, string>> MatchLockedTerms(IEnumerable<string> sourceTexts, int maxItems = 24)',
    'OrderByDescending(x => x.Source.Length)',
    'if (!Relevant(section, source)) continue;',
]
for marker in required_skill:
    if marker not in skill:
        raise SystemExit(f"FAIL: compact cultivation skill/glossary marker missing: {marker}")

if 'section.StartsWith("#", StringComparison.Ordinal) && section.Length < 800' in skill:
    raise SystemExit('FAIL: generic markdown headings can still consume the compact skill budget before matched cultivation terms')

print("PASS: per-cue prompt stays compact while locking matched cultivation terms, names and relation memory")
