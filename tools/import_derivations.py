#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 kaikki（Wiktionary）英文 JSONL 提取派生词关系，写入 word_derivations 表。

数据源：kaikki.org-dictionary-English.jsonl 的 `derived` 字段
（每个词条列出从它派生的词，如 confident → confidently/confidentness）。
规则：
  - 只保留「单词」（无空格/连字符），过滤 "confidence game" 这类短语；
  - 源词与派生词都必须存在于 words 表（词库内交集）；
  - 幂等：清空 word_derivations 后全量重导。

用法：
  python tools/import_derivations.py \
    --kaikki resources/downloads/kaikki.org-dictionary-English.jsonl \
    --db resources/fishword.db
"""

import argparse
import json
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


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
    CREATE TABLE IF NOT EXISTS word_derivations (
        word_id INTEGER NOT NULL,
        derived_word_id INTEGER NOT NULL,
        PRIMARY KEY (word_id, derived_word_id)
    )
    """)
    cur.execute("CREATE INDEX IF NOT EXISTS idx_deriv_derived ON word_derivations(derived_word_id)")
    con.commit()
    con.close()


def main():
    ap = argparse.ArgumentParser(description="从 kaikki 提取派生词关系写入 word_derivations")
    ap.add_argument("--kaikki", required=True, help="kaikki 英文词典 JSONL 路径")
    ap.add_argument("--db", default="resources/fishword.db", help="fishword.db 路径")
    args = ap.parse_args()

    word_map = load_words(args.db)
    ensure_schema(args.db)
    print(f"words 表 {len(word_map)} 词")

    con = sqlite3.connect(args.db)
    cur = con.cursor()
    cur.execute("DELETE FROM word_derivations")

    n_src = 0
    n_edges = 0
    n_phrase_skipped = 0
    with open(args.kaikki, "r", encoding="utf-8") as f:
        for i, line in enumerate(f, 1):
            if not line.strip():
                continue
            try:
                entry = json.loads(line)
            except json.JSONDecodeError:
                continue
            if (entry.get("lang_code") or entry.get("lang")) not in ("en", "English"):
                continue
            word = (entry.get("word") or "").strip().lower()
            wid = word_map.get(word)
            if wid is None:
                continue  # 源词不在词库
            n_src += 1

            for d in entry.get("derived") or []:
                dw = (d.get("word") or "").strip().lower()
                if not dw or " " in dw or "-" in dw or dw == word:
                    if dw:
                        n_phrase_skipped += 1
                    continue
                did = word_map.get(dw)
                if did is None:
                    continue  # 派生词不在词库
                cur.execute(
                    "INSERT OR IGNORE INTO word_derivations(word_id, derived_word_id) VALUES(?,?)",
                    (wid, did))
                n_edges += 1

            if i % 500000 == 0:
                con.commit()
                print(f"  已处理 {i:,} 行，边 {n_edges:,}")

    con.commit()
    n_pairs = cur.execute("SELECT COUNT(*) FROM word_derivations").fetchone()[0]
    n_roots = cur.execute(
        "SELECT COUNT(DISTINCT word_id) FROM word_derivations").fetchone()[0]
    print(f"\n完成：词库内有派生关系的源词 {n_roots:,}，派生边 {n_pairs:,}（短语跳过 {n_phrase_skipped:,}）")
    con.close()


if __name__ == "__main__":
    main()
