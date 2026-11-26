#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""清空开发库「今日」的学习记录（daily_study），用于重复测试学习流程。

只删当天记录，历史日期与复习数据（memory_states 等）不受影响。
运行前请关闭正在运行的 FishWord（避免 SQLite 锁冲突）。

用法：
    python tools/clear_today_study.py --db resources/fishword.db
"""

import argparse
import sqlite3
import sys
from datetime import date

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def main():
    ap = argparse.ArgumentParser(description="清空开发库今日学习记录（daily_study）")
    ap.add_argument("--db", default="resources/fishword.db", help="fishword.db 路径")
    args = ap.parse_args()

    today = date.today().isoformat()
    con = sqlite3.connect(args.db)
    cur = con.cursor()
    n = cur.execute("SELECT COUNT(*) FROM daily_study WHERE date = ?", (today,)).fetchone()[0]
    cur.execute("DELETE FROM daily_study WHERE date = ?", (today,))
    con.commit()
    left = cur.execute("SELECT COUNT(*) FROM daily_study").fetchone()[0]
    con.close()
    print(f"已清空 {today} 的学习记录（{n} 条）；daily_study 剩余 {left} 条（其他日期，不受影响）")


if __name__ == "__main__":
    main()
