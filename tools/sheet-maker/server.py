"""Serves the tools/ folder on 127.0.0.1 only, and saves the sheets the page renders into
tools/sheet-maker/out/. Run from the repo: python3 tools/sheet-maker/server.py  (then open
http://127.0.0.1:4660/sheet-maker/?model=../example-character/sprout.glb&name=sprout)"""
import http.server
import os
import re
import sys
from urllib.parse import urlparse, parse_qs

TOOLS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(TOOLS, "sheet-maker", "out")
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 4660


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=TOOLS, **kw)

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def do_POST(self):
        url = urlparse(self.path)
        name = parse_qs(url.query).get("name", [""])[0]
        if url.path != "/save" or not re.fullmatch(r"[a-z0-9-]+-sheet\.(png|json)", name):
            self.send_error(400)
            return
        size = int(self.headers.get("Content-Length", 0))
        os.makedirs(OUT, exist_ok=True)
        with open(os.path.join(OUT, name), "wb") as f:
            f.write(self.rfile.read(size))
        self.send_response(204)
        self.end_headers()


print(f"http://127.0.0.1:{PORT}/sheet-maker/  (saves to {OUT})")
http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
