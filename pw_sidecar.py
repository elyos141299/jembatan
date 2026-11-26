# pw_sidecar.py — Pendamping Playwright MuseLink (v5.14.0)
#
# Server HTTP lokal 127.0.0.1:8771 yang mengendarai Chrome
# TERPASANG lewat Playwright (pip, venv AI-Tools) dengan
# PROFIL OTOMASI SENDIRI (%LOCALAPPDATA%\Jembatan\pw-profile)
# — profil Chrome harian user TIDAK disentuh. Satu konteks
# persisten + satu halaman aktif; semua perintah berurutan
# lewat satu kunci. Melayani:
#   GET  /health                  -> status
#   POST /open   {url}            -> luncurkan (bila perlu) + buka url
#   POST /goto   {url}            -> navigasi halaman aktif
#   POST /click  {text|selector}  -> klik (teks bahasa manusia dulu)
#   POST /fill   {selector|text, value} -> isi kolom
#   POST /text   {selector?}      -> teks elemen / seluruh halaman
#   POST /wait   {text?, ms?}     -> tunggu teks muncul / tidur
#   POST /screenshot {}           -> PNG base64 halaman aktif
#   POST /close  {}               -> tutup konteks
# Playwright sync API wajib di thread tanpa event loop —
# semua kerja browser di SATU thread pekerja khusus; handler
# HTTP menyerahkan tugas lewat antrean.
import base64
import json
import os
import queue
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = 8771
PROFILE = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Jembatan", "pw-profile")
CHROME_CANDIDATES = [
    os.path.join(os.environ.get("ProgramFiles", ""), "Google", "Chrome", "Application", "chrome.exe"),
    os.path.join(os.environ.get("ProgramFiles(x86)", ""), "Google", "Chrome", "Application", "chrome.exe"),
    os.path.join(os.environ.get("LOCALAPPDATA", ""), "Google", "Chrome", "Application", "chrome.exe"),
]

_jobs = queue.Queue()
_lock = threading.Lock()


def _chrome_path():
    for p in CHROME_CANDIDATES:
        if p and os.path.exists(p):
            return p
    return ""


class Browser:
    """Dijalankan HANYA di thread pekerja."""

    def __init__(self):
        self.pw = None
        self.ctx = None
        self.page = None

    def ensure(self):
        if self.ctx is not None and self.page is not None and not self.page.is_closed():
            return
        from playwright.sync_api import sync_playwright
        chrome = _chrome_path()
        if not chrome:
            raise RuntimeError("chrome.exe tidak ditemukan di jalur lazim")
        os.makedirs(PROFILE, exist_ok=True)
        if self.pw is None:
            self.pw = sync_playwright().start()
        self.ctx = self.pw.chromium.launch_persistent_context(
            PROFILE, executable_path=chrome, headless=False,
            viewport={"width": 1280, "height": 800},
        )
        self.page = self.ctx.pages[0] if self.ctx.pages else self.ctx.new_page()

    def close(self):
        try:
            if self.ctx is not None:
                self.ctx.close()
        finally:
            self.ctx = None
            self.page = None

    def run(self, cmd, arg):
        if cmd == "close":
            self.close()
            return {"ok": True, "closed": True}
        self.ensure()
        page = self.page
        if cmd in ("open", "goto"):
            url = arg.get("url", "")
            if not url:
                raise ValueError("url wajib diisi")
            page.goto(url, wait_until="domcontentloaded", timeout=45000)
            return {"ok": True, "url": page.url, "title": page.title()}
        if cmd == "click":
            if arg.get("text"):
                page.get_by_text(arg["text"], exact=False).first.click(timeout=15000)
            elif arg.get("selector"):
                page.click(arg["selector"], timeout=15000)
            else:
                raise ValueError("text atau selector wajib diisi")
            return {"ok": True, "url": page.url, "title": page.title()}
        if cmd == "fill":
            value = arg.get("value", "")
            if arg.get("selector"):
                page.fill(arg["selector"], value, timeout=15000)
            elif arg.get("text"):
                page.get_by_label(arg["text"], exact=False).first.fill(value, timeout=15000)
            else:
                raise ValueError("selector atau text (label) wajib diisi")
            return {"ok": True}
        if cmd == "text":
            if arg.get("selector"):
                t = page.inner_text(arg["selector"], timeout=15000)
            else:
                t = page.inner_text("body", timeout=15000)
            return {"ok": True, "text": t[:20000], "url": page.url, "title": page.title()}
        if cmd == "wait":
            if arg.get("text"):
                page.get_by_text(arg["text"], exact=False).first.wait_for(timeout=int(arg.get("timeoutMs", 15000)))
                return {"ok": True, "found": True}
            page.wait_for_timeout(int(arg.get("ms", 1000)))
            return {"ok": True, "found": True}
        if cmd == "screenshot":
            png = page.screenshot(type="png")
            return {"ok": True, "png": base64.b64encode(png).decode("ascii"),
                    "url": page.url, "title": page.title()}
        raise ValueError("perintah tidak dikenal: " + cmd)


_browser = Browser()


def _worker():
    while True:
        cmd, arg, done, box = _jobs.get()
        try:
            box["result"] = _browser.run(cmd, arg)
        except Exception as ex:  # dilaporkan jujur ke pemanggil
            box["result"] = {"ok": False, "error": type(ex).__name__ + ": " + str(ex)[:400]}
        done.set()


threading.Thread(target=_worker, daemon=True, name="pw-worker").start()


def dispatch(cmd, arg):
    done = threading.Event()
    box = {}
    _jobs.put((cmd, arg, done, box))
    if not done.wait(timeout=120):
        return {"ok": False, "error": "timeout 120 dtk di sidecar"}
    return box.get("result", {"ok": False, "error": "tanpa hasil"})


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _json(self, obj, status=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._json({"ok": True, "app": "pw-sidecar", "chrome": _chrome_path(),
                        "profile": PROFILE})
        else:
            self._json({"ok": False, "error": "tidak dikenal"}, 404)

    def do_POST(self):
        try:
            n = int(self.headers.get("Content-Length", "0"))
            arg = json.loads(self.rfile.read(n).decode("utf-8") or "{}")
        except Exception:
            arg = {}
        cmd = self.path.lstrip("/")
        if cmd not in ("open", "goto", "click", "fill", "text", "wait", "screenshot", "close"):
            self._json({"ok": False, "error": "tidak dikenal"}, 404)
            return
        with _lock:
            self._json(dispatch(cmd, arg))


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
