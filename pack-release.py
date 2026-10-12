# -*- coding: utf-8 -*-
"""XMA Browser 发布打包：把 dotnet 的构建产物打成 zip + Dalamud 自定义插件源清单。

用法：
    python pack-release.py                 # 默认打进 dist/（随仓库提交，走 jsdelivr）
    python pack-release.py <bin目录> <输出目录>

产出（全部纯 ASCII 文件名，中文名会被托管方吞掉）：
    <输出目录>/XmaBrowser-v<版本>.zip
    <输出目录>/pluginmaster.json      ← 插件源清单：下载链接指向 jsdelivr（仓库内 dist 的 zip，国内稳）
    <输出目录>/pluginmaster-gh.json   ← 同一份清单，但下载链接指向 GitHub Release 附件（以后发 Release 时用）

两条路的取舍：
    · jsdelivr（默认）：不发 Release 就能用，代价是 zip 要提交进仓库（~1.3 MB/版）
    · GitHub Release：仓库干净，但必须先建 Release，否则 DownloadLink 会 404
"""
import json
import os
import re
import shutil
import sys
import zipfile

REPO = "yomo1024/XmaBrowser"
BRANCH = "main"
ROOT = os.path.dirname(os.path.abspath(__file__))


def find_manifest(bin_dir: str) -> str:
    for name in os.listdir(bin_dir):
        if name.endswith(".json") and not name.endswith(".deps.json"):
            p = os.path.join(bin_dir, name)
            with open(p, encoding="utf-8") as f:
                data = json.load(f)
            if "InternalName" in data and "DalamudApiLevel" in data:
                return p
    raise SystemExit("没找到插件清单 json（先 dotnet build -c Release）")


def main() -> int:
    bin_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "bin", "Release")
    out_dir = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "dist")
    if not os.path.isdir(bin_dir):
        raise SystemExit("构建目录不存在：%s" % bin_dir)

    manifest_path = find_manifest(bin_dir)
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)

    internal = manifest["InternalName"]
    version = manifest["AssemblyVersion"]
    ver3 = ".".join(version.split(".")[:3])
    zip_name = "%s-v%s.zip" % (internal, ver3)

    os.makedirs(out_dir, exist_ok=True)
    zip_path = os.path.join(out_dir, zip_name)

    # 打包：源码构建目录里除了 latest.zip 之外的全部文件；优先用 DalamudPackager 的产物
    packer_zip = os.path.join(bin_dir, internal, "latest.zip")
    if os.path.isfile(packer_zip):
        shutil.copy2(packer_zip, zip_path)
        source = "DalamudPackager 的 %s/latest.zip" % internal
    else:
        skip = {zip_name}
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
            for name in sorted(os.listdir(bin_dir)):
                p = os.path.join(bin_dir, name)
                if not os.path.isfile(p) or name in skip:
                    continue
                z.write(p, name)
        source = "手工打包 %s" % bin_dir

    gh_url = "https://github.com/%s/releases/download/v%s/%s" % (REPO, ver3, zip_name)
    cdn_url = "https://cdn.jsdelivr.net/gh/%s@%s/dist/%s" % (REPO, BRANCH, zip_name)
    raw_url = "https://raw.githubusercontent.com/%s/%s/dist/%s" % (REPO, BRANCH, zip_name)

    def manifest_for(link):
        # 字段结构照「已在主人的 Dalamud 里验证能用」的那份清单（ModBridge/FFXIV_XMA_Manager）对齐：
        # 该补的元字段都补上，尤其是 LastUpdate 必须是 Unix 秒时间戳（不能是 null —— null 有反序列化风险）
        e = dict(manifest)
        e.update({
            "CategoryTags": ["utility"],
            "DownloadCount": 0,
            "IconUrl": "",
            "IsHide": False,
            "IsTestingExclusive": False,
            "LastUpdate": int(os.path.getmtime(zip_path)),
            "DownloadLinkInstall": link,
            "DownloadLinkUpdate": link,
            "DownloadLinkTesting": "",
        })
        return e

    # 主源用 raw.githubusercontent —— 与主人「已在用的」ModBridge 仓库同一套路（那份清单就是走 raw），
    # 且没有 jsdelivr 对新建仓库的「负缓存 404」问题（实测踩过：新提交后 jsdelivr 曾返回
    # "Couldn't find the requested file"，必须手动 purge 才恢复）。
    # jsdelivr 作为备选源（国内多数网络更快，但对新路径要 purge 才即时生效）。
    for fname, link, host in (
        ("pluginmaster.json",     raw_url, "raw.githubusercontent（主）"),
        ("pluginmaster-cdn.json", cdn_url, "jsdelivr（备选）"),
        ("pluginmaster-gh.json",  gh_url,  "GitHub Release 附件（以后发 Release 用）"),
    ):
        with open(os.path.join(ROOT, fname), "w", encoding="utf-8") as f:
            json.dump([manifest_for(link)], f, ensure_ascii=False, indent=2)
            f.write("\n")

    size_mb = os.path.getsize(zip_path) / 1048576.0
    print("插件：%s v%s（API %s）" % (manifest["Name"], ver3, manifest["DalamudApiLevel"]))
    print("zip ：%s  （%.2f MB，来自 %s）" % (zip_name, size_mb, source))
    print("清单：pluginmaster.json（主，raw）/ pluginmaster-cdn.json（备，jsdelivr）/ pluginmaster-gh.json（Release）")
    print("Dalamud 自定义插件源 —— 主地址（粘这个）：")
    print("   https://raw.githubusercontent.com/%s/%s/pluginmaster.json" % (REPO, BRANCH))
    print("备选（主地址不通时）：")
    print("   https://cdn.jsdelivr.net/gh/%s@%s/pluginmaster-cdn.json" % (REPO, BRANCH))

    # 自检：zip 里必须能读到清单，且 InternalName/版本对得上
    with zipfile.ZipFile(zip_path) as z:
        names = z.namelist()
        inner = [n for n in names if n.endswith(".json") and not n.endswith(".deps.json")]
        blob = z.read(inner[0]).decode("utf-8") if inner else "{}"
        inner_manifest = json.loads(blob)
    ok = (inner_manifest.get("InternalName") == internal and
          inner_manifest.get("AssemblyVersion") == version)
    print("自检：zip 内清单 InternalName=%s AssemblyVersion=%s → %s"
          % (inner_manifest.get("InternalName"), inner_manifest.get("AssemblyVersion"),
             "OK" if ok else "不一致！"))
    if not re.fullmatch(r"[A-Za-z0-9._-]+", zip_name):
        print("警告：zip 文件名含非 ASCII 字符")
        return 1
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
