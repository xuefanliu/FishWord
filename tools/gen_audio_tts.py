#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
用 edge-tts 批量生成单词发音（最小方案），并回写 fishword.db 的 audio_path。

- 支持断点续跑：已存在的音频文件自动跳过。
- 并发控制：--concurrency（默认 8）。
- 真人发音切换：后续在 tools/import_ielts_wordbook.py 的 fetch_audio 接口
  中接入真人音频源（Wiktionary），替代本 TTS 生成。

用法：
  # 先跑 20 个词测试
  python tools/gen_audio_tts.py --db resources/fishword.db --limit 20
  # 全量
  python tools/gen_audio_tts.py --db resources/fishword.db
"""

import argparse
import asyncio
import os
import re
import sqlite3
import sys

import edge_tts

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 文件名非法字符替换
_SAFE = re.compile(r'[\\/:*?"<>|\s]+')


def safe_name(word):
    return _SAFE.sub("_", word).strip("_") or "word"


async def _gen_one(word, voice, out_path, sem, lock, stats):
    async with sem:
        try:
            com = edge_tts.Communicate(word, voice)
            await com.save(out_path)
            stats["ok"] += 1
        except Exception as exc:  # noqa: BLE001
            stats["fail"] += 1
            async with lock:
                print(f"  [FAIL] {word}: {exc}")


async def _run(words, voice, audio_dir, concurrency):
    os.makedirs(audio_dir, exist_ok=True)
    sem = asyncio.Semaphore(concurrency)
    lock = asyncio.Lock()
    stats = {"ok": 0, "fail": 0, "skip": 0}
    todo = []
    for w in words:
        fname = safe_name(w) + ".mp3"
        out_path = os.path.join(audio_dir, fname)
        if os.path.exists(out_path) and os.path.getsize(out_path) > 0:
            stats["skip"] += 1
            continue
        todo.append((w, out_path))

    total = len(todo)
    print(f"待生成 {total} 个（跳过已存在 {stats['skip']} 个），并发 {concurrency}")
    done = 0
    batch = []
    for w, out_path in todo:
        batch.append(_gen_one(w, voice, out_path, sem, lock, stats))
        if len(batch) >= concurrency * 4:
            await asyncio.gather(*batch)
            done += len(batch)
            batch = []
            print(f"  进度 {done}/{total}，成功 {stats['ok']}，失败 {stats['fail']}", flush=True)
    if batch:
        await asyncio.gather(*batch)
        done += len(batch)
    print(f"完成：成功 {stats['ok']}，失败 {stats['fail']}，跳过 {stats['skip']}")
    return stats


def update_db(db_path, words, audio_dir):
    con = sqlite3.connect(db_path)
    cur = con.cursor()
    for w in words:
        rel = f"audio/{safe_name(w)}.mp3"
        full = os.path.join(audio_dir, safe_name(w) + ".mp3")
        if os.path.exists(full) and os.path.getsize(full) > 0:
            cur.execute("UPDATE words SET audio_path=? WHERE spelling=?", (rel, w))
    con.commit()
    con.close()
    print("audio_path 已回写。")


def main():
    ap = argparse.ArgumentParser(description="TTS 批量生成单词发音")
    ap.add_argument("--db", default="resources/fishword.db", help="fishword.db 路径")
    ap.add_argument("--audio-dir", default="resources/audio", help="音频输出目录")
    ap.add_argument("--voice", default="en-US-AriaNeural", help="edge-tts 音色")
    ap.add_argument("--concurrency", type=int, default=8, help="并发数")
    ap.add_argument("--limit", type=int, default=0, help="只处理前 N 个词（0=全部，用于测试）")
    args = ap.parse_args()

    con = sqlite3.connect(args.db)
    rows = con.execute(
        "SELECT spelling FROM words ORDER BY freq_rank DESC, frq ASC, spelling"
    ).fetchall()
    con.close()
    words = [r[0] for r in rows]
    if args.limit > 0:
        words = words[: args.limit]
    print(f"共 {len(words)} 个词，音色 {args.voice}")

    asyncio.run(_run(words, args.voice, args.audio_dir, args.concurrency))
    update_db(args.db, words, args.audio_dir)


if __name__ == "__main__":
    main()
