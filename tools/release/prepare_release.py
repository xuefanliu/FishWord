#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从开发库生成发布库：复制副本 → 清空用户数据 → 预填 505 系统熟词 → 完整性校验。

开发库 resources/fishword.db 只读（仅被复制），所有写操作在副本上进行，
开发库的学习记录/熟词/设置不受任何影响。发布库 = 一次性管线产物。

清空内容：daily_study / memory_states / review_logs / user_words / settings。
预填内容：basic_505.json 名单中命中词库的词 → user_words（known_source='system'）。

用法：
    python tools/release/prepare_release.py \
        --dev-db resources/fishword.db \
        --basic505 resources/basic_505.json \
        --out resources/fishword_release.db
"""

import argparse
import json
import shutil
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

USER_TABLES = ("daily_study", "memory_states", "review_logs", "user_words", "settings")


def load505(path):
    with open(path, encoding="utf-8") as f:
        return [str(w).strip().lower() for w in json.load(f)]


def main():
    ap = argparse.ArgumentParser(description="从开发库生成发布库（清空用户数据 + 预填 505 + 校验）")
    ap.add_argument("--dev-db", default="resources/fishword.db", help="开发库路径（只读）")
    ap.add_argument("--basic505", default="resources/basic_505.json", help="505 基础词名单")
    ap.add_argument("--out", default="resources/fishword_release.db", help="发布库输出路径")
    args = ap.parse_args()

    print(f"复制开发库 → {args.out}")
    shutil.copy2(args.dev_db, args.out)

    con = sqlite3.connect(args.out)
    cur = con.cursor()

    for t in USER_TABLES:
        n = cur.execute(f"SELECT COUNT(*) FROM {t}").fetchone()[0]
        cur.execute(f"DELETE FROM {t}")
        print(f"  清空 {t}（原 {n} 行）")
    con.commit()

    words505 = load505(args.basic505)
    hit = 0
    for w in words505:
        row = cur.execute("SELECT id FROM words WHERE spelling = ?", (w,)).fetchone()
        if row:
            cur.execute(
                "INSERT INTO user_words(word_id, status, is_known, known_source, created_at) "
                "VALUES(?, 'known', 1, 'system', datetime('now', 'localtime'))",
                (row[0],))
            hit += 1
    con.commit()
    print(f"预填 505 系统熟词：名单 {len(words505)} 词，命中词库 {hit}")

    print("\n== 发布库校验 ==")
    checks = [
        ("words 总词数", "SELECT COUNT(*) FROM words"),
        ("四本词书(books)", "SELECT COUNT(*) FROM books WHERE is_builtin = 1"),
        ("词书映射(book_words)", "SELECT COUNT(*) FROM book_words"),
        ("标签(word_tags)", "SELECT COUNT(*) FROM word_tags"),
        ("词根表(roots)", "SELECT COUNT(*) FROM roots"),
        ("派生关系(word_derivations)", "SELECT COUNT(*) FROM word_derivations"),
        ("有例句的词", "SELECT COUNT(*) FROM words WHERE examples IS NOT NULL AND examples != '' AND examples != '[]'"),
        ("有词源/语素的词", "SELECT COUNT(*) FROM words WHERE mnemonics IS NOT NULL AND mnemonics != '' AND mnemonics != '{}'"),
        ("audio_path 缺失", "SELECT COUNT(*) FROM words WHERE audio_path IS NULL OR audio_path = ''"),
        ("user_words(system)", "SELECT COUNT(*) FROM user_words WHERE known_source = 'system'"),
        ("user_words(user) 残留", "SELECT COUNT(*) FROM user_words WHERE known_source = 'user'"),
        ("daily_study 残留", "SELECT COUNT(*) FROM daily_study"),
        ("memory_states 残留", "SELECT COUNT(*) FROM memory_states"),
        ("review_logs 残留", "SELECT COUNT(*) FROM review_logs"),
        ("settings 残留", "SELECT COUNT(*) FROM settings"),
    ]
    ok = True
    for label, sql in checks:
        v = cur.execute(sql).fetchone()[0]
        print(f"  {label}: {v}")
    con.close()

    print(f"\n发布库已生成：{args.out}")


if __name__ == "__main__":
    main()
