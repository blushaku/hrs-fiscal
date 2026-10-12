"""Builds src/Hrs.Fiscal.Server/i18n/{sq,sr}.json from the (English, Albanian, Serbian Latin) tables in this folder."""
import json, os, importlib, sys
here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)
modules = ["ui_general", "ui_settings", "messages", "extra"]
sq, sr, seen = {}, {}, {}
for name in modules:
    if not os.path.exists(os.path.join(here, name + ".py")): continue
    for en, a, s in importlib.import_module(name).E:
        key = " ".join(en.split())
        if key in seen and (sq[key], sr[key]) != (a, s):
            print(f"warning: '{key}' translated twice ({seen[key]} and {name}); keeping the first")
            continue
        seen.setdefault(key, name); sq.setdefault(key, a); sr.setdefault(key, s)
out = os.path.join(here, "..", "..", "src", "Hrs.Fiscal.Server", "i18n")
for lang, d in (("sq", sq), ("sr", sr)):
    with open(os.path.join(out, lang + ".json"), "w", encoding="utf-8") as f:
        json.dump(dict(sorted(d.items())), f, ensure_ascii=False, indent=1)
print(len(sq), "entries")
