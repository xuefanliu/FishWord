#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从 ECDICT stardict.db 导入 word 条目，构建 FishWord 单词池（fishword.db 的 words 表）。

职责（单一）：
  - 只导入 ECDICT 中"有 tag"的词条（约 1.5 万），作为全局单词池。
  - 只写基础字段：拼写、音标、中文/英文释义、词性、词频、tag。
  - 不生成语音、不填例句/词根——这些由词书脚本（import_ielts_wordbook.py）按需补全。

幂等：
  - 可重复执行；已存在词条更新基础字段，新词条插入，不 DROP 重建，
    避免清掉词书脚本后续补全的例句/读音/词根（examples/mnemonics/audio_path）。

用法：
  python tools/import_ecdict_words.py \
    --stardict resources/downloads/ecdict-sqlite-28/stardict.db \
    --out resources/fishword.db
"""

import argparse
import json
import os
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def _int_or_none(s):
    try:
        return int(s)
    except (TypeError, ValueError):
        return None


def load_stardict_tagged(db_path):
    """读取 stardict.db 中所有"有 tag"的词条，按 spelling 去重（保留首条）。"""
    con = sqlite3.connect(db_path)
    con.row_factory = sqlite3.Row
    rows = {}
    cur = con.execute("SELECT * FROM stardict WHERE tag != '' AND tag IS NOT NULL")
    for r in cur:
        w = (r["word"] or "").strip()
        if not w:
            continue
        if w not in rows:
            rows[w] = dict(r)
    con.close()
    return list(rows.values())


def to_record(r):
    """把一行 ECDICT 转成单词池记录（只含基础字段）。"""
    w = (r.get("word") or "").strip()
    definitions = [{
        "pos": (r.get("pos") or "").strip(),
        "meaning_cn": (r.get("translation") or "").strip(),
        "meaning_en": (r.get("definition") or "").strip(),
    }]
    collins = _int_or_none(r.get("collins"))
    return {
        "spelling": w,
        "phonetic_uk": (r.get("phonetic") or "").strip() or None,
        "phonetic_us": None,
        "definitions": json.dumps(definitions, ensure_ascii=False),
        "collins": collins,
        "oxford": _int_or_none(r.get("oxford")),
        "frq": _int_or_none(r.get("frq")),
        "freq_rank": collins or 0,   # 越大越常用（collins 1~5 星）
        "tags": [t for t in (r.get("tag") or "").split() if t],
    }


def ensure_schema(con):
    """确保 words / word_tags 表存在，并对旧库做 enriched 列增量迁移。"""
    cur = con.cursor()
    cur.execute("""
    CREATE TABLE IF NOT EXISTS words (
        id INTEGER PRIMARY KEY,
        spelling TEXT NOT NULL UNIQUE,
        phonetic_uk TEXT,
        phonetic_us TEXT,
        definitions TEXT,
        examples TEXT,
        mnemonics TEXT,
        audio_path TEXT,
        audio_url TEXT,
        collins INTEGER,
        oxford INTEGER,
        frq INTEGER,
        freq_rank INTEGER,
        enriched INTEGER NOT NULL DEFAULT 0
    )
    """)
    cols = [c[1] for c in cur.execute("PRAGMA table_info(words)")]
    if "enriched" not in cols:
        cur.execute("ALTER TABLE words ADD COLUMN enriched INTEGER NOT NULL DEFAULT 0")
    cur.execute("""
    CREATE TABLE IF NOT EXISTS word_tags (
        word_id INTEGER NOT NULL,
        tag TEXT NOT NULL,
        PRIMARY KEY (word_id, tag)
    )
    """)
    cur.execute("CREATE INDEX IF NOT EXISTS idx_word_tags_tag ON word_tags(tag)")
    con.commit()


def upsert_words(records, out_db):
    """幂等写入单词池：基础字段更新，重数据字段（examples/mnemonics/audio/enriched）不覆盖。"""
    os.makedirs(os.path.dirname(out_db) or ".", exist_ok=True)
    con = sqlite3.connect(out_db)
    ensure_schema(con)
    cur = con.cursor()
    for r in records:
        cur.execute("""
            INSERT INTO words(spelling, phonetic_uk, phonetic_us, definitions, collins, oxford, frq, freq_rank)
            VALUES (?,?,?,?,?,?,?,?)
            ON CONFLICT(spelling) DO UPDATE SET
                phonetic_uk = excluded.phonetic_uk,
                phonetic_us = excluded.phonetic_us,
                definitions  = excluded.definitions,
                collins      = excluded.collins,
                oxford       = excluded.oxford,
                frq          = excluded.frq,
                freq_rank    = excluded.freq_rank
        """, (r["spelling"], r["phonetic_uk"], r["phonetic_us"], r["definitions"],
              r["collins"], r["oxford"], r["frq"], r["freq_rank"]))
        wid = cur.execute("SELECT id FROM words WHERE spelling = ?", (r["spelling"],)).fetchone()[0]
        for tag in r["tags"]:
            cur.execute("INSERT OR IGNORE INTO word_tags(word_id, tag) VALUES(?,?)", (wid, tag))
    con.commit()
    con.close()


def main():
    ap = argparse.ArgumentParser(description="从 ECDICT 导入 word 条目，构建单词池")
    ap.add_argument("--stardict", required=True, help="ECDICT stardict.db 路径")
    ap.add_argument("--out", default="resources/fishword.db", help="输出 SQLite 路径")
    args = ap.parse_args()

    rows = load_stardict_tagged(args.stardict)
    records = [to_record(r) for r in rows]
    records.sort(key=lambda x: (-x["freq_rank"], x["frq"] if x["frq"] else 1 << 30, x["spelling"]))
    print(f"从 ECDICT 读入有 tag 词条 {len(records)} 个")

    upsert_words(records, args.out)
    print(f"已写入单词池 → {args.out}（幂等，基础字段更新）")


if __name__ == "__main__":
    main()
