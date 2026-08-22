#!/usr/bin/env python3
from __future__ import annotations

import argparse
import re
from pathlib import Path
from bs4 import BeautifulSoup

ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / "internal/api/server.go"
UI = ROOT / "web/index.html"
OUT = ROOT / "docs/engineering/CODE_MAP.generated.md"


def go_imports(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8")
    found: list[str] = []
    block = re.search(r"import\s*\((.*?)\)", text, re.S)
    if block:
        found.extend(re.findall(r'"([^"]+)"', block.group(1)))
    found.extend(re.findall(r'^import\s+"([^"]+)"', text, re.M))
    return sorted({x for x in found if x.startswith("bilisubstudio/")})


def package_graph() -> dict[str, set[str]]:
    graph: dict[str, set[str]] = {}
    for path in sorted((ROOT / "cmd").rglob("*.go")) + sorted((ROOT / "internal").rglob("*.go")):
        if path.name.endswith("_test.go"):
            continue
        rel = path.relative_to(ROOT).as_posix()
        package = "/".join(rel.split("/")[:-1])
        graph.setdefault(package, set())
        for imp in go_imports(path):
            local = imp.removeprefix("bilisubstudio/")
            graph[package].add(local)
    return graph


def server_routes() -> list[tuple[str, str]]:
    text = SERVER.read_text(encoding="utf-8")
    return re.findall(r'mux\.HandleFunc\("(/api/[^"]+)",\s*s\.auth\(s\.([A-Za-z0-9_]+)\)\)', text)


def ui_routes() -> set[str]:
    text = UI.read_text(encoding="utf-8")
    return {x.split("?")[0] for x in re.findall(r'(?<![A-Za-z0-9_])(/api/[A-Za-z0-9_./?-]+)', text)}


def go_function_count() -> int:
    total = 0
    for path in sorted((ROOT / "cmd").rglob("*.go")) + sorted((ROOT / "internal").rglob("*.go")):
        if path.name.endswith("_test.go"):
            continue
        text = path.read_text(encoding="utf-8")
        total += len(re.findall(r'^func\s+(?:\([^\n]+\)\s*)?[A-Za-z0-9_]+\s*\(', text, re.M))
    return total


def js_function_spans(js: str) -> list[tuple[int, int, str]]:
    spans: list[tuple[int, int, str]] = []
    for m in re.finditer(r'(?:async\s+)?function\s+([A-Za-z_$][\w$]*)\s*\(', js):
        brace = js.find("{", m.end() - 1)
        if brace < 0:
            continue
        depth = 0
        end = len(js)
        quote = ""
        escape = False
        for i in range(brace, len(js)):
            c = js[i]
            if quote:
                if escape:
                    escape = False
                elif c == "\\":
                    escape = True
                elif c == quote:
                    quote = ""
                continue
            if c in "'\"`":
                quote = c
            elif c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    end = i + 1
                    break
        spans.append((m.start(), end, m.group(1)))
    return spans


def critical_ocr_writers() -> dict[str, list[str]]:
    html = UI.read_text(encoding="utf-8")
    scripts = re.findall(r"<script[^>]*>(.*?)</script>", html, re.S)
    js = "\n".join(scripts)
    spans = js_function_spans(js)

    def owner(pos: int) -> str:
        matches = [x for x in spans if x[0] <= pos < x[1]]
        if not matches:
            return "<top-level/event>"
        return min(matches, key=lambda x: x[1] - x[0])[2]

    ids = [
        "startOCR", "testOCR", "ocrPlay", "ocrScrub", "ocrMute",
        "ocrFullscreen", "ocrSubtitlePreset", "ocrDevice", "ocrParallelism", "stopOCR", "restartOCR", "clearOCR", "exportOCR",
    ]
    out: dict[str, list[str]] = {}
    for element_id in ids:
        writers: list[str] = []
        pat = re.compile(r's\("' + re.escape(element_id) + r'"\)\.disabled\s*=')
        for m in pat.finditer(js):
            writers.append(owner(m.start()))
        out[element_id] = sorted(set(writers))
    return out


def render() -> str:
    routes = server_routes()
    used = ui_routes()
    graph = package_graph()
    writers = critical_ocr_writers()
    html = UI.read_text(encoding="utf-8")
    soup = BeautifulSoup(html, "html.parser")
    buttons = len(soup.find_all("button"))
    dom_ids = len(soup.find_all(attrs={"id": True}))

    lines: list[str] = []
    lines += [
        "# BiliSub Studio generated code map",
        "",
        "> GENERATED FROM CURRENT SOURCE by `scripts/generate_code_map.py`. Do not hand-edit.",
        "> The release gate runs this generator with `--check`; a stale map blocks release.",
        "",
        "## Inventory",
        "",
        f"- Production Go functions: **{go_function_count()}**",
        f"- Registered authenticated API routes: **{len(routes)}**",
        f"- Frontend-referenced API routes: **{len(used)}**",
        f"- Product buttons: **{buttons}**",
        f"- DOM ids: **{dom_ids}**",
        "",
        "## Package dependency graph",
        "",
        "```text",
    ]
    for pkg in sorted(graph):
        deps = sorted(graph[pkg])
        lines.append(pkg)
        if deps:
            for dep in deps:
                lines.append(f"  -> {dep}")
        else:
            lines.append("  -> (stdlib only)")
    lines += ["```", "", "## HTTP route ownership", "", "| Route | Handler | Used by frontend |", "|---|---|---|"]
    for route, handler in routes:
        lines.append(f"| `{route}` | `api.Server.{handler}` | {'yes' if route in used else 'no'} |")

    lines += [
        "",
        "## OCR control-state ownership",
        "",
        "Critical OCR controls must have exactly one state writer: `ocrSyncControls`.",
        "This prevents a status refresh, preview-mode switch, engine transition, or scan transition from re-enabling/disabling controls with incompatible rules.",
        "",
        "| Control | `.disabled` writers |",
        "|---|---|",
    ]
    for element_id, owners in writers.items():
        lines.append(f"| `#{element_id}` | {', '.join('`'+x+'`' for x in owners) if owners else '_none_'} |")

    lines += [
        "",
        "## Verified top-level execution map",
        "",
        "```text",
        "BiliSubStudio.exe",
        "  -> cmd/bilisub.main",
        "     -> proc.EnableContainment -> Windows Job Object (normal helpers die with app)",
        "     -> appstate.New",
        "     -> application.New",
        "        -> jobs.Manager",
        "        -> tools.Manager (app-owned ffmpeg/ffprobe/yt-dlp only)",
        "        -> ocr.Manager",
        "        -> video.Service / YTDLPResolver",
        "        -> subtitle.Service",
        "        -> videoedit.Service",
        "     -> nativeui.Run -> native Windows x64 window/message loop",
        "        -> application.App methods directly; no localhost/browser/WebView runtime",
        "        -> nativeplayer.Player -> app-owned FFmpeg decode -> GDI frame render + Windows audio",
        "        -> qrcode.Encode -> native QR matrix render",
        "        -> WM_CLOSE -> application.PrepareShutdown -> PauseJob for every active pausable OCR job -> fsynced checkpoint -> cancel remaining work -> close",
        "     -> update result -> proc.Breakaway updater -> atomic self-swap -> restart native EXE",
        "",
        "Native OCR UI",
        "  -> native Windows file picker -> application.PreviewInfo / EnsureFFmpeg -> nativeplayer.Player",
        "  -> timeline WM_HSCROLL -> nativeplayer.Seek -> syncCueToTime -> nearest cue highlight/scroll",
        "  -> cue LISTBOX selection -> seekSelectedCue -> nativeplayer.Seek",
        "  -> native preview drag -> ROI controls -> ocr.ScanRegion",
        "  -> Auto/CPU/GPU/Hybrid -> application.ConfigureOCRDevice -> ocr.Manager.ConfigureDevice",
        "  -> Auto/1/2/4/8/16 -> ScanRequest.Parallelism -> ParallelScanCoordinator",
        "  -> Test OCR -> application.OCRFrame -> FFmpeg crop -> PaddleOCR",
        "  -> Start/Resume -> application.StartOCRScan -> ocr.Scanner.Run -> schema-3 legacy or schema-4 parallel scan",
        "  -> Pause -> application.PauseJob -> jobs.Job.RequestPause -> tracker-safe boundary -> checkpoint fsync -> PauseComplete",
        "  -> Restart -> application.RemoveOCRCheckpoint -> new scan from zero",
        "  -> Export -> application.ExportOCR -> NormalizeChineseSubtitleText -> Chinese-only sequential SRT",
        "  -> Fullscreen -> borderless native monitor window; Escape restores previous window style/rect",
        "",
        "Legacy browser regression adapter (source/tests only; not imported by cmd/bilisub)",
        "  -> internal/api + embedded HTML remain as a parity oracle during migration",
        "  -> browser_e2e.py exercises legacy contracts but does not define production runtime",
        "",
        "Native Video Editor UI",
        "  -> native picker -> application.PreviewInfo / EnsureFFmpeg -> nativeplayer.Player",
        "  -> preview drag -> editor X/Y/W/H controls",
        "  -> timeline -> nativeplayer.Seek",
        "  -> Export -> application.StartEditor -> videoedit.Service.Run -> app-owned FFmpeg output",
        "```",
        "",
    ]
    return "\n".join(lines)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()
    content = render()
    if args.check:
        if not OUT.exists() or OUT.read_text(encoding="utf-8") != content:
            print("CODE MAP: FAIL (generated map is stale; run scripts/generate_code_map.py)")
            return 1
        writers = critical_ocr_writers()
        bad = {k: v for k, v in writers.items() if v != ["ocrSyncControls"]}
        if bad:
            print("CODE MAP: FAIL (OCR critical controls have multiple/incorrect state writers)")
            for k, v in bad.items():
                print(" -", k, v)
            return 1
        print("CODE MAP: PASS")
        return 0
    OUT.write_text(content, encoding="utf-8")
    print(f"wrote {OUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
