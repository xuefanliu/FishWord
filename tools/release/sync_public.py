#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把发布集从开发仓库同步到本地发布目录（只做文件拷贝，不做任何 git 操作）。

发布集 = 程序代码 + 发布版 db + 505 名单 + 全量音频 + 发布版 README/LICENSE。
目标目录（如 E:/Projects/FishWord-publish）由发布者自行 git init 并推送到公开仓库，
本脚本不执行任何 git 命令。

用法：
    python tools/release/sync_public.py \
        --src <开发仓库根目录> \
        --dst <发布目录，如 E:/Projects/FishWord-publish> \
        --release-db resources/fishword_release.db
"""

import argparse
import os
import shutil
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

EXCLUDE_DIRS = {"bin", "obj", "__pycache__", ".git", ".vs", ".idea"}
EXCLUDE_FILES = {".gitignore"}  # 发布目录的 .gitignore 由发布者自行准备（如需要）


def copy_tree(src_root, dst_root, exclude_dirs):
    """拷贝目录树，跳过构建产物目录。"""
    n = 0
    for dirpath, dirnames, filenames in os.walk(src_root):
        dirnames[:] = [d for d in dirnames if d not in exclude_dirs]
        rel = os.path.relpath(dirpath, src_root)
        out_dir = dst_root if rel == "." else os.path.join(dst_root, rel)
        os.makedirs(out_dir, exist_ok=True)
        for f in filenames:
            if f in EXCLUDE_FILES:
                continue
            shutil.copy2(os.path.join(dirpath, f), os.path.join(out_dir, f))
            n += 1
    return n


def dir_size(path):
    total = 0
    for dirpath, _, filenames in os.walk(path):
        for f in filenames:
            total += os.path.getsize(os.path.join(dirpath, f))
    return total


def main():
    ap = argparse.ArgumentParser(description="同步发布集到本地发布目录（不做 git 操作）")
    ap.add_argument("--src", required=True, help="开发仓库根目录")
    ap.add_argument("--dst", required=True, help="发布目录（如 E:/Projects/FishWord-publish）")
    ap.add_argument("--release-db", default="resources/fishword_release.db",
                    help="发布库路径（prepare_release.py 的产物，相对 --src）")
    args = ap.parse_args()

    src = args.src.rstrip("/\\")
    dst = args.dst.rstrip("/\\")
    if not os.path.exists(args.release_db):
        print(f"错误：发布库不存在 {args.release_db}，先运行 prepare_release.py")
        sys.exit(1)

    os.makedirs(dst, exist_ok=True)
    print(f"开发仓库：{src}")
    print(f"发布目录：{dst}\n")

    # 1. 程序代码（排除 bin/obj 等构建产物）
    n = copy_tree(os.path.join(src, "src"), os.path.join(dst, "src"), EXCLUDE_DIRS)
    print(f"src/ 拷贝 {n} 个文件")

    # 2. 数据管线脚本（已脱敏）
    n = copy_tree(os.path.join(src, "tools"), os.path.join(dst, "tools"), EXCLUDE_DIRS)
    print(f"tools/ 拷贝 {n} 个文件")

    # 3. 发布库 → resources/fishword.db
    os.makedirs(os.path.join(dst, "resources"), exist_ok=True)
    shutil.copy2(args.release_db, os.path.join(dst, "resources", "fishword.db"))
    print(f"发布库 → resources/fishword.db（{os.path.getsize(args.release_db) / 1024 / 1024:.1f} MB）")

    # 4. 505 名单
    shutil.copy2(os.path.join(src, "resources", "basic_505.json"),
                 os.path.join(dst, "resources", "basic_505.json"))
    print("resources/basic_505.json 已拷贝")

    # 5. 全量音频（覆盖拷贝，不删除目标目录——音频为全量追加性质）
    audio_src = os.path.join(src, "resources", "audio")
    audio_dst = os.path.join(dst, "resources", "audio")
    shutil.copytree(audio_src, audio_dst, dirs_exist_ok=True)
    print(f"resources/audio/ 已拷贝（{len(os.listdir(audio_dst))} 个文件，{dir_size(audio_dst) / 1024 / 1024:.1f} MB）")

    # 6. 发布版 README / LICENSE
    assets = os.path.join(src, "tools", "release", "assets")
    shutil.copy2(os.path.join(assets, "README.md"), os.path.join(dst, "README.md"))
    shutil.copy2(os.path.join(assets, "LICENSE"), os.path.join(dst, "LICENSE"))
    print("README.md / LICENSE 已拷贝（发布版）")

    # 7. README 截图与 logo → docs/images/
    images_dst = os.path.join(dst, "docs", "images")
    os.makedirs(images_dst, exist_ok=True)
    shots = os.path.join(assets, "screenshots")
    if os.path.isdir(shots):
        shutil.copytree(shots, images_dst, dirs_exist_ok=True)
    logo_png = os.path.join(assets, "logo.png")
    if os.path.exists(logo_png):
        shutil.copy2(logo_png, os.path.join(images_dst, "logo.png"))
    print(f"docs/images/ 已拷贝（{len(os.listdir(images_dst))} 个文件）")

    # 8. .gitignore（发布仓库：排除打包产物 dist/ 与构建产物）
    gitignore = os.path.join(dst, ".gitignore")
    with open(gitignore, "w", encoding="utf-8") as f:
        f.write("# 打包产物（Release 附件，不入 git）\ndist/\n\n# 构建产物\n**/bin/\n**/obj/\n__pycache__/\n")
    print(".gitignore 已生成（排除 dist/）")

    print(f"\n完成。发布目录总大小：{dir_size(dst) / 1024 / 1024:.1f} MB")
    print("提示：本脚本未执行任何 git 操作，请在发布目录中自行 git init 并推送到公开仓库。")


if __name__ == "__main__":
    main()
