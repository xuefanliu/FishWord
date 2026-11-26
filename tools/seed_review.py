#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
往复习队列（memory_states）塞入指定数量的词，用于立即测试复习功能。

正常路径：单词学满三遍后由应用自动写入 memory_states（次日到期）。
本脚本用于开发/测试时绕过"等第二天"：把词书里尚未进入复习队列的词
直接标记为"今天到期"，重启应用即可在首页看到复习任务。

用法：
  # 塞入 10 个词，今天到期（默认）
  python tools/seed_review.py --count 10

  # 塞入 20 个词，指定到期日期
  python tools/seed_review.py --count 20 --due-date 2026-11-11
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
    ap = argparse.ArgumentParser(description="往复习队列塞入到期词，方便测试复习功能")
    ap.add_argument("--db", default="resources/fishword.db", help="fishword.db 路径")
    ap.add_argument("--count", type=int, default=10, help="塞入词数（默认 10）")
    ap.add_argument("--due-date", default=None, help="到期日期 YYYY-MM-DD（默认今天）")
    args = ap.parse_args()

    due = args.due_date or date.today().isoformat()

    con = sqlite3.connect(args.db)
    cur = con.cursor()

    # 从词书（book_words）里取尚未进入复习队列的词，按词书顺序
    cur.execute("""
        SELECT bw.word_id
        FROM book_words bw
        LEFT JOIN memory_states ms ON ms.word_id = bw.word_id
        WHERE ms.word_id IS NULL
        ORDER BY bw.seq
        LIMIT ?
    """, (args.count,))
    rows = cur.fetchall()
    if not rows:
        print("没有可塞入的词（词书里的词可能都已在复习队列中）。")
        con.close()
        return

    for (wid,) in rows:
        cur.execute("""
            INSERT INTO memory_states(word_id, algorithm, interval_days, ease_factor, repetitions, due_at)
            VALUES(?, 'sm2', 0, 2.5, 0, ?)
            ON CONFLICT(word_id) DO NOTHING
        """, (wid, due))

    con.commit()
    con.close()
    print(f"已把 {len(rows)} 个词塞入复习队列，到期日 {due}。重启应用即可在首页看到复习任务。")


if __name__ == "__main__":
    main()
