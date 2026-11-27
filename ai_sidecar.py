# ai_sidecar.py — Pendamping AI visual MuseLink (v5.4.0;
# v5.5.0: + transkrip Whisper GPU & YOLO medium;
# v5.11.0: + /parse OmniParser)
#
# Server HTTP lokal 127.0.0.1:8770 yang memuat YOLO (deteksi
# objek) + EasyOCR (OCR) SEKALI ke VRAM GPU lalu melayani:
#   GET  /health             -> status + device + model
#   POST /detect {image, imgsz?} -> daftar objek + kotak (koord. gambar)
#   POST /ocr    {image}     -> teks + kata + kotak (koord. gambar)
#   POST /transcribe {audio} -> teks transkrip (Whisper, GPU;
#                               model dimuat malas saat pertama)
#   POST /parse  {image}     -> kotak elemen UI (OmniParser
#                               icon-detect, dimuat malas) +
#                               label teks dari EasyOCR yang
#                               pusatnya jatuh di dalam kotak
# image/audio = base64. Berkas ini tinggal di
# %USERPROFILE%\AI-Tools\sidecar\ dan dijalankan oleh MuseLink
# memakai python venv AI-Tools (torch CUDA). Tanpa internet.
import base64
import io
import json
import os
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = 8770
_lock = threading.Lock()
_yolo = None
_yolo_name = ""
_reader = None
_whisper = None
_omni = None
_omni_err = ""


def _register_cuda_dlls():
    # faster-whisper (CTranslate2) butuh cuBLAS/cuDNN yang
    # dibundel torch — daftarkan folder lib-nya ke pencarian DLL.
    try:
        import torch
        lib = os.path.join(os.path.dirname(torch.__file__), "lib")
        if os.path.isdir(lib) and hasattr(os, "add_dll_directory"):
            os.add_dll_directory(lib)
    except Exception:
        pass


def load_models():
    global _yolo, _yolo_name, _reader
    _register_cuda_dlls()
    base = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    from ultralytics import YOLO
    # v5.5.0: medium jadi utama (lebih akurat); nano cadangan.
    for name in ("yolo11m.pt", "yolo11n.pt"):
        path = os.path.join(base, name)
        if os.path.exists(path):
            _yolo = YOLO(path)
            _yolo_name = name
            break
    import numpy as np
    blank = np.zeros((64, 64, 3), dtype=np.uint8)
    _yolo.predict(blank, device=0, verbose=False)  # pemanasan GPU
    import easyocr
    _reader = easyocr.Reader(["id", "en"], gpu=True, verbose=False)
    print("[sidecar] model siap: %s + EasyOCR di GPU" % _yolo_name, flush=True)


def get_whisper():
    global _whisper
    if _whisper is None:
        from faster_whisper import WhisperModel
        _whisper = WhisperModel("small", device="cuda", compute_type="float16")
        print("[sidecar] Whisper siap di GPU", flush=True)
    return _whisper


def omni_weights_path():
    base = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base, "models", "icon_detect.pt")


def get_omni():
    # OmniParser icon-detect (v5.11.0): model YOLO khusus
    # elemen UI dari microsoft/OmniParser-v2.0, dimuat MALAS
    # pada panggilan /parse pertama agar start sidecar tetap
    # ringan. Bobot diunduh sekali ke AI-Tools/models/.
    global _omni, _omni_err
    if _omni is not None or _omni_err:
        return _omni
    try:
        path = omni_weights_path()
        if not os.path.exists(path):
            _omni_err = "model OmniParser belum ada di AI-Tools/models/icon_detect.pt"
            return None
        from ultralytics import YOLO
        _omni = YOLO(path)
        print("[sidecar] OmniParser icon-detect siap di GPU", flush=True)
    except Exception as ex:
        _omni_err = str(ex)
    return _omni


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _send(self, obj, code=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send({"ok": True, "device": "cuda",
                        "yolo": _yolo is not None, "ocr": _reader is not None,
                        "model": _yolo_name, "whisper": _whisper is not None,
                        "omni": os.path.exists(omni_weights_path()),
                        "omniLoaded": _omni is not None})
        else:
            self._send({"ok": False, "error": "unknown path"}, 404)

    def _transcribe(self, req):
        audio_b64 = req.get("audio", "")
        if not audio_b64:
            return self._send({"ok": False, "error": "audio kosong"}, 400)
        raw = base64.b64decode(audio_b64)
        fd, tmp = tempfile.mkstemp(suffix=".wav")
        try:
            with os.fdopen(fd, "wb") as f:
                f.write(raw)
            t = time.time()
            with _lock:
                segs, info = get_whisper().transcribe(tmp)
                text = " ".join(s.text for s in segs).strip()
            return self._send({"ok": True, "text": text,
                               "language": info.language,
                               "ms": int((time.time() - t) * 1000)})
        finally:
            try:
                os.remove(tmp)
            except OSError:
                pass

    def do_POST(self):
        try:
            n = int(self.headers.get("Content-Length", 0))
            req = json.loads(self.rfile.read(n) or b"{}")
            if self.path == "/transcribe":
                return self._transcribe(req)
            img_b64 = req.get("image", "")
            if not img_b64:
                return self._send({"ok": False, "error": "image kosong"}, 400)
            import numpy as np
            from PIL import Image
            img = Image.open(io.BytesIO(base64.b64decode(img_b64))).convert("RGB")
            arr = np.array(img)
            t = time.time()
            with _lock:
                if self.path == "/parse":
                    omni = get_omni()
                    if omni is None:
                        return self._send({"ok": False,
                                           "error": _omni_err or "OmniParser tidak siap"}, 503)
                    r = omni.predict(arr, device=0, verbose=False, conf=0.05)[0]
                    boxes = []
                    for b in r.boxes:
                        x1, y1, x2, y2 = (float(v) for v in b.xyxy[0])
                        boxes.append({
                            "label": "icon", "conf": round(float(b.conf[0]), 3),
                            "x": int(x1), "y": int(y1),
                            "w": int(x2 - x1), "h": int(y2 - y1),
                        })
                    # Label teks: kata EasyOCR yang PUSATNYA
                    # jatuh di dalam kotak menjadi label kotak
                    # itu (gabungan urut kiri->kanan).
                    if _reader is not None and boxes:
                        words = []
                        for quad, text, conf in _reader.readtext(arr):
                            xs = [p[0] for p in quad]
                            ys = [p[1] for p in quad]
                            words.append((sum(xs) / len(xs), sum(ys) / len(ys), text))
                        for bx in boxes:
                            inside = [w for w in words
                                      if bx["x"] <= w[0] <= bx["x"] + bx["w"]
                                      and bx["y"] <= w[1] <= bx["y"] + bx["h"]]
                            if inside:
                                inside.sort(key=lambda w: w[0])
                                bx["label"] = " ".join(w[2] for w in inside)
                    boxes.sort(key=lambda b: (b["y"], b["x"]))
                    return self._send({"ok": True, "detections": boxes,
                                       "ms": int((time.time() - t) * 1000)})
                if self.path == "/detect":
                    # imgsz opsional (v5.19.0): ukuran inferensi
                    # YOLO dari pemanggil. Tanpa param -> predict
                    # dipanggil persis seperti dulu (bawaan
                    # Ultralytics 640). Nilai sah di-clamp
                    # 320..1920; yang bukan int positif diabaikan.
                    kw = {}
                    imgsz = req.get("imgsz")
                    if isinstance(imgsz, int) and not isinstance(imgsz, bool) and imgsz > 0:
                        kw["imgsz"] = max(320, min(1920, imgsz))
                    r = _yolo.predict(arr, device=0, verbose=False, **kw)[0]
                    dets = []
                    for b in r.boxes:
                        x1, y1, x2, y2 = (float(v) for v in b.xyxy[0])
                        dets.append({
                            "label": r.names[int(b.cls[0])],
                            "conf": round(float(b.conf[0]), 3),
                            "x": int(x1), "y": int(y1),
                            "w": int(x2 - x1), "h": int(y2 - y1),
                        })
                    resp = {"ok": True, "detections": dets,
                            "ms": int((time.time() - t) * 1000)}
                    if kw:
                        resp["imgsz"] = kw["imgsz"]
                    return self._send(resp)
                if self.path == "/ocr":
                    words = []
                    texts = []
                    for quad, text, conf in _reader.readtext(arr):
                        xs = [p[0] for p in quad]
                        ys = [p[1] for p in quad]
                        x, y = int(min(xs)), int(min(ys))
                        words.append({
                            "t": text, "x": x, "y": y,
                            "w": int(max(xs) - x), "h": int(max(ys) - y),
                            "conf": round(float(conf), 3),
                        })
                        texts.append(text)
                    return self._send({"ok": True, "text": " ".join(texts),
                                       "words": words,
                                       "ms": int((time.time() - t) * 1000)})
            return self._send({"ok": False, "error": "unknown path"}, 404)
        except Exception as ex:  # laporkan jujur, jangan mati
            return self._send({"ok": False, "error": str(ex)}, 500)


if __name__ == "__main__":
    load_models()
    print("[sidecar] dengar di 127.0.0.1:%d" % PORT, flush=True)
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
