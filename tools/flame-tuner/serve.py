"""Serves the flame tuner and saves what is tuned.

    py -I serve.py --data <export folder> --out <flame-placements.txt> [--port 8766]
"""
import argparse
import json
import os
import tempfile
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
parser = argparse.ArgumentParser()
parser.add_argument("--data", required=True)
parser.add_argument("--out", required=True)
parser.add_argument("--port", type=int, default=8766)
args = parser.parse_args()
DATA = os.path.abspath(args.data)
OUT = os.path.abspath(args.out)


def number(value):
    text = f"{value:.3f}".rstrip("0").rstrip(".")
    return text if text not in ("", "-0") else "0"


def replace(path, text):
    folder = os.path.dirname(path)
    fd, temp = tempfile.mkstemp(dir=folder, suffix=".tmp")
    with os.fdopen(fd, "w", encoding="ascii", newline="\n") as f:
        f.write(text)
    os.replace(temp, path)


class Handler(SimpleHTTPRequestHandler):
    def translate_path(self, path):
        clean = path.split("?", 1)[0].split("#", 1)[0]
        root = HERE
        if clean.startswith("/data/"):
            root, clean = DATA, clean[len("/data"):]
        target = os.path.normpath(os.path.join(root, clean.lstrip("/")))
        return target if target == root or target.startswith(root + os.sep) else os.path.join(root, "index.html")

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def do_POST(self):
        if self.path != "/save":
            self.send_error(404)
            return
        try:
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            lines = ["# graphic scene x y height scale (written by tools/flame-tuner)"]
            for item in body["objects"]:
                for flame in item["flames"]:
                    lines.append(" ".join([
                        f"0x{item['graphic']:04X}", flame["scene"], number(flame["x"]), number(flame["y"]),
                        number(flame["height"]), number(flame["scale"]), number(flame.get("size", 1)),
                    ]))
            replace(OUT, "\n".join(lines) + "\n")
            replace(os.path.join(DATA, "flames.json"), json.dumps(body, indent=1))
        except (KeyError, TypeError, ValueError, OSError) as error:
            self.send_error(500, str(error))
            return
        self.send_response(200)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def log_message(self, *a):
        pass


print(f"Flame tuner on http://127.0.0.1:{args.port}/")
ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
