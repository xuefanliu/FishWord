#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把发布截图缩放到指定宽度（GitHub README 小图显示，减小文件体积）。

用法：
    python tools/release/resize_screenshots.py \
        --dir tools/release/assets/screenshots --width 240
"""

import argparse
import os

from PIL import Image


def main():
    ap = argparse.ArgumentParser(description="缩放发布截图")
    ap.add_argument("--dir", default="tools/release/assets/screenshots")
    ap.add_argument("--width", type=int, default=240)
    args = ap.parse_args()

    for f in sorted(os.listdir(args.dir)):
        if not f.lower().endswith(".png"):
            continue
        path = os.path.join(args.dir, f)
        img = Image.open(path)
        if img.width <= args.width:
            print(f"  {f}: {img.width}x{img.height} 已足够小，跳过")
            continue
        ratio = args.width / img.width
        img = img.resize((args.width, int(img.height * ratio)), Image.LANCZOS)
        img.save(path, optimize=True)
        print(f"  {f}: → {img.width}x{img.height}")


if __name__ == "__main__":
    main()
