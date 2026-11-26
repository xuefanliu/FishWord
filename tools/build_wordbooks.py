#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建内置词书：四级(CET4) / 六级(CET6) / 雅思(IELTS) / 托福(TOEFL)。

按 ECDICT tag 从单词池（words + word_tags）筛选，生成 books + book_words 有序映射，
并随机打乱词书内顺序（避免开头都是高频基础词，与现有雅思词书保持一致）。

幂等：
  - 已存在的词书（按 name 判断）跳过，不重建，避免打乱已学用户的词书顺序。
  - 已学词走 daily_study / memory_states（按 word_id），不受 book_words.seq 影响。

用法：
  python tools/build_wordbooks.py --db resources/fishword.db
"""

import argparse
import random
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# tag → 词书名（顺序即 UI 展示顺序）
BOOKS = [
    ("cet4", "四级核心"),
    ("cet6", "六级核心"),
    ("ielts", "雅思核心"),
    ("toefl", "托福核心"),
]
SEED = 20261112  # 固定随机种子，可复现


def ensure_schema(con):
    cur = con.cursor()
    cur.execute("""
    CREATE TABLE IF NOT EXISTS books (
        id INTEGER PRIMARY KEY,
        name TEXT,
        lang TEXT,
        is_builtin INTEGER DEFAULT 0
    )
    """)
    cur.execute("""
    CREATE TABLE IF NOT EXISTS book_words (
        book_id INTEGER NOT NULL,
        word_id INTEGER NOT NULL,
        seq INTEGER,
        PRIMARY KEY (book_id, word_id)
    )
    """)
    con.commit()


def build_book(con, tag, name, seed):
    """构建单本词书（已存在则跳过）。返回 (book_id, 成员数, 是否新建)。"""
    cur = con.cursor()
    row = cur.execute("SELECT id FROM books WHERE name = ?", (name,)).fetchone()
    if row:
        return row[0], 0, False

    cur.execute("INSERT INTO books(name, lang, is_builtin) VALUES(?, 'en', 1)", (name,))
    book_id = cur.lastrowid

    rows = cur.execute("""
        SELECT w.id FROM words w
        JOIN word_tags wt ON wt.word_id = w.id
        WHERE wt.tag = ?
        ORDER BY w.freq_rank DESC, w.frq ASC, w.spelling
    """, (tag,)).fetchall()

    rng = random.Random(seed)
    ids = [r[0] for r in rows]
    rng.shuffle(ids)  # 构建后直接打乱，与雅思一致

    for seq, wid in enumerate(ids):
        cur.execute("INSERT INTO book_words(book_id, word_id, seq) VALUES(?,?,?)", (book_id, wid, seq))
    con.commit()
    return book_id, len(ids), True


def main():
    ap = argparse.ArgumentParser(description="构建内置词书（四级/六级/雅思/托福）")
    ap.add_argument("--db", default="resources/fishword.db", help="fishword.db 路径")
    ap.add_argument("--seed", type=int, default=SEED, help="随机种子")
    args = ap.parse_args()

    con = sqlite3.connect(args.db)
    ensure_schema(con)
    for tag, name in BOOKS:
        book_id, n, created = build_book(con, tag, name, args.seed)
        action = "新建" if created else "跳过（已存在）"
        if created:
            print(f"[{action}] 词书「{name}」(id={book_id}, tag={tag}) 成员 {n} 个")
        else:
            cnt = con.execute("SELECT COUNT(1) FROM book_words WHERE book_id = ?", (book_id,)).fetchone()[0]
            print(f"[{action}] 词书「{name}」(id={book_id}) 现有 {cnt} 个")

    # 汇总
    print("\n== 词书总览 ==")
    for r in con.execute("""
        SELECT b.id, b.name, COUNT(bw.word_id)
        FROM books b LEFT JOIN book_words bw ON bw.book_id = b.id
        WHERE b.is_builtin = 1
        GROUP BY b.id ORDER BY b.id
    """):
        print(f"  id={r[0]}  {r[1]}  {r[2]} 词")
    con.close()


if __name__ == "__main__":
    main()
