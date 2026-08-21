#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import subprocess
import tempfile
import time
from pathlib import Path
from urllib.parse import urlparse

from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
UI = ROOT / "web" / "index.html"
MAIN_GO = ROOT / "cmd" / "bilisub" / "main.go"
_version_match = re.search(r'const version = "([^"]+)"', MAIN_GO.read_text(encoding="utf-8"))
if not _version_match:
    raise RuntimeError("cannot resolve app version from cmd/bilisub/main.go")
CURRENT_VERSION = _version_match.group(1)
_beta_match = re.fullmatch(r"(.*-beta\.)(\d+)", CURRENT_VERSION)
LATEST_VERSION = (_beta_match.group(1) + str(int(_beta_match.group(2)) + 1)) if _beta_match else CURRENT_VERSION + ".next"


class MockBackend:
    def __init__(self, media_path: Path):
        self.media_path = media_path
        self.calls: list[tuple[str, str]] = []
        self.requests: list[dict] = []
        self.job_calls: dict[str, int] = {}
        self.job_seq: dict[str, int] = {"ocr": 0, "editor": 0}
        self.cancelled_jobs: set[str] = set()
        self.paused_jobs: set[str] = set()
        self.ocr_checkpoint: dict | None = None
        self.editor_preview_direct = True
        self.preview_direct_codec = "vp9"
        self.pick_folder_result = r"E:\Picked"
        self.status = {
            "version": CURRENT_VERSION,
            "cookie_saved": False,
            "cookie_valid": False,
            "cookie_user": "",
            "cookie_error": "",
            "drive": "E:",
            "root": r"E:\BiliSubStudio",
            "config": {
                "theme": "dark",
                "output_dir": r"E:\BiliSubStudio\Downloads",
                "sub_format": "srt",
                "video_speed": "fast",
                "video_container": "mp4",
                "video_mode": "video+audio",
                "check_updates": False,
                "ocr_top": 70,
                "ocr_bottom": 96,
                "ocr_left": 8,
                "ocr_right": 92,
                "ocr_device": "auto",
            },
            "storage": {"data": 1024, "tools": 2048, "ocr": 4096, "temp": 512, "cache": 256},
            "ocr_ready": False,
            "ocr_status": {
                "state": 0, "ready": False, "device_mode": "auto",
                "gpu_detected": True, "gpu_available": True,
                "gpu_name": "NVIDIA GeForce RTX Fixture", "gpu_driver": "566.36",
            },
            "ytdlp_ready": True,
            "ffmpeg_ready": True,
        }

    @staticmethod
    def _json(route, payload, status=200):
        route.fulfill(status=status, content_type="application/json", body=json.dumps(payload, ensure_ascii=False))

    def api(self, route):
        req = route.request
        parsed = urlparse(req.url)
        path = parsed.path
        self.calls.append((req.method, path))
        raw_body = req.post_data or ""
        try:
            parsed_body = json.loads(raw_body) if raw_body else None
        except json.JSONDecodeError:
            parsed_body = raw_body
        self.requests.append({"method": req.method, "path": path, "query": parsed.query, "body": parsed_body})

        if path == "/api/media":
            data = self.media_path.read_bytes()
            range_header = req.headers.get("range", "")
            m = re.match(r"bytes=(\d+)-(\d*)", range_header)
            if m:
                start = min(len(data), int(m.group(1)))
                end = len(data) - 1 if not m.group(2) else min(len(data) - 1, int(m.group(2)))
                if start <= end:
                    chunk = data[start:end + 1]
                    route.fulfill(
                        status=206,
                        content_type="video/webm",
                        body=chunk,
                        headers={
                            "Access-Control-Allow-Origin": "*",
                            "Accept-Ranges": "bytes",
                            "Content-Range": f"bytes {start}-{end}/{len(data)}",
                            "Content-Length": str(len(chunk)),
                        },
                    )
                    return
            route.fulfill(
                status=200,
                content_type="video/webm",
                body=data,
                headers={"Access-Control-Allow-Origin": "*", "Accept-Ranges": "bytes", "Content-Length": str(len(data))},
            )
            return
        if path == "/api/status":
            self._json(route, self.status)
            return
        if path == "/api/theme":
            body = json.loads(req.post_data or "{}")
            self.status["config"]["theme"] = body.get("theme", "dark")
            self._json(route, {"ok": True, "theme": self.status["config"]["theme"]})
            return
        if path == "/api/pick-folder":
            self.status["config"]["output_dir"] = self.pick_folder_result
            self._json(route, {"path": self.pick_folder_result, "cancelled": False})
            return
        if path == "/api/open-folder":
            body = json.loads(req.post_data or "{}")
            self._json(route, {"path": body.get("path") or r"E:\Picked"})
            return
        if path == "/api/pick-video":
            self._json(route, {"path": r"E:\Videos\sample.mp4", "cancelled": False})
            return
        if path == "/api/preview-info":
            if self.editor_preview_direct:
                self._json(route, {"width": 320, "height": 180, "duration": 2.0, "codec": self.preview_direct_codec, "container": "mov,mp4" if self.preview_direct_codec == "hevc" else "webm", "direct_compatible": True})
            else:
                self._json(route, {"width": 1920, "height": 1080, "duration": 7449.0, "codec": "hevc", "container": "mov,mp4", "direct_compatible": False})
            return
        if path == "/api/preview-frame":
            route.fulfill(status=200, content_type="image/svg+xml", body='<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360"><rect width="640" height="360" fill="#203040"/><text x="30" y="60" fill="white" font-size="30">preview</text></svg>')
            return
        if path == "/api/metadata":
            body = json.loads(req.post_data or "{}")
            if body.get("purpose") == "subtitle":
                self._json(route, {
                    "id": "BVTEST", "title": "Fixture video",
                    "subtitles": [
                        {"lang": "zh-CN", "lang_doc": "中文", "official": True},
                        {"lang": "ai-zh", "lang_doc": "中文 AI", "ai": True},
                    ],
                })
            else:
                self._json(route, {"id": "BVTEST", "title": "Fixture video", "qualities": ["1080p", "720p"]})
            return
        if path == "/api/subtitle/download":
            self._json(route, {"job_id": "sub-job"})
            return
        if path == "/api/video/download":
            self._json(route, {"job_id": "video-job"})
            return
        if path == "/api/editor/export":
            self.job_seq["editor"] += 1
            self._json(route, {"job_id": f"editor-job-{self.job_seq['editor']}"})
            return
        if path == "/api/ocr/scan":
            self.job_seq["ocr"] += 1
            self._json(route, {"job_id": f"ocr-job-{self.job_seq['ocr']}"})
            return
        if path == "/api/job/cancel":
            q = parsed.query
            for part in q.split("&"):
                if part.startswith("id="):
                    self.cancelled_jobs.add(part[3:])
            self._json(route, {"ok": True})
            return
        if path == "/api/job/pause":
            job_id = ""
            for part in parsed.query.split("&"):
                if part.startswith("id="):
                    job_id = part[3:]
            self.paused_jobs.add(job_id)
            self.ocr_checkpoint = {"exists": True, "media_seconds": 0.8, "progress_percent": 40.0, "cue_count": 1, "frames": 2, "ocr_calls": 3, "ocr_batch_calls": 2, "parallelism_selected": 4, "active_lanes": 0, "completed_lanes": 0, "total_lanes": 4, "boundary_merges": 0, "recent_cues": [{"start": 0.1, "end": 0.8, "text": "实时字幕", "conf": 0.88}]}
            self._json(route, {"id": job_id, "status": "paused", "done": True, "progress": 35, "message": "Đã tạm dừng an toàn tại 00:00.", "pause_supported": True, "result": {"media_seconds": 0.8, "ocr_images": 3, "ocr_batch_calls": 2, "parallelism_selected": 4, "active_lanes": 0, "completed_lanes": 0}})
            return
        if path == "/api/ocr/checkpoint":
            if req.method == "DELETE":
                self.ocr_checkpoint = None
                self._json(route, {"ok": True})
            else:
                self._json(route, self.ocr_checkpoint or {"exists": False})
            return
        if path == "/api/job":
            q = parsed.query
            job_id = ""
            for part in q.split("&"):
                if part.startswith("id="):
                    job_id = part[3:]
            n = self.job_calls.get(job_id, 0) + 1
            self.job_calls[job_id] = n
            if job_id in self.paused_jobs:
                self._json(route, {"status": "paused", "done": True, "progress": 35, "message": "Đã tạm dừng an toàn tại checkpoint.", "logs": [], "log_next": 0, "pause_supported": True, "result": {"cue_count": 1, "ocr_images": 3, "ocr_batch_calls": 2, "parallelism_selected": 4, "active_lanes": 0, "completed_lanes": 0, "decoder": "nvdec", "media_seconds": 0.8, "recent_cues": [{"start": 0.1, "end": 0.8, "text": "实时字幕", "conf": 0.88}]}})
            elif job_id in self.cancelled_jobs:
                self._json(route, {"status": "cancelled", "done": True, "progress": 40, "message": "Đã hủy", "logs": ["cancel fixture"], "log_next": 1, "result": {}})
            elif job_id == "video-job" and n == 1:
                self._json(route, {"status": "running", "done": False, "progress": 35, "message": "Đang tải", "logs": ["segment fixture"], "log_next": 1})
            elif job_id in {"ocr-job-1", "ocr-job-2"} or (job_id.startswith("ocr-job-") and n == 1):
                # The first two OCR jobs are intentionally held in RUNNING until
                # the test requests Pause. Do not let wall-clock/browser speed
                # decide whether the mock scan finishes before the pause click.
                # The third OCR job is the actual Resume path and completes on
                # its second poll below.
                self._json(route, {"status": "running", "done": False, "progress": 35, "message": "Đang nhận diện", "logs": [], "log_next": 0, "result": {"cue_count": 1, "ocr_calls": 3, "ocr_images": 3, "ocr_batch_calls": 2, "parallelism_selected": 4, "active_lanes": 4, "completed_lanes": 0, "decoder": "nvdec", "frame_pipeline_seconds": 0.3, "visual_seconds": 0.04, "media_seconds": 0.8, "realtime_speed": 1.2, "last_text": "实时字幕", "last_confidence": 0.88, "recent_cues": [{"start": 0.1, "end": 0.8, "text": "实时字幕", "conf": 0.88}]}})
            elif job_id.startswith("ocr-job-"):
                # Production removes a durable OCR checkpoint after a successful
                # completed scan. Keep the mock lifecycle identical so a later
                # video pick cannot inherit a stale "Tiếp tục quét" state.
                self.ocr_checkpoint = None
                self._json(route, {
                    "status": "done", "done": True, "progress": 100, "message": "OCR xong", "logs": [], "log_next": 0,
                    "result": {"cue_count": 1, "ocr_calls": 9, "ocr_images": 9, "ocr_batch_calls": 4, "parallelism_selected": 4, "active_lanes": 0, "completed_lanes": 4, "boundary_merges": 1, "decoder": "nvdec", "frame_pipeline_seconds": 0.8, "visual_seconds": 0.1, "realtime_speed": 1.7, "last_text": "测试字幕", "cues": [{"start": 0.2, "end": 1.4, "text": "测试字幕", "conf": 0.95}]},
                })
            elif job_id.startswith("editor-job-") and n == 1:
                self._json(route, {"status": "running", "done": False, "progress": 30, "message": "Đang render", "logs": ["ffmpeg fixture"], "log_next": 1, "result": {}})
            else:
                self._json(route, {"status": "done", "done": True, "progress": 100, "message": "Hoàn tất", "logs": ["done fixture"], "log_next": 1, "result": {}})
            return
        if path == "/api/ocr/engine/ensure":
            body = json.loads(req.post_data or "{}")
            mode = body.get("device") or self.status["config"].get("ocr_device", "auto")
            self.status["config"]["ocr_device"] = mode
            active = "gpu" if mode == "auto" else mode
            self.status["ocr_ready"] = True
            self.status["ocr_status"].update({
                "state": 2, "ready": True, "device_mode": mode, "active_mode": active,
                "active_devices": ["cpu", "gpu:0"] if active == "hybrid" else (["gpu:0"] if active == "gpu" else ["cpu"]),
                "fallback_reason": "",
            })
            self._json(route, {"ok": True})
            return
        if path == "/api/ocr/engine/status":
            self._json(route, self.status["ocr_status"])
            return
        if path == "/api/ocr":
            self._json(route, {"ok": True, "text": "测试字幕", "confidence": 0.95})
            return
        if path == "/api/ocr/export":
            self._json(route, {"count": 1, "path": r"E:\Picked\BiliSub_OCR_Chinese.srt"})
            return
        if path == "/api/ocr/engine/remove":
            self.status["ocr_ready"] = False
            self._json(route, {"ok": True})
            return
        if path == "/api/update/check":
            self._json(route, {"available": True, "current": CURRENT_VERSION, "latest": LATEST_VERSION, "notes": ["fixture update"]})
            return
        if path == "/api/update/apply":
            self._json(route, {"ok": True, "version": LATEST_VERSION})
            return
        if path == "/api/update/setting":
            self._json(route, {"ok": True})
            return
        if path == "/api/cookie":
            if req.method == "POST":
                self.status.update(cookie_saved=True, cookie_valid=True, cookie_user="FixtureUser")
                self._json(route, {"ok": True, "logged_in": True, "user": "FixtureUser"})
            else:
                self.status.update(cookie_saved=False, cookie_valid=False, cookie_user="")
                self._json(route, {"ok": True})
            return
        if path == "/api/login/qr/start":
            self._json(route, {"key": "fixture-key", "url": "https://example.test/qr"})
            return
        if path == "/api/login/qr/poll":
            self._json(route, {"logged_in": True, "message": "Đăng nhập thành công"})
            return
        if path == "/api/storage/cleanup":
            self._json(route, {"ok": True, "locked": 0})
            return
        if path == "/api/tools/reset":
            self._json(route, {"ok": True})
            return
        if path == "/api/exit":
            self._json(route, {"ok": True})
            return
        if path == "/api/ping":
            self._json(route, {"ok": True})
            return
        self._json(route, {"error": f"unmocked route {path}"}, 500)


def must(condition: bool, message: str):
    if not condition:
        raise AssertionError(message)


def prepare_html() -> str:
    html = UI.read_text(encoding="utf-8")
    html = html.replace("__APP_TOKEN__", "E2E-TOKEN")
    html = html.replace('<video id="ocrVideo"', '<video id="ocrVideo" crossorigin="anonymous"', 1)
    html = html.replace('<video id="editorVideo"', '<video id="editorVideo" crossorigin="anonymous"', 1)
    html = html.replace("<head>", '<head><base href="http://app.local/">', 1)
    html = html.replace(
        '<script src="https://cdnjs.cloudflare.com/ajax/libs/qrcodejs/1.0.0/qrcode.min.js"></script>',
        '<script>window.QRCode=function(el,opt){el.textContent="QR:"+opt.text}</script>',
    )
    return html


def run(media_path: Path, screenshot_dir: Path | None = None):
    backend = MockBackend(media_path)
    errors: list[str] = []
    console_errors: list[str] = []

    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True, executable_path="/usr/bin/chromium", args=["--no-sandbox"])
        page = browser.new_page(viewport={"width": 1440, "height": 1000})
        page.on("pageerror", lambda err: errors.append(str(err)))
        page.on("console", lambda msg: console_errors.append(msg.text) if msg.type == "error" else None)
        def app_route(route):
            path = urlparse(route.request.url).path
            if path.startswith("/api/"):
                backend.api(route)
            elif path in {"/app-icon.png", "/favicon.ico"}:
                route.fulfill(status=200, content_type="image/png", body=b"\x89PNG\r\n\x1a\n")
            elif path == "/manifest.webmanifest":
                route.fulfill(status=200, content_type="application/manifest+json", body="{}")
            else:
                route.fulfill(status=204, body="")
        page.route("http://app.local/**", app_route)
        page.route("https://script.google.com/**", lambda route: route.fulfill(status=204, body=""))
        page.on("dialog", lambda dialog: dialog.accept())
        page.set_content(prepare_html(), wait_until="load")
        # Instrument every static product button. Release acceptance requires
        # every button shipped in the source UI to be exercised at least once
        # by this E2E flow, not merely present or statically wired. Use stable
        # semantic signatures because some UI state updates re-render controls.
        page.evaluate(r"""() => {
          window.__e2eButtonClicks = [];
          window.__e2eButtonSig = b => {
            if (b.id) return 'id:' + b.id;
            if (b.dataset.page) return 'page:' + b.dataset.page;
            if (b.dataset.effect) return 'effect:' + b.dataset.effect;
            const oc = b.getAttribute('onclick');
            if (oc) return 'onclick:' + oc;
            return 'text:' + (b.textContent || '').trim().replace(/\s+/g, ' ');
          };
          window.__e2eInitialButtons = [...document.querySelectorAll('button')].map(window.__e2eButtonSig);
          document.addEventListener('click', e => {
            const b = e.target.closest && e.target.closest('button');
            if (b) window.__e2eButtonClicks.push(window.__e2eButtonSig(b));
          }, true);
          Element.prototype.requestFullscreen = function(){ window.__e2eFullscreenRequested = true; return Promise.resolve(); };
        }""")

        print("E2E startup", flush=True)
        # Startup/sidebar must populate without opening Settings.
        expect(page.locator("#version")).to_have_text(CURRENT_VERSION)
        expect(page.locator("#driveSide")).to_have_text("E:")
        expect(page.locator("#rootSetting")).to_have_text(r"E:\BiliSubStudio")
        must("dark" in (page.locator("body").get_attribute("class") or ""), "dark theme not applied on bootstrap")

        print("E2E nav", flush=True)
        # Navigation: every page is directly reachable and exclusive.
        for name, sec in [("Phụ đề", "subtitle"), ("Video", "video"), ("OCR phụ đề", "ocr"), ("Chỉnh video", "editor"), ("Cài đặt", "settings")]:
            page.get_by_role("button", name=name, exact=True).click()
            expect(page.locator(f"#{sec}")).to_have_class("page active")
            must(page.locator(".page.active").count() == 1, f"multiple active pages after {name}")

        print("E2E modals", flush=True)
        # Modal open/close flows.
        page.get_by_role("button", name="Cookie", exact=True).click()
        expect(page.locator("#cookieModal")).to_have_class("modal show")
        page.locator("#cookieInput").fill("SESSDATA=fixture")
        page.get_by_role("button", name="Lưu Cookie", exact=True).click()
        expect(page.locator("#cookieModal")).to_have_class("modal")
        expect(page.locator("#cookieSide")).to_have_text("Đã đăng nhập")
        # Exercise explicit Cookie close independently from save-success auto-close.
        page.get_by_role("button", name="Cookie", exact=True).click()
        page.locator("#cookieModal").get_by_role("button", name="Hủy", exact=True).click()
        expect(page.locator("#cookieModal")).to_have_class("modal")

        page.get_by_role("button", name="QR Login", exact=True).click()
        expect(page.locator("#qrModal")).to_have_class("modal show")
        page.wait_for_timeout(120)
        expect(page.locator("#qrStatus")).to_contain_text("Đăng nhập")
        page.get_by_role("button", name="Tạo lại", exact=True).click()
        page.get_by_role("button", name="Đóng", exact=True).click()
        expect(page.locator("#qrModal")).to_have_class("modal")

        page.get_by_role("button", name="Báo lỗi", exact=True).click()
        expect(page.locator("#bugModal")).to_have_class("modal show")
        page.locator("#bugNote").fill("fixture bug")
        page.locator("#bugSend").click()
        page.wait_for_timeout(50)
        expect(page.locator("#bugStatus")).to_contain_text("Đã gửi")
        page.get_by_role("button", name="Đóng", exact=True).last.click()

        print("E2E folders", flush=True)
        # Shared folder picker/open flows in all four consumers.
        folder_targets = ["outDir1", "outDir2", "ocrOut", "editorOut"]
        section_ids = ["subtitle", "video", "ocr", "editor"]
        for field, section in zip(folder_targets, section_ids):
            page.get_by_role("button", name={"subtitle":"Phụ đề","video":"Video","ocr":"OCR phụ đề","editor":"Chỉnh video"}[section], exact=True).click()
            sec = page.locator(f"#{section}")
            sec.get_by_role("button", name="Chọn", exact=True).last.click()
            expect(page.locator(f"#{field}")).to_have_value(r"E:\Picked")
            sec.get_by_role("button", name="Mở", exact=True).last.click()

        print("E2E subtitle", flush=True)
        # Subtitle metadata + download + log clear.
        page.get_by_role("button", name="Phụ đề", exact=True).click()
        page.locator("#subUrl").fill("https://www.bilibili.com/video/BVTEST")
        page.get_by_role("button", name="Kiểm tra", exact=True).click()
        expect(page.locator("#subMeta")).to_contain_text("2 track")
        must(page.locator("#subTrack option").count() == 2, "subtitle track options not rendered")
        page.locator("#subTrack").select_option(index=1)
        page.locator("#subFormat").select_option(label="json")
        page.locator("#subDownloadBtn").click()
        page.wait_for_timeout(650)
        expect(page.locator("#subDownloadBtn")).to_have_text("Tải phụ đề")
        expect(page.locator("#subLog")).to_contain_text("done fixture")
        page.locator("#subtitle").get_by_role("button", name="Xóa log", exact=True).click()
        expect(page.locator("#subLog")).to_have_text("Sẵn sàng.")

        print("E2E video", flush=True)
        # Video metadata + download + cancel route + log clear.
        page.get_by_role("button", name="Video", exact=True).click()
        page.locator("#videoUrl").fill("https://www.bilibili.com/video/BVTEST")
        page.get_by_role("button", name="Kiểm tra stream", exact=True).click()
        expect(page.locator("#videoMeta")).to_contain_text("1080p")
        page.locator("#quality").select_option(label="720p")
        page.locator("#mode").select_option("video-only")
        page.locator("#containerFmt").select_option("mkv")
        page.locator("#speed").select_option("turbo")
        page.locator("#videoDownloadBtn").click()
        page.wait_for_timeout(80)
        must(not page.locator("#videoCancelBtn").is_disabled(), "video cancel did not enable")
        page.locator("#videoCancelBtn").click()
        page.wait_for_timeout(600)
        page.locator("#video").get_by_role("button", name="Xóa log", exact=True).click()
        expect(page.locator("#videoLog")).to_have_text("Sẵn sàng.")

        print("E2E ocr", flush=True)
        # OCR: shared video picker -> media preview -> region -> engine -> frame -> scan -> export.
        page.get_by_role("button", name="OCR phụ đề", exact=True).click()
        # HF7: HEVC MP4 must enter the real <video> path first instead of being
        # pre-rejected into FFmpeg frame fallback. The mocked media bytes remain
        # browser-decodable; codec metadata exercises the HEVC decision branch.
        backend.preview_direct_codec = "hevc"
        page.locator("#ocrPick").click()
        page.wait_for_function("document.getElementById('ocrVideo').videoWidth > 0")
        must(page.locator("#ocrPath").input_value().endswith("sample.mp4"), "OCR picker path not applied")
        must(not page.evaluate("ocrFallbackMode"), "HEVC direct candidate was pre-rejected into fallback")
        expect(page.locator("#ocrNote")).to_contain_text("HEVC/H.265")
        # HF6 Windows field regression: direct-browser playback controls must not
        # depend on a decoded frame being ready at the exact moment
        # ocrSyncControls() runs. Seeking/checkpoint refresh can temporarily make
        # readyState < HAVE_CURRENT_DATA even though the direct media source is
        # valid, which previously left Play/Mute stuck disabled forever.
        page.evaluate("""() => {
            window.__ocrFrameReadyHF6 = ocrFrameReady;
            ocrFrameReady = () => false;
            ocrSyncControls();
        }""")
        must(not page.locator("#ocrPlay").is_disabled(), "OCR direct Play incorrectly depends on frame-ready state")
        must(not page.locator("#ocrMute").is_disabled(), "OCR direct Mute incorrectly depends on frame-ready state")
        must(not page.locator("#ocrScrub").is_disabled(), "OCR direct scrub unexpectedly disabled before scan")
        page.evaluate("""() => {
            ocrFrameReady = window.__ocrFrameReadyHF6;
            delete window.__ocrFrameReadyHF6;
            ocrSyncControls();
        }""")
        must(not page.locator("#ocrSubtitlePreset").is_disabled(), "OCR subtitle preset not enabled")
        page.locator("#ocrSubtitlePreset").click()
        must(float(page.locator("#bottomR").input_value()) > float(page.locator("#topR").input_value()), "OCR ROI invalid")
        page.locator("#ocrMode").select_option("balanced")
        page.locator("#ocrSensitivity").select_option("0.75")
        must(not page.locator('#ocrDevice option[value="gpu"]').is_disabled(), "GPU mode disabled despite compatible NVIDIA fixture")
        must(not page.locator('#ocrDevice option[value="hybrid"]').is_disabled(), "Hybrid mode disabled despite compatible NVIDIA fixture")
        before_device_ensure = len([x for x in backend.requests if x["path"] == "/api/ocr/engine/ensure"])
        page.locator("#ocrDevice").select_option("hybrid")
        expect(page.locator("#prepareOCR")).to_have_text("Bộ nhận diện đã sẵn sàng", timeout=5000)
        device_ensures = [x for x in backend.requests if x["path"] == "/api/ocr/engine/ensure"]
        must(len(device_ensures) == before_device_ensure + 1, "OCR device change did not reconfigure engine")
        must(device_ensures[-1]["body"].get("device") == "hybrid", "Hybrid selection not sent to ensure API")
        expect(page.locator("#ocrDeviceInfo")).to_contain_text("Đang dùng CPU + GPU")
        page.eval_on_selector("#leftR", "el => { el.value='6'; el.dispatchEvent(new Event('input',{bubbles:true})); }")
        page.eval_on_selector("#rightR", "el => { el.value='94'; el.dispatchEvent(new Event('input',{bubbles:true})); }")
        page.eval_on_selector("#topR", "el => { el.value='69'; el.dispatchEvent(new Event('input',{bubbles:true})); }")
        page.eval_on_selector("#bottomR", "el => { el.value='97'; el.dispatchEvent(new Event('input',{bubbles:true})); }")
        page.eval_on_selector("#ocrScrub", "el => { el.value='250'; el.dispatchEvent(new Event('input',{bubbles:true})); }")
        page.locator("#ocrPlay").click()
        page.wait_for_timeout(70)
        page.locator("#ocrPlay").click()
        page.locator("#ocrMute").click()
        page.locator("#ocrFullscreen").click()
        must(page.evaluate("Boolean(window.__e2eFullscreenRequested)"), "OCR fullscreen control did not request fullscreen")
        page.locator("#prepareOCR").click()
        page.wait_for_timeout(1150)
        expect(page.locator("#prepareOCR")).to_have_text("Bộ nhận diện đã sẵn sàng")
        page.locator("#testOCR").click()
        page.wait_for_timeout(80)
        expect(page.locator("#ocrText")).to_contain_text("测试字幕")
        manual_device_reqs = [x for x in backend.requests if x["path"] == "/api/ocr"]
        must(manual_device_reqs[-1]["body"].get("device") == "hybrid", "manual OCR did not preserve Hybrid device mode")
        page.locator("#startOCR").click()
        expect(page.locator("#stopOCR")).to_be_enabled()
        scan_device_reqs = [x for x in backend.requests if x["path"] == "/api/ocr/scan"]
        must(scan_device_reqs[-1]["body"].get("device") == "hybrid", "OCR scan did not preserve Hybrid device mode")
        must(scan_device_reqs[-1]["body"].get("parallelism") == "auto", "OCR parallelism mode did not reach scan API")
        expect(page.locator("#cueList")).to_contain_text("实时字幕", timeout=1800)
        expect(page.locator("#ocrConf")).to_have_text("88%")
        expect(page.locator("#ocrBatchCalls")).to_have_text("2")
        expect(page.locator("#ocrParallelSelected")).to_have_text("4")
        expect(page.locator("#ocrActiveLanes")).to_have_text("4")
        page.wait_for_function("Math.abs(document.getElementById('ocrVideo').currentTime - 0.8) < 0.25")
        must(page.locator("#ocrPlay").is_disabled(), "OCR manual play must be disabled while backend scan owns preview time")
        must(page.locator("#ocrScrub").is_disabled(), "OCR manual seek must be disabled while backend scan owns preview time")
        if screenshot_dir:
            screenshot_dir.mkdir(parents=True, exist_ok=True)
            page.screenshot(path=str(screenshot_dir / "beta12-ocr-live.png"), full_page=True)
        # True pause must create an inspectable checkpoint and preserve live cues.
        page.locator("#stopOCR").click()
        expect(page.locator("#startOCR")).to_have_text("Tiếp tục quét", timeout=2500)
        expect(page.locator("#ocrCheckpointInfo")).to_contain_text("Đã lưu 40.0% tổng công việc")
        expect(page.locator("#ocrCheckpointInfo")).to_contain_text("mốc liên tục 00:00")
        expect(page.locator("#ocrActiveLanes")).to_have_text("0")
        expect(page.locator("#ocrRealSpeed")).to_have_text("—")
        expect(page.locator("#ocrConf")).to_have_text("88%")
        expect(page.locator("#cueList")).to_contain_text("实时字幕")
        must(not page.locator("#restartOCR").is_hidden(), "restart-from-zero control not shown for checkpoint")
        # Exercise explicit restart-from-zero once, then pause again and actually resume.
        page.locator("#restartOCR").click()
        expect(page.locator("#startOCR")).to_have_text("Bắt đầu quét chính xác")
        page.locator("#startOCR").click()
        expect(page.locator("#stopOCR")).to_be_enabled()
        expect(page.locator("#cueList")).to_contain_text("实时字幕", timeout=1800)
        page.locator("#stopOCR").click()
        expect(page.locator("#startOCR")).to_have_text("Tiếp tục quét", timeout=2500)
        page.locator("#startOCR").click()
        page.wait_for_timeout(1100)
        expect(page.locator("#cueCount")).to_have_text("1")
        must(not page.locator("#exportOCR").is_disabled(), "OCR export not enabled after cues")
        page.locator("#exportOCR").click()
        page.wait_for_timeout(50)
        expect(page.locator("#ocrNote")).to_contain_text("BiliSub_OCR_Chinese.srt")
        # Completed scans must expose every cue in the list (not only the live
        # 120-cue window), and seeking the source timeline must center/highlight
        # the nearest subtitle for manual QC.
        page.evaluate("""() => {
            ocrCues = Array.from({length:130}, (_,i) => ({
                start: i * 0.015, end: i * 0.015 + 0.012,
                text: '字幕' + i, conf: 0.95
            }));
            ocrRenderCues();
        }""")
        expect(page.locator("#cueCount")).to_have_text("130")
        expect(page.locator("#cueShown")).to_have_text("130 / 130 câu")
        must(page.locator("#cueList .cue").count() == 130, "completed OCR list still truncates cues")
        page.eval_on_selector("#ocrScrub", "el => { el.value='750'; el.dispatchEvent(new Event('input',{bubbles:true})); }")
        page.wait_for_timeout(80)
        active = page.locator("#cueList .cue.active")
        must(active.count() == 1, "timeline seek did not highlight a subtitle row")
        active_start = float(active.get_attribute("data-start") or "-1")
        must(abs(active_start - 1.5) < 0.08, f"timeline seek synced wrong cue at {active_start}")
        page.locator("#clearOCR").click()
        expect(page.locator("#cueCount")).to_have_text("0")

        print(" OCR shared fallback preview", flush=True)
        backend.editor_preview_direct = False
        page.locator("#ocrPick").click()
        page.wait_for_function("ocrPreviewReady() && ocrFallbackMode")
        page.wait_for_function("document.getElementById('ocrFallbackFrame').naturalWidth > 0")
        expect(page.locator("#ocrNote")).to_contain_text("xem theo khung hình")
        must(page.locator("#ocrPlay").is_disabled(), "OCR fallback must not expose fake browser playback")
        must(page.locator("#ocrMute").is_disabled(), "OCR fallback must not expose a fake audio control")
        must(not page.locator("#ocrScrub").is_disabled(), "OCR fallback scrub should remain available before scan")
        if screenshot_dir:
            screenshot_dir.mkdir(parents=True, exist_ok=True)
            page.screenshot(path=str(screenshot_dir / "beta12-ocr-fallback.png"), full_page=True)
        page.eval_on_selector("#ocrScrub", "el => { el.value='120'; el.dispatchEvent(new Event('input',{bubbles:true})); }")
        page.wait_for_timeout(180)
        must(any(x[1] == "/api/preview-frame" for x in backend.calls), "OCR fallback did not use shared preview-frame route")
        # RC5 Windows field regression: preparing the OCR engine while the source
        # uses FFmpeg fallback must NOT re-disable Start. refreshAppStatus() used
        # to inspect ocrVideo.duration (zero in fallback mode) after ocrEnsure().
        backend.status["ocr_ready"] = False
        page.evaluate("ocrEngineReady=false; document.getElementById('prepareOCR').textContent='Chuẩn bị bộ nhận diện'; ocrSyncControls()")
        expect(page.locator("#testOCR")).to_be_disabled()
        page.locator("#prepareOCR").click()
        page.wait_for_timeout(1150)
        expect(page.locator("#prepareOCR")).to_have_text("Bộ nhận diện đã sẵn sàng")
        expect(page.locator("#startOCR")).to_be_enabled()
        expect(page.locator("#testOCR")).to_be_enabled()
        # RC6 Windows field regression: manual "Đọc thử khung hiện tại" must
        # work in FFmpeg fallback mode too. It now sends path/time/ROI to the
        # backend instead of trying to OCR a browser canvas capture.
        manual_at = float(page.evaluate("ocrFallbackTime"))
        before_manual = len([x for x in backend.requests if x["path"] == "/api/ocr"])
        page.locator("#testOCR").click()
        expect(page.locator("#ocrText")).to_contain_text("测试字幕", timeout=1800)
        manual_reqs = [x for x in backend.requests if x["path"] == "/api/ocr"]
        must(len(manual_reqs) == before_manual + 1, "fallback manual OCR did not call /api/ocr")
        manual_body = manual_reqs[-1]["body"]
        must(isinstance(manual_body, dict) and manual_body.get("path", "").endswith("sample.mp4"), "manual OCR did not send video path")
        must(abs(float(manual_body.get("time", -999)) - manual_at) < 0.05, "manual OCR did not send current preview time")
        must(isinstance(manual_body.get("region"), dict) and float(manual_body["region"].get("h", 0)) > 0, "manual OCR did not send ROI")
        must("imageBase64" not in manual_body, "manual OCR still depends on browser canvas capture")
        page.locator("#startOCR").click()
        expect(page.locator("#stopOCR")).to_be_enabled()
        expect(page.locator("#cueList")).to_contain_text("实时字幕", timeout=1800)
        page.wait_for_function("Math.abs(ocrFallbackTime - 0.8) < 0.25")
        page.locator("#stopOCR").click()
        expect(page.locator("#startOCR")).to_be_enabled(timeout=2500)
        backend.editor_preview_direct = True
        backend.preview_direct_codec = "vp9"

        print("E2E editor", flush=True)
        # Editor: picker -> preview -> presets -> all effect buttons -> range -> undo/delete -> export.
        page.get_by_role("button", name="Chỉnh video", exact=True).click()
        print(" editor pick", flush=True)
        page.locator("#editorPick").click()
        page.wait_for_function("editorReady() && !editorFallbackMode")
        print(" editor loaded", flush=True)
        page.locator("#editorPlay").click()
        page.wait_for_timeout(60)
        page.locator("#editorPlay").click()
        must(not page.locator("#editorSubtitlePreset").is_disabled(), "editor preset not enabled")
        print(" editor preset", flush=True)
        page.locator("#editorSubtitlePreset").click()
        expect(page.locator("#editorRegionCount")).to_contain_text("1 vùng")
        print(" editor scrub", flush=True)
        page.eval_on_selector("#editorScrub", "el => { el.value='300'; el.dispatchEvent(new Event('input',{bubbles:true})); }")
        print(" editor precision", flush=True)
        for field, value in [("editorX","7.5"),("editorY","71"),("editorW","85"),("editorH","20")]:
            page.eval_on_selector("#" + field, "(el, value) => { el.value=value; el.dispatchEvent(new Event('change',{bubbles:true})); }", value)
        print(" editor effects", flush=True)
        for effect in ["blur", "mosaic", "cover"]:
            page.locator(f'#editorEffects button[data-effect="{effect}"]').click()
            must("active" in (page.locator(f'#editorEffects button[data-effect="{effect}"]').get_attribute("class") or ""), f"effect {effect} did not activate")
        page.locator('#editorEffects button[data-effect="blur"]').click()
        print(" editor strength", flush=True)
        page.eval_on_selector("#editorStrength", "el => { el.value = '24'; el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new Event('change',{bubbles:true})); }")
        expect(page.locator("#editorStrengthValue")).to_have_text("24")
        print(" editor whole", flush=True)
        page.locator("#editorWhole").uncheck()
        must(not page.locator("#editorRangeControls").get_attribute("hidden"), "editor time range stayed hidden")
        print(" editor range", flush=True)
        page.locator("#editorSetStart").click()
        page.locator("#editorSetEnd").click()
        print(" editor undo", flush=True)
        page.locator("#editorUndo").click()
        print(" editor delete", flush=True)
        page.locator("#editorDelete").click()
        expect(page.locator("#editorRegionCount")).to_contain_text("0 vùng")
        print(" editor watermark", flush=True)
        page.locator("#editorWatermarkPreset").click()
        expect(page.locator("#editorRegionCount")).to_contain_text("1 vùng")
        # Final exported region is deliberately configured through inspector
        # controls so their values can be asserted in the backend payload.
        page.locator('#editorEffects button[data-effect="blur"]').click()
        page.eval_on_selector("#editorStrength", "el => { el.value='24'; el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new Event('change',{bubbles:true})); }")
        page.locator("#editorWhole").uncheck()
        for field, value in [("editorX","7.5"),("editorY","71"),("editorW","85"),("editorH","20")]:
            page.eval_on_selector("#" + field, "(el, value) => { el.value=value; el.dispatchEvent(new Event('change',{bubbles:true})); }", value)
        print(" editor export", flush=True)
        page.locator("#editorExport").click()
        expect(page.locator("#editorCancel")).to_be_enabled()
        page.locator("#editorCancel").click()
        expect(page.locator("#editorExport")).to_be_enabled(timeout=2500)
        expect(page.locator("#editorStatus")).to_contain_text("Đã hủy")
        page.locator("#editorExport").click()
        page.wait_for_timeout(1100)
        expect(page.locator("#editorStatus")).to_contain_text("Hoàn tất")

        print(" editor fallback preview", flush=True)
        backend.editor_preview_direct = False
        page.locator("#editorPick").click()
        page.wait_for_function("editorReady() && editorFallbackMode")
        page.wait_for_function("document.getElementById('editorFallbackFrame').naturalWidth > 0")
        expect(page.locator("#editorStatus")).to_contain_text("Xem theo khung hình")
        must(page.locator("#editorPlay").is_disabled(), "fallback preview should not expose fake realtime playback")
        if screenshot_dir:
            screenshot_dir.mkdir(parents=True, exist_ok=True)
            page.screenshot(path=str(screenshot_dir / "beta12-editor-fallback.png"), full_page=True)
        page.eval_on_selector("#editorScrub", "el => { el.value='450'; el.dispatchEvent(new Event('input',{bubbles:true})); }")
        page.wait_for_timeout(260)
        must(any(x[1] == "/api/preview-frame" for x in backend.calls), "fallback preview frame route was not called")

        print("E2E settings", flush=True)
        # Settings: theme, update, update setting, cleanup/tool actions, side update affordance.
        page.get_by_role("button", name="Cài đặt", exact=True).click()
        expect(page.locator("#defaultOut")).to_have_value(backend.status["config"]["output_dir"])
        backend.pick_folder_result = r"E:\DefaultChanged"
        page.locator("#defaultOutPick").click()
        expect(page.locator("#defaultOut")).to_have_value(r"E:\DefaultChanged")
        for target in ["#outDir1", "#outDir2", "#ocrOut", "#editorOut"]:
            expect(page.locator(target)).to_have_value(r"E:\DefaultChanged")
        page.locator("#defaultOutOpen").click()
        must("setTheme(this.value)" in (page.locator("#themeSelect").get_attribute("onchange") or ""), "theme select is not wired")
        page.evaluate("void setTheme('light')")
        page.wait_for_function("!document.body.classList.contains('dark') && document.getElementById('themeSelect').value === 'light'")
        page.evaluate("void setTheme('dark')")
        page.wait_for_function("document.body.classList.contains('dark') && document.getElementById('themeSelect').value === 'dark'")
        page.locator("#autoUpdateCheck").uncheck()
        page.locator("#autoUpdateCheck").dispatch_event("change")
        page.locator("#checkUpdateBtn").click()
        page.wait_for_timeout(50)
        expect(page.locator("#updateLatest")).to_have_text("v" + LATEST_VERSION)
        must(page.locator("#sideUpdate").is_visible(), "side update badge not visible")
        page.locator("#sideUpdate").click()
        expect(page.locator("#settings")).to_have_class("page active")
        page.locator("#applyUpdateBtn").click()
        page.wait_for_timeout(50)
        expect(page.locator("#updateNotes")).to_contain_text("Đã tải v" + LATEST_VERSION)
        page.get_by_role("button", name="Dọn Temp/Cache", exact=True).click()
        page.get_by_role("button", name="Xóa OCR Engine", exact=True).click()
        page.get_by_role("button", name="Xóa Tools", exact=True).click()

        print("E2E exit", flush=True)
        # Exit is last because production window.close is allowed to close the page.
        if screenshot_dir:
            screenshot_dir.mkdir(parents=True, exist_ok=True)
            page.screenshot(path=str(screenshot_dir / "beta12-e2e-final.png"), full_page=True)
        # Validate the route coverage before Exit closes the browser page.
        called = {path for _, path in backend.calls}
        required_routes = {
            "/api/status", "/api/theme", "/api/pick-folder", "/api/open-folder", "/api/pick-video", "/api/media",
            "/api/preview-info", "/api/preview-frame",
            "/api/metadata", "/api/subtitle/download", "/api/video/download", "/api/job", "/api/job/cancel", "/api/job/pause",
            "/api/ocr/engine/ensure", "/api/ocr/engine/status", "/api/ocr", "/api/ocr/scan", "/api/ocr/checkpoint", "/api/ocr/export",
            "/api/editor/export", "/api/update/check", "/api/update/setting", "/api/update/apply",
            "/api/cookie", "/api/login/qr/start", "/api/login/qr/poll", "/api/storage/cleanup", "/api/ocr/engine/remove",
            "/api/tools/reset",
        }
        missing = sorted(required_routes - called)
        must(not missing, "browser E2E missed critical routes: " + ", ".join(missing))
        clicked = set(page.evaluate("window.__e2eButtonClicks"))
        initial_buttons = set(page.evaluate("window.__e2eInitialButtons"))
        exit_sig = "onclick:exitApp()"
        missing_buttons = sorted(initial_buttons - clicked - {exit_sig})
        must(not missing_buttons, "browser E2E did not click product buttons: " + json.dumps(missing_buttons, ensure_ascii=False))

        # Verify configurable controls actually reach backend request payloads.
        def last_body(path):
            matches = [x.get("body") for x in backend.requests if x["path"] == path and isinstance(x.get("body"), dict)]
            return matches[-1] if matches else {}
        sub_req = last_body("/api/subtitle/download")
        must(str(sub_req.get("Format", "")).lower() == "json", "subtitle format select did not reach backend")
        must(sub_req.get("Track") not in (None, "", "0"), "subtitle track select did not reach backend")
        vid_req = last_body("/api/video/download")
        must(vid_req.get("Quality") == "720p" and vid_req.get("Mode") == "video-only" and vid_req.get("Container") == "mkv" and vid_req.get("Speed") == "turbo", "video quality/mode/container/speed controls did not reach backend")
        ocr_reqs = [x.get("body") for x in backend.requests if x["path"] == "/api/ocr/scan" and isinstance(x.get("body"), dict)]
        must(bool(ocr_reqs), "OCR scan request missing")
        ocr_req = ocr_reqs[-1]
        must(ocr_req.get("mode") == "balanced" and ocr_req.get("parallelism") == "auto" and abs(float(ocr_req.get("sensitivity", 0)) - 0.75) < 1e-9, "OCR mode/parallelism/sensitivity controls did not reach backend")
        reg = ocr_req.get("region") or {}
        must(abs(float(reg.get("x", 0)) - 0.06) < 0.01 and abs(float(reg.get("y", 0)) - 0.69) < 0.01, "OCR ROI controls did not reach backend")
        editor_reqs = [x.get("body") for x in backend.requests if x["path"] == "/api/editor/export" and isinstance(x.get("body"), dict)]
        must(bool(editor_reqs) and bool(editor_reqs[-1].get("regions")), "editor region payload missing")
        final_region = editor_reqs[-1]["regions"][0]
        must(final_region.get("effect") == "blur", "editor effect control did not reach backend")
        must(abs(float(final_region.get("strength", 0)) - 24) < 0.01, "editor strength did not reach backend")
        must(final_region.get("whole") is False, "editor time-range toggle did not reach backend")
        must(abs(float(final_region.get("x", 0)) - 0.075) < 0.01 and abs(float(final_region.get("y", 0)) - 0.71) < 0.01, "editor precision position controls did not reach backend")

        must(not errors, "page errors: " + " | ".join(errors))
        must(not console_errors, "console errors: " + " | ".join(console_errors))

        # Exit is destructive by design and closes the page; verify it last.
        try:
            page.get_by_role("button", name="Đóng BiliSub Studio hoàn toàn", exact=True).click()
        except Exception:
            pass
        time.sleep(0.05)
        must(any(path == "/api/exit" for _, path in backend.calls), "Exit button did not call /api/exit")
        browser.close()

    print(f"BROWSER E2E: PASS ({len(backend.calls)} API calls, {len(set(p for _, p in backend.calls))} unique API routes, all static product buttons exercised)")


def make_media_fixture(path: Path):
    cmd = [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "color=c=black:s=320x180:d=2:r=25",
        "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
        "-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-shortest", str(path),
    ]
    subprocess.run(cmd, check=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--media", type=Path, help="optional browser-decodable local video fixture")
    ap.add_argument("--screenshots", type=Path)
    args = ap.parse_args()
    if args.media:
        if not args.media.is_file():
            raise SystemExit(f"media fixture not found: {args.media}")
        run(args.media, args.screenshots)
        return
    with tempfile.TemporaryDirectory(prefix="bilisub-browser-e2e-") as td:
        media = Path(td) / "fixture.webm"
        make_media_fixture(media)
        run(media, args.screenshots)


if __name__ == "__main__":
    main()
