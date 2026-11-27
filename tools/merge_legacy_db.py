#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把旧版遗留 db 的用户表合并到当前用户数据副本（便携版用户手动恢复学习记录用）。

适用场景：便携版旧数据写在「旧解压目录/resources/fishword.db」里，升级后新版本读
%LocalAppData%\\FishWord\\fishword.db 看不到旧记录——用本工具把旧库的用户表合并进来。

用法：
    python tools/merge_legacy_db.py --legacy <旧解压目录>/resources/fishword.db
"""

import argparse
import os
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

TABLES = [
    ("user_words",    "word_id, status, is_known, created_at, known_source"),
    ("daily_study",   "date, word_id, pass_count, is_extra"),
    ("memory_states", "word_id, algorithm, interval_days, ease_factor, repetitions, due_at, last_review_at"),
    ("review_logs",   "word_id, reviewed_at, rating, interval_days"),
    ("settings",      "key, value"),
]


def main():
    ap = argparse.ArgumentParser(description="把旧版遗留 db 的用户表合并到用户数据副本")
    ap.add_argument("--legacy", required=True, help="旧 db 路径（如 旧解压目录/resources/fishword.db）")
    args = ap.parse_args()

    target = os.path.join(os.environ.get("LOCALAPPDATA", ""), "FishWord", "fishword.db")
    if not os.path.exists(target):
        print(f"错误：用户数据副本不存在 {target}")
        sys.exit(1)
    if not os.path.exists(args.legacy):
        print(f"错误：旧库不存在 {args.legacy}")
        sys.exit(1)

    legacy_path = args.legacy.replace("'", "''")
    con = sqlite3.connect(target)
    cur = con.cursor()
    cur.execute(f"ATTACH DATABASE '{legacy_path}' AS legacy")
    for table, cols in TABLES:
        cur.execute(f"INSERT OR IGNORE INTO main.{table}({cols}) SELECT {cols} FROM legacy.{table}")
        print(f"{table}: 已合并（IGNORE 保留已有数据）")
    con.commit()
    cur.execute("DETACH DATABASE legacy")
    con.close()

    print(f"\n完成：旧库 {args.legacy} 的用户数据已合并到 {target}")
    print("（旧库文件未被修改；确认学习记录恢复后可自行删除旧解压目录。）")


if __name__ == "__main__":
    main()
