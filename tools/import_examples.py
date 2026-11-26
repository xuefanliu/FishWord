#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 kaikki（Wiktionary）英文词典 JSONL 提取例句，写入 words.examples。

数据源：kaikki.org-dictionary-English.jsonl（约 3.2GB，每行一个词条 entry）。
规则：
  - 只处理英文词条（lang_code == 'en'），按拼写小写匹配 words 表里的词。
  - 跨 sense 采样：每个 sense 取 1 条例句，每词最多 5 条；sense 超过 5 个时随机取 5 个
    （kaikki 的 sense 无频率字段；固定种子保证可复现）。
  - 从单个 sense 挑例句：优先 10~20 词的例句；没有该范围的则取最短的。
  - 例句清洗：去 wikitext 模板、去 HTML 标签、去实体、压缩空白、截断超长。
  - 幂等：导入前清空 examples 并重置 enriched 位 1，重新写入（无例句的词也置位，
    表示"已处理"，避免重复回查）。

用法：
  python tools/import_examples.py \
    --kaikki resources/downloads/kaikki.org-dictionary-English.jsonl \
    --db resources/fishword.db [--seed 20261116]
"""

import argparse
import html
import json
import random
import re
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

MAX_EXAMPLES = 3          # 每词最多例句数（= 最多采样的 sense 数）
MIN_WORDS = 10            # 例句词数优选下限
MAX_WORDS = 20            # 例句词数优选上限
MAX_WORD_COUNT = 25       # 单条例句最大词数（超过直接舍弃）
MAX_LEN = 300             # 单条例句最大字符数（兜底截断）
EXAMPLES_BIT = 1          # enriched 位掩码：1 = 例句

_TEMPLATE = re.compile(r"\{\{[^{}]*\}\}")   # wikitext 模板 {{...}}
_TAG = re.compile(r"<[^>]+>")               # HTML 标签
_WS = re.compile(r"\s+")                    # 连续空白
_SENT_SPLIT = re.compile(r"(?<=[.!?])\s+")  # 句末标点后的空白（句子分隔）


def clean_text(s):
    """清洗例句文本：去模板/HTML/实体（多重解码，处理双重编码），压缩空白，截断超长。"""
    if not s:
        return ""
    s = _TEMPLATE.sub(" ", s)
    s = _TAG.sub(" ", s)
    # HTML 实体可能被双重编码（&amp;#123; → &#123; → {），循环解码直到稳定
    for _ in range(3):
        decoded = html.unescape(s)
        if decoded == s:
            break
        s = decoded
    s = _WS.sub(" ", s).strip()
    if len(s) > MAX_LEN:
        s = s[: MAX_LEN - 1].rstrip() + "…"
    return s


_BAD_CHARS = re.compile(r"[{}\\]|&#\d|#\d{2,4};")  # 花括号/反斜杠/实体残留（LaTeX 公式、&#123; 或损坏的 #123; 形式）


def looks_clean(text):
    """清洗后仍含花括号/反斜杠/实体残留的文本（LaTeX 数学记号等）不适合作为例句。"""
    return _BAD_CHARS.search(text) is None


def word_count(text):
    return len(text.split())


def contains_word(text, word):
    """文本是否包含目标单词（含词形变化，如 run→running、prototype→prototypes）。"""
    return re.search(r"\b" + re.escape(word) + r"\w*\b", text, re.IGNORECASE) is not None


def load_target_words(db):
    """返回 {小写拼写: [word_id, ...]}（拼写理论上唯一，这里兼容重复）。"""
    con = sqlite3.connect(db)
    cur = con.cursor()
    target = {}
    for wid, spelling in cur.execute("SELECT id, spelling FROM words"):
        key = (spelling or "").strip().lower()
        if key:
            target.setdefault(key, []).append(wid)
    con.close()
    return target


def reset_examples(db):
    """清空已有例句 + 重置 enriched 位 1（重新导入前调用）。"""
    con = sqlite3.connect(db)
    cur = con.cursor()
    cur.execute("UPDATE words SET examples = NULL, enriched = enriched & ~1")
    con.commit()
    con.close()


def keep_target_sentence(text, word, bold_offsets):
    """例句含多句话时，只保留包含目标单词的那一句（用 bold 位置定位，清洗前调用）。"""
    text = text.strip()
    if not text or not _SENT_SPLIT.search(text):
        return text
    # 优先 bold_text_offsets 定位目标单词所在句子
    if bold_offsets and bold_offsets[0]:
        pos = bold_offsets[0][0]
        start, end = 0, len(text)
        for m in _SENT_SPLIT.finditer(text):
            if m.end() <= pos:
                start = m.end()
            else:
                end = m.start()  # 句子到分隔空格前的句末标点为止
                break
        return text[start:end].strip()
    # 兜底：正则匹配目标单词（含词形变化）
    for s in _SENT_SPLIT.split(text):
        if contains_word(s, word):
            return s.strip()
    return text


def pick_example(sense, word):
    """从单个 sense 挑 1 条例句：必须含目标词；多句只留含目标词那句；>25 词舍弃；优先 10~20 词，否则取最短。"""
    examples = sense.get("examples") or []
    # 按 type 优先级收集候选（优先 example 造句，其次 quote 引用，最后任意）
    candidates = []
    for pref in ("example", "quote", None):
        for ex in examples:
            if pref is None or ex.get("type") == pref:
                t = keep_target_sentence(
                    ex.get("text") or "", word, ex.get("bold_text_offsets")
                )
                t = clean_text(t)
                # 必须包含目标单词，否则舍弃（过滤 "Near-synonym: xxx" 等混入 examples 的注释）；
                # 含花括号/反斜杠/实体残留的舍弃（过滤 LaTeX 数学记号、编码错误文本）
                if t and word_count(t) <= MAX_WORD_COUNT and contains_word(t, word) and looks_clean(t):
                    candidates.append(t)
        if candidates:
            break
    if not candidates:
        return None
    # 优先 10~20 词（取第一条命中）；否则取最短
    for t in candidates:
        if MIN_WORDS <= word_count(t) <= MAX_WORDS:
            return t
    return min(candidates, key=word_count)


def extract(entry, target, collected, rng):
    """从单个 entry 提取例句，按词累计（去重，每词 ≤3 条）。"""
    if (entry.get("lang_code") or entry.get("lang")) not in ("en", "English"):
        return
    word = (entry.get("word") or "").strip()
    ids = target.get(word.lower())
    if not ids:
        return

    senses = [s for s in (entry.get("senses") or []) if s.get("examples")]
    if not senses:
        return

    # 超过 3 个 sense：kaikki 无频率字段，随机取 3 个（保持原 sense 相对顺序）
    if len(senses) > MAX_EXAMPLES:
        chosen = rng.sample(senses, MAX_EXAMPLES)
        chosen.sort(key=senses.index)
    else:
        chosen = senses

    for sense in chosen:
        text = pick_example(sense, word)
        if not text:
            continue
        for wid in ids:
            lst = collected.setdefault(wid, [])
            if text not in lst and len(lst) < MAX_EXAMPLES:
                lst.append(text)
        if all(len(collected.get(wid, [])) >= MAX_EXAMPLES for wid in ids):
            break


def main():
    ap = argparse.ArgumentParser(description="从 kaikki JSONL 提取例句写入 words.examples")
    ap.add_argument("--kaikki", required=True, help="kaikki 英文词典 JSONL 路径")
    ap.add_argument("--db", default="resources/fishword.db", help="fishword.db 路径")
    ap.add_argument("--seed", type=int, default=20261116, help="随机种子（sense 采样可复现）")
    args = ap.parse_args()

    target = load_target_words(args.db)
    print(f"目标词数：{sum(len(v) for v in target.values())} 个 word_id（{len(target)} 个拼写）")

    print("清空已有例句并重置 enriched 位…")
    reset_examples(args.db)

    rng = random.Random(args.seed)
    collected = {}  # word_id -> [例句文本...]
    with open(args.kaikki, "r", encoding="utf-8") as f:
        for i, line in enumerate(f, 1):
            if not line.strip():
                continue
            try:
                entry = json.loads(line)
            except json.JSONDecodeError:
                continue
            extract(entry, target, collected, rng)
            if i % 500000 == 0:
                print(f"  已处理 {i:,} 行，命中 {len(collected):,} 词")

    # 写库：有例句的写 examples；所有 target 词都置 enriched 位 1（含"确认无例句"）
    con = sqlite3.connect(args.db)
    cur = con.cursor()
    for wid, texts in collected.items():
        data = json.dumps([{"en": t, "cn": ""} for t in texts], ensure_ascii=False)
        cur.execute("UPDATE words SET examples = ?, enriched = enriched | 1 WHERE id = ?", (data, wid))
    all_ids = [wid for ids in target.values() for wid in ids]
    cur.executemany(
        "UPDATE words SET enriched = enriched | 1 WHERE id = ?",
        [(wid,) for wid in all_ids],
    )
    con.commit()

    n_total = len(all_ids)
    n_with = len(collected)
    n_empty = n_total - n_with
    avg = (sum(len(v) for v in collected.values()) / n_with) if n_with else 0
    print(f"\n完成：总词 {n_total:,}，有例句 {n_with:,}（平均 {avg:.1f} 条），无例句 {n_empty:,}")
    con.close()


if __name__ == "__main__":
    main()
