#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""组装便携版并打 zip（覆盖更新策略，全程无删除操作）：
  <staging>/            ← 解压版组装目录（gitignore 排除，可作为解压版直接使用）
    FishWord.App.exe
    resources/fishword.db  ← 发布库（prepare_release.py 产物）
    resources/basic_505.json
    resources/audio/       ← 全量发音
  <zip-out> ← 上述目录的压缩包（发布仓库 dist/ 下，作为 Release 附件）

用法：
    python tools/release/make_portable.py \
        --publish src/FishWord.App/bin/Release/net9.0-windows/win-x64/publish \
        --release-db resources/fishword_release.db \
        --staging <发布目录>/dist/_portable/FishWord \
        --zip-out <发布目录>/dist/FishWord-portable.zip
"""

import argparse
import os
import shutil
import sys
import zipfile

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

EXCLUDE = {".pdb"}  # 打包排除符号文件


def main():
    ap = argparse.ArgumentParser(description="组装便携版 zip（覆盖更新策略）")
    ap.add_argument("--publish", default="src/FishWord.App/bin/Release/net9.0-windows/win-x64/publish")
    ap.add_argument("--release-db", default="resources/fishword_release.db")
    ap.add_argument("--basic505", default="resources/basic_505.json")
    ap.add_argument("--audio", default="resources/audio")
    ap.add_argument("--staging", required=True, help="便携版组装目录（gitignore 排除）")
    ap.add_argument("--zip-out", required=True, help="zip 输出路径（发布仓库 dist/ 下）")
    args = ap.parse_args()

    root = args.staging
    os.makedirs(root, exist_ok=True)

    # 1. publish 产物（exe + 原生 dll，覆盖拷贝）
    n = 0
    for f in os.listdir(args.publish):
        if any(f.endswith(ext) for ext in EXCLUDE):
            continue
        shutil.copy2(os.path.join(args.publish, f), os.path.join(root, f))
        n += 1
    print(f"publish 产物拷贝 {n} 个文件")

    # 2. 发布库 + 505 名单
    os.makedirs(os.path.join(root, "resources"), exist_ok=True)
    shutil.copy2(args.release_db, os.path.join(root, "resources", "fishword.db"))
    shutil.copy2(args.basic505, os.path.join(root, "resources", "basic_505.json"))
    print("resources/fishword.db + basic_505.json 已就位")

    # 3. 音频（覆盖拷贝）
    shutil.copytree(args.audio, os.path.join(root, "resources", "audio"), dirs_exist_ok=True)
    n_audio = len(os.listdir(os.path.join(root, "resources", "audio")))
    print(f"resources/audio/ 已就位（{n_audio} 个文件）")

    # 4. 打 zip（覆盖旧 zip 文件）
    os.makedirs(os.path.dirname(args.zip_out), exist_ok=True)
    if os.path.exists(args.zip_out):
        os.remove(args.zip_out)
    zip_base = os.path.dirname(args.zip_out)
    with zipfile.ZipFile(args.zip_out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for dirpath, _, filenames in os.walk(root):
            for f in filenames:
                full = os.path.join(dirpath, f)
                arc = os.path.relpath(full, zip_base)
                zf.write(full, arc)

    print(f"完成：{args.zip_out}（{os.path.getsize(args.zip_out) / 1024 / 1024:.1f} MB）")


if __name__ == "__main__":
    main()
