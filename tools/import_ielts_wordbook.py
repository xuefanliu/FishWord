#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
构建 ielts 词书，并完善词书成员单词的详细数据（例句/读音/词根）。

职责：
  1. 从单词池（words + word_tags）按 tag=ielts 筛选单词，构建词书 books + book_words。
  2. 检查词书成员数据完整性（enriched 位掩码），缺例句/读音/词根则调用对应补全接口。
     补全接口当前为占位，后续接入 Wiktionary(kaikki)/OpenEtymology/真人音频 或 TTS。

enriched 位掩码：
  1 = 例句(examples)，2 = 读音(audio)，4 = 词根(mnemonics.root)

用法：
  python tools/import_ielts_wordbook.py \
    --db resources/fishword.db --tag ielts --book-name 雅思核心
"""

import argparse
import json
import os
import re
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

EXAMPLES_BIT = 1
AUDIO_BIT = 2
ROOT_BIT = 4


# ------------------------------------------------------------------ 补全接口（占位）
# 后续把具体数据源接进来：例句→Wiktionary(kaikki)；词根→OpenEtymology；读音→真人音频或 TTS。

def fetch_examples(spelling):
    """拉取例句。TODO: 接入 Wiktionary(kaikki) 等数据源。"""
    return []


_AUDIO_SAFE = re.compile(r'[\\/:*?"<>|\s]+')
AUDIO_DIR = "resources/audio"


def safe_name(word):
    return _AUDIO_SAFE.sub("_", word).strip("_") or "word"


def fetch_audio(spelling):
    """定位/拉取读音文件。当前：复用已有 TTS 音频；TODO: 接真人音频或 TTS 生成。"""
    fname = safe_name(spelling) + ".mp3"
    full = os.path.join(AUDIO_DIR, fname)
    if os.path.exists(full) and os.path.getsize(full) > 0:
        return f"audio/{fname}"
    return None


def fetch_root(spelling):
    """拉取词根词缀标注。TODO: 接入 OpenEtymology 等数据源。"""
    return []


# ------------------------------------------------------------------ 词书构建

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


def _get_or_create_book(cur, name, lang="en", is_builtin=1):
    row = cur.execute("SELECT id FROM books WHERE name = ?", (name,)).fetchone()
    if row:
        return row[0]
    cur.execute("INSERT INTO books(name, lang, is_builtin) VALUES(?,?,?)", (name, lang, is_builtin))
    return cur.lastrowid


def build_book(con, tag, book_name):
    """按 tag 从单词池筛选，构建（重建）词书成员。返回 (book_id, 成员数)。"""
    cur = con.cursor()
    book_id = _get_or_create_book(cur, book_name)
    cur.execute("DELETE FROM book_words WHERE book_id = ?", (book_id,))
    rows = cur.execute("""
        SELECT w.id FROM words w
        JOIN word_tags wt ON wt.word_id = w.id
        WHERE wt.tag = ?
        ORDER BY w.freq_rank DESC, w.frq ASC, w.spelling
    """, (tag,)).fetchall()
    for seq, (wid,) in enumerate(rows):
        cur.execute("INSERT INTO book_words(book_id, word_id, seq) VALUES(?,?,?)", (book_id, wid, seq))
    con.commit()
    return book_id, len(rows)


# ------------------------------------------------------------------ 补全

def enrich_book(con, book_id):
    """检查词书成员完整性，缺哪类补哪类，并置 enriched 位。"""
    cur = con.cursor()
    rows = cur.execute("""
        SELECT w.id, w.spelling, w.enriched
        FROM book_words bw JOIN words w ON w.id = bw.word_id
        WHERE bw.book_id = ? ORDER BY bw.seq
    """, (book_id,)).fetchall()

    stats = {"examples": 0, "audio": 0, "root": 0, "total": len(rows)}
    for wid, spelling, enriched in rows:
        new_enriched = enriched

        if not (enriched & EXAMPLES_BIT):
            exs = fetch_examples(spelling)
            if exs:
                cur.execute("UPDATE words SET examples = ? WHERE id = ?",
                            (json.dumps(exs, ensure_ascii=False), wid))
            new_enriched |= EXAMPLES_BIT
            stats["examples"] += 1

        if not (enriched & AUDIO_BIT):
            audio = fetch_audio(spelling)
            if audio is not None:
                cur.execute("UPDATE words SET audio_path = ? WHERE id = ?", (audio, wid))
            new_enriched |= AUDIO_BIT
            stats["audio"] += 1

        if not (enriched & ROOT_BIT):
            roots = fetch_root(spelling)
            if roots:
                mnemonics = {"root": roots, "collocation": [], "related": []}
                cur.execute("UPDATE words SET mnemonics = ? WHERE id = ?",
                            (json.dumps(mnemonics, ensure_ascii=False), wid))
            new_enriched |= ROOT_BIT
            stats["root"] += 1

        if new_enriched != enriched:
            cur.execute("UPDATE words SET enriched = ? WHERE id = ?", (new_enriched, wid))

    con.commit()
    return stats


def main():
    ap = argparse.ArgumentParser(description="构建 ielts 词书并完善成员数据")
    ap.add_argument("--db", default="resources/fishword.db", help="单词池 SQLite 路径")
    ap.add_argument("--tag", default="ielts", help="词书对应的 tag")
    ap.add_argument("--book-name", default="雅思核心", help="词书名称")
    args = ap.parse_args()

    con = sqlite3.connect(args.db)
    ensure_schema(con)

    book_id, n = build_book(con, args.tag, args.book_name)
    print(f"[1/2] 词书「{args.book_name}」(id={book_id}) 成员 {n} 个（tag={args.tag}）")

    stats = enrich_book(con, book_id)
    print(f"[2/2] 补全：例句 {stats['examples']}，读音 {stats['audio']}，词根 {stats['root']}（共 {stats['total']} 词）")
    con.close()


if __name__ == "__main__":
    main()
