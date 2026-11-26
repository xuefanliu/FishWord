# -*- coding: utf-8 -*-
"""验证 kaikki entry 的 derived（派生词）字段结构与内容。"""
import json
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

targets = {"confident", "confidence", "continue", "chord", "normal"}
found = {}
n_with_derived = 0
n_entries = 0

with open("resources/downloads/kaikki.org-dictionary-English.jsonl", "r", encoding="utf-8") as f:
    for line in f:
        if not line.strip():
            continue
        n_entries += 1
        if n_entries > 300000:
            break
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            continue
        if (e.get("lang_code") or e.get("lang")) not in ("en", "English"):
            continue
        if e.get("derived"):
            n_with_derived += 1
        w = (e.get("word") or "").strip().lower()
        if w in targets and w not in found:
            found[w] = e.get("derived")

print(f"扫描 {n_entries:,} 英文词条，含 derived 的：{n_with_derived}")
print()
for w in sorted(targets):
    d = found.get(w)
    if d is None:
        print(f"[{w}] 未找到或 derived 为空")
        continue
    print(f"[{w}] derived 结构样例（前 8 条）：")
    for item in d[:8]:
        if isinstance(item, dict):
            print(f"    keys={list(item.keys())[:6]} word={item.get('word')}")
        else:
            print(f"    {item!r}")
    print()
