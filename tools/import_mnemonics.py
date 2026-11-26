#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 OpenEtymology 考试单词本 PDF 解析词根/语素/词源，写入 words.mnemonics + roots 表。

数据源：OpenEtymology（github.com/openetymology/OpenEtymology，CC BY-SA 4.0）的
CET4/CET6/TEM8/TOEFL/GRE8000 五个单词本 PDF。PDF 词条格式：
    1. abandon
    UK /əˈbændən/
    Definitions
    - vt. 抛弃；放弃（责任、计划等）
    Morphemes
    - a: 加强
    - bandon: 控制
    Etymology
    最初在古法语中为 abandoner（……）
    Examples
    - The crew had to abandon the sinking ship. / 船员不得不弃离沉船。

规则：
  - 语素拆分 → words.mnemonics.root（每条 "piece（gloss）"），词源叙述 → mnemonics.etymology。
  - 主词根识别（宁缺毋滥）：仅多语素词；排除 gloss 含"后缀/前缀"的语素；取最长；≥3 字符。
  - roots 表：词根多词共享（root 文本 UNIQUE 去重，保留第一条 gloss）；words.root_id 记主词根。
  - enriched 位 4：PDF 中出现的词一律置位（含撇掉的，表示"已处理"避免重复回查）。

用法：
  python tools/import_mnemonics.py --db resources/fishword.db
"""

import argparse
import json
import re
import sqlite3
import sys

from pypdf import PdfReader

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

PDFS = [
    "resources/downloads/openetymology/CET4.pdf",
    "resources/downloads/openetymology/CET6.pdf",
    "resources/downloads/openetymology/TEM8.pdf",
    "resources/downloads/openetymology/TOEFL.pdf",
    "resources/downloads/openetymology/GRE8000.pdf",
]
ROOT_BIT = 4

_ENTRY_HEAD = re.compile(r"^\d+\.\s+(\S+)\s*$")
_MORPH = re.compile(r"^-\s*(.+?):\s*(.+)$")
_PAGE_HEADER = re.compile(r"^\s*OpenEtymology Word Books · \d+\s*$", re.M)


def clean_text(s):
    return re.sub(r"\s+", " ", s).strip()


def parse_entry_block(block):
    """解析单个词条文本块 → (word, morphemes[(piece, gloss)], etymology)。"""
    word = None
    morphemes = []
    etymology = []
    section = None
    for line in block.splitlines():
        m = _ENTRY_HEAD.match(line.strip())
        if m and word is None:
            word = m.group(1).strip().lower()
            continue
        if line.strip() in ("Definitions", "Morphemes", "Etymology", "Examples"):
            section = line.strip()
            continue
        if word is None:
            continue
        if section == "Morphemes":
            m = _MORPH.match(line.strip())
            if m:
                morphemes.append((m.group(1).strip(), m.group(2).strip()))
        elif section == "Etymology":
            if line.strip():
                etymology.append(line.strip())
    if word is None:
        return None
    return word, morphemes, clean_text(" ".join(etymology))


def pick_main_root(morphemes):
    """主词根启发式：多语素词；排除前后缀语素；取最长（并列长度取靠后，前缀通常在前）；≥3 字符（宁缺毋滥）。"""
    if len(morphemes) < 2:
        return None
    cands = [(i, p, g) for i, (p, g) in enumerate(morphemes)
             if "后缀" not in g and "前缀" not in g]
    if not cands:
        return None
    _, piece, gloss = max(cands, key=lambda c: (len(c[1]), c[0]))
    if len(piece) < 3:
        return None
    return piece, gloss


def load_words(db):
    """返回 {小写拼写: word_id}。"""
    con = sqlite3.connect(db)
    cur = con.cursor()
    m = {str(r[0]).strip().lower(): r[1] for r in cur.execute("SELECT spelling, id FROM words")}
    con.close()
    return m


def ensure_schema(db):
    con = sqlite3.connect(db)
    cur = con.cursor()
    cur.execute("""
    CREATE TABLE IF NOT EXISTS roots (
        id INTEGER PRIMARY KEY,
        root TEXT NOT NULL UNIQUE,
        gloss TEXT,
        lang TEXT
    )
    """)
    cols = [c[1] for c in cur.execute("PRAGMA table_info(words)")]
    if "root_id" not in cols:
        cur.execute("ALTER TABLE words ADD COLUMN root_id INTEGER")
    cur.execute("CREATE INDEX IF NOT EXISTS idx_words_root ON words(root_id)")
    con.commit()
    con.close()


def main():
    ap = argparse.ArgumentParser(description="从 OpenEtymology PDF 导入词根助记")
    ap.add_argument("--db", default="resources/fishword.db")
    ap.add_argument("--pdfs", nargs="*", default=PDFS)
    args = ap.parse_args()

    word_map = load_words(args.db)
    ensure_schema(args.db)
    print(f"words 表 {len(word_map)} 词")

    entries = {}   # word -> (morphemes, etymology)，多本 PDF 去重取第一条
    seen_blocks = 0
    for pdf in args.pdfs:
        reader = PdfReader(pdf)
        full = []
        for page in reader.pages:
            t = page.extract_text() or ""
            t = _PAGE_HEADER.sub("", t)
            full.append(t)
        text = "\n".join(full)
        blocks = re.split(r"(?=^\d+\.\s+\S+\s*$)", text, flags=re.M)
        for block in blocks:
            parsed = parse_entry_block(block)
            if not parsed:
                continue
            word, morphemes, etymology = parsed
            seen_blocks += 1
            if word in word_map and word not in entries:
                entries[word] = (morphemes, etymology)

    print(f"解析词条 {seen_blocks}，命中词库 {len(entries)} 词")

    con = sqlite3.connect(args.db)
    cur = con.cursor()

    n_morph = 0     # 有语素拆分的词
    n_etym = 0      # 有词源说明的词
    n_main = 0      # 有主词根
    n_skip = 0      # 撇掉（无主词根）
    root_cache = {}  # root 文本 -> root_id

    for word, (morphemes, etymology) in entries.items():
        wid = word_map[word]
        mnemonics = {"root": [f"{p}（{g}）" for p, g in morphemes],
                     "collocation": [], "related": []}
        if etymology:
            mnemonics["etymology"] = etymology
            n_etym += 1
        if morphemes:
            n_morph += 1

        main = pick_main_root(morphemes)
        root_id = None
        if main is not None:
            piece, gloss = main
            if piece not in root_cache:
                cur.execute(
                    "INSERT INTO roots(root, gloss) VALUES(?,?) ON CONFLICT(root) DO NOTHING",
                    (piece, gloss))
                rid = cur.execute("SELECT id FROM roots WHERE root = ?", (piece,)).fetchone()[0]
                root_cache[piece] = rid
            else:
                rid = root_cache[piece]
            root_id = rid
            n_main += 1
        else:
            n_skip += 1

        cur.execute(
            "UPDATE words SET mnemonics = ?, root_id = ?, enriched = enriched | 4 WHERE id = ?",
            (json.dumps(mnemonics, ensure_ascii=False), root_id, wid))

    con.commit()
    n_roots = cur.execute("SELECT COUNT(*) FROM roots").fetchone()[0]
    print(f"完成：语素拆分 {n_morph}，词源说明 {n_etym}，主词根 {n_main}，撇掉 {n_skip}")
    print(f"roots 表：{n_roots} 个词根")
    con.close()


if __name__ == "__main__":
    main()
