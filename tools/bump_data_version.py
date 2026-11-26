#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""递增/设置 fishword.db 的数据版本号（SQLite 内置 PRAGMA user_version）。

词库内容更新后（例句重导、词根/派生重导、音频重生成等）运行一次，
AppData 里的用户副本检测到种子版本更高时会自动迁移用户数据（保留学习记录）。

用法：
    python tools/bump_data_version.py --db resources/fishword.db            # 当前值 +1
    python tools/bump_data_version.py --db resources/fishword.db --set 5    # 设为指定值
"""

import argparse
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def main():
    ap = argparse.ArgumentParser(description="递增/设置词库数据版本号（PRAGMA user_version）")
    ap.add_argument("--db", default="resources/fishword.db", help="fishword.db 路径")
    ap.add_argument("--set", dest="set_to", type=int, default=None, help="直接设置为该版本（默认当前值 +1）")
    args = ap.parse_args()

    con = sqlite3.connect(args.db)
    cur = con.cursor()
    old = cur.execute("PRAGMA user_version").fetchone()[0]
    new = args.set_to if args.set_to is not None else old + 1
    cur.execute(f"PRAGMA user_version = {int(new)}")
    con.commit()
    con.close()
    print(f"数据版本号：{old} → {new}")


if __name__ == "__main__":
    main()
