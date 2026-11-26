#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""一键打包：logo 资产 → 发布库 → 发布集 → 程序发布 → 便携 zip → 安装器。

产物：
  <dst>/（发布仓库目录，如 E:/Projects/FishWord-publish）
    ├── 发布集文件（src/、resources/、README…）—— 发布者手动 git 提交推送
    └── dist/FishWord-portable.zip、dist/FishWord-setup.exe —— Release 附件（.gitignore 排除）

本脚本不执行任何 git 操作。

用法：
    python tools/release/make_release.py --dst E:/Projects/FishWord-publish
"""

import argparse
import os
import subprocess
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def run(cmd, desc):
    print(f"\n== {desc} ==")
    r = subprocess.run(cmd)
    if r.returncode != 0:
        print(f"\n失败：{desc}（exit {r.returncode}）")
        sys.exit(r.returncode)


def find_iscc():
    candidates = [
        r"C:\Program Files\Inno Setup 7\ISCC.exe",
        r"C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
        r"C:\Program Files\Inno Setup 6\ISCC.exe",
        r"C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    ]
    for p in candidates:
        if os.path.exists(p):
            return p
    return None


def main():
    ap = argparse.ArgumentParser(description="一键打包发布产物（不执行 git 操作）")
    ap.add_argument("--dst", default="E:/Projects/FishWord-publish", help="发布仓库目录")
    args = ap.parse_args()
    dst = args.dst.rstrip("/\\")
    py = sys.executable

    dist = os.path.join(dst, "dist")
    os.makedirs(dist, exist_ok=True)

    # 1. logo 资产（logo.png 进发布集 docs/images，logo.ico 进程序 Assets）
    run([py, "tools/release/make_logo.py"], "生成 logo 资产")

    # 2. 发布库（副本上清空用户数据 + 预填 505 + 校验）
    run([py, "tools/release/prepare_release.py"], "生成发布库")

    # 3. 发布集同步到发布仓库目录（只拷文件，不碰 git）
    run([py, "tools/release/sync_public.py", "--src", ".", "--dst", dst,
         "--release-db", "resources/fishword_release.db"], "同步发布集")

    # 4. 程序发布（self-contained 单文件）
    run(["dotnet", "publish", "src/FishWord.App/FishWord.App.csproj", "-c", "Release",
         "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true",
         "-v", "minimal", "--nologo"], "dotnet publish")

    # 5. 打包前纯净性校验：发布目录里的发布库必须无任何用户数据残留（先验纯再打包）
    release_db = os.path.join(dst, "resources", "fishword.db")
    run([py, "-c",
         "import sqlite3, sys; "
         "con = sqlite3.connect(sys.argv[1]); "
         "rows = [con.execute(f'SELECT COUNT(*) FROM {t}').fetchone()[0] for t in "
         "('daily_study','memory_states','review_logs','settings')]; "
         "user = con.execute(\"SELECT COUNT(*) FROM user_words WHERE known_source='user'\").fetchone()[0]; "
         "sys505 = con.execute(\"SELECT COUNT(*) FROM user_words WHERE known_source='system'\").fetchone()[0]; "
         "con.close(); "
         "print(f'发布库校验：用户数据残留 {rows}，user 熟词 {user}，system 熟词 {sys505}'); "
         "assert rows == [0,0,0,0] and user == 0, '发布库包含用户数据，终止打包！'",
         release_db],
        "纯净性校验（发布库用户数据残留 = 0）")

    # 6. 便携版 zip（组装目录在发布仓库 dist/_portable，zip 输出到发布仓库 dist/）
    #    打包源全部取自发布目录（发布库 = 发布目录 resources/fishword.db，步骤 3 同步的纯净库）
    staging = os.path.join(dist, "_portable", "FishWord")
    zip_out = os.path.join(dist, "FishWord-portable.zip")
    run([py, "tools/release/make_portable.py",
         "--publish", "src/FishWord.App/bin/Release/net9.0-windows/win-x64/publish",
         "--release-db", release_db,
         "--staging", staging, "--zip-out", zip_out], "便携版 zip")

    # 7. 安装器（Inno Setup，/D 参数传入便携版目录与输出目录）
    iscc = find_iscc()
    if iscc is None:
        print("\n警告：未找到 Inno Setup（ISCC.exe），跳过安装器生成。"
              "\n安装：https://jrsoftware.org/isdl.php")
    else:
        run([iscc, "tools/release/FishWord-setup.iss",
             f"/DSourceDir={staging}", f"/DOutputDir={dist}"], "安装器（Inno Setup）")

    print("\n== 打包完成 ==")
    print(f"发布仓库目录：{dst}（发布集，git 手动提交）")
    print(f"Release 附件：{zip_out}")
    if iscc:
        print(f"              {os.path.join(dist, 'FishWord-setup.exe')}")


if __name__ == "__main__":
    main()
