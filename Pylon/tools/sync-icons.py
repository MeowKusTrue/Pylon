"""Copy the Phosphor icons the app uses into Assets/Icons.

Usage: python tools/sync-icons.py <path to @phosphor-icons/core/assets>
Get the assets with:  npm pack @phosphor-icons/core@2.1.1 && tar xzf phosphor-icons-core-2.1.1.tgz
Glyph names in code: "house" (regular), "house-fill", "file-duotone".
"""
import os, re, shutil, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEST = os.path.join(ROOT, "Assets", "Icons")
KEEP = {"logo.svg"}  # app logo, not Phosphor


def string_literals():
    """Every quoted kebab-case literal in .xaml/.cs; the ones that name a Phosphor icon get copied."""
    names = set()
    for d, dirs, files in os.walk(ROOT):
        dirs[:] = [x for x in dirs if x not in ("bin", "obj", "tools", "Assets")]
        for f in files:
            if f.endswith((".xaml", ".cs")):
                names |= set(re.findall(r'"([a-z0-9]+(?:-[a-z0-9]+)*)"', open(os.path.join(d, f), encoding="utf-8").read()))
    return names


def main(assets):
    for f in os.listdir(DEST):
        if f.endswith(".svg") and f not in KEEP:
            os.remove(os.path.join(DEST, f))
    for name in sorted(string_literals()):
        weight = next((w for w in ("fill", "duotone") if name.endswith("-" + w)), "regular")
        src = os.path.join(assets, weight, name + ".svg")
        if name + ".svg" not in KEEP and os.path.exists(src):
            shutil.copy(src, os.path.join(DEST, name + ".svg"))
    print("icons:", sorted(f[:-4] for f in os.listdir(DEST)))


if __name__ == "__main__":
    main(sys.argv[1])
