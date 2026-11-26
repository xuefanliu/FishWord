#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""随机打乱词书内单词顺序（重排 book_words.seq）。

背景：词书按 collins 词频降序排列（freq_rank DESC），导致开头都是高频基础词；
随机打乱后简单/难词交错，避免「学来学去都是认识的词」。

只改数据不动代码：学习选词仍按 seq 顺序取（WordRepository.GetBookWords ORDER BY seq）。
已学过的词走 daily_study / memory_states，不受 seq 影响；洗牌只改变「新词」的学习顺序。

运行：
    python tools/shuffle_wordbook.py            # 打乱默认词书 id=1
    python tools/shuffle_wordbook.py --book 1   # 指定词书
    python tools/shuffle_wordbook.py --seed 42  # 固定随机种子（可复现）
"""
import argparse
import random
import sqlite3
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DB = ROOT / "resources" / "fishword.db"


def main() -> None:
    parser = argparse.ArgumentParser(description="随机打乱词书单词顺序")
    parser.add_argument("--book", type=int, default=1, help="词书 id（默认 1）")
    parser.add_argument("--seed", type=int, default=None, help="随机种子（可选，用于复现）")
    args = parser.parse_args()

    rng = random.Random(args.seed)

    conn = sqlite3.connect(DB)
    try:
        cur = conn.cursor()
        rows = cur.execute(
            "SELECT book_id, word_id FROM book_words WHERE book_id = ? ORDER BY seq",
            (args.book,),
        ).fetchall()
        rng.shuffle(rows)
        for seq, (bid, wid) in enumerate(rows):
            cur.execute(
                "UPDATE book_words SET seq = ? WHERE book_id = ? AND word_id = ?",
                (seq, bid, wid),
            )
        conn.commit()
        print(f"[shuffle] 词书 {args.book} 洗牌完成，共 {len(rows)} 词")

        head = cur.execute(
            "SELECT w.spelling FROM book_words bw JOIN words w ON w.id = bw.word_id "
            "WHERE bw.book_id = ? ORDER BY bw.seq LIMIT 20",
            (args.book,),
        ).fetchall()
        print("[shuffle] 新顺序前 20 词：", ", ".join(r[0] for r in head))
    finally:
        conn.close()


if __name__ == "__main__":
    main()
