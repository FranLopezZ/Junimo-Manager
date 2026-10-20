"""Shows how complete each translation file is compared to en.json, and lists unknown keys.

Usage: python scripts/dev/translation_status.py [language-code]    (e.g. fr -> also lists the missing texts)
"""
import glob
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")
folder = os.path.join(os.path.dirname(__file__), "..", "..", "translations")
load = lambda p: {k: v for k, v in json.load(open(p, encoding="utf-8")).items() if not k.startswith("_")}
en = load(os.path.join(folder, "en.json"))
holes = lambda s: sorted(re.findall(r"\{\d+[^}]*\}", s))
detail = sys.argv[1] if len(sys.argv) > 1 else None

for path in sorted(glob.glob(os.path.join(folder, "*.json"))):
    code = os.path.splitext(os.path.basename(path))[0]
    if code == "en":
        continue
    table = load(path)
    done = [k for k, v in table.items() if k in en and v.strip()]
    unknown = [k for k in table if k not in en]
    bad = [k for k in done if holes(k) != holes(table[k])]
    print(f"{code}: {len(done)}/{len(en)} translated ({100 * len(done) // len(en)}%), {len(unknown)} unknown keys, {len(bad)} placeholder problems")
    if detail == code:
        print("\nMissing:\n  " + "\n  ".join(k for k in en if k not in done))
        print("\nUnknown keys:\n  " + "\n  ".join(unknown))
        print("\nPlaceholder problems:\n  " + "\n  ".join(bad))
if not any(os.path.splitext(os.path.basename(p))[0] != "en" for p in glob.glob(os.path.join(folder, "*.json"))):
    print("Only en.json so far: no translations to compare.")
