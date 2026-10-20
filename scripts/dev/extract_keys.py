"""Prints every translatable Spanish key used by the app (Loc.T / Loc.F in C#, literal texts in the XAML) as JSON.

Usage: python scripts/dev/extract_keys.py [existing-en.json]   -> JSON {key: translation-or-""} on stdout
"""
import glob
import html
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")
root = os.path.join(os.path.dirname(__file__), "..", "..", "src")


def unescape_cs(s: str) -> str:
    return re.sub(r'\\(["\\ntr])', lambda m: {"n": "\n", "t": "\t", "r": "\r"}.get(m.group(1), m.group(1)), s)


def keys() -> list:
    found = []
    call = re.compile(r'Loc\.[TF]\(\s*\$?"((?:[^"\\]|\\.)*)"')
    for f in glob.glob(os.path.join(root, "**", "*.cs"), recursive=True):
        n = f.replace("\\", "/")
        if "/obj/" in n or "/bin/" in n or n.endswith("Loc.cs"):
            continue
        text = open(f, encoding="utf-8-sig").read()
        found += [unescape_cs(m.group(1)) for m in call.finditer(text)]
    attr = re.compile(r'\b(?:Text|Content|Header|PlaceholderText)="([^"]*)"')
    for f in glob.glob(os.path.join(root, "**", "*.axaml"), recursive=True):
        if "/obj/" in f.replace("\\", "/"):
            continue
        for m in attr.finditer(open(f, encoding="utf-8-sig").read()):
            v = html.unescape(m.group(1))
            if v and not v.startswith("{") and re.search(r"[A-Za-zÁÉÍÓÚáéíóúñ]{2}", v):
                found.append(v)
    return sorted(set(found), key=lambda k: k.lower())


if __name__ == "__main__":
    existing = {}
    if len(sys.argv) > 1 and os.path.exists(sys.argv[1]):
        existing = json.load(open(sys.argv[1], encoding="utf-8"))
    out = {k: existing.get(k, "") for k in keys()}
    print(json.dumps(out, ensure_ascii=False, indent=1))
