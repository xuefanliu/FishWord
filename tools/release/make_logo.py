#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 logo 源图（tools/release/assets/logo-source.png，自带透明通道）生成发布资产：
  1. logo.png  —— 裁掉透明边 → 正方形透明画布居中 → 缩放 512（README 顶部展示）
  2. logo.ico  —— Windows 多尺寸图标（16~256，系统托盘与窗口图标）

源图更新后重新运行本脚本即可。

用法：
    python tools/release/make_logo.py
"""

import argparse
import os

from PIL import Image


def main():
    ap = argparse.ArgumentParser(description="生成 logo.png（512 保留透明）与 logo.ico（多尺寸）")
    ap.add_argument("--in", dest="src", default="tools/release/assets/logo-source.png",
                    help="logo 源图（默认 tools/release/assets/logo-source.png，更新 logo 时替换该文件）")
    ap.add_argument("--assets", default="tools/release/assets", help="发布资产目录（输出 logo.png）")
    ap.add_argument("--app-assets", default="src/FishWord.App/Assets", help="应用资源目录（输出 logo.ico）")
    args = ap.parse_args()

    img = Image.open(args.src).convert("RGBA")  # 保留原 alpha 通道

    # 裁掉透明边（防御性：源图可能带空白边），再贴到正方形透明画布居中（避免非正方形拉伸变形）
    bbox = img.getchannel("A").getbbox()
    if bbox:
        img = img.crop(bbox)
    side = max(img.size)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(img, ((side - img.width) // 2, (side - img.height) // 2), img)
    img = canvas

    # README 用的透明底 PNG（512 足够）
    logo512 = img.resize((512, 512), Image.LANCZOS)
    os.makedirs(args.assets, exist_ok=True)
    png_path = os.path.join(args.assets, "logo.png")
    logo512.save(png_path, optimize=True)
    print(f"logo.png → {png_path}（512x512，{os.path.getsize(png_path) / 1024:.0f} KB）")

    # Windows 多尺寸 .ico（托盘 16/32、Alt-Tab 48、大图 256）
    os.makedirs(args.app_assets, exist_ok=True)
    ico_path = os.path.join(args.app_assets, "logo.ico")
    img.save(ico_path, format="ICO",
             sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"logo.ico → {ico_path}（16/24/32/48/64/128/256）")


if __name__ == "__main__":
    main()
