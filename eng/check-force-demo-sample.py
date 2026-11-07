import os
import re
import sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
guide = open(os.path.join(root, "docs", "USING-ECS.md"), encoding="utf-8").read()
sample = os.path.join(root, "com.tl.unity", "Samples~", "ForceDemo")
squash = lambda text: re.sub(r"\s+", "", text)
blocks = {squash(m.group(1)) for m in re.finditer(r"```csharp\n(.*?)\n```", guide, re.S)}
legacy = re.compile(r"TimelineLibraryAsset|TimelinePlayerAuthoring|TimelineBridge|Kinds\.g\.cs|library\.json|\btlb\b|Tl\.Unity\.Generate|RequiresKinds|KindProfile")
failed = 0
for folder, files in (("Force", None), ("Control", None)):
    for name in sorted(os.listdir(os.path.join(sample, folder))):
        if not name.endswith(".cs"):
            continue
        text = open(os.path.join(sample, folder, name), encoding="utf-8").read()
        same = squash(text) in blocks
        print(("same  " if same else "DIFF  ") + folder + "/" + name)
        failed += 0 if same else 1
for base, _, names in os.walk(sample):
    for name in names:
        if name.endswith((".cs", ".asmdef", ".json")) and legacy.search(open(os.path.join(base, name), encoding="utf-8").read()):
            print("LEGACY  " + os.path.relpath(os.path.join(base, name), sample))
            failed += 1
sys.exit(1 if failed else 0)
