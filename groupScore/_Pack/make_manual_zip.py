# 生成「解压即用」绿色版：把 .cipx 解包，再按 `<插件id>/...` 的结构打成标准 zip。
#
# 为什么不用 PowerShell 的 Compress-Archive：
#   PS 5.1 会把 zip 里的路径分隔符写成反斜杠（`bojinxia.groupScore\cover.png`），
#   这不符合 ZIP 规范（规范要求正斜杠）。Windows 资源管理器能容忍，
#   但 7-Zip / 部分解压工具 / Linux 上会解出名为 `bojinxia.groupScore\cover.png`
#   的**单个文件**，而不是文件夹 —— 老师照着说明放进 Plugins 目录就会失败。
#
# 用法：python make_manual_zip.py <插件.cipx 路径>
#   插件 id 从 cipx 文件名自动推断（去掉 .cipx 后缀），所以同一个脚本
#   既能打 bojinxia.groupScore.cipx 也能打 bojinxia.homework.cipx。
import os
import sys
import tempfile
import zipfile


def main() -> int:
    if len(sys.argv) < 2:
        print("用法：python make_manual_zip.py <插件.cipx 路径>")
        return 2

    cipx = os.path.abspath(sys.argv[1])
    if not os.path.isfile(cipx):
        print(f"找不到文件：{cipx}")
        return 2

    # 插件 id = cipx 文件名去掉 .cipx 后缀
    plugin_id = os.path.splitext(os.path.basename(cipx))[0]
    out = os.path.join(os.path.dirname(cipx), f"{plugin_id}-manual.zip")
    if os.path.exists(out):
        os.remove(out)

    # 先解到临时目录，让 zip 里天然带上 `<插件id>/` 这一层 ——
    # 老师把整个文件夹丢进 data\Plugins\ 就完事，不用自己建目录、不用改名。
    with tempfile.TemporaryDirectory() as tmp:
        inner = os.path.join(tmp, plugin_id)
        os.makedirs(inner, exist_ok=True)
        with zipfile.ZipFile(cipx) as z:
            z.extractall(inner)

        with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
            for root, _dirs, files in os.walk(inner):
                for name in sorted(files):
                    full = os.path.join(root, name)
                    # 关键：arcname 必须用正斜杠，且以插件 id 目录开头
                    arc = os.path.relpath(full, tmp).replace(os.sep, "/")
                    z.write(full, arc)

    with zipfile.ZipFile(out) as z:
        names = z.namelist()

    bad = [n for n in names if "\\" in n]
    tops = {n.split("/")[0] for n in names}
    leaves = {n.split("/")[-1] for n in names}
    print(f"已生成 {os.path.basename(out)}  {os.path.getsize(out)} 字节  {len(names)} 个条目")
    print(f"  反斜杠条目：{len(bad)}（必须为 0）")
    print(f"  顶层目录：{sorted(tops)}（必须只有 {plugin_id}）")
    print(f"  含 manifest.yml：{'manifest.yml' in leaves}")

    # 原生库是软性检查：小组积分用 SQLite 才有 e_sqlite3.dll，
    # 远程作业用 JSON 就没有 —— 有则提示、没有不算错。
    if "e_sqlite3.dll" in leaves:
        print("  含 e_sqlite3.dll（SQLite 插件才有的原生库）")
    else:
        print("  不含 e_sqlite3.dll（JSON 存储插件，正常）")

    ok = (not bad and tops == {plugin_id} and "manifest.yml" in leaves)
    print("结果：" + ("OK" if ok else "FAIL"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
