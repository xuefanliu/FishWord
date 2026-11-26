# -*- coding: utf-8 -*-
"""验证例句清洗修复：vector 例句 + 全库扫描坏字符残留。"""
import json
import re
import sqlite3

con = sqlite3.connect("resources/fishword.db")
cur = con.cursor()

print("== vector 例句 ==")
row = cur.execute("SELECT examples FROM words WHERE spelling = 'vector'").fetchone()
for ex in json.loads(row[0]) if row and row[0] else []:
    print(" ·", ex.get("en"))

print("\n== 全库坏字符扫描（&#实体 / { } / 反斜杠）==")
bad = 0
total = 0
for (examples,) in cur.execute(
        "SELECT examples FROM words WHERE examples IS NOT NULL AND examples != '[]'"):
    for ex in json.loads(examples):
        en = ex.get("en") or ""
        total += 1
        if re.search(r"&#\d|[{}\\]", en):
            bad += 1
            if bad <= 5:
                print(" 残留:", en[:100])
print(f"总例句 {total}，坏字符残留 {bad}")

con.close()
