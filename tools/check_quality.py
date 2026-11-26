#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查 fishword.db 拼写数据质量，辅助发现需清洗的异常词条。"""
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def main():
    db = sys.argv[1] if len(sys.argv) > 1 else "resources/fishword.db"
    con = sqlite3.connect(db)
    cur = con.cursor()

    def count(where):
        return cur.execute(f"SELECT COUNT(*) FROM words WHERE {where}").fetchone()[0]

    print("含空格:", count("spelling GLOB '* *'"))
    print("含撇号:", count("spelling GLOB \"*'*\""))
    print("含连字符:", count("spelling GLOB '*-*'"))
    print("含数字:", count("spelling GLOB '*[0-9]*'"))
    print("首字母大写:", count("spelling GLOB '[A-Z]*'"))
    print("拼写样例(前 15):", [r[0] for r in cur.execute("SELECT spelling FROM words LIMIT 15")])
    print("异常拼写样例:", [r[0] for r in cur.execute(
        "SELECT spelling FROM words WHERE spelling GLOB \"*'*\" OR spelling GLOB '* *' OR spelling GLOB '*[0-9]*' LIMIT 20")])
    con.close()


if __name__ == "__main__":
    main()
