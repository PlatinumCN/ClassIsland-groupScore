#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把仓库里的 __USER__ / __REPO__ 占位符换成真实的 GitHub 用户名与仓库名。

用法（在本仓库根目录执行）：

    python _Pack/fill-repo.py PlatinumCN classisland-plugins

为什么要有这个脚本：这几处必须**完全一致**，漏一处就会出现
「README 里的图片裂了」「市场首页跳到 404」「Release 挂在不存在的仓库下」——

    GroupScorePlugin/manifest.yml   url                   （打进 .cipx）
    groupScore.yml                  url/repoOwner/repoName（提交给 PluginIndex）

★ manifest 会被打进插件包，所以**改完必须重新打包**，脚本最后会提醒。
★ 只改占位符，已经填过的不会被动。
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

TARGETS = [
    "GroupScorePlugin/manifest.yml",
    "groupScore.yml",
]

# GitHub 用户名：字母数字 + 单个连字符，不能以连字符开头/结尾，最长 39
USER_RE = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")
# 仓库名：字母数字 . - _ ，不能以 . 开头
REPO_RE = re.compile(r"^[A-Za-z0-9._-]{1,100}$")


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__.strip())
        return 2

    user, repo = sys.argv[1].strip(), sys.argv[2].strip()

    if not USER_RE.match(user):
        print(f"✗ 用户名不合法：{user!r}（GitHub 用户名只允许字母数字和单个连字符）")
        return 1
    if not REPO_RE.match(repo) or repo.startswith("."):
        print(f"✗ 仓库名不合法：{repo!r}")
        return 1

    print(f"用户名：{user}")
    print(f"仓库名：{repo}")
    print()

    changed = 0
    for rel in TARGETS:
        path = ROOT / rel
        if not path.exists():
            print(f"  ! 跳过（文件不存在）：{rel}")
            continue

        text = path.read_text(encoding="utf-8")
        before = text
        text = text.replace("__USER__", user).replace("__REPO__", repo)

        if text == before:
            marks = []
            if f"github.com/{user}/{repo}" in before:
                marks.append("已指向该仓库")
            if not marks:
                marks.append("没有占位符，未改动")
            print(f"  · {rel}：{'，'.join(marks)}")
            continue

        path.write_text(text, encoding="utf-8")
        left = text.count("__USER__") + text.count("__REPO__")
        print(f"  ✓ {rel}" + (f"（还剩 {left} 处占位符，请人工看一眼）" if left else ""))
        changed += 1

    print()
    if changed == 0:
        print("没有任何改动。如果你的仓库地址不是这个，先看一眼上面每行的状态。")
        return 0

    print("下一步（顺序不能反）：")
    print("  1. 重新打包 —— manifest 在包里，不重打等于没改：")
    print("       cd GroupScorePlugin && dotnet build -c Release -p:CreateCipx=true -p:GenerateHashSummary=false")
    print("  2. 产物改名成插件 id，并生成绿色版：")
    print("       python _Pack/make_manual_zip.py cipx/<插件id>.cipx")
    print("  3. 提交这些改动，再发 Release（Tag 必须与 manifest 的 version 完全一致）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
