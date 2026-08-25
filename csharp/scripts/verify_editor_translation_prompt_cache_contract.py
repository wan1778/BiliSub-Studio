from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SERVICE = ROOT / "csharp/src/BiliSubStudio.Core/Editor/LocalSubtitleTranslationService.cs"
SKILL = ROOT / "csharp/src/BiliSubStudio.Core/Editor/TranslationSkillBundle.cs"
service = SERVICE.read_text(encoding="utf-8")
skill = SKILL.read_text(encoding="utf-8")

required_service = [
    '["cache_prompt"] = true',
    'var coreSkill = Skill.BuildCoreInstructions();',
    'var relevantSkill = Skill.BuildReferenceInstructions(context.Select(x => x.SourceText), 34_000, coreSkill.Length);',
    '{{coreSkill}}', '{{bible}}', '{{relevantSkill}}', '{{contextJson}}', '{{targetJson}}',
]
for marker in required_service:
    if marker not in service:
        raise SystemExit(f"FAIL: SPEED-03 service marker missing: {marker}")
if 'var skill = Skill.BuildInstructions(context.Select(x => x.SourceText), 34_000);' in service:
    raise SystemExit("FAIL: SPEED-03 translation batches still place context-varying skill before the cacheable bible")

start = service.index('private string BuildTranslationPrompt')
end = service.index('private static void ValidateTranslationText', start)
prompt = service[start:end]
order = [
    prompt.index('{{coreSkill}}'), prompt.index('HỒ SƠ PHIM ĐÃ KHÓA:'), prompt.index('{{bible}}'),
    prompt.index('{{relevantSkill}}'), prompt.index('NGỮ CẢNH LÂN CẬN'), prompt.index('{{contextJson}}'),
    prompt.index('TARGET PHẢI DỊCH:'), prompt.index('{{targetJson}}'),
]
if order != sorted(order):
    raise SystemExit("FAIL: SPEED-03 stable core+bible prefix is not ahead of batch-varying references/context/target")

required_skill = [
    'public string BuildCoreInstructions()',
    'public string BuildReferenceInstructions(IEnumerable<string> sourceTexts, int maxCharacters, int initialCharacters = 0)',
    'if (initialCharacters + selected.Length + section.Length + 80 > maxCharacters) break;',
    'var core = BuildCoreInstructions();',
    'return core + BuildReferenceInstructions(sourceTexts, maxCharacters, core.Length);',
]
for marker in required_skill:
    if marker not in skill:
        raise SystemExit(f"FAIL: SPEED-03 skill decomposition marker missing: {marker}")

core = "CORE-SKILL\n"
bible = "FILM-BIBLE: Van Tieu Tong / Lam Phong\n"
def compose(refs: str, context: str, target: str) -> str:
    return "TRANSLATE\n" + core + "FILM-BIBLE\n" + bible + refs + context + target
p1 = compose("REF-A\n", "CTX-A\n", "TARGET-A\n")
p2 = compose("REF-B\n", "CTX-B\n", "TARGET-B\n")
expected_prefix = "TRANSLATE\n" + core + "FILM-BIBLE\n" + bible
if not (p1.startswith(expected_prefix) and p2.startswith(expected_prefix)):
    raise SystemExit("FAIL: SPEED-03 synthetic stable-prefix invariant failed")

print("PASS: SPEED-03 keeps core skill + finalized film bible as the shared KV-cache prefix while preserving batch-relevant references")
