"""Finds genuinely double-encoded Chinese in the Android sources.

The tell: a run of CJK characters whose GBK re-encoding is itself valid UTF-8, which decodes to the
text that was actually meant. Display problems in a console or tool output can look identical, so this
works only on raw bytes and prints the recovered text for a human to judge.
"""
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent
RUN = re.compile(r"[\u4e00-\u9fff\uff00-\uffef\u3000-\u303f]{3,}")

suspects = []
for path in root.rglob("*"):
    if path.suffix.lower() not in (".cs", ".xaml"):
        continue
    if any(part in ("bin", "obj", "packages") for part in path.parts):
        continue

    try:
        text = path.read_bytes().decode("utf-8", "strict")
    except UnicodeDecodeError as exc:
        suspects.append((path, f"NOT VALID UTF-8 ({exc})", ""))
        continue

    for match in RUN.finditer(text):
        run = match.group(0)
        try:
            recovered = run.encode("gbk").decode("utf-8")
        except (UnicodeEncodeError, UnicodeDecodeError):
            continue
        # Only report runs that actually look like Chinese after recovery.
        if re.search(r"[\u4e00-\u9fff]{2,}", recovered):
            line = text.count("\n", 0, match.start()) + 1
            suspects.append((path, f"line {line}", recovered))

if not suspects:
    print("no double-encoded Chinese found")
    sys.exit(0)

for path, where, recovered in suspects:
    rel = path.relative_to(root)
    print(f"{rel} [{where}] -> {recovered[:70]}")
print(f"\n{len(suspects)} suspect run(s)")
