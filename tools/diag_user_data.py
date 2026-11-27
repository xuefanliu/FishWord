#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""诊断 FishWord 用户数据状态（用户反馈学习记录丢失时收集信息用）。

在用户机器上运行（需要 Python），输出 AppData 数据目录的状态、数据库各表计数、
迁移残留文件等，用于排查数据丢失原因。

用法：
    python tools/diag_user_data.py
"""

import os
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

LOCAL = os.environ.get("LOCALAPPDATA", "")
DATA_DIR = os.path.join(LOCAL, "FishWord")


def main():
    print("== FishWord 用户数据诊断 ==")
    print(f"数据目录: {DATA_DIR}")
    print()

    if not os.path.isdir(DATA_DIR):
        print("❌ 数据目录不存在——AppData 里从未创建过用户数据（全新安装状态）。")
        print("   若用户认为应该有学习记录：检查是否换过 Windows 账户 / 清理过系统 / 卸载时删除了目录。")
        return

    for name in sorted(os.listdir(DATA_DIR)):
        full = os.path.join(DATA_DIR, name)
        if os.path.isfile(full):
            size = os.path.getsize(full)
            mtime = __import__("datetime").datetime.fromtimestamp(os.path.getmtime(full)).strftime("%Y-%m-%d %H:%M:%S")
            print(f"  [文件] {name}  {size / 1024:.0f} KB  {mtime}")
        else:
            n = sum(len(fs) for _, _, fs in os.walk(full))
            print(f"  [目录] {name}/  {n} 个文件")
    print()

    db = os.path.join(DATA_DIR, "fishword.db")
    if not os.path.exists(db):
        print("❌ fishword.db 不存在（用户库被删除/未创建）。")
        print("   若存在 fishword.db.bak：这是迁移前的备份，可用其恢复学习记录。")
        return

    con = sqlite3.connect(db)
    cur = con.cursor()
    print(f"数据库版本: user_version = {cur.execute('PRAGMA user_version').fetchone()[0]}")
    print(f"单词总数: {cur.execute('SELECT COUNT(*) FROM words').fetchone()[0]}")
    print()
    print("用户数据表计数：")
    print(f"  daily_study（学习队列）: {cur.execute('SELECT COUNT(*) FROM daily_study').fetchone()[0]}")
    recent = cur.execute(
        "SELECT date, COUNT(*) FROM daily_study GROUP BY date ORDER BY date DESC LIMIT 3"
    ).fetchall()
    print(f"    └ 最近 3 天: {recent}")
    print(f"  memory_states（复习队列）: {cur.execute('SELECT COUNT(*) FROM memory_states').fetchone()[0]}")
    print(f"  review_logs（复习日志）: {cur.execute('SELECT COUNT(*) FROM review_logs').fetchone()[0]}")
    print(f"  user_words（熟词标记）: {cur.execute('SELECT COUNT(*) FROM user_words').fetchone()[0]}")
    print(f"  settings（设置）: {cur.execute('SELECT COUNT(*) FROM settings').fetchone()[0]}")
    print()
    vec = cur.execute("SELECT examples FROM words WHERE spelling='vector'").fetchone()
    print("vector 例句乱码残留:", "有" if vec and vec[0] and "#123;" in vec[0] else "无")
    con.close()

    err = os.path.join(DATA_DIR, "migration_error.log")
    if os.path.exists(err):
        print("\n⚠️ 存在迁移错误日志：")
        with open(err, encoding="utf-8", errors="replace") as f:
            print(f.read()[:500])

    # UAC 虚拟化检查：旧版无 manifest 时写 Program Files 可能被重定向到 VirtualStore
    vstore = os.path.join(LOCAL, "VirtualStore")
    if os.path.isdir(vstore):
        print(f"\n⚠️ 存在 UAC 虚拟化目录（VirtualStore）：{vstore}")
        found = False
        for dirpath, _, filenames in os.walk(vstore):
            if "fishword.db" in filenames:
                found = True
                full = os.path.join(dirpath, "fishword.db")
                try:
                    con2 = sqlite3.connect(full)
                    n = con2.execute("SELECT COUNT(*) FROM daily_study").fetchone()[0]
                    con2.close()
                    print(f"   ⚠️ 在虚拟化目录发现 fishword.db：{full}（daily_study {n} 行）——旧版学习记录可能在这里！")
                except Exception:
                    print(f"   ⚠️ 在虚拟化目录发现 fishword.db：{full}")
        if not found:
            print("   （虚拟化目录中无 fishword.db）")


if __name__ == "__main__":
    main()
