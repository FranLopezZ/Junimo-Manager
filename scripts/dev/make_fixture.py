"""Creates a fake Stardew Valley folder with a few mods and a settings folder with two profiles, for screenshots and manual tests.

Usage: python scripts/dev/make_fixture.py <output-folder>
Then run the app with:  JUNIMO_CONFIG_DIR=<output-folder>/cfg  (see shot.ps1 -Config)
"""
import json
import os
import shutil
import sys

out = os.path.abspath(sys.argv[1])
shutil.rmtree(out, ignore_errors=True)
game = os.path.join(out, "game")
cfg = os.path.join(out, "cfg")
os.makedirs(os.path.join(game, "smapi-internal"))
os.makedirs(cfg)
for f in ("Stardew Valley.dll", "StardewModdingAPI.dll"):
    open(os.path.join(game, f), "w").write("x")

mods = [  # folder, name, author, version, description, id, nexus id
    ("ContentPatcher", "Content Patcher", "Pathoschild", "2.9.1", "Loads content packs that change the game's data and images.", "Pathoschild.ContentPatcher", 1915),
    (".GenericModConfigMenu", "Generic Mod Config Menu", "spacechase0", "1.16.0", "Adds an in-game UI to edit other mods' options.", "spacechase0.GenericModConfigMenu", 5098),
    ("LookupAnything", "Lookup Anything", "Pathoschild", "1.55.0", "See live info about whatever is under your cursor.", "Pathoschild.LookupAnything", 541),
    ("SaveAnywhere", "Save Anywhere", "Omegasis", "3.0.0", "Save anywhere you like.", "Omegasis.SaveAnywhere", 444),
]
for folder, name, author, ver, desc, uid, nexus in mods:
    d = os.path.join(game, "Mods", folder)
    os.makedirs(d)
    manifest = {"Name": name, "Author": author, "Version": ver, "Description": desc, "UniqueID": uid, "UpdateKeys": [f"Nexus:{nexus}"]}
    if uid == "Pathoschild.LookupAnything":  # needs Content Patcher, so turning Content Patcher off asks first
        manifest["Dependencies"] = [{"UniqueID": "Pathoschild.ContentPatcher"}]
    json.dump(manifest, open(os.path.join(d, "manifest.json"), "w"))
    if uid == "Pathoschild.ContentPatcher":
        json.dump({"EnableDebugFeatures": False, "MaxDaysToRetainLogs": 7, "ShowCompatibilityWarnings": True, "UiScale": 1.25, "Language": "es",
                   "Keybinds": {"ReloadPatches": "F5", "OpenMenu": "LeftShift, K"}, "Whitelist": ["Pathoschild", "Abagaianye"]},
                  open(os.path.join(d, "config.json"), "w"), indent=2)

def entry(i, enabled):
    _, name, _, ver, _, uid, nexus = mods[i]
    return {"UniqueID": uid, "Name": name, "Version": ver, "Enabled": enabled, "UpdateKeys": [f"Nexus:{nexus}"]}

profiles = {"Format": 1, "Profiles": [
    {"Id": "a" * 32, "Name": "Granja", "UpdatedAt": "2026-10-01T10:00:00+00:00", "Deleted": False, "Mods": [entry(0, True), entry(1, False), entry(2, True)]},
    {"Id": "b" * 32, "Name": "Vanilla", "UpdatedAt": "2026-10-01T10:00:00+00:00", "Deleted": False, "Mods": []},
]}
json.dump(profiles, open(os.path.join(cfg, "profiles.json"), "w"), indent=1)
json.dump({"GamePath": game, "SyncMode": 0, "SyncOnStartup": False, "ActiveProfileId": "a" * 32, "Language": "es", "ControllerMode": "auto"},
          open(os.path.join(cfg, "settings.json"), "w"), indent=1)
print(out)
