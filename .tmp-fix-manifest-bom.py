#!/usr/bin/env python3
from pathlib import Path
p = Path("/var/lib/videodownloader-updates/beta/manifest.json")
text = p.read_text(encoding="utf-8-sig")
p.write_text(text, encoding="utf-8")
print("ok", p.read_bytes()[:1], "bom" if p.read_bytes().startswith(b"\xef\xbb\xbf") else "nobom")
