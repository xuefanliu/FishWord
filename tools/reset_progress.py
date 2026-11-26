#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""重置学习/复习进度，并随机塞入 N 个词到复习队列，用于重头测试。

清空：daily_study（学习遍数）、memory_states（复习调度）、review_logs（复习日志）。
不清：user_words（熟词标记，含 505 系统排除）、settings（如 basic_505_marked）。

随机塞入：从词书 book_words 随机挑 N 个「非熟词」（排除 is_known=1），写入 memory_states，今天到期。

用法：
    python tools/reset_progress.py            # 清空 + 随机 20 个复习词
    python tools/reset_progress.py --count 20
"""
import argparse
import sqlite3
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DB = ROOT / "resources" / "fishword.db"


def main() -> None:
    parser = argparse.ArgumentParser(description="重置学习/复习进度并随机塞入复习词")
    parser.add_argument("--count", type=int, default=20, help="随机塞入复习队列的词数（默认 20）")
    parser.add_argument("--book", type=int, default=1, help="词书 id（默认 1）")
    args = parser.parse_args()

    due = date.today().isoformat()
    conn = sqlite3.connect(DB)
    try:
        cur = conn.cursor()

        # 1. 清空学习/复习记录
        cur.execute("DELETE FROM daily_study")
        cur.execute("DELETE FROM memory_states")
        cur.execute("DELETE FROM review_logs")

        # 2. 随机挑 N 个非熟词（排除 is_known=1，即 505 系统排除 + 用户熟词）
        rows = cur.execute(
            """
            SELECT bw.word_id
            FROM book_words bw
            LEFT JOIN user_words uw ON uw.word_id = bw.word_id
            WHERE bw.book_id = ?
              AND COALESCE(uw.is_known, 0) = 0
            ORDER BY RANDOM()
            LIMIT ?
            """,
            (args.book, args.count),
        ).fetchall()

        # 3. 写入复习队列（今天到期，SM-2 初始态）
        for (wid,) in rows:
            cur.execute(
                """
                INSERT INTO memory_states(word_id, algorithm, interval_days, ease_factor, repetitions, due_at)
                VALUES(?, 'sm2', 0, 2.5, 0, ?)
                """,
                (wid, due),
            )

        conn.commit()
        print("[reset] 已清空 daily_study / memory_states / review_logs")
        print(f"[reset] 随机塞入复习队列 {len(rows)} 词，到期日 {due}")
        head = cur.execute(
            "SELECT w.spelling FROM memory_states ms JOIN words w ON w.id = ms.word_id "
            "ORDER BY ms.due_at, w.spelling"
        ).fetchall()
        print("[reset] 复习词：", ", ".join(r[0] for r in head))
    finally:
        conn.close()


if __name__ == "__main__":
    main()
