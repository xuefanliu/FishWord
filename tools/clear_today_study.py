#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""清空「今日」的学习记录（daily_study），用于重复测试学习流程。

程序的学习记录唯一存放在用户数据副本 %LocalAppData%\\FishWord\\fishword.db
（开发库/安装目录的 resources\\fishword.db 只是只读种子，清它无效）。
本脚本默认清用户数据副本；--db 可指定其他库（一般不需要）。

只删当天记录，历史日期与复习数据（memory_states 等）不受影响。
运行前请关闭正在运行的 FishWord（避免 SQLite 锁冲突）。

用法：
    python tools/clear_today_study.py              # 清用户数据副本（默认，测试用）
    python tools/clear_today_study.py --db <路径>  # 清指定库
"""

import argparse
import os
import sqlite3
import sys
from datetime import date

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def default_db():
    user = os.path.join(os.environ.get("LOCALAPPDATA", ""), "FishWord", "fishword.db")
    return user if os.path.exists(user) else "resources/fishword.db"


def main():
    ap = argparse.ArgumentParser(description="清空今日学习记录（默认用户数据副本）")
    ap.add_argument("--db", default=None, help="fishword.db 路径（默认：用户数据副本）")
    args = ap.parse_args()

    db = args.db or default_db()
    print(f"目标库：{db}")

    today = date.today().isoformat()
    con = sqlite3.connect(db)
    cur = con.cursor()
    n = cur.execute("SELECT COUNT(*) FROM daily_study WHERE date = ?", (today,)).fetchone()[0]
    cur.execute("DELETE FROM daily_study WHERE date = ?", (today,))
    con.commit()
    left = cur.execute("SELECT COUNT(*) FROM daily_study").fetchone()[0]
    con.close()
    print(f"已清空 {today} 的学习记录（{n} 条）；daily_study 剩余 {left} 条（其他日期，不受影响）")


if __name__ == "__main__":
    main()
