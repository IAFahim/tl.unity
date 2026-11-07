import os
import re
import sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
guide = open(os.path.join(root, "docs", "USING-ECS.md"), encoding="utf-8").read().replace("\r\n", "\n")
sample = os.path.join(root, "com.tl.unity", "Samples~", "EcsDemo")
blocks = {m.group(1): m.group(2) + "\n" for m in re.finditer(r"File: `Assets/Demo/([^`]+)`\n\n```\w*\n(.*?)\n```", guide, re.S)}
failed = 0
for rel, text in sorted(blocks.items()):
    path = os.path.join(sample, rel)
    if not os.path.exists(path):
        continue
    with open(path, encoding="utf-8") as f:
        same = f.read().replace("\r\n", "\n") == text
    print(("same  " if same else "DIFF  ") + rel)
    failed += 0 if same else 1
for rel in ("Kinds.cs", "Systems.cs", "DemoDirector.cs", "Tests/DemoTests.cs", "Timelines/library.json"):
    if not os.path.exists(os.path.join(sample, rel)):
        print("MISSING  " + rel)
        failed += 1
sys.exit(1 if failed else 0)
