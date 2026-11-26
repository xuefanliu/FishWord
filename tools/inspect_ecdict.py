#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查 ECDICT stardict.db 的表结构与数据分布，用于确认字段名与选词条件。

用法：python tools/inspect_ecdict.py <stardict.db 路径>
"""
import sqlite3
import sys


def main():
    db = sys.argv[1] if len(sys.argv) > 1 else "resources/downloads/ecdict-sqlite-28/stardict.db"
    con = sqlite3.connect(db)
    cur = con.cursor()

    print("== 表清单 ==")
    for (t,) in cur.execute("SELECT name FROM sqlite_master WHERE type='table'"):
        print(" ", t)

    print("\n== stardict 表字段 ==")
    for c in cur.execute("PRAGMA table_info(stardict)"):
        print(" ", c[1], c[2])

    print("\n== 总词数 ==")
    print(" ", cur.execute("SELECT COUNT(*) FROM stardict").fetchone()[0])

    print("\n== tag 分布（含 ielts 的词数） ==")
    print(" ", cur.execute("SELECT COUNT(*) FROM stardict WHERE tag LIKE '%ielts%'").fetchone()[0])

    print("\n== tag 取值样例 ==")
    for r in cur.execute(
            "SELECT DISTINCT tag FROM stardict WHERE tag != '' LIMIT 30"):
        print(" ", r[0])

    print("\n== 样例行（前 2 条，含所有字段） ==")
    cols = [c[1] for c in cur.execute("PRAGMA table_info(stardict)")]
    for row in cur.execute("SELECT * FROM stardict LIMIT 2"):
        for k, v in zip(cols, row):
            if isinstance(v, str) and len(v) > 120:
                v = v[:120] + "..."
            print(f"  {k}: {v}")
        print("  ---")

    con.close()


if __name__ == "__main__":
    main()
