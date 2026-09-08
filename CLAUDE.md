<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **BiliSub-Studio** (8238 symbols, 21097 relationships, 707 execution flows).

> Index stale? Run `node .gitnexus/run.cjs analyze --index-only` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? Bootstrap with `npx`, `bunx`, or `pnpm dlx` — e.g. `bunx gitnexus@latest analyze` (npm 11 npx crash; #1939).

## Always Do

- **MUST run impact before editing.** Use `impact({target: "symbolName", direction: "upstream"})` or `node .gitnexus/run.cjs impact "symbolName" --direction upstream --repo .`; report callers, processes, and risk. Never substitute grep for graph analysis.
- **MUST analyze graph changes before committing.** Use `detect_changes({scope: "all"})` (MCP) or `node .gitnexus/run.cjs detect-changes --scope all --repo .` (CLI fallback). `partial: true` or `truncated: true` is not a clean check — a zero means unseen, not unaffected; re-run it. For regression review: `detect_changes({scope: "compare", base_ref: "main"})` or `node .gitnexus/run.cjs detect-changes --scope compare --base-ref "main" --repo .`.
- MUST warn on HIGH/CRITICAL `risk` pre-edit; never use `riskSharedAxes` to waive a HIGH/CRITICAL `risk` warning. Compare File/symbol: MCP File omits axes; Graph-RAG expands File.
- **MUST treat `risk: UNKNOWN` as unresolved, not as low.** An empty caller set is not evidence the symbol is unused — it can also mean the callers are not resolvable by the index (plain-object property access, dynamic dispatch, cross-language calls). `impact` pairs `UNKNOWN` with a `riskNote` saying so. Confirm with a text search before treating the symbol as safe to change or delete; do not proceed on the strength of a zero.
- **MUST use `query({search_query: "concept"})` for concepts/flows, `context({name: "symbolName"})` for a named symbol, or `impact` for blast radius, on read-only callers, dependencies, imports, or execution flow.** Graph first; text search only for empty/`UNKNOWN`/literals.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method before MCP/CLI impact analysis.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis, and never read `UNKNOWN` as an all-clear — it means the walk could not answer, which is the one verdict that requires confirming by other means.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit before MCP/CLI graph change analysis.

## Resources

| Resource | Use for |
| --- | --- |
| `gitnexus://repo/BiliSub-Studio/context` | Codebase overview, check index freshness |
| `gitnexus://repo/BiliSub-Studio/clusters` | All functional areas |
| `gitnexus://repo/BiliSub-Studio/processes` | All execution flows |
| `gitnexus://repo/BiliSub-Studio/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
| --- | --- |
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus-cli/SKILL.md` |
| Work in the BiliSubStudio.Core.ContractTests area (650 symbols) | `.claude/skills/gitnexus-area-bilisubstudio-core-contracttests/SKILL.md` |
| Work in the Pages area (559 symbols) | `.claude/skills/gitnexus-area-pages/SKILL.md` |
| Work in the Editor area (296 symbols) | `.claude/skills/gitnexus-area-editor/SKILL.md` |
| Work in the Ocr area (256 symbols) | `.claude/skills/gitnexus-area-ocr/SKILL.md` |
| Work in the Timeline area (230 symbols) | `.claude/skills/gitnexus-area-timeline/SKILL.md` |
| Work in the Scripts area (229 symbols) | `.claude/skills/gitnexus-area-scripts/SKILL.md` |
| Work in the Video area (95 symbols) | `.claude/skills/gitnexus-area-video/SKILL.md` |
| Work in the Authentication area (48 symbols) | `.claude/skills/gitnexus-area-authentication/SKILL.md` |
| Work in the Services area (40 symbols) | `.claude/skills/gitnexus-area-services/SKILL.md` |
| Work in the Tts area (32 symbols) | `.claude/skills/gitnexus-area-tts/SKILL.md` |
| Work in the Application area (31 symbols) | `.claude/skills/gitnexus-area-application/SKILL.md` |
| Work in the Maintenance area (31 symbols) | `.claude/skills/gitnexus-area-maintenance/SKILL.md` |
| Work in the ViewModels area (21 symbols) | `.claude/skills/gitnexus-area-viewmodels/SKILL.md` |
| Work in the Jobs area (19 symbols) | `.claude/skills/gitnexus-area-jobs/SKILL.md` |
| Work in the Processes area (16 symbols) | `.claude/skills/gitnexus-area-processes/SKILL.md` |
| Work in the Asr area (15 symbols) | `.claude/skills/gitnexus-area-asr/SKILL.md` |
| Work in the Hardware area (12 symbols) | `.claude/skills/gitnexus-area-hardware/SKILL.md` |
| Work in the Diagnostics area (12 symbols) | `.claude/skills/gitnexus-area-diagnostics/SKILL.md` |
| Work in the Subtitle area (12 symbols) | `.claude/skills/gitnexus-area-subtitle/SKILL.md` |
| Work in the BiliSubStudio.SubtitleRegression area (8 symbols) | `.claude/skills/gitnexus-area-bilisubstudio-subtitleregression/SKILL.md` |

<!-- gitnexus:end -->
