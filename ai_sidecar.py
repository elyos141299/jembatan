# ai_sidecar.py — Pendamping AI visual MuseLink (v5.4.0)
#
# Server HTTP lokal 127.0.0.1:8770 yang memuat YOLO (deteksi
# objek) + EasyOCR (OCR) SEKALI ke VRAM GPU lalu melayani:
#   GET  /health          -> status + device
#   POST /detect {image}  -> daftar objek + kotak (koord. gambar)
#   POST /ocr    {image}  -> teks + kata + kotak (koord. gambar)
# image = base64 PNG/JPEG. Berkas ini tinggal di
# %USERPROFILE%\AI-Tools\sidecar\ dan dijalankan oleh MuseLink
# memakai python venv AI-Tools (torch CUDA). Tanpa internet.
import base64
import io
import json
import os
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = 8770
_lock = threading.Lock()
_yolo = None
_reader = None


def load_models():
    global _yolo, _reader
    base = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    from ultralytics import YOLO
    _yolo = YOLO(os.path.join(base, "yolo11n.pt"))
    import numpy as np
    blank = np.zeros((64, 64, 3), dtype=np.uint8)
    _yolo.predict(blank, device=0, verbose=False)  # pemanasan GPU
    import easyocr
    _reader = easyocr.Reader(["id", "en"], gpu=True, verbose=False)
    print("[sidecar] model siap: YOLO + EasyOCR di GPU", flush=True)


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
                        "yolo": _yolo is not None, "ocr": _reader is not None})
        else:
            self._send({"ok": False, "error": "unknown path"}, 404)

    def do_POST(self):
        try:
            n = int(self.headers.get("Content-Length", 0))
            req = json.loads(self.rfile.read(n) or b"{}")
            img_b64 = req.get("image", "")
            if not img_b64:
                return self._send({"ok": False, "error": "image kosong"}, 400)
            import numpy as np
            from PIL import Image
            img = Image.open(io.BytesIO(base64.b64decode(img_b64))).convert("RGB")
            arr = np.array(img)
            t = time.time()
            with _lock:
                if self.path == "/detect":
                    r = _yolo.predict(arr, device=0, verbose=False)[0]
                    dets = []
                    for b in r.boxes:
                        x1, y1, x2, y2 = (float(v) for v in b.xyxy[0])
                        dets.append({
                            "label": r.names[int(b.cls[0])],
                            "conf": round(float(b.conf[0]), 3),
                            "x": int(x1), "y": int(y1),
                            "w": int(x2 - x1), "h": int(y2 - y1),
                        })
                    return self._send({"ok": True, "detections": dets,
                                       "ms": int((time.time() - t) * 1000)})
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
