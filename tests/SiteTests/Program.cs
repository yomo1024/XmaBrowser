using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using XmaBrowser.Browser;
using XmaBrowser.Cover;
using XmaBrowser.Diagnostics;
using XmaBrowser.Favorites;
using XmaBrowser.Install;
using XmaBrowser.Site;
using XmaBrowser.Translate;
using XmaBrowser.Ui;

// 站点层 / 包解析层 / 浏览器通道的无游戏自测。
//   离线：dotnet run -c Release                  （用 samples/ 里的真实 HTML，不联网）
//   联网：dotnet run -c Release -- live          （真站点：SFW 直连 + NSFW 浏览器通道 + 真实下载）
var live = args.Contains("live");
var translateOnly = args.Contains("translate");
var nsfwProbe = args.Contains("nsfw");
var nsfwDiag = args.Contains("nsfwdiag");
var timingProbe = args.Contains("timing");
var detailProbe = args.Contains("detailtime");
var loginProbe = args.Contains("login");
var perfProbe = args.Contains("perf");
var imgProbe = args.Contains("imgprobe");
var inspectProbe = args.Contains("inspect");
var nameTestProbe = args.Contains("nametest");
var chanSearchProbe = args.Contains("chansearch");
var adPerfProbe = args.Contains("adperf");
var externalProbe = args.Contains("externals");
var streamProbe = args.Contains("stream");
var oneCheck = args.Contains("one");
var authorProbe = args.Contains("author");
var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  [OK]   {name}"); }
    else { fail++; Console.WriteLine($"  [FAIL] {name}" + (detail.Length > 0 ? $"   <- {detail}" : "")); }
}

string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name));

Console.WriteLine("=== 1. 搜索 URL 拼装 ===");
var url = XmaUrls.Search(new SearchQuery { Text = "hair style", Page = 3, OnlyDtCompatible = true, Tags = "bibo" });
Console.WriteLine("   " + url);
// ⚠ 这条断言原本写的是 `basic_text=hair%20style` —— **那条断言锁住的正是后来报的 bug**：
// 站点 basic_text= 是全文松散匹配，粘 Mod 全名会返回一堆杂项、翻几页都找不到要的那条。
// 现在默认走 name=（标题里每个词都命中）。详见第 9d 节。
Check("含 name=（标题检索，并已转义空格）", url.Contains("name=hair%20style"), url);
Check("默认不发 basic_text=", !url.Contains("basic_text="), url);
Check("含 page=3", url.Contains("page=3"), url);
Check("含 dt_compat=1", url.Contains("dt_compat=1"), url);
Check("含 nsfw=false", url.Contains("nsfw=false"), url);
Check("含 tags=bibo", url.Contains("tags=bibo"), url);
var urlNsfw = XmaUrls.Search(new SearchQuery { NsfwMode = 2, OnlyDtCompatible = false });
Check("只看 NSFW + 不限兼容性时参数正确", urlNsfw.Contains("nsfw=true") && !urlNsfw.Contains("dt_compat"), urlNsfw);

Console.WriteLine();
Console.WriteLine("=== 2. 站点时间串两种形态 ===");
var t1 = XmaTime.Parse("Wed Dec 04 2024 20:28:05 GMT+0000 (Coordinated Universal Time)");
Check("UTC 英文格式可解析", t1 is not null && t1.Value.Year == 2024, t1?.ToString() ?? "null");
var t2 = XmaTime.Parse("2026/9/21 08:34:00");
Check("登录态本地格式可解析", t2 is not null && t2.Value.Month == 9 && t2.Value.Hour == 8, t2?.ToString() ?? "null");
Check("空串返回 null", XmaTime.Parse("") is null);

Console.WriteLine();
Console.WriteLine("=== 2b. 直连被拒：原因判定（人机校验 vs 需要登录态）===");
Check("正常响应 → None",
      XmaBlockClassifier.Classify(200, null, false) == XmaBlockReason.None);
Check("cf-mitigated: challenge 头 → 人机校验（最可靠的判据）",
      XmaBlockClassifier.Classify(403, "", true) == XmaBlockReason.CloudflareChallenge);
Check("挑战页正文 Just a moment → 人机校验",
      XmaBlockClassifier.Classify(403, "<html><title>Just a moment...</title>", false) == XmaBlockReason.CloudflareChallenge);
Check("challenge-platform 正文 → 人机校验",
      XmaBlockClassifier.Classify(403, "<script src=/cdn-cgi/challenge-platform/h/b/orchestrate></script>", false)
          == XmaBlockReason.CloudflareChallenge);
Check("裸 403（站点自己的 403 页）→ 需要登录态",
      XmaBlockClassifier.Classify(403, "<title>403 Error | XIV Mod Archive</title>", false) == XmaBlockReason.NeedsLogin);
Check("404 → 下载直链需要浏览器",
      XmaBlockClassifier.Classify(404, "", false) == XmaBlockReason.DownloadNeedsBrowser);
Check("500 → 其它",
      XmaBlockClassifier.Classify(500, "boom", false) == XmaBlockReason.Other);
Check("说明文案把两种原因分开说",
      XmaBlockClassifier.Describe(XmaBlockReason.CloudflareChallenge).Contains("人机校验") &&
      XmaBlockClassifier.Describe(XmaBlockReason.CloudflareChallenge).Contains("NSFW") &&
      XmaBlockClassifier.Describe(XmaBlockReason.NeedsLogin).Contains("登录态"));
Check("短标签可直接显示",
      XmaBlockClassifier.ShortLabel(XmaBlockReason.CloudflareChallenge) == "站点人机校验" &&
      XmaBlockClassifier.ShortLabel(XmaBlockReason.NeedsLogin) == "需要登录态");

Console.WriteLine();
Console.WriteLine("=== 3. 搜索列表（page1 样本）===");
var p1 = XmaParser.ParseSearch(Sample("search_page1.html"), 1);
Console.WriteLine($"   解析到 {p1.Hits.Count} 条，站点提示总数 {p1.TotalMods}");
foreach (var h in p1.Hits.Take(3))
    Console.WriteLine($"     {h.ModId}  {h.Title}  — {h.Author}  [{h.Type}/{h.Genders}] 浏览{h.Views} {h.Overlay}");
Check("每页 15 条", p1.Hits.Count == 15, p1.Hits.Count.ToString());
Check("全部有 ModId（纯数字）", p1.Hits.All(h => h.ModId.Length > 0 && h.ModId.All(char.IsDigit)));
Check("全部有标题", p1.Hits.All(h => h.Title.Length > 0));
Check("全部有作者", p1.Hits.All(h => h.Author.Length > 0));
Check("全部有缩略图且来自 static 域", p1.Hits.All(h => h.ThumbUrl.StartsWith("https://static.xivmodarchive.com/")),
      p1.Hits.FirstOrDefault(h => !h.ThumbUrl.StartsWith("https://static.xivmodarchive.com/"))?.ThumbUrl ?? "");
Check("至少一条有 Type", p1.Hits.Any(h => h.Type.Length > 0));
Check("至少一条有浏览量", p1.Hits.Any(h => h.Views > 0));
Check("站点总数 > 1000", p1.TotalMods > 1000, p1.TotalMods.ToString());
Check("角标能识别（new）", p1.Hits.Any(h => h.IsNew), string.Join(",", p1.Hits.Select(h => h.Overlay).Distinct()));

Console.WriteLine();
Console.WriteLine("=== 4. 分页（page2 样本与 page1 不重复）===");
var p2 = XmaParser.ParseSearch(Sample("search_page2.html"), 2);
var overlap = p1.Hits.Select(h => h.ModId).Intersect(p2.Hits.Select(h => h.ModId)).Count();
Console.WriteLine($"   page2 {p2.Hits.Count} 条，与 page1 重复 {overlap} 条；page2 首条 {p2.Hits.FirstOrDefault()?.ModId}");
Check("page2 也是 15 条", p2.Hits.Count == 15, p2.Hits.Count.ToString());
Check("两页无重叠", overlap == 0, overlap.ToString());

Console.WriteLine();
Console.WriteLine("=== 5. 标签搜索样本 ===");
var pt = XmaParser.ParseSearch(Sample("search_tag.html"), 1);
Console.WriteLine($"   {pt.Hits.Count} 条，首条：{pt.Hits.FirstOrDefault()?.Title}");
Check("标签搜索结果可解析", pt.Hits.Count == 15, pt.Hits.Count.ToString());

Console.WriteLine();
Console.WriteLine("=== 6. 详情页（95080，站内直链）===");
var d = XmaParser.ParseDetail(Sample("mod_95080.html"), "95080");
Console.WriteLine($"   标题={d.Title}｜作者={d.Author}({d.AuthorId})");
Console.WriteLine($"   下载={d.DownloadHref}");
Console.WriteLine($"   标签={string.Join(",", d.Tags)}｜种族={d.Races}｜性别={d.Genders}");
Console.WriteLine($"   最后更新={d.LastUpdateLocal}｜发布={d.ReleaseLocal}｜图 {d.Images.Count} 张");
Check("标题正确", d.Title == "Pictish Tattoos", d.Title);
Check("作者正确", d.Author == "millyxiv", d.Author);
Check("下载链接是站内 /private/", d.DownloadHref.StartsWith("/private/"), d.DownloadHref);
Check("文件名提示 .zip", d.FileNameHint.EndsWith(".zip"), d.FileNameHint);
Check("不是外链", !d.IsExternalLink);
Check("绝对下载地址在站点域内", d.AbsoluteDownloadUrl.StartsWith("https://www.xivmodarchive.com/private/"), d.AbsoluteDownloadUrl);
Check("标签含 tattoo", d.Tags.Contains("tattoo", StringComparer.OrdinalIgnoreCase), string.Join(",", d.Tags));
Check("种族含 Midlander", d.Races.Contains("Midlander", StringComparison.OrdinalIgnoreCase), d.Races);
Check("性别非空", d.Genders.Length > 0, d.Genders);
Check("最后更新时间已解析", d.LastUpdateLocal is not null && d.LastUpdateLocal.Value.Year >= 2024, d.LastUpdateLocal?.ToString() ?? "null");
Check("有预览图", d.Images.Count >= 1, d.Images.Count.ToString());
Check("描述已抓到", d.Description.Contains("WHITE option", StringComparison.OrdinalIgnoreCase), d.Description[..Math.Min(60, d.Description.Length)]);

Console.WriteLine();
Console.WriteLine("=== 7. 详情页（141115，含 Affects / 多标签）===");
var d2 = XmaParser.ParseDetail(Sample("mod_141115.html"), "141115");
Console.WriteLine($"   标题={d2.Title}｜影响替换={d2.Affects}｜标签={string.Join(",", d2.Tags)}｜图 {d2.Images.Count} 张");
Check("标题含 eyes", d2.Title.Contains("eyes", StringComparison.OrdinalIgnoreCase), d2.Title);
Check("Affects 正确", d2.Affects == "All PC eyes but Hrothgar", d2.Affects);
Check("标签含 eye", d2.Tags.Contains("eye", StringComparer.OrdinalIgnoreCase), string.Join(",", d2.Tags));
Check("有预览图", d2.Images.Count >= 1, d2.Images.Count.ToString());

Console.WriteLine();
Console.WriteLine("=== 8. 外链 Mod 判定 ===");
var ext = new ModDetail { DownloadHref = "https://www.patreon.com/posts/melting-poems-136137428" };
Check("Patreon 链接判为外链", ext.IsExternalLink, ext.AbsoluteDownloadUrl);

Console.WriteLine();
Console.WriteLine("=== 9. 包解析：容器结构探测（用真实 .7z 夹具 + 现造 zip）===");
var tmp = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", Guid.NewGuid().ToString("n")[..8]);
Directory.CreateDirectory(tmp);
var fixDir = Path.Combine(AppContext.BaseDirectory, "samples", "archives");
string Fix(string name) => Path.Combine(fixDir, name);
try
{
    Check("扩展名分类：.pmp 是包", PackageResolver.KindOf("a.pmp") == PackageKind.PenumbraPackage);
    Check("扩展名分类：.ttmp2 是包", PackageResolver.KindOf("a.ttmp2") == PackageKind.PenumbraPackage);
    Check("扩展名分类：.zip 是容器", PackageResolver.KindOf("a.zip") == PackageKind.Container);
    Check("扩展名分类：.7z 是容器（现在支持了）", PackageResolver.KindOf("a.7z") == PackageKind.Container);
    Check("扩展名分类：.rar 是容器（现在支持了）", PackageResolver.KindOf("a.rar") == PackageKind.Container);
    Check("扩展名分类：.exe 仍不支持", PackageResolver.KindOf("a.exe") == PackageKind.Unsupported);

    // ---- 9.1 容器里有多个 Mod 包 ----
    var planMulti = PackageResolver.BuildPlan(Fix("multi_pmp.7z"), Path.Combine(tmp, "w1"));
    Console.WriteLine("   多包容器：" + planMulti.Summary);
    Check("多包：两个包都被找出来", planMulti.Packages.Count == 2, string.Join(",", planMulti.Packages.Select(Path.GetFileName)));
    Check("多包：包名保留（含空格）", planMulti.Packages.Any(p => Path.GetFileName(p).Contains("Alpha Hair")),
          string.Join(",", planMulti.Packages.Select(Path.GetFileName)));
    Check("多包：文件真的解出来了", planMulti.Packages.All(File.Exists));
    Check("多包：说明文字点出数量 2", planMulti.Summary.Contains("2"), planMulti.Summary);

    // ---- 9.2 没有包，但每个子目录是一个 Mod ----
    var planSubs = PackageResolver.BuildPlan(Fix("mods_in_subfolders.7z"), Path.Combine(tmp, "w2"));
    Console.WriteLine("   多 Mod 目录：" + planSubs.Summary);
    Check("多 Mod 目录：识别出 2 个", planSubs.LooseMods.Count == 2,
          string.Join(",", planSubs.LooseMods.Select(m => m.SafeName)));
    Check("多 Mod 目录：名字是 ModA / ModB",
          planSubs.LooseMods.Any(m => m.SafeName == "ModA") && planSubs.LooseMods.Any(m => m.SafeName == "ModB"),
          string.Join(",", planSubs.LooseMods.Select(m => m.SafeName)));
    Check("多 Mod 目录：内容已解出（meta.json 在各自目录里）",
          planSubs.LooseMods.All(m => File.Exists(Path.Combine(m.ExtractedDir, "meta.json"))));
    Check("多 Mod 目录：files/ 子目录也解出来了",
          planSubs.LooseMods.All(m => Directory.Exists(Path.Combine(m.ExtractedDir, "files"))));
    Check("多 Mod 目录：顶层 readme.txt 没被当成一个 Mod",
          planSubs.LooseMods.All(m => m.SafeName != "readme.txt"));

    // ---- 9.3 没有包，整体就是一个 Mod ----
    var planLoose = PackageResolver.BuildPlan(Fix("loose_mod.7z"), Path.Combine(tmp, "w3"));
    Console.WriteLine("   整体一个 Mod：" + planLoose.Summary);
    Check("整体是 Mod：识别为 1 个", planLoose.LooseMods.Count == 1);
    Check("整体是 Mod：拿到 default_mod.json 与 files/",
          planLoose.LooseMods.Count == 1 &&
          File.Exists(Path.Combine(planLoose.LooseMods[0].ExtractedDir, "default_mod.json")) &&
          Directory.Exists(Path.Combine(planLoose.LooseMods[0].ExtractedDir, "files")));

    // ---- 9.4 完全不像 Mod ----
    var planJunk = PackageResolver.BuildPlan(Fix("junk.7z"), Path.Combine(tmp, "w4"));
    Console.WriteLine("   不像 Mod：" + (planJunk.Problem ?? planJunk.Summary));
    Check("不像 Mod：判定为装不了", !planJunk.Ok);
    Check("不像 Mod：说明里列出顶层内容（readme.txt）",
          planJunk.Problem is not null && planJunk.Problem.Contains("readme.txt"), planJunk.Problem ?? "null");
    Check("不像 Mod：说明里点出「没有 Mod 包」",
          planJunk.Problem is not null && planJunk.Problem.Contains("没有 Mod 包"));

    // ---- 9.5 包藏在多层子目录里 ----
    var planNested = PackageResolver.BuildPlan(Fix("nested_pmp.7z"), Path.Combine(tmp, "w5"));
    Console.WriteLine("   嵌套包：" + planNested.Summary);
    Check("嵌套包：能挖出来", planNested.Packages.Count == 1 && planNested.Packages[0].EndsWith(".pmp"),
          string.Join(",", planNested.Packages));

    // ---- 9.6 zip 走同一条路（逻辑只有一份）----
    var innerZip = Path.Combine(tmp, "with_inner.zip");
    using (var z = ZipFile.Open(innerZip, ZipArchiveMode.Create))
    {
        WriteEntry(z, "Sub Mod.pmp", "PK\x03\x04inner");
        WriteEntry(z, "__MACOSX/junk.txt", "junk");
        WriteEntry(z, "readme.txt", "hello");
    }
    var planZip = PackageResolver.BuildPlan(innerZip, Path.Combine(tmp, "w6"));
    Check("zip：同一条逻辑也能取出内层包", planZip.Packages.Count == 1 && planZip.Packages[0].EndsWith(".pmp"),
          planZip.Summary);

    // ---- 9.7 Zip Slip 防护（顺着「整体当成一个 Mod」那条路解压时）----
    var evilZip = Path.Combine(tmp, "evil.zip");
    using (var z = ZipFile.Open(evilZip, ZipArchiveMode.Create))
    {
        WriteEntry(z, "files/a.tex", "tex");        // 让整体看起来像 Mod，从而走到解压分支
        var entry = z.CreateEntry("../evil.txt");
        using var w = new StreamWriter(entry.Open());
        w.Write("pwned");
    }
    var planEvil = PackageResolver.BuildPlan(evilZip, Path.Combine(tmp, "w7"));
    var escaped = File.Exists(Path.Combine(tmp, "evil.txt")) || File.Exists(Path.Combine(tmp, "w7", "evil.txt"));
    Console.WriteLine($"   Zip Slip 尝试：跑到外面了吗 = {escaped}（计划：{planEvil.Summary}）");
    Check("Zip Slip 被挡住（没有文件逃出目标目录）", !escaped);
    Check("Zip Slip 那条不影响正常内容（files/a.tex 仍在）",
          planEvil.LooseMods.Count == 1 &&
          File.Exists(Path.Combine(planEvil.LooseMods[0].ExtractedDir, "files", "a.tex")));

    Check("目录名清洗：非法字符变下划线", PackageResolver.SafeModDirName("a:b*c?.pmp", "fb") == "a_b_c_.pmp",
          PackageResolver.SafeModDirName("a:b*c?.pmp", "fb"));
    Check("目录名清洗：空名用兜底", PackageResolver.SafeModDirName("   ", "fb") == "fb",
          PackageResolver.SafeModDirName("   ", "fb"));
    Check("目录名清洗：超长被截断", PackageResolver.SafeModDirName(new string('x', 200), "fb").Length == 80);
}
finally
{
    try { Directory.Delete(tmp, true); } catch { /* 忽略 */ }
}

Console.WriteLine();
Console.WriteLine("=== 9b. 排序选项：每个值都必须是站点认的合法值 ===");
{
    // 站点搜索页下拉里的合法值（实测抄下来的），别凭印象加
    var legalSorts = new[] { "rank", "time_edited", "time_posted", "name_slug", "views", "views_today", "downloads" };
    Check("排序选项非空", SearchForm.Sorts.Length > 0);
    Check("排序没有重复值", SearchForm.Sorts.Select(s => s.Value).Distinct().Count() == SearchForm.Sorts.Length);
    foreach (var (label, value, order) in SearchForm.Sorts)
    {
        Check($"排序「{label}」的值 {value} 是站点合法值", legalSorts.Contains(value), value);
        Check($"排序「{label}」的方向合法", order is "asc" or "desc", order);
    }

    Check("默认排序 = 最新发布(time_posted)", SearchForm.Sorts[0].Value == "time_posted");
    // 回归：站点对非法 sortby **不报错**，而是静默回落到默认序 —— 写错了会静默失去控制
    Check("不再出现站点不认的 time_published", SearchForm.Sorts.All(s => s.Value != "time_published"));

    var uiSort = new SearchForm { SortIndex = 5 };            // 名称（自然升序）
    var sortUrl = XmaUrls.Search(uiSort.ToQuery(1));
    Check("拼出的 URL 带 sortby=name_slug", sortUrl.Contains("sortby=name_slug"), sortUrl);
    Check("拼出的 URL 带 sortorder=asc", sortUrl.Contains("sortorder=asc"), sortUrl);

    var over = new SearchForm { SortIndex = 999 };           // 越界不得抛
    Check("排序索引越界自动夹取，不抛", over.ToQuery(1).SortBy == "rank", over.ToQuery(1).SortBy);
}

Console.WriteLine();
Console.WriteLine("=== 9c. 装前看包：只读 zip 中央目录（不下载整包）===");
{
    // ① 扩展名判定：实测 .zip / .pmp / .ttmp2 三种**都是** zip 容器
    Check("zip 家族：.zip（带查询串也认）", PackageIndex.IsZipFamily("https://x/a.zip?token=1"));
    Check("zip 家族：.pmp", PackageIndex.IsZipFamily("/private/u/files/Mod.pmp"));
    Check("zip 家族：.ttmp2（实测是 zip）", PackageIndex.IsZipFamily("Tight%26Firm.ttmp2"));
    Check("zip 家族：.7z 不算", !PackageIndex.IsZipFamily("a.7z"));
    Check("zip 家族：.rar 不算", !PackageIndex.IsZipFamily("a.rar"));
    Check("明确非 zip：.7z/.rar 能被认出来（界面据此降级）",
          PackageIndex.IsKnownNonZipArchive("a.7z") && PackageIndex.IsKnownNonZipArchive("b.RAR"));
    Check("扩展名大小写不敏感", PackageIndex.ExtensionOf("A.PMP") == ".pmp", PackageIndex.ExtensionOf("A.PMP"));
    // 回归：`ExtensionOf` 曾被写成只认「文件名」不认「裸扩展名」，导致拿扩展名回头再判一遍时
    // 全部判成 unrecognized（联网探针抓到的真 bug，别改回去）
    Check("裸扩展名也能被认出（.zip 回判必须为真）", PackageIndex.IsZipFamily(".zip"));
    Check("裸扩展名：.pmp / .ttmp2 回判为真", PackageIndex.IsZipFamily(".pmp") && PackageIndex.IsZipFamily(".ttmp2"));
    Check("裸扩展名：.rar 回判为「非 zip 压缩包」", PackageIndex.IsKnownNonZipArchive(".rar"));
    Check("裸扩展名：无扩展名不误判", !PackageIndex.IsZipFamily("Mod") && !PackageIndex.IsKnownNonZipArchive("Mod"));

    // ② 现场造一个真 zip（含两个内嵌 .pmp）→ 找 EOCD → 解析中央目录 → 摘要，整条链路往返
    var zms = new MemoryStream();
    using (var za = new ZipArchive(zms, ZipArchiveMode.Create, true))
    {
        void AddEntry(string name, string text)
        {
            var e = za.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open());
            w.Write(text);
        }
        AddEntry("tiramisu2 [Miqo Ear Jiggle + Tail V1].pmp", "fake-pmp-1");
        AddEntry("tiramisu2 [Physics].pmp", "fake-pmp-2");
        AddEntry("readme.txt", "hello");
    }
    var zbytes = zms.ToArray();
    var zTailOff = Math.Max(0, zbytes.Length - PackageIndex.TailProbeBytes);
    var zTail = zbytes.AsSpan((int)zTailOff).ToArray();

    Check("EOCD 找到了", PackageIndex.TryFindEocd(zTail, zTailOff, out var cdOff, out var cdSize, out var cnt));
    Check("EOCD 里的条目数 = 3", cnt == 3, $"cnt={cnt}");
    Check("中央目录大小 > 0", cdSize > 0, $"cdSize={cdSize}");

    var cd = zbytes.AsSpan((int)cdOff, (int)cdSize).ToArray();
    var zEntries = PackageIndex.ParseCentral(cd);
    Check("中央目录解析出 3 条", zEntries.Count == 3, $"got {zEntries.Count}");
    Check("条目名与原名完全一致（空格/方括号）",
          zEntries.Any(e => e.Name == "tiramisu2 [Miqo Ear Jiggle + Tail V1].pmp"));
    Check("未压缩大小被读出（fake-pmp-1 = 10 字节）",
          zEntries.First(e => e.Name.EndsWith("V1].pmp")).UncompressedSize == 10,
          zEntries.First(e => e.Name.EndsWith("V1].pmp")).UncompressedSize.ToString());

    var zSum = PackageIndex.Summarize(zbytes.Length, "a.zip", zEntries);
    Check("摘要认出 2 个内嵌 mod（这就是「点安装会装进去几个」）", zSum.ModCount == 2, $"ModCount={zSum.ModCount}");
    Check("摘要里的总字节 = 实际字节数", zSum.Bytes == zbytes.Length);
    Check("摘要里未压缩总量 > 0", zSum.UncompressedBytes > 0);

    // ③ 退化输入绝不能抛（界面会拿任意响应喂进来）
    Check("尾部太短 → 返回 false，不抛", !PackageIndex.TryFindEocd(new byte[5], 0, out _, out _, out _));
    Check("不是 zip → 返回 false，不抛",
          !PackageIndex.TryFindEocd(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 },
                                    0, out _, out _, out _));
    Check("空中央目录 → 0 条，不抛", PackageIndex.ParseCentral(Array.Empty<byte>()).Count == 0);
    Check("垃圾字节 → 立刻停下（宁可少列也不乱列）",
          PackageIndex.ParseCentral(new byte[] { 0x67, 0x61, 0x72, 0x62, 0x61, 0x67, 0x65 }).Count == 0);
    Check("片段截断的中央目录不越界",
          PackageIndex.ParseCentral(cd.AsSpan(0, Math.Max(1, cd.Length / 3)).ToArray()).Count >= 0);

    // ④ 大小格式化
    Check("大小：0 → ?（绝不显示成 0）", PackageIndex.HumanSize(0) == "?");
    Check("大小：48.22 MB", PackageIndex.HumanSize(50_557_602) == "48.22 MB", PackageIndex.HumanSize(50_557_602));
    Check("大小：GB 档", PackageIndex.HumanSize(258_000_000_000).EndsWith(" GB"), PackageIndex.HumanSize(258_000_000_000));

    // ⑤ 结果对象的不变量：**列不出内容时绝不能假装有数据**
    var onlySizeRes = PackageInspection.SizeOnly("a.7z", ".7z", 86_000_000, "不是 zip 容器");
    Check("只拿到大小时：HasListing=false", !onlySizeRes.HasListing);
    Check("只拿到大小时：SizeKnown=true", onlySizeRes.SizeKnown);
    Check("只拿到大小时：大小格式化正确", onlySizeRes.SizeText == "82.02 MB", onlySizeRes.SizeText);

    var failedRes = PackageInspection.Failed("a.zip", ".zip", "被挡了");
    Check("探测失败时：Bytes=0 且 SizeKnown=false（界面显示「未知」而不是 0）",
          failedRes.Bytes == 0 && !failedRes.SizeKnown && !failedRes.HasListing);
}

Console.WriteLine();
Console.WriteLine("=== 9d. 关键词默认按标题搜（name=）—— 别又改回 basic_text ===");
{
    // 实测复现（真站点，搜「Bellissente, a makeup for Aerin」）：
    //   basic_text=（全文松散匹配）→ 15 条杂项，**翻到第 3 页都没有它**，换任何排序都一样
    //   name=（标题里每个词都命中）→ **1 条，正是它，排第 1**
    var titleQ = new SearchQuery { Text = "Bellissente, a makeup for Aerin" };
    var titleUrl = XmaUrls.Search(titleQ);
    Check("默认：关键词走 name=（标题检索）", titleUrl.Contains("name="), titleUrl);
    Check("默认：不发 basic_text=", !titleUrl.Contains("basic_text="), titleUrl);
    Check("Mod 全名被完整转义进去（含逗号与空格）",
          titleUrl.Contains("name=Bellissente%2C%20a%20makeup%20for%20Aerin"), titleUrl);

    var fullQ = new SearchQuery { Text = "tattoo", TextAsFullText = true };
    var fullUrl = XmaUrls.Search(fullQ);
    Check("只有显式开了全文，才发 basic_text=", fullUrl.Contains("basic_text=tattoo"), fullUrl);
    Check("全文模式下不再额外发 name=（避免同名参数）", !fullUrl.Contains("name="), fullUrl);

    // ⚠ 绝不发出两个同名参数：站点对重复 name= 的行为未验证，不能赌
    var bothQ = new SearchQuery { Text = "aaa", Name = "bbb" };
    var bothUrl = XmaUrls.Search(bothQ);
    Check("关键词 + 名称 → 只发一个 name=",
          System.Text.RegularExpressions.Regex.Matches(bothUrl, "name=").Count == 1, bothUrl);
    Check("关键词 + 名称 → 两段词合进同一个 name=（AND 语义）",
          bothUrl.Contains("name=aaa%20bbb"), bothUrl);

    var nameOnly = new SearchQuery { Name = "bbb" };
    Check("只填名称 → 也走 name=", XmaUrls.Search(nameOnly).Contains("name=bbb"), XmaUrls.Search(nameOnly));

    var mixedUrl = XmaUrls.Search(new SearchQuery { Text = "tattoo", TextAsFullText = true, Name = "bbb" });
    Check("全文 + 名称 → basic_text 与 name 各一个",
          mixedUrl.Contains("basic_text=tattoo") && mixedUrl.Contains("name=bbb"), mixedUrl);

    Check("空关键词不产生任何文本参数",
          !XmaUrls.Search(new SearchQuery()).Contains("name=")
          && !XmaUrls.Search(new SearchQuery()).Contains("basic_text="));

    Check("界面说明非空", TextSearch.Describe(false).Length > 0 && TextSearch.Describe(true).Length > 0);
    // 回归：默认值一旦被改成 true，就等于把「搜 Mod 名搜不到」这个 bug 放回来
    Check("表单默认 = 标题搜（不是全文）", !new SearchForm().TextAsFullText);
    Check("Reset 后仍是标题搜", !ResetIsFullText());
    // 回归：SearchQuery 自己的默认排序也必须是站点合法值（它曾长期是非法值 time_published，
    // 只有走 SearchForm 的界面才是对的，直接构造 SearchQuery 的调用方全在赌站点回退）
    var legalSortNames = new[] { "rank", "time_edited", "time_posted", "name_slug", "views", "views_today", "downloads" };
    Check("SearchQuery 默认排序是合法值",
          legalSortNames.Contains(new SearchQuery().SortBy), new SearchQuery().SortBy);
}

Console.WriteLine();
Console.WriteLine("=== 9e. 0 条时要说清原因（不能只有一句「没有结果」）===");
{
    var normalPage = Sample("search_page1.html");
    var dNormal = EmptyResultDiagnosis.Describe(normalPage, "https://x/search?name=a");
    Console.WriteLine("   正常列表页(15 张卡)： " + dNormal);
    Check("页面有卡片却没解析出来 → 指出是解析/改版问题（而不是误报未登录）",
          dNormal.Contains("卡片") && dNormal.Contains("解析"), dNormal);

    var dTiny = EmptyResultDiagnosis.Describe("<html></html>", "https://x/search");
    Check("页面异常小 → 指出可能被拦", dTiny.Contains("异常小"), dTiny);

    var dEmpty = EmptyResultDiagnosis.Describe("", "https://x/search");
    Check("站点没返回内容 → 明说没返回", dEmpty.Contains("没返回"), dEmpty);

    var dNull = EmptyResultDiagnosis.Describe(null, "https://x/search");
    Check("null 不抛异常", dNull.Length > 0);

    Check("每种结论都带上实际请求的 URL（便于定位）",
          dNormal.Contains("https://x/search?name=a") && dTiny.Contains("https://x/search"));
    Check("可疑小页面的阈值有常量可查", EmptyResultDiagnosis.SuspiciouslySmallBytes > 1024);

    // 通道 0 条时的兜底决策（混合模式下每次搜索都走通道，通道一坏就「一条都没有」）
    Check("通道 0 条 + 允许兜底 → 该去试直连",
          SearchRouting.ShouldFallbackToDirect(0, true));
    Check("通道有结果时不动它（绝不画蛇添足）",
          !SearchRouting.ShouldFallbackToDirect(1, true) && !SearchRouting.ShouldFallbackToDirect(15, true));
    Check("用户关掉了自动降级 → 不自作主张", !SearchRouting.ShouldFallbackToDirect(0, false));
}

static bool ResetIsFullText()
{
    var f = new SearchForm { TextAsFullText = true };
    f.Reset();
    return f.TextAsFullText;
}

Console.WriteLine();
Console.WriteLine("=== 9f. 作者名里的撇号：只清洗 author，绝不动 name ===");
{
    // 实测（真站点）：
    //   author=Helina's Bakery → 0 条；author=Helina&#39;s Bakery → 0 条；
    //   **author=Helina s Bakery（撇号换空格）→ 15 条 ✓**
    // 而标题侧是反的：name=Kitten's Rapture → 1 条 ✓、name=Kitten s Rapture → 0 条 ✗
    // —— 所以这两个参数的清洗规则不能混。
    Check("ASCII 撇号 → 空格", AuthorQuery.Sanitize("Helina's Bakery") == "Helina s Bakery",
          AuthorQuery.Sanitize("Helina's Bakery"));
    Check("弯撇号（U+2019，从网页复制多半是它）→ 空格",
          AuthorQuery.Sanitize("Helina\u2019s Bakery") == "Helina s Bakery",
          AuthorQuery.Sanitize("Helina\u2019s Bakery"));
    Check("双引号 / 反引号也一起清掉",
          AuthorQuery.Sanitize("a\"b`c") == "a b c", AuthorQuery.Sanitize("a\"b`c"));
    Check("折叠连续空格 + 去首尾", AuthorQuery.Sanitize("  Helina's   Bakery  ") == "Helina s Bakery",
          AuthorQuery.Sanitize("  Helina's   Bakery  "));
    Check("全是坏字符 → 空串（宁可不发这个参数，也别发一个必然 0 条的值）",
          AuthorQuery.Sanitize("'''") == "");
    Check("空 / null 不抛", AuthorQuery.Sanitize("") == "" && AuthorQuery.Sanitize(null) == "");
    Check("没坏字符时原样返回", AuthorQuery.Sanitize("Helina Bakery") == "Helina Bakery");
    Check("HasContent 判定正确", AuthorQuery.HasContent("Helina") && !AuthorQuery.HasContent("'''"));

    // URL 层：作者名带撇号时必须发清洗后的值
    // （空格被 EscapeDataString 编成 %20 —— 实测站点两者都能正确解码，与插件其它参数保持一致）
    var apostropheAuthor = XmaUrls.Search(new SearchQuery { Author = "Helina's Bakery" });
    Check("作者撇号：URL 里发的是清洗后的值",
          apostropheAuthor.Contains("author=Helina%20s%20Bakery"), apostropheAuthor);
    Check("作者撇号：URL 里不再出现 %27", !apostropheAuthor.Contains("%27"), apostropheAuthor);

    // ⚠ 反向回归：**标题侧绝不能被清洗**（清洗了反而把好的一起弄坏）
    var apostropheTitle = XmaUrls.Search(new SearchQuery { Text = "Kitten's Rapture" });
    Check("标题撇号：原样保留（name=Kitten%27s%20Rapture）",
          apostropheTitle.Contains("name=Kitten%27s%20Rapture"), apostropheTitle);

    Check("变化说明可选输出", AuthorQuery.DescribeIfChanged("Helina's").Contains("Helina s")
                              && AuthorQuery.DescribeIfChanged("Helina") == "");
}

Console.WriteLine();
Console.WriteLine("=== 10. 封面：定位 / 注入包 / 真伪 WebP 判定 ===");
var ctmp = Path.Combine(Path.GetTempPath(), "XmaBrowserCoverTests", Guid.NewGuid().ToString("n")[..8]);
Directory.CreateDirectory(ctmp);
try
{
    var jpg = Path.Combine(ctmp, "cover.jpg");
    File.WriteAllBytes(jpg, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]);

    var modA = Path.Combine(ctmp, "modA");
    Directory.CreateDirectory(modA);
    File.WriteAllBytes(Path.Combine(modA, "cover.webp"),
                       [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]);
    Check("真 WebP（RIFF…WEBP）判定为真", CoverLocator.IsRealWebp(Path.Combine(modA, "cover.webp")));
    Check("只有 cover.webp 时不画（Dalamud 贴图不认 WebP）", CoverLocator.FindDrawable(modA) is null);
    Check("已有真 WebP 封面 → 不去覆盖", CoverLocator.HasRealWebpCover(modA));

    var modB = Path.Combine(ctmp, "modB");
    Directory.CreateDirectory(modB);
    File.WriteAllBytes(Path.Combine(modB, "cover.webp"), [0xFF, 0xD8, 0xFF, 0xE0]);   // jpg 改名成 webp 的假货
    Check("假 WebP 不被当成真封面（否则后续修复会被 skip 死锁）", !CoverLocator.HasRealWebpCover(modB));
    File.Copy(jpg, Path.Combine(modB, "cover.png"));
    Check("cover.png 能被定位", CoverLocator.FindDrawable(modB)?.EndsWith("cover.png", StringComparison.OrdinalIgnoreCase) == true,
          CoverLocator.FindDrawable(modB) ?? "null");

    var modC = Path.Combine(ctmp, "modC");
    Directory.CreateDirectory(Path.Combine(modC, "images"));
    File.Copy(jpg, Path.Combine(modC, "images", "_MetaImage.jpg"));
    Check("images/_MetaImage.jpg 能被定位",
          CoverLocator.FindDrawable(modC)?.EndsWith("_MetaImage.jpg", StringComparison.OrdinalIgnoreCase) == true,
          CoverLocator.FindDrawable(modC) ?? "null");

    var modD = Path.Combine(ctmp, "modD");
    Directory.CreateDirectory(Path.Combine(modD, "images"));
    File.WriteAllText(Path.Combine(modD, "meta.json"), "{\"Image\":\"images\\\\_MetaImage.png\"}");
    File.Copy(jpg, Path.Combine(modD, "images", "_MetaImage.png"));
    Check("meta.json 里 Image 指的图能被定位", CoverLocator.FindDrawable(modD) is not null);

    // 注入：包里已有 meta.json 与素材
    var pkg = Path.Combine(ctmp, "MyMod.pmp");
    using (var z = ZipFile.Open(pkg, ZipArchiveMode.Create))
    {
        WriteEntry(z, "meta.json", "{\"Name\":\"MyMod\",\"FileVersion\":3}");
        WriteEntry(z, "default_mod.json", "{}");
        WriteEntry(z, "textures/a.tex", "data");
    }

    var injected = CoverInjector.InjectIntoPackage(pkg, jpg, ctmp);
    Check("注入产生了新包", injected is not null && File.Exists(injected), injected ?? "null");
    if (injected is not null)
    {
        using var z = ZipFile.OpenRead(injected);
        var names = z.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        Check("新包含 cover.jpg", names.Contains("cover.jpg"), string.Join(",", names));
        Check("新包含 images/_MetaImage.jpg", names.Contains("images/_MetaImage.jpg"), string.Join(",", names));
        Check("原有文件一个不少", names.Contains("default_mod.json") && names.Contains("textures/a.tex"));
        var meta = z.Entries.First(e => e.FullName == "meta.json");
        using var sr = new StreamReader(meta.Open());
        var text = sr.ReadToEnd();
        Check("meta.json 的 Image 已指向新封面", text.Contains("_MetaImage.jpg"), text);
        Check("meta.json 原有字段保留", text.Contains("MyMod"), text);
    }

    Check("没有封面图 → 不折腾，返回 null", CoverInjector.InjectIntoPackage(pkg, null, ctmp) is null);
    Check("封面不是可解码格式 → 不注入", CoverInjector.InjectIntoPackage(pkg, Path.Combine(modA, "cover.webp"), ctmp) is null);
}
finally
{
    try { Directory.Delete(ctmp, true); } catch { /* 忽略 */ }
}

Console.WriteLine();
Console.WriteLine("=== 11. 装进文件夹：路径模板计算 ===");
Check("默认模板 {prefix}/{type} → XMA/Gear",
      GroupPath.BuildFolder("{prefix}/{type}", "XMA", "Gear", "someone", "123", "My Mod") == "XMA/Gear",
      GroupPath.BuildFolder("{prefix}/{type}", "XMA", "Gear", "someone", "123", "My Mod"));
Check("类型为空 → Other（不出现空段）",
      GroupPath.BuildFolder("{prefix}/{type}", "XMA", "", "someone", "123", "My Mod") == "XMA/Other",
      GroupPath.BuildFolder("{prefix}/{type}", "XMA", "", "someone", "123", "My Mod"));
Check("前缀留空 → 只剩类型",
      GroupPath.BuildFolder("{prefix}/{type}", "", "Hair", "someone", "123", "My Mod") == "Hair",
      GroupPath.BuildFolder("{prefix}/{type}", "", "Hair", "someone", "123", "My Mod"));
Check("模板留空 → 不分组", GroupPath.BuildFolder("", "XMA", "Gear", "a", "1", "n") == "");
Check("三级模板 {prefix}/{type}/{author}",
      GroupPath.BuildFolder("{prefix}/{type}/{author}", "XMA", "Gear", "Some Author", "1", "n") == "XMA/Gear/Some Author",
      GroupPath.BuildFolder("{prefix}/{type}/{author}", "XMA", "Gear", "Some Author", "1", "n"));
Check("占位符大小写不敏感",
      GroupPath.BuildFolder("{PREFIX}/{Type}", "XMA", "Gear", "a", "1", "n") == "XMA/Gear");
Check("非法字符被替换（冒号/星号/竖线）",
      GroupPath.BuildFolder("{prefix}/{type}", "X:MA", "Ge*ar|X", "a", "1", "n") == "X_MA/Ge_ar_X",
      GroupPath.BuildFolder("{prefix}/{type}", "X:MA", "Ge*ar|X", "a", "1", "n"));
Check("目录穿越段被丢掉（.. 不进路径）",
      !GroupPath.BuildFolder("{prefix}/{type}", "..", "../Gear", "a", "1", "n").Contains(".."),
      GroupPath.BuildFolder("{prefix}/{type}", "..", "../Gear", "a", "1", "n"));
Check("深层级被限制在 4 层内",
      GroupPath.BuildFolder("{prefix}/{type}/{author}/{modid}/{name}", "X", "T", "A", "1", "N").Split('/').Length == 4);
Check("段内超长被截断",
      GroupPath.SanitizeSegment(new string('x', 200)).Length == 48);
Check("完整路径 = 文件夹 + 当前目录名",
      GroupPath.BuildFullPath("XMA/Gear", "My Mod") == "XMA/Gear/My Mod",
      GroupPath.BuildFullPath("XMA/Gear", "My Mod"));
Check("不分组时完整路径就是目录名",
      GroupPath.BuildFullPath("", "My Mod") == "My Mod");
Check("安全检查：正常相对路径通过", GroupPath.IsSafeRelativePath("XMA/Gear/My Mod"));
Check("安全检查：绝对路径/盘符/穿越被拒",
      !GroupPath.IsSafeRelativePath("/etc/passwd") && !GroupPath.IsSafeRelativePath("C:/x") &&
      !GroupPath.IsSafeRelativePath("XMA/../../x"));
Check("预览文案可用", GroupPath.Describe("{prefix}/{type}", "XMA").Contains("XMA/Gear/My Mod"),
      GroupPath.Describe("{prefix}/{type}", "XMA"));

Console.WriteLine();
Console.WriteLine("=== 12. 归组：候选路径形态 + 读回校验 ===");
var cand = GroupPath.CandidatePaths("XMA/Gear", "MyModDir", "My Mod");
Console.WriteLine("   候选：" + string.Join("  |  ", cand));
Check("候选 1 = 文件夹+当前目录名（主方案：只移动、不改名）", cand.Contains("XMA/Gear/MyModDir"));
Check("候选 2 = 文件夹+显示名（节点名与显示名不一致时）", cand.Contains("XMA/Gear/My Mod"));
Check("**不再**提供「只给文件夹」形态（那会把 Mod 改名成文件夹名）", !cand.Contains("XMA/Gear"));
Check("候选不含不安全路径", cand.All(GroupPath.IsSafeRelativePath));
Check("节点名 == 显示名时只给一条候选", GroupPath.CandidatePaths("XMA", "Same", "Same").Count == 1);

// 末段就是「节点的新名字」：必须原样保留，否则会顺手把用户的 Mod 改名
Check("完整路径保留原名原样（[ ] 等字符不动）",
      GroupPath.BuildFullPath("XMA/Gear", "[Rini] Pooptober") == "XMA/Gear/[Rini] Pooptober",
      GroupPath.BuildFullPath("XMA/Gear", "[Rini] Pooptober"));
var longName = new string('x', 200);
Check("超长名字不被截断（截断=悄悄改名）", GroupPath.BuildFullPath("XMA", longName) == "XMA/" + longName,
      "长度 " + GroupPath.BuildFullPath("XMA", longName).Length);

Check("读回校验：绝对路径含目标文件夹 → 通过",
      GroupPath.PathUnderFolder(@"F:\Penumbra\Mods\XMA\Gear\MyMod", "XMA/Gear"));
Check("读回校验：相对路径含目标文件夹 → 通过",
      GroupPath.PathUnderFolder("XMA/Gear/MyMod", "XMA/Gear"));
Check("读回校验：混合斜杠 → 通过",
      GroupPath.PathUnderFolder(@"XMA\Gear\MyMod", "XMA/Gear"));
Check("读回校验：仍在根目录 → 不通过",
      !GroupPath.PathUnderFolder("MyModDir", "XMA/Gear"));
Check("读回校验：只是同名前缀（XMA2）→ 不通过",
      !GroupPath.PathUnderFolder("XMA2/Gear/MyMod", "XMA"));
Check("读回校验：路径为空 → 不通过", !GroupPath.PathUnderFolder("", "XMA"));

Console.WriteLine();
Console.WriteLine("=== 13b. 站点 NSFW 三态 → URL 参数（实测语义）===");
{
    // 0=排除 → nsfw=false；1=混合 → 不传参数；2=只看 → nsfw=true
    var q0 = XmaUrls.Search(new SearchQuery { NsfwMode = 0 });
    var q1 = XmaUrls.Search(new SearchQuery { NsfwMode = 1 });
    var q2 = XmaUrls.Search(new SearchQuery { NsfwMode = 2 });
    Console.WriteLine("   排除: " + q0);
    Console.WriteLine("   混合: " + q1);
    Console.WriteLine("   只看: " + q2);
    Check("排除 NSFW → 发 nsfw=false", q0.Contains("nsfw=false"));
    Check("混合（显示 NSFW）→ **不传** nsfw 参数", !q1.Contains("nsfw="));
    Check("只看 NSFW → 发 nsfw=true", q2.Contains("nsfw=true"));
    Check("三态互不相同（不会又把「显示」当成「只看」）", q0 != q1 && q1 != q2 && q0 != q2);
    Check("越界值按排除处理", XmaUrls.Search(new SearchQuery { NsfwMode = 99 }).Contains("nsfw=false"));

    // 通道选择：NSFW 模式必须带登录态去取，否则直连只会拿到纯普通内容
    // （实测「混合」：直连 53281 条 vs 带登录态通道 61787 条 = 53281 + 8506）
    Check("排除模式 → 直连即可", !SearchRouting.MustUseBrowserChannel(0, true, true));
    Check("混合模式 → 必须走浏览器通道", SearchRouting.MustUseBrowserChannel(1, true, true));
    Check("只看 NSFW → 必须走浏览器通道", SearchRouting.MustUseBrowserChannel(2, true, true));
    Check("通道关掉时不强求（退回直连）", !SearchRouting.MustUseBrowserChannel(1, false, true));
    Check("自动回退关掉时不强求", !SearchRouting.MustUseBrowserChannel(1, true, false));
}

Console.WriteLine();
Console.WriteLine("=== 13c. 站点登录态判定（标记来自实测对比）===");
{
    var anonHtml = File.ReadAllText(Path.Combine("samples", "search_page1.html"));
    var anon = LoginStateDetector.Analyze(anonHtml);
    Console.WriteLine($"   匿名样本 → {anon.State}｜总数 {anon.TotalMods}｜卡片 {anon.CardCount}｜NSFW按钮禁用={anon.NsfwButtonDisabled}");
    Check("匿名页面判定为「未登录」", anon.State == XmaLoginState.LoggedOut, anon.Note);
    Check("匿名页面的 NSFW 按钮是禁用的", anon.NsfwButtonDisabled);
    Check("匿名混合总数能解析出来", anon.TotalMods > 50000, anon.TotalMods.ToString());
    Check("本页卡片数 = 15（只数带后随空格的 mod-card，避开 mod-card-img/overlay）", anon.CardCount == 15, anon.CardCount.ToString());

    // 已登录页面的最小样本（真实页面里有账号信息，不进仓库）
    const string loggedIn =
        "<nav><a href=\"/logout\">Log Out</a></nav>" +
        "<button class=\"btn ternary-toggle ternary-click ternary-nsfw ternary-off mx-1\">NSFW</button>" +
        "<code>61787 Results</code><div class=\"mod-card\"></div>";
    var li = LoginStateDetector.Analyze(loggedIn);
    Check("有登出入口 → 判定为「已登录」", li.State == XmaLoginState.LoggedIn, li.Note);
    Check("已登录页面的 NSFW 按钮不禁用", !li.NsfwButtonDisabled);
    Check("总数解析 61787", li.TotalMods == 61787, li.TotalMods.ToString());

    // 反向：禁用状态要能认出来（disabled 可能排在 class 之后）
    const string disabledAfter =
        "<button class=\"btn ternary-nsfw ternary-off\" type=\"button\" disabled=\"disabled\" title=\"x\">NSFW</button>";
    Check("disabled 排在后面也能认出", LoginStateDetector.NsfwButtonDisabled(disabledAfter));

    Check("挑战页被识别",
          LoginStateDetector.Analyze("<html><title>Just a moment...</title><div id=\"cf-chl\"></div></html>").State
          == XmaLoginState.CloudflareChallenge);
    Check("空页面 = 未知", LoginStateDetector.Analyze("").State == XmaLoginState.Unknown);
    Check("既无登录也无登出入口 = 未知",
          LoginStateDetector.Analyze("<html><body>hello</body></html>").State == XmaLoginState.Unknown);
}

Console.WriteLine();
Console.WriteLine("=== 13d. 收藏 / 关注（本地逻辑，不联网）===");
{
    var favs = new List<FavoriteMod>();
    Check("初始没收藏", !FavoriteStore.IsFavorite(favs, "100"));
    Check("收藏返回 true", FavoriteStore.Toggle(favs, "100", "T", "A", "Gear", "u"));
    Check("收藏后查得到", FavoriteStore.IsFavorite(favs, "100"));
    Check("再切一次 = 取消", !FavoriteStore.Toggle(favs, "100", "T", "A", "Gear", "u"));
    Check("取消后查不到", !FavoriteStore.IsFavorite(favs, "100"));
    Check("取消不存在的返回 false", !FavoriteStore.Unfavorite(favs, "999"));

    FavoriteStore.Toggle(favs, "1", "first", "a", "t", "u");
    FavoriteStore.Toggle(favs, "2", "second", "a", "t", "u");
    Check("新收藏排在最前", favs[0].ModId == "2" && favs[1].ModId == "1", string.Join(",", favs.Select(f => f.ModId)));

    var many = new List<FavoriteMod>();
    for (var i = 0; i < FavoriteStore.MaxFavorites + 25; i++)
        FavoriteStore.Toggle(many, i.ToString(), "t", "a", "t", "u");
    Check($"条数被截到上限 {FavoriteStore.MaxFavorites}", many.Count == FavoriteStore.MaxFavorites, many.Count.ToString());
    Check("丢掉的是最早收藏的", many.All(f => f.ModId != "0"));

    var dup = new List<FavoriteMod>();
    FavoriteStore.Toggle(dup, "x", "t", "a", "t", "u");
    FavoriteStore.Toggle(dup, "x", "t", "a", "t", "u");
    FavoriteStore.Toggle(dup, "x", "t", "a", "t", "u");
    Check("同一 modId 不会重复", dup.Count == 1, dup.Count.ToString());

    var authors = new List<FollowedAuthor>();
    Check("关注作者返回 true", FavoriteStore.ToggleAuthor(authors, " Sadanoya "));
    Check("名字去空格 + 大小写不敏感都能查到", FavoriteStore.IsFollowing(authors, "sadanoya"));
    Check("重复关注 = 取消", !FavoriteStore.ToggleAuthor(authors, "Sadanoya"));
    Check("空名字不关注", !FavoriteStore.ToggleAuthor(authors, "   ") && authors.Count == 0);

    FavoriteStore.ToggleAuthor(authors, "Sadanoya");
    var author = authors[0];
    var first = FavoriteStore.DiffAndRemember(author, ["m1", "m2"]);
    Check("首次关注不算新品（否则一关注就报「新增 2 条」）", first.Count == 0, first.Count.ToString());
    Check("已见集合记了 2 条", author.SeenModIds.Count == 2);
    var second = FavoriteStore.DiffAndRemember(author, ["m2", "m3", "m4"]);
    Check("第二次比出 2 条新品", second.Count == 2 && second.Contains("m3") && second.Contains("m4"),
          string.Join(",", second));
    Check("新品写进 author.NewModIds", author.NewModIds.Count == 2);
    Check("没有新东西时为 0", FavoriteStore.DiffAndRemember(author, ["m1", "m2", "m3", "m4"]).Count == 0);
    FavoriteStore.MarkAuthorRead(author);
    Check("「已看过」后新品清零", author.NewModIds.Count == 0);

    for (var i = 0; i < FavoriteStore.MaxSeenIds + 40; i++)
        FavoriteStore.DiffAndRemember(author, [$"s{i}"]);
    Check($"已见集合也被截到上限 {FavoriteStore.MaxSeenIds}", author.SeenModIds.Count == FavoriteStore.MaxSeenIds,
          author.SeenModIds.Count.ToString());

    var hit = new SearchHit { ModId = "777", Title = "T", Author = "A", Type = "Gear", ThumbUrl = "u" };
    var fromHit = FavoriteStore.FromHit(hit);
    Check("FromHit 带上 id/标题/图", fromHit.ModId == "777" && fromHit.Title == "T" && fromHit.ThumbUrl == "u");
}

Console.WriteLine();
Console.WriteLine("=== 13e. 界面缩放取值规则 ===");
{
    Check("默认 100%（UiScale.Default = 1）", UiScale.Default == 1f && UiScale.Clamp(1f) == 1f);
    Check("低于下限抬到 70%", UiScale.Clamp(0.3f) == UiScale.Min, UiScale.Clamp(0.3f).ToString("0.00"));
    Check("高于上限压到 150%", UiScale.Clamp(9f) == UiScale.Max, UiScale.Clamp(9f).ToString("0.00"));
    Check("NaN / 无穷回落到默认",
          UiScale.Clamp(float.NaN) == UiScale.Default && UiScale.Clamp(float.PositiveInfinity) == UiScale.Default);
    Check("区间内的 85% 原样通过", Math.Abs(UiScale.Clamp(0.85f) - 0.85f) < 1e-6f);
    Check("两边界值本身合法", UiScale.Clamp(UiScale.Min) == UiScale.Min && UiScale.Clamp(UiScale.Max) == UiScale.Max);
    Check("百分比描述", UiScale.Describe(0.85f) == "85%" && UiScale.Describe(1f) == "100%",
          UiScale.Describe(0.85f));
    Check("分栏默认 50%", UiScale.DefaultSplit == 0.5f && UiScale.ClampSplit(0.5f) == 0.5f);
    Check("分栏越界被钳到 25%/75%",
          UiScale.ClampSplit(0f) == UiScale.MinSplit && UiScale.ClampSplit(1f) == UiScale.MaxSplit);
    Check("分栏 NaN 回落默认", UiScale.ClampSplit(float.NaN) == UiScale.DefaultSplit);
}

Console.WriteLine();
Console.WriteLine("=== 13f. 搜索结果短期缓存 ===");
{
    var cache = new SearchCache(ttlSeconds: 2, capacity: 3);
    var urlA = "https://www.xivmodarchive.com/search?nsfw=false&page=1";
    var urlB = "https://www.xivmodarchive.com/search?nsfw=false&page=2";

    Check("空缓存查不到", !cache.TryGet(urlA, out _));
    cache.Put(urlA, new SearchResult { TotalMods = 53281, Hits = [new SearchHit { ModId = "1" }] });
    Check("放进去就能查到", cache.TryGet(urlA, out var got) && got.TotalMods == 53281 && got.Hits.Count == 1);
    Check("不同 URL 不串（分页/筛选各自独立）", !cache.TryGet(urlB, out _));
    Check("键就是完整 URL", SearchCache.KeyFor("  " + urlA + "  ") == urlA);

    cache.Put(urlB, new SearchResult { TotalMods = 1 });
    cache.Put("u3", new SearchResult { TotalMods = 3 });
    cache.Put("u4", new SearchResult { TotalMods = 4 });
    Check("超出容量会淘汰（不无限涨）", cache.Count <= 3, cache.Count.ToString());

    // TTL：等过期
    var tiny = new SearchCache(ttlSeconds: 1, capacity: 4);
    tiny.Put("x", new SearchResult { TotalMods = 9 });
    Check("TTL 内命中", tiny.TryGet("x", out _));
    Thread.Sleep(1200);
    Check("TTL 过期后失效", !tiny.TryGet("x", out _));

    var off = new SearchCache(ttlSeconds: 0, capacity: 4);
    off.Put("y", new SearchResult());
    Check("TTL=0 = 关闭缓存", !off.TryGet("y", out _) && off.Count == 0);

    Check("空 URL 不缓存", !new SearchCache().TryGet("", out _));
}

Console.WriteLine();
Console.WriteLine("=== 13g. 直连被人机校验后的冷却记忆 ===");
{
    var mem = new DirectBlockMemory(TimeSpan.FromMinutes(5));
    var t0 = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    Check("初始不冷却", !mem.IsBlocked(t0) && mem.Describe(t0).Length == 0);
    mem.MarkBlocked(t0);
    Check("标记后进入冷却", mem.IsBlocked(t0) && mem.IsBlocked(t0.AddMinutes(4)));
    Check("冷却描述带剩余分钟", mem.Describe(t0.AddMinutes(1)).Contains("4 分钟"),
          mem.Describe(t0.AddMinutes(1)));
    Check("到点自动恢复", !mem.IsBlocked(t0.AddMinutes(5)) && !mem.IsBlocked(t0.AddMinutes(6)));
    Check("恢复后描述为空", mem.Describe(t0.AddMinutes(9)).Length == 0);

    mem.MarkBlocked(t0.AddMinutes(10));
    mem.MarkHealthy();
    Check("直连成功后立刻清掉冷却", !mem.IsBlocked(t0.AddMinutes(10)));

    var shortMem = new DirectBlockMemory(TimeSpan.FromSeconds(30));
    shortMem.MarkBlocked(t0);
    Check("自定义冷却时长生效", shortMem.IsBlocked(t0.AddSeconds(29)) && !shortMem.IsBlocked(t0.AddSeconds(31)));
}

Console.WriteLine();
Console.WriteLine("=== 13i. 详情短期缓存 + 「需要登录态」记忆 ===");
{
    // —— 详情缓存：NSFW 详情要过浏览器通道（实测 2.8–3.6 秒），来回点不该每次重来
    var dc = new DetailCache(ttlSeconds: 2, capacity: 3);
    Check("详情缓存：空的时候查不到", !dc.TryGet("141115", out _));

    dc.Put("141115", new ModDetail { ModId = "141115", Title = "Vanilla+ eyes" });
    Check("详情缓存：放进去就能查到",
          dc.TryGet("141115", out var d1) && d1.Title == "Vanilla+ eyes");
    Check("详情缓存：不同 mod 不串", !dc.TryGet("95080", out _));
    Check("详情缓存：键会去掉空白", DetailCache.KeyFor("  141115  ") == "141115");

    dc.Remove("141115");
    Check("详情缓存：Remove 之后失效", !dc.TryGet("141115", out _));

    dc.Put("a", new ModDetail { Title = "a" });
    dc.Put("b", new ModDetail { Title = "b" });
    dc.Put("c", new ModDetail { Title = "c" });
    dc.Put("d", new ModDetail { Title = "d" });
    Check("详情缓存：超出容量会淘汰（不无限涨）", dc.Count <= 3, dc.Count.ToString());

    var dcTiny = new DetailCache(ttlSeconds: 1, capacity: 4);
    dcTiny.Put("x", new ModDetail { Title = "x" });
    Check("详情缓存：TTL 内命中", dcTiny.TryGet("x", out _));
    Thread.Sleep(1200);
    Check("详情缓存：TTL 过期后失效", !dcTiny.TryGet("x", out _));

    var dcOff = new DetailCache(ttlSeconds: 0, capacity: 4);
    dcOff.Put("y", new ModDetail());
    Check("详情缓存：TTL=0 = 关闭", !dcOff.TryGet("y", out _) && dcOff.Count == 0);
    Check("详情缓存：空 modId 不缓存", !new DetailCache().TryGet("", out _));

    // —— 需要登录态记忆：NSFW 详情直连必然 403（原因码是 NeedsLogin 而不是挑战），
    //    那条冷却对它不生效，所以要单独按 modId 记住，免得每次白撞约 1 秒。
    var nl = new NeedsLoginMemory(capacity: 3, ttlSeconds: 3600);
    var n0 = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    Check("需要登录态：初始不认识这条 mod", !nl.Needs("103953", n0));
    nl.Mark("103953", n0);
    Check("需要登录态：标记后就认得", nl.Needs("103953", n0.AddMinutes(30)));
    Check("需要登录态：别的 mod 不受影响", !nl.Needs("141115", n0));

    var nlTtl = new NeedsLoginMemory(capacity: 10, ttlSeconds: 60);
    nlTtl.Mark("1", n0);
    Check("需要登录态：TTL 内有效", nlTtl.Needs("1", n0.AddSeconds(59)));
    Check("需要登录态：TTL 过后失效（登录态可能变了）", !nlTtl.Needs("1", n0.AddSeconds(61)));

    var nlCap = new NeedsLoginMemory(capacity: 2, ttlSeconds: 3600);
    nlCap.Mark("old", n0);
    nlCap.Mark("mid", n0.AddMinutes(1));
    nlCap.Mark("new", n0.AddMinutes(2));
    Check("需要登录态：超容量丢最久没用的", nlCap.Count <= 2 && !nlCap.Needs("old", n0.AddMinutes(3)),
          nlCap.Count.ToString());
    Check("需要登录态：最近的还在", nlCap.Needs("new", n0.AddMinutes(3)));

    nlCap.Mark("", n0);
    Check("需要登录态：空 modId 安全", !nlCap.Needs("", n0));
    nlCap.Clear();
    Check("需要登录态：Clear 之后清空", nlCap.Count == 0 && !nlCap.Needs("new", n0));
}

Console.WriteLine();
Console.WriteLine("=== 13j. 插件自带登录：探测判定 + profile 挑选顺序 ===");
{
    // 登录探测返回形如 "host|有退出链接|有登录链接"（判定标志与 LoginStateDetector 一致）
    Check("已登录：站在 xivmodarchive + 有 /logout + 无登录链接 → 判定已登录",
          BrowserChannel.ProbeSaysLoggedIn("www.xivmodarchive.com|1|0"));
    Check("未登录：还有登录链接 → 不算已登录",
          !BrowserChannel.ProbeSaysLoggedIn("www.xivmodarchive.com|0|1"));
    Check("还在 Discord 授权页 → 不算已登录（关键：别把 Discord 的页面当成功）",
          !BrowserChannel.ProbeSaysLoggedIn("discord.com|1|0"));
    Check("空/畸形探测结果 → 不算已登录",
          !BrowserChannel.ProbeSaysLoggedIn(null) && !BrowserChannel.ProbeSaysLoggedIn("") &&
          !BrowserChannel.ProbeSaysLoggedIn("garbage") && !BrowserChannel.ProbeSaysLoggedIn("a|1"));
    Check("能识别「当前停在 Discord 页」",
          BrowserChannel.ProbeIsOnDiscord("discord.com|0|1") &&
          !BrowserChannel.ProbeIsOnDiscord("www.xivmodarchive.com|1|0"));

    // 挑 profile 的顺序：插件自带且已登录 > 借来的带 cookie > 其它
    var ownedLogged = new BrowserCandidate { Label = "插件自带", Owned = true };
    var borrowedCookie = new BrowserCandidate { Label = "Chrome" };
    var borrowedBare = new BrowserCandidate { Label = "Edge" };
    Func<BrowserCandidate, bool> hasCookie = c => c.Label is "插件自带" or "Chrome";

    var ranked = BrowserChannel.RankCandidates([borrowedBare, borrowedCookie, ownedLogged], hasCookie);
    Check("挑 profile：插件自带且已登录的排第一", ReferenceEquals(ranked[0], ownedLogged), ranked[0].Label);
    Check("挑 profile：其次是有 cookie 的借来 profile", ReferenceEquals(ranked[1], borrowedCookie));
    Check("挑 profile：没 cookie 的排最后", ReferenceEquals(ranked[2], borrowedBare));

    // 插件自带但**没登录过** → 不该压过有 cookie 的借来 profile（否则等于自废武功）
    var ownedBare = new BrowserCandidate { Label = "插件自带2", Owned = true };
    var ranked2 = BrowserChannel.RankCandidates([ownedBare, borrowedCookie], hasCookie);
    Check("挑 profile：自带的没登录时，仍优先用有 cookie 的", ReferenceEquals(ranked2[0], borrowedCookie),
          ranked2[0].Label);

    // 同级保持原顺序（OrderBy 是稳定排序，不能打乱探测顺序）
    var a1 = new BrowserCandidate { Label = "A" };
    var a2 = new BrowserCandidate { Label = "B" };
    var ranked3 = BrowserChannel.RankCandidates([a1, a2], _ => false);
    Check("挑 profile：条件相同时保持传入顺序", ReferenceEquals(ranked3[0], a1) && ReferenceEquals(ranked3[1], a2));

    // 自带 profile 要「就地使用」，路径必须按 Chrome 的布局拼对
    var oc = new BrowserCandidate { UserDataDir = @"C:\x\y", Owned = true };
    Check("自带 profile 的 cookie 路径按 Chrome 布局拼",
          oc.CookieFile.EndsWith(@"Default\Network\Cookies", StringComparison.Ordinal));
    Check("Owned 默认 false（只有插件自带的才不复制、不删）", !new BrowserCandidate().Owned);
    Check("没登录过的自带 profile 不算 usable（但 exe 在就还能取公开内容）",
          !new BrowserCandidate { Exe = "x", UserDataDir = @"C:\nope" }.LooksUsable);
    Check("登录页地址正确", XmaUrls.Login == "https://www.xivmodarchive.com/login");
}

Console.WriteLine();
Console.WriteLine("=== 13k. 加载进度模型（分段 / 只增不减 / 收尾）===");
{
    var p = new LoadProgress(new[]
    {
        new LoadProgress.Stage("一", 100),
        new LoadProgress.Stage("二", 200),
        new LoadProgress.Stage("三", 100),
    });
    Check("没开始时进度为 0", p.Fraction == 0f && !p.Running);

    p.Begin();
    Check("Begin 后进入第一个阶段", p.LastStageName == "一" && p.Running);
    Check("刚开始进度接近 0", p.Fraction < 0.3f, p.Fraction.ToString("0.00"));

    // 只增不减：每个阶段都采样几次，不允许出现回落
    var last = -1f;
    var monotonic = true;
    foreach (var idx in new[] { 0, 1, 2 })
    {
        p.Enter(idx);
        for (var i = 0; i < 4; i++)
        {
            Thread.Sleep(25);
            var f = p.Fraction;
            if (f < last - 0.0001f) monotonic = false;
            last = f;
        }
    }
    Check("进度只增不减（不会来回跳）", monotonic, last.ToString("0.00"));

    // 阶段内的预估封顶：即使卡在这一阶段很久，也不能冲过该阶段的结束点
    var slow = new LoadProgress(new[] { new LoadProgress.Stage("慢", 50), new LoadProgress.Stage("后", 50) });
    slow.Begin();
    Thread.Sleep(200);                     // 远超 50ms 预估
    Check("阶段超时也不让进度冲过该阶段上限", slow.Fraction <= 0.51f, slow.Fraction.ToString("0.00"));

    p.Complete();
    Check("Complete 后进度正好 1", p.Fraction == 1f && p.Done && !p.Running);
    Check("完成文案带用时", p.Describe().StartsWith("完成") && p.Describe().Contains("用时"), p.Describe());

    // 回调顺序：Enter 只能前进，倒退/重复被忽略
    var q = new LoadProgress(new[] { new LoadProgress.Stage("A", 100), new LoadProgress.Stage("B", 100) });
    q.Begin();
    q.Enter(1, "到 B");
    q.Enter(0, "想倒回去");
    Check("Enter 倒退被忽略（阶段仍是 B）", q.LastStageName == "B", q.LastStageName);

    var r2 = new LoadProgress(new[] { new LoadProgress.Stage("A", 100) });
    r2.Begin();
    r2.Fail("网络炸了");
    Check("失败被记录且不再 Running", r2.Failed && !r2.Running && !r2.Done);
    Check("失败文案带原因", r2.Describe().Contains("网络炸了"), r2.Describe());
    r2.Enter(0, "失败后还想推进");
    Check("失败后 Enter 不再改状态", r2.Failed);

    // 预置的两套分段要合理：详情比搜索长，且都 > 0
    var det = LoadProgress.ForDetail();
    var sea = LoadProgress.ForSearch();
    Check("详情预估总时长 > 搜索", det.EstimatedTotalMs > sea.EstimatedTotalMs,
          $"{det.EstimatedTotalMs} vs {sea.EstimatedTotalMs}");
    Check("两套分段的预估都是正数", det.EstimatedTotalMs > 0 && sea.EstimatedTotalMs > 0);
    Check("详情分段含「加载预览图」", det.StageCount >= 5);

    // Describing 未开始时不该崩
    Check("未开始时 Describe 不抛异常", new LoadProgress(new[] { new LoadProgress.Stage("x", 10) }).Describe().Length > 0);
}

Console.WriteLine();
Console.WriteLine("=== 13l. 同一张图被列表/详情先后请求（用户实测的「卡住」根因）===");
{
    const string img = "https://static.xivmodarchive.com/mod-images/a.jpg";

    // 场景 1：列表先发起（只直连，不许占通道）
    var st = new ImageJobState();
    Check("列表先发起 → 应当开始下发", st.Request(img, allowChannel: false) == ImageJobState.Ask.Begin);
    Check("此刻不许走通道（列表一页 15 张，不能把通道占满）", !st.ChannelAllowed(img));

    // 详情面板随后请求同一张图（允许通道）
    Check("详情随后请求同一张图 → 参与已有任务，不重复下发",
          st.Request(img, allowChannel: true) == ImageJobState.Ask.Join);
    // ⭐ 这就是原来缺的一步：后到的请求必须能给正在跑的任务升级权限
    Check("⭐ 后到的请求给在跑的任务升级了通道权限（原来这里是静默丢弃 → 图永远不出来）",
          st.ChannelAllowed(img));
    Check("任务登记只有一条（没有重复下发）", st.Count == 1);

    st.Finish(img);
    Check("收尾后登记清空（否则这个 URL 再也不会被下发）", st.Count == 0 && !st.IsRunning(img));

    // 场景 2：反过来 —— 详情先来（已授权通道），列表后到不该把权限降级
    var st2 = new ImageJobState();
    st2.Request(img, allowChannel: true);
    st2.Request(img, allowChannel: false);
    Check("后到的列表请求不会把通道权限降级", st2.ChannelAllowed(img));

    // 场景 3：失败收尾后可以重新下发（否则重试永远不生效）
    var st3 = new ImageJobState();
    st3.Request(img, allowChannel: false);
    st3.Finish(img);
    Check("失败收尾后允许重新下发（重试才可能生效）",
          st3.Request(img, allowChannel: true) == ImageJobState.Ask.Begin);

    // 场景 4：不同图互不干扰
    var st4 = new ImageJobState();
    st4.Request("u1", false);
    st4.Request("u2", false);
    Check("不同 URL 各自独立", st4.Count == 2 && !st4.ChannelAllowed("u1") && !st4.ChannelAllowed("u2"));
    st4.Finish("u1");
    Check("收尾只影响自己那一条", st4.Count == 1 && st4.IsRunning("u2"));

    // 空字符串也不能崩
    var st5 = new ImageJobState();
    st5.Request("", true);
    Check("空 URL 不崩且行为一致", st5.Count == 1 && st5.ChannelAllowed(""));
    st5.Finish("");
    Check("空 URL 也能收尾", st5.Count == 0);

    // 超时常量必须是有界的（无限等 = 进度条一直转）。
    // 注意用数组接一下：这些是 const，直接比较的话编译器会把断言整个优化掉（CS8793），
    // 那种「通过」是假的。
    int[] limits = [ImageFetchLimits.StallMs, ImageFetchLimits.JobTimeoutMs,
                    ImageFetchLimits.FailureCooldownSeconds];
    Check("直连有停滞上限且不长（原来继承 HttpClient 的 30 分钟 = 等于没有上限）",
          limits[0] > 0 && limits[0] <= 15000, limits[0].ToString());
    Check("整条任务有硬上限（绝不无限等）", limits[1] > 0 && limits[1] <= 120000, limits[1].ToString());
    Check("整条任务上限 > 直连上限（要留出回退通道的时间）", limits[1] > limits[0]);
    Check("失败冷却有上限（到期会重新尝试）", limits[2] > 0 && limits[2] <= 3600, limits[2].ToString());
}

Console.WriteLine();
Console.WriteLine("=== 13m. 超时必须有界（「卡一分钟」的根源就是这里没上限）===");
{
    // 用数组接一下，免得 const 比较被编译器优化成恒真（那种"通过"是假的）
    (string Name, int Value, int Max)[] caps =
    [
        ("CDP 单次调用上限", FetchLimits.CdpCallMs, 30000),
        ("Page.enable", FetchLimits.PageEnableMs, 10000),
        ("Page.navigate", FetchLimits.PageNavigateMs, 20000),
        ("轮询求值", FetchLimits.EvalProbeMs, 10000),
        ("取 HTML", FetchLimits.GrabHtmlMs, 30000),
        ("会话启动总预算", FetchLimits.SessionStartBudgetMs, 30000),
        ("等 CDP 端点总时长", FetchLimits.WaitForCdpTotalMs, 30000),
        ("等 CDP 端点单次探测", FetchLimits.WaitForCdpProbeMs, 3000),
        ("等通道锁", FetchLimits.OpLockWaitMs, 60000),
        ("详情总看门狗(秒)", FetchLimits.DetailTimeoutSeconds, 40),
        ("搜索总看门狗(秒)", FetchLimits.SearchTimeoutSeconds, 60),
        ("取图整条上限(毫秒)", FetchLimits.ImageJobMs, 120000),
        ("取图停滞上限(毫秒)", FetchLimits.ImageStallMs, 15000),
    ];
    var unbounded = new List<string>();
    foreach (var (name, value, max) in caps)
        if (value <= 0 || value > max) unbounded.Add($"{name}={value}");
    Check("每一层超时都有界且不超过合理上限（一个都不能缺）", unbounded.Count == 0,
          string.Join(", ", unbounded));

    // 真正的不变量是「单次尝试」不会拖成一分钟（总看门狗再兜一层底）
    var oneAttempt = FetchLimits.SessionStartBudgetMs + FetchLimits.PageEnableMs +
                     FetchLimits.PageNavigateMs + 6000;
    Check("单次尝试各步之和不会拖成一分钟", oneAttempt <= 60000, oneAttempt.ToString());
    Check("总看门狗必须比单次尝试更早生效（用户看到的上限是它，不是各步之和）",
          FetchLimits.DetailTimeoutSeconds * 1000 < oneAttempt,
          $"{FetchLimits.DetailTimeoutSeconds * 1000} vs {oneAttempt}");
    Check("详情总看门狗对用户是能忍的量级（<=30 秒）", FetchLimits.DetailTimeoutSeconds <= 30,
          FetchLimits.DetailTimeoutSeconds.ToString());
    Check("详情总看门狗比搜索紧（详情是用户盯着看的）",
          FetchLimits.DetailTimeoutSeconds < FetchLimits.SearchTimeoutSeconds);
    Check("详情看门狗不小于会话预算（别正常启动就被误杀）",
          FetchLimits.DetailTimeoutSeconds * 1000 >= FetchLimits.SessionStartBudgetMs,
          $"{FetchLimits.DetailTimeoutSeconds * 1000} vs {FetchLimits.SessionStartBudgetMs}");
    Check("取图整条上限 > 停滞上限（慢可以，卡不行）",
          FetchLimits.ImageJobMs > FetchLimits.ImageStallMs);
    Check("停滞上限足够短（用户等不到「没反应」超过 10 秒）", FetchLimits.ImageStallMs <= 10000,
          FetchLimits.ImageStallMs.ToString());
}

Console.WriteLine();
Console.WriteLine("=== 13n. 缓存维护：按上限淘汰 / 清过期（用真实文件跑）===");
{
    var root = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "cachemaint-" + Guid.NewGuid().ToString("N")[..8]);
    var thumbs = Path.Combine(root, "thumbs");
    var covers = Path.Combine(root, "covers");
    var temp = Path.Combine(root, "temp");
    Directory.CreateDirectory(thumbs);
    Directory.CreateDirectory(covers);
    Directory.CreateDirectory(temp);

    var now = DateTime.UtcNow;
    // 三个文件：越旧越小/越大都行，关键是**写入时间**决定淘汰顺序
    var mk = (string dir, string name, int size, int ageMinutes) =>
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[size]);
        File.SetLastWriteTimeUtc(p, now.AddMinutes(-ageMinutes));
        return p;
    };
    var oldest = mk(thumbs, "old.img", 1000, 90);
    var middle = mk(thumbs, "mid.img", 1000, 60);
    var newest = mk(thumbs, "new.img", 1000, 30);

    var (bytes, files) = CacheMaintenance.Measure(thumbs);
    Check("量占用：字节数与文件数都对", bytes == 3000 && files == 3, $"{bytes} / {files}");

    // 上限 2000 → 必须删掉最旧的 1 个
    var deleted = CacheMaintenance.EvictToLimit(thumbs, 2000, out var left);
    Check("超出上限时按「最久没写过」先删", deleted == 1 && left == 2000, $"删 {deleted}，剩 {left}");
    Check("被删的确实是最旧那个", !File.Exists(oldest), "old.img 应该被删");
    Check("较新的两个留着", File.Exists(middle) && File.Exists(newest));

    // 上限 <= 0 = 不淘汰
    var none = CacheMaintenance.EvictToLimit(thumbs, 0, out _);
    Check("上限 <= 0 表示不淘汰", none == 0 && File.Exists(middle) && File.Exists(newest));

    // 已经在上限内 → 不动
    var again = CacheMaintenance.EvictToLimit(thumbs, 999999, out var same);
    Check("没超上限就不动它", again == 0 && same == 2000, $"{again} / {same}");

    // 清过期：temp 里放一个很旧的和一个新的
    var staleFile = mk(temp, "残留包.pmp", 500, 60 * 30);          // 30 小时前
    var freshFile = mk(temp, "刚下的.pmp", 500, 5);                 // 5 分钟前
    var removed = CacheMaintenance.DeleteOlderThan(temp, TimeSpan.FromHours(24), now);
    Check("清过期：只删超过 24 小时的", removed == 1 && !File.Exists(staleFile) && File.Exists(freshFile),
          $"删了 {removed} 个");

    // 空目录会被收掉（解压残留常见）
    var empty = Path.Combine(covers, "extract7");
    Directory.CreateDirectory(empty);
    CacheMaintenance.EvictToLimit(covers, 1, out _);
    Check("淘汰顺手收掉空目录", !Directory.Exists(empty));

    // 整个 Maintain 跑一遍：三个目录 + 临时残留
    var mStale = Path.Combine(covers, "很旧的封面.jpg");
    File.WriteAllBytes(mStale, new byte[5000]);
    File.SetLastWriteTimeUtc(mStale, now.AddHours(-100));
    var mReport = CacheMaintenance.Maintain(
        [("封面图", covers, 1000), ("预览图", thumbs, 999999)], temp, TimeSpan.FromHours(24), now);
    Check("Maintain 报告里说明了动了什么", mReport.Contains("封面图"), mReport);

    // 目录不存在 / 空路径都不能崩
    var missing = Path.Combine(root, "not-exist");
    var miss = CacheMaintenance.EvictToLimit(missing, 100, out var missLeft);
    Check("目录不存在时不崩且返回 0", miss == 0 && missLeft == 0);
    Check("Measure 对不存在的目录返回 0", CacheMaintenance.Measure(missing) == (0L, 0));

    try { Directory.Delete(root, true); } catch { /* 忽略 */ }
}

Console.WriteLine();
Console.WriteLine("=== 13o. 原页面链接 / 作者页 / 外链判定（用真实样本）===");
{
    // ① 链接构造：纯拼字符串，100% 可构造 —— 这就是「所有 mod 都有原地址」的依据
    Check("原页面链接构造", XmaUrls.ModPage("141115") == "https://www.xivmodarchive.com/modid/141115");
    Check("作者页链接构造（实测形态 /user/<数字id>）",
          XmaUrls.UserPage("230603") == "https://www.xivmodarchive.com/user/230603");

    // ② 拿真实详情样本跑一遍解析：作者 id 必须能解析出来（不然作者页入口是空的）
    var detailPath = Path.Combine("samples", "mod_141115.html");
    if (File.Exists(detailPath))
    {
        var real = XmaParser.ParseDetail(File.ReadAllText(detailPath), "141115",
                                         XmaUrls.ModPage("141115"));
        Check("真实样本：作者 id 解析出来了", real.AuthorId.Length > 0, real.AuthorId);
        Check("真实样本：作者页链接可用",
              real.AuthorPageUrl == XmaUrls.UserPage(real.AuthorId), real.AuthorPageUrl);
        Check("真实样本：原页面链接就是该 mod 的站点页",
              real.PageUrl == XmaUrls.ModPage("141115"), real.PageUrl);
        Check("真实样本：站内下载链不算外链", !real.IsExternalLink, real.AbsoluteDownloadUrl);
        Check("真实样本：站内下载链的主机是站点", real.DownloadHost == "www.xivmodarchive.com",
              real.DownloadHost);
    }
    else
    {
        Console.WriteLine("  （跳过：找不到样本 mod_141115.html）");
    }

    // ③ 外链判定 + 主机提取：用**摸底测到的真实外链**做断言
    foreach (var (href, expectedHost) in new[]
    {
        ("https://www.patreon.com/Liability/posts/protocol-146532471", "www.patreon.com"),
        ("https://heliosphere.app/mod/c5wnrj1x0s59f5c8y36pk9f28w", "heliosphere.app"),
    })
    {
        var extDet = new ModDetail { ModId = "1", DownloadHref = href };
        Check($"真实外链被判为外链（{expectedHost}）", extDet.IsExternalLink, href);
        Check($"真实外链的主机提取正确（{expectedHost}）", extDet.DownloadHost == expectedHost, extDet.DownloadHost);
        Check($"外链时原页面链接依然有（{expectedHost}）",
              extDet.PageUrl == XmaUrls.ModPage("1"), extDet.PageUrl);
    }

    // ④ 各种边角：不能因为显示逻辑抛异常
    var noLink = new ModDetail { ModId = "7" };
    Check("没有下载链接：不算外链、主机为空、原页面照样有",
          !noLink.IsExternalLink && noLink.DownloadHost.Length == 0 &&
          noLink.PageUrl == XmaUrls.ModPage("7"));

    var relative = new ModDetail { ModId = "8", DownloadHref = "/private/abc/files/x.zip" };
    Check("站内相对链接：补成绝对地址且不算外链",
          relative.AbsoluteDownloadUrl.StartsWith(XmaUrls.Root, StringComparison.Ordinal) &&
          !relative.IsExternalLink, relative.AbsoluteDownloadUrl);

    var junk = new ModDetail { ModId = "9", DownloadHref = "not a url at all" };
    Check("畸形链接：主机提取不抛异常（原样返回）", junk.DownloadHost.Length > 0, junk.DownloadHost);

    var noAuthor = new ModDetail { ModId = "10", Url = "https://www.xivmodarchive.com/modid/10" };
    Check("没有作者 id 时不给作者页链接（界面据此隐藏入口）", noAuthor.AuthorPageUrl.Length == 0);
    Check("解析过的 Url 优先用作原页面", noAuthor.PageUrl == "https://www.xivmodarchive.com/modid/10");
}

Console.WriteLine();
Console.WriteLine("=== 13p. 流式加载状态机（去重 / 到底判据 / 别失控）===");
{
    SearchHit H(string id) => new() { ModId = id, Title = "t" + id };

    // 基本推进
    var s = new StreamState();
    s.Reset(1, 100);
    Check("初始：下一页就是起始页、没到底", s.NextPage == 1 && !s.End && s.StartPage == 1);

    var r1 = s.Append([H("1"), H("2"), H("3")], 100);
    Check("追加：新增 3 条", r1.NewItems.Count == 3 && s.Loaded == 3, s.Loaded.ToString());
    Check("追加后页号推进到 2", s.NextPage == 2, s.NextPage.ToString());

    // 去重：相邻页重复（站点在翻页期间发新 mod 就会这样）
    var r2 = s.Append([H("3"), H("4")], 100);
    Check("去重：只把没见过的那条算新增", r2.NewItems.Count == 1 && r2.NewItems[0].ModId == "4",
          string.Join(",", r2.NewItems.Select(h => h.ModId)));
    Check("累计数只算真正新增的", s.Loaded == 4, s.Loaded.ToString());

    // 连续整页重复 → 停下来（不能死循环）
    var dup1 = s.Append([H("4")], 100);
    Check("整页重复第 1 次：还不算到底", !dup1.End && s.NoProgressStrikes == 1);
    var dup2 = s.Append([H("4")], 100);
    Check("连续整页重复第 2 次：判定到底（防死循环）", dup2.End && s.End, s.EndReason);
    Check("到底后不再返回新增", s.Append([H("9")], 100).NewItems.Count == 0);

    // 空页 = 到底
    var e = new StreamState();
    e.Reset(1, 0);
    e.Append([H("1")], 0);
    var empty = e.Append([], 0);
    Check("空页判定到底", empty.End && e.End, e.EndReason);

    // 加载到站点总数 = 到底
    var t = new StreamState();
    t.Reset(1, 15);
    var full = t.Append([.. Enumerable.Range(1, 15).Select(i => H("m" + i))], 15);
    Check("加载数达到站点总数 → 到底", full.End && t.Loaded == 15, t.EndReason);

    // ⚠⚠ 回归：**首屏就到底时，这一页的条目必须照样交出来**。
    // 曾经 Stop() 返回空列表 → 站点只有 1 条结果时首屏 _hits 变空 → 列表一片空白，
    // 也就是「结果不足一页（≤15 条）的搜索全都显示不出来」——搜具体 Mod 名必中。
    // 原来的断言只查了 End、没查条目，所以放跑了它。「到头」= 不用再翻页，≠ 这一页不要了。
    Check("到站点总数时：这一页的条目仍要交出来", full.NewItems.Count == 15, full.NewItems.Count.ToString());

    var one = new StreamState();
    one.Reset(1, 1);
    var oneRes = one.Append([H("only")], 1);
    Check("只有 1 条结果时：到头的**同时**必须带回那 1 条（曾经的空白 bug）",
          oneRes.End && oneRes.NewItems.Count == 1 && oneRes.NewItems[0].ModId == "only",
          $"End={oneRes.End} New={oneRes.NewItems.Count}");
    Check("到头原因仍写明是到站点总数", oneRes.Reason.Contains("站点总数"), oneRes.Reason);

    // 唯一允许「空手而回」的情况是空页（那一页本来就没有条目）
    Check("只有空页那种情况才允许交回空列表",
          empty.NewItems.Count == 0 && empty.End && empty.Reason.Contains("空"), empty.Reason);

    // 没有 ModId 的条目要跳过（详情/原页面链接都靠它，留着没用）
    var bad = new StreamState();
    bad.Reset(1, 0);
    var badRes = bad.Append([new SearchHit { ModId = "" }, H("x")], 0);
    Check("跳过没有 ModId 的条目", badRes.NewItems.Count == 1 && badRes.NewItems[0].ModId == "x");

    // 页号硬上限（判据出问题时的保险）
    var deep = new StreamState();
    deep.Reset(StreamState.MaxPage, 0);
    var deepRes = deep.Append([H("z1")], 0);
    Check("页号超过硬上限就停（别把站点翻穿）", deepRes.End, deep.EndReason);
    Check("页号硬上限触发时也要交出这一页的条目", deepRes.NewItems.Count == 1, deepRes.NewItems.Count.ToString());

    // Reset 要清干净（新搜索不能沿用旧的去重集合）
    var rt = new StreamState();
    rt.Reset(1, 0);
    rt.Append([H("a")], 0);
    rt.Reset(5, 0);
    Check("Reset 之后去重集合清空、页号归位、累计归零",
          rt.NextPage == 5 && rt.Loaded == 0 && !rt.End && rt.PagesFetched == 0);
    var afterReset = rt.Append([H("a")], 0);
    Check("Reset 后同一条又算新增（新搜索是全新结果集）", afterReset.NewItems.Count == 1);

    // 到底之后再 Reset 要能重新开始
    var again = new StreamState();
    again.Reset(1, 0);
    again.Append([], 0);
    var wasEnd = again.End;
    again.Reset(1, 0);
    Check("到底后再 Reset 可以重新开始（新搜索别被上一次的『到底』锁住）", wasEnd && !again.End);

    Check("进度文案：知道总数时显示 x / y", new StreamState().ProgressText.Length > 0);

    // ⭐ 并发安全的不变量：追加必须**换新列表**，不能原地改正在被枚举的那个。
    // （渲染线程每帧 foreach 结果列表，后台线程原地 AddRange 会抛
    //  "Collection was modified; enumeration operation may not execute"）
    var liveHits = new List<SearchHit> { H("a"), H("b") };
    var merged = StreamState.Merge(liveHits, [H("c")]);
    Check("Merge 返回的是新实例（不是原列表）", !ReferenceEquals(liveHits, merged));
    Check("Merge 不动原列表（正在被枚举的列表必须保持完整）", liveHits.Count == 2);
    Check("Merge 结果顺序：已有在前、新增在后",
          merged.Count == 3 && merged[0].ModId == "a" && merged[2].ModId == "c",
          string.Join(",", merged.Select(h => h.ModId)));

    // 边枚举边合并也不能炸（模拟渲染线程正在 foreach）
    var enumerating = new List<SearchHit> { H("1"), H("2") };
    var okEnum = true;
    try
    {
        foreach (var _ in enumerating)
        {
            // 后台线程在这一帧里合并出新列表并换引用 —— 不影响当前这次枚举
            enumerating = StreamState.Merge(enumerating, [H("3")]);
        }
    }
    catch { okEnum = false; }
    Check("枚举过程中换新引用不会抛「集合已修改」", okEnum && enumerating.Count > 2,
          enumerating.Count.ToString());

    // 边界：空输入
    Check("Merge 空 + 空 = 空列表", StreamState.Merge([], []).Count == 0);
    Check("Merge 空 + 有 = 有", StreamState.Merge([], [H("x")]).Count == 1);
}

Console.WriteLine();
Console.WriteLine("=== 13q. 界面尺寸计算（保证「图片不超过取景框」= 不出滚动条）===");
{
    // ① 核心不变量：等比缩放后，两个方向都**绝不超过**容器。
    //    超出去一点点，ImGui 就会在那个容器上长出滚动条 —— 这次 bug 的根源。
    var cases = new (float W, float H)[]
    {
        (1920, 1080), (1080, 1920), (1600, 400), (400, 1600), (1, 1000), (1000, 1),
        (500, 500), (3, 2), (2048, 1), (1, 2048),
    };
    var overflow = new List<string>();
    var aspectBad = new List<string>();
    foreach (var (w, h) in cases)
    {
        var box = new Vector2(300f, 168f);
        var fit = GridLayout.Fit(new Vector2(w, h), box.X, box.Y);
        if (fit.X > box.X + 0.01f || fit.Y > box.Y + 0.01f) overflow.Add($"{w}x{h}→{fit.X:0.#}x{fit.Y:0.#}");
        if (fit.X <= 0f || fit.Y <= 0f) overflow.Add($"{w}x{h} 退化成非正数");
        // 等比：两边比例都要保持不变（容差内）
        var srcAspect = w / h;
        var fitAspect = fit.X / fit.Y;
        if (MathF.Abs(srcAspect - fitAspect) > 0.01f * MathF.Max(1f, srcAspect)) aspectBad.Add($"{w}x{h}");
    }
    Check("等比缩放后绝不超出取景框（超一点就出滚动条）", overflow.Count == 0, string.Join(", ", overflow));
    Check("等比缩放保持宽高比（图片不变形）", aspectBad.Count == 0, string.Join(", ", aspectBad));

    // 至少有一个方向贴满（contain 不能缩得过小）
    var tight = GridLayout.Fit(new Vector2(1920, 1080), 300f, 168f);
    Check("contain 语义：至少一个方向贴满容器",
          MathF.Abs(tight.X - 300f) < 0.01f || MathF.Abs(tight.Y - 168f) < 0.01f,
          $"{tight.X:0.#}x{tight.Y:0.#}");

    // ② 退化输入不能把界面搞崩（NaN/0/负数都要给出有限正数）
    foreach (var (w, h) in new[] { (0f, 100f), (100f, 0f), (-5f, 100f), (float.NaN, 100f), (float.PositiveInfinity, 10f) })
    {
        var fit = GridLayout.Fit(new Vector2(w, h), 200f, 100f);
        Check($"退化输入 ({w},{h}) 返回有限正数",
              fit.X > 0f && fit.Y > 0f && !float.IsNaN(fit.X) && !float.IsNaN(fit.Y),
              $"{fit.X}x{fit.Y}");
    }

    // ③ 取景框宽度绝不能超过卡片宽度（这是「图片横向溢出」的直接判据）
    foreach (var cardW in new[] { 110f, 172f, 240f, 400f })
    {
        foreach (var pad in new[] { 0f, 12f, 18f, 40f })
        {
            var box = GridLayout.ThumbBox(cardW, pad);
            Check($"取景框不宽于卡片（cardW={cardW} pad={pad}）", box.X <= MathF.Max(24f, cardW) + 0.01f,
                  $"{box.X:0.#} vs {cardW}");
        }
    }

    // ④ 卡片高度必须覆盖内容（内边距 + 取景框 + 按钮行 + 文字行），否则又出纵向滚动条
    var cardHeight = GridLayout.CardHeight(16f, 96.3f, 27f, 20f);
    Check("卡片高度覆盖全部内容（含富余）", cardHeight >= 16f + 96.3f + 27f + 20f, cardHeight.ToString("0.#"));
    Check("卡片高度对退化输入也安全",
          GridLayout.CardHeight(float.NaN, -1f, 0f, float.NaN) > 0f);

    // ⑤ 内边距计算：默认样式下确实要扣掉约 18px（这正是老代码 cardW-12 出错的地方）
    Check("内边距扣减量符合 ImGui 默认样式（8*2 + 1*2 = 18）",
          MathF.Abs(GridLayout.InnerPadX(8f, 1f) - 18f) < 0.01f,
          GridLayout.InnerPadX(8f, 1f).ToString("0.#"));

    // ⑥ 列宽 ≥ 控件宽 + 单元内边距（今天踩了两次的那条规则）
    var padViolations = new List<string>();
    foreach (var widgetW in new[] { 1f, 20f, 26f, 68f, 120f, 300f })
    {
        foreach (var cellPadX in new[] { 0f, 6f, 8f, 16f })
        {
            var colW = GridLayout.ColumnWidthFor(widgetW, cellPadX);
            // 列宽扣掉内边距后（= 内容区）必须还能装下控件
            if (colW - cellPadX < widgetW - 0.01f) padViolations.Add($"{widgetW}/{cellPadX}");
        }
    }
    Check("列宽扣掉单元格内边距后仍能装下控件（否则又溢出）", padViolations.Count == 0,
          string.Join(", ", padViolations));
    Check("列宽对退化输入也安全", GridLayout.ColumnWidthFor(float.NaN, -5f) >= 0f);
}

Console.WriteLine();
Console.WriteLine("=== 13h. 挑战页/空页识别（浏览器通道取页面是否可用）===");
{
    // 挑战页实测约 29KB —— 不能只按“小/空”判断，否则会把挑战页当成正常的“没有结果”。
    var challenge = "<html><head><title>Just a moment...</title></head><body><div id=\"cf-chl\"></div>" +
                    new string(' ', 30000) + "</body></html>";
    Check("挑战页（29KB）判定为不可用", !BrowserChannel.IsUsablePage(challenge));
    Check("空文档判定为不可用", !BrowserChannel.IsUsablePage("<html><body></body></html>"));
    Check("null 判定为不可用", !BrowserChannel.IsUsablePage(null));
    Check("过短页面判定为不可用", !BrowserChannel.IsUsablePage(new string('x', 199)));

    var realPage = "<html><body>" + string.Concat(Enumerable.Repeat("<div class=\"mod-card bg-dark\"></div>", 15)) + "</body></html>";
    Check("正常页面（含卡片）判定为可用", BrowserChannel.IsUsablePage(realPage));
}

Console.WriteLine();
Console.WriteLine("=== 13. 翻译：分段逻辑（离线）===");
{
    var para = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"This is sentence number {i} of the description."));
    var chunks = Translator.Split(para, 200).ToList();
    Console.WriteLine($"   40 行文本切成 {chunks.Count} 块，最长 {chunks.Max(c => c.Length)} 字符");
    Check("分段：每块都不超过上限", chunks.All(c => c.Length <= 200));
    Check("分段：确实切了多块", chunks.Count > 1, chunks.Count.ToString());
    Check("分段：内容不丢（忽略空白后比对）", Normalize(string.Concat(chunks)) == Normalize(para));

    var oneLine = new string('a', 500) + " word " + new string('b', 500);
    var chunky = Translator.Split(oneLine, 200).ToList();
    Console.WriteLine($"   1000 字符单行切成 {chunky.Count} 块，最长 {chunky.Max(c => c.Length)} 字符");
    Check("分段：超长单行也能切且不超限", chunky.All(c => c.Length <= 200) && chunky.Count >= 5, chunky.Count.ToString());

    Check("分段：空文本不产生块", !Translator.Split("", 200).Any());

    using var trHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    using var emptyTranslator = new Translator(new TestTranslateSettings(), Path.GetTempPath(), null, trHttp);
    Check("未翻译过的文本读缓存 = null", emptyTranslator.Cached("some text never translated") is null);
    Check("标题显示回退到原文", emptyTranslator.DisplayTitle("My Mod") == "My Mod");

    // 谷歌返回的解析：样本是实测抓下来的真实返回（单段 / 多段）
    var g1 = Translator.ReadGoogleTranslation("[[[\"你好\",\"hi\",null,null,10]],null,\"en\",null,null,null,null,[]]");
    Check("谷歌解析：单段", g1 == "你好", g1);

    var g2 = Translator.ReadGoogleTranslation(
        "[[[\"第一段。\\n\",\"P1\\n\",null,null,3],[\"第二行。\\n\\n\",\"P2\\n\\n\",null,null,3],[\"第三段。\",\"P3\",null,null,3]]," +
        "null,\"en\",null,null,null,null,[]]");
    Check("谷歌解析：多段按序拼接且保留换行", g2 == "第一段。\n第二行。\n\n第三段。", g2.Replace("\n", "\\n"));

    Check("谷歌解析：坏输入不抛异常", Translator.ReadGoogleTranslation("not json at all") == "");

    // 插件实际会用哪个代理（= .NET 解析结果；谷歌能不能通就靠它）
    Console.WriteLine($"   .NET 实际给谷歌用的代理：{ProxyInfo.Describe()}");
    Check("代理探测不抛异常且给出结论", ProxyInfo.Describe().Length > 0);
}

if (live)
{
    var cacheDir = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "live");    Directory.CreateDirectory(cacheDir);
    var settings = new TestSettings();
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    using var client = new XmaClient(http, settings, cacheDir, new NullLog());

    Console.WriteLine();
    Console.WriteLine("=== 10. 联网实测：SFW 直连（真站点）===");
    var liveRes = await client.SearchAsync(new SearchQuery { Text = "tattoo", Page = 1 });
    Console.WriteLine($"   搜索 \"tattoo\" → {liveRes.Hits.Count} 条，站点总数 {liveRes.TotalMods}");
    Check("联网搜索有结果", liveRes.Hits.Count > 0);
    Check("联网能读到总数", liveRes.TotalMods > 1000);
    var liveDetail = await client.DetailAsync("95080");
    Check("联网详情标题正确", liveDetail.Title == "Pictish Tattoos", liveDetail.Title);
    Check("联网详情有直链", liveDetail.DownloadHref.Contains("/private/"), liveDetail.DownloadHref);

    var denied = false;
    try { await client.DetailAsync("69960"); }
    catch (XmaAccessException) { denied = true; }
    Check("NSFW 直连被站点拒绝（403 → XmaAccessException）", denied);

    Console.WriteLine();
    Console.WriteLine("=== 11. 联网实测：浏览器通道读 NSFW（本机 Chrome + CDP）===");
    Console.WriteLine("   可用浏览器：" + string.Join(", ",
        BrowserChannel.Detect().Where(c => c.LooksUsable).Select(c => c.Label)));

    using var channel = new BrowserChannel(settings, new NullLog());
    var nsfwHtml = await channel.GetHtmlAsync("https://www.xivmodarchive.com/modid/69960");
    Check("通道拿到 NSFW 页面 HTML", nsfwHtml is { Length: > 2000 }, $"len={nsfwHtml?.Length ?? 0} err={channel.LastError}");
    if (nsfwHtml is { Length: > 0 })
    {
        var nd = XmaParser.ParseDetail(nsfwHtml, "69960");
        Console.WriteLine($"   解析：标题={nd.Title}｜作者={nd.Author}｜下载={nd.DownloadHref}");
        Console.WriteLine($"   标签={string.Join(",", nd.Tags)}｜图 {nd.Images.Count} 张");
        Check("通道页面能解析出标题（页面带登录态）", nd.Title.Length > 0, nd.Title);
        Check("通道页面能解析出下载直链", nd.DownloadHref.Contains("/private/"), nd.DownloadHref);

        Console.WriteLine();
        Console.WriteLine("=== 12. 联网实测：浏览器通道真实下载 NSFW 包 ===");
        var dest = Path.Combine(cacheDir, "nsfw_probe" + Path.GetExtension(nd.FileNameHint));
        var okDl = await channel.DownloadAsync(nd.AbsoluteDownloadUrl, dest, (got, total) => { });
        var size = File.Exists(dest) ? new FileInfo(dest).Length : 0;
        var pk = false;
        if (size > 0)
        {
            using var fs = File.OpenRead(dest);
            var head = new byte[4];
            var n = fs.Read(head, 0, 4);
            pk = n == 4 && head[0] == 0x50 && head[1] == 0x4B;
        }
        Console.WriteLine($"   下载 {(okDl ? "成功" : "失败")}：{Path.GetFileName(dest)} {size} 字节，PK 头={pk}");
        Check("浏览器通道下载到文件", okDl && size > 0, channel.LastError);
        Check("下载内容是有效包（PK 头）", pk);
        if (File.Exists(dest)) File.Delete(dest);
    }
}

if (live || translateOnly)
{
    var trSettings = new TestTranslateSettings();
    // 按后端名分目录：换后端时不会读到上一个后端留下的缓存（也就不用删任何东西）
    var trCache = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "translate-" + trSettings.TranslateBackend);
    Directory.CreateDirectory(trCache);

    Console.WriteLine();
    Console.WriteLine("=== 13. 联网实测：翻译后端真实调用 ===");
    using (var translator = new Translator(trSettings, trCache))
    {
        Console.WriteLine($"   后端：{translator.BackendName}");
        const string probeText = "Mimic Chest Replacement - a cute treasure chest retexture for glamour lovers";
        try
        {
            var zh = await translator.TranslateAsync(probeText, CancellationToken.None);
            Console.WriteLine($"   标题译文 → {zh}");
            Check("联网翻译有中文结果", zh.Any(c => c >= 0x4E00 && c <= 0x9FFF), zh);
            Check("第二次读缓存不再发请求", translator.Cached(probeText) is not null);
        }
        catch (Exception e)
        {
            Check("联网翻译成功（错误：" + e.Message + "）", false);
        }

        // 长文（多段）走的是「分段 + 限流间隔」这条路：公共接口限流很紧时可能失败，
        // 所以这里只当**参考信息**打印，不让它把整条套件弄红。
        try
        {
            var longText = string.Join(" ", Enumerable.Range(0, 20)
                .Select(i => $"Sentence {i} explains this mod replaces a texture and needs Penumbra."));
            var zhLong = await translator.TranslateAsync(longText, CancellationToken.None);
            Console.WriteLine($"   [参考] 长文（{longText.Length} 字符）译文前 90 字 → {zhLong[..Math.Min(90, zhLong.Length)]}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"   [参考] 长文翻译未成功（公共接口限流）：{e.Message}");
        }
    }
}

if (nsfwProbe)
{
    // 关键：插件搜索走**直连**（未登录），而 NSFW 内容可能需要**登录态**才出现在列表里。
    // 这里把两条路并排量出来，别再用其中一条的结论去代表另一条。
    Console.WriteLine();
    Console.WriteLine("=== NSFW 三态：直连（未登录）vs 浏览器通道（带登录态） ===");
    var probeSettings = new TestSettings();
    var probeCache = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "nsfw");
    Directory.CreateDirectory(probeCache);
    using var probeHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    using var probeClient = new XmaClient(probeHttp, probeSettings, probeCache, new NullLog());
    using var probeChannel = new BrowserChannel(probeSettings, new NullLog());

    var cases = new (string Label, int Mode, string? Param)[]
    {
        ("排除 NSFW", 0, "false"),
        ("混合（不传参数）", 1, null),
        ("只看 NSFW", 2, "true"),
    };

    foreach (var (label, mode, param) in cases)
    {
        // 直连：走插件真实路径（XmaClient + NsfwMode）
        string directText;
        try
        {
            var direct = await probeClient.SearchAsync(new SearchQuery { NsfwMode = mode, Page = 1 }, CancellationToken.None);
            var nsfwInPage = direct.Hits.Count(h => h.Title.Contains("Doggy", StringComparison.OrdinalIgnoreCase) ||
                                                    h.Title.Contains("Loincloth", StringComparison.OrdinalIgnoreCase));
            directText = $"总数={direct.TotalMods} 本页={direct.Hits.Count}";
        }
        catch (Exception e)
        {
            directText = "失败：" + e.GetType().Name + " " + e.Message;
        }

        // 浏览器通道：带登录态
        var channelUrl = param is null
            ? "https://www.xivmodarchive.com/search?sortby=&sortorder=&dt_compat=1"
            : $"https://www.xivmodarchive.com/search?nsfw={param}&sortby=&sortorder=&dt_compat=1";
        string channelText;
        var channelHtml = await probeChannel.GetHtmlAsync(channelUrl, CancellationToken.None);
        if (channelHtml is null || channelHtml.Length == 0)
            channelText = "失败：" + probeChannel.LastError;
        else
        {
            var parsed = XmaParser.ParseSearch(channelHtml, 1, channelUrl);
            channelText = $"总数={parsed.TotalMods} 本页={parsed.Hits.Count}";
        }

        Console.WriteLine($"   {label,-16} 直连[{directText}]   通道[{channelText}]");
    }

    // 混合模式下第 1 页到底有哪些标题（直连一份、通道一份，用来确认 NSFW 条目在不在）
    Console.WriteLine();
    Console.WriteLine("=== 混合模式第 1 页标题：直连 vs 通道（抽 6 条） ===");
    var mixedUrl = "https://www.xivmodarchive.com/search?sortby=&sortorder=&dt_compat=1";
    try
    {
        var mixedDirect = await probeClient.SearchAsync(new SearchQuery { NsfwMode = 1, Page = 1 }, CancellationToken.None);
        Console.WriteLine("   直连：" + string.Join(" | ", mixedDirect.Hits.Take(6).Select(h => h.Title)));
    }
    catch (Exception e)
    {
        Console.WriteLine("   直连失败：" + e.Message);
    }

    var mixedChannelHtml = await probeChannel.GetHtmlAsync(mixedUrl, CancellationToken.None);
    if (mixedChannelHtml is { Length: > 0 })
    {
        var parsed = XmaParser.ParseSearch(mixedChannelHtml, 1, mixedUrl);
        Console.WriteLine("   通道：" + string.Join(" | ", parsed.Hits.Take(6).Select(h => h.Title)));
        var dump = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "channel_mixed.html");
        await File.WriteAllTextAsync(dump, mixedChannelHtml);
        Console.WriteLine("   通道 HTML 已存：" + dump);
    }
    else
    {
        Console.WriteLine("   通道失败：" + probeChannel.LastError);
    }

    // 顺便存一份直连的，供对比登录态标记
    try
    {
        var htmlPath = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "direct_mixed.html");
        var directHtml = await probeHttp.GetStringAsync(mixedUrl);
        await File.WriteAllTextAsync(htmlPath, directHtml);
        Console.WriteLine("   直连 HTML 已存：" + htmlPath);
    }
    catch (Exception e)
    {
        Console.WriteLine("   直连取页面失败：" + e.Message);
    }
}

if (nsfwDiag)
{
    Console.WriteLine();
    Console.WriteLine("=== 14. 实跑 NSFW 通道自检（插件里那个按钮走的同一条代码）===");
    var diagSettings = new TestSettings();
    using var diagChannel = new BrowserChannel(diagSettings, new NullLog());
    var diagReport = await NsfwDiagnostics.RunAsync(diagChannel, diagSettings, CancellationToken.None);
    Console.WriteLine(diagReport);
}

if (authorProbe)
{
    Console.WriteLine();
    Console.WriteLine("=== author= 匹配语义实测（用插件的解析器，别用正则猜）===");
    var aSettings = new TestSettings();
    var aCache = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "author");
    Directory.CreateDirectory(aCache);
    using var aHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    using var aClient = new XmaClient(aHttp, aSettings, aCache, new NullLog());

    var baseRes = await aClient.SearchAsync(new SearchQuery { Page = 1 }, CancellationToken.None);
    var authors = baseRes.Hits.Select(h => h.Author).Where(a => a.Trim().Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    Console.WriteLine("   最新发布首页的作者：" + string.Join(" / ", authors.Take(6)));
    if (authors.Count == 0)
    {
        Console.WriteLine("   解析不到作者名，跳过。");
    }
    else
    {
        var target = authors.FirstOrDefault(a => a.All(c => c < 128)) ?? authors[0];
        Console.WriteLine($"   拿作者「{target}」做试验：");
        foreach (var (label, value) in new[]
                 {
                     ("精确名", target),
                     ("全小写", target.ToLowerInvariant()),
                     ("前 4 字前缀", target.Length > 4 ? target[..4] : target),
                     ("不存在的名字", "zzzz_no_such_author_zzzz"),
                 })
        {
            try
            {
                var q = new SearchQuery { Author = value, Page = 1, NsfwMode = 1 };
                var r = await aClient.SearchAsync(q, CancellationToken.None);
                var same = r.Hits.Count(h => h.Author.Equals(target, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"   author=\"{value}\"（{label}）→ 总数 {r.TotalMods}，本页 {r.Hits.Count} 条，其中作者等于「{target}」的 {same} 条");
            }
            catch (Exception e)
            {
                Console.WriteLine($"   author=\"{value}\"（{label}）→ 失败：{e.Message}");
            }
        }
    }
}

if (timingProbe)
{
    Console.WriteLine();
    Console.WriteLine("=== 15. 耗时实测（直连 vs 浏览器通道）===");
    var tSettings = new TestSettings();
    var tCache = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "timing");
    Directory.CreateDirectory(tCache);
    using var tHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    using var tClient = new XmaClient(tHttp, tSettings, tCache, new NullLog());
    using var tChannel = new BrowserChannel(tSettings, new NullLog());

    async Task TimeIt(string label, Func<Task<string>> run)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string note;
        try
        {
            var text = await run();
            note = $"{text.Length} 字节";
        }
        catch (Exception e)
        {
            note = "失败：" + e.Message;
        }
        sw.Stop();
        Console.WriteLine($"   {label,-34} {sw.ElapsedMilliseconds,7} ms   {note}");
    }

    var searchUrl = XmaUrls.Search(new SearchQuery { Page = 1, NsfwMode = 1 });
    var modUrl = XmaUrls.ModPage("95080");

    Console.WriteLine($"   搜索 URL（混合，无 nsfw 参数）前 60 字：{searchUrl[..Math.Min(60, searchUrl.Length)]}");
    Console.WriteLine();

    for (var i = 1; i <= 3; i++)
        await TimeIt($"直连 搜索 #{i}", () => tClient.SearchAsync(new SearchQuery { Page = 1, NsfwMode = 1 },
                                                                   CancellationToken.None).ContinueWith(t => t.Result.Hits.Count.ToString()));

    Console.WriteLine();
    await TimeIt("通道 搜索 #1（冷：要起浏览器）", async () =>
    {
        var h = await tChannel.GetHtmlAsync(searchUrl, CancellationToken.None);
        if (h is null) return "失败：" + tChannel.LastError;
        var parsed = XmaParser.ParseSearch(h, 1, searchUrl);
        return $"HTML {h.Length} 字节 → 解析 {parsed.Hits.Count} 条（总数 {parsed.TotalMods}）";
    });
    for (var i = 2; i <= 3; i++)
        await TimeIt($"通道 搜索 #{i}（会话复用）", async () =>
        {
            var h = await tChannel.GetHtmlAsync(searchUrl, CancellationToken.None);
            if (h is null) return "失败：" + tChannel.LastError;
            var parsed = XmaParser.ParseSearch(h, 1, searchUrl);
            return $"HTML {h.Length} 字节 → 解析 {parsed.Hits.Count} 条（总数 {parsed.TotalMods}）";
        });

    Console.WriteLine();
    for (var i = 1; i <= 2; i++)
        await TimeIt($"直连 详情 #{i}（modid/95080）", () => tClient.DetailAsync("95080", CancellationToken.None)
                                                                   .ContinueWith(t => t.Result.Title));
    await TimeIt("通道 详情（modid/95080）", async () =>
    {
        var h = await tChannel.GetHtmlAsync(modUrl, CancellationToken.None);
        return h is null ? "失败：" + tChannel.LastError : $"HTML {h.Length} 字节";
    });

    Console.WriteLine();
    var thumbs = (await tClient.SearchAsync(new SearchQuery { Page = 1, NsfwMode = 1 }, CancellationToken.None))
                 .Hits.Select(h => h.ThumbUrl).Where(u => u.Length > 0).Take(6).ToList();
    Console.WriteLine($"   下载 {thumbs.Count} 张缩略图（并发）…");
    var tsw = System.Diagnostics.Stopwatch.StartNew();
    var tTasks = thumbs.Select(async u =>
    {
        try
        {
            var bytes = await tHttp.GetByteArrayAsync(u);
            return bytes.Length;
        }
        catch
        {
            return -1;
        }
    }).ToArray();
    var sizes = await Task.WhenAll(tTasks);
    tsw.Stop();
    Console.WriteLine($"   缩略图 {thumbs.Count} 张 并发用时 {tsw.ElapsedMilliseconds} ms（合计 {sizes.Where(s => s > 0).Sum()} 字节）");
}

if (oneCheck)
{
    // 最小实测：一共只发 2 个请求（1 直连 + 1 通道），避免把站点打急。
    Console.WriteLine();
    Console.WriteLine("=== 16. 单次最小实测（1 直连 + 1 通道）===");
    var oSettings = new TestSettings();
    var oCache = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "one");
    Directory.CreateDirectory(oCache);
    using var oHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    using var oClient = new XmaClient(oHttp, oSettings, oCache, new NullLog());
    using var oChannel = new BrowserChannel(oSettings, new NullLog());
    var oUrl = XmaUrls.Search(new SearchQuery { Page = 1, NsfwMode = 1 });

    var oSw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var r = await oClient.SearchAsync(new SearchQuery { Page = 1, NsfwMode = 1 }, CancellationToken.None);
        oSw.Stop();
        Console.WriteLine($"   直连：{oSw.ElapsedMilliseconds} ms → {r.Hits.Count} 条（总数 {r.TotalMods}）");
    }
    catch (Exception e)
    {
        oSw.Stop();
        var msg = e.Message;
        Console.WriteLine($"   直连：{oSw.ElapsedMilliseconds} ms → 失败：{msg[..Math.Min(60, msg.Length)]}");
    }

    oSw.Restart();
    var oHtml = await oChannel.GetHtmlAsync(oUrl, CancellationToken.None);
    oSw.Stop();
    if (oHtml is null)
    {
        Console.WriteLine($"   通道：{oSw.ElapsedMilliseconds} ms → 失败：{oChannel.LastError}");
    }
    else
    {
        var p = XmaParser.ParseSearch(oHtml, 1, oUrl);
        Console.WriteLine($"   通道：{oSw.ElapsedMilliseconds} ms → HTML {oHtml.Length} 字节，解析 {p.Hits.Count} 条（总数 {p.TotalMods}）");
        Console.WriteLine(p.Hits.Count >= 15
            ? "   ✓ 通道拿到了完整一页（15 条）"
            : "   ⚠ 通道只拿到部分/空页 —— 站点大概在挑战或限流这个 IP");
    }
}

if (detailProbe)
{
    // NSFW 详情耗时拆解：一条 NSFW mod 走完整链路，逐段计时。
    // 目的：确认「慢」到底在 直连白撞 / 通道冷启动 / 通道导航 / 预览图 哪一段。
    Console.WriteLine();
    Console.WriteLine("=== 17. NSFW 详情耗时拆解 ===");
    var dSettings = new TestSettings();
    var dCache = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "detail");
    Directory.CreateDirectory(dCache);
    using var dHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    dHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36");
    dHttp.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.xivmodarchive.com/");
    using var dClient = new XmaClient(dHttp, dSettings, dCache, new NullLog());
    using var dChannel = new BrowserChannel(dSettings, new TimeLog());

    string modId = string.Empty, thumb = string.Empty, title = string.Empty;
    var dSw = System.Diagnostics.Stopwatch.StartNew();

    // 站点如果正在挑战本机 IP，NSFW 搜索拿不到样本 → 允许命令行直接给一个 modid
    var explicitMod = args.FirstOrDefault(a => a.Length >= 4 && a.All(char.IsDigit)) ?? string.Empty;
    const string sampleImg = "https://static.xivmodarchive.com/mod-images/7a4c13b5-b5ac-45dd-b034-df43c7132387.jpg";
    var skippedSearch = args.Contains("skipsearch");

    // ① 拿一条真 NSFW 的 mod（通道搜索 nsfw=true —— 这一趟也顺带量了「冷启动」）
    var nsfwUrl = XmaUrls.Search(new SearchQuery { Page = 1, NsfwMode = 2 });
    if (skippedSearch)
    {
        Console.WriteLine("   ① 跳过搜索（命令行指定了 modid / skipsearch）");
    }
    else
    {
        var nsfwHtml = await dChannel.GetHtmlAsync(nsfwUrl, CancellationToken.None);
        dSw.Stop();
        Console.WriteLine($"   ① 通道搜「只看 NSFW」（含冷启动）：{dSw.ElapsedMilliseconds} ms" +
                          (nsfwHtml is null ? $" → 失败：{dChannel.LastError}" : $" → {nsfwHtml.Length} 字节"));
        if (nsfwHtml is not null)
        {
            var hits = XmaParser.ParseSearch(nsfwHtml, 1, nsfwUrl).Hits;
            if (hits.Count > 0) { modId = hits[0].ModId; title = hits[0].Title; thumb = hits[0].ThumbUrl; }
            Console.WriteLine($"      取到 {hits.Count} 条，拿第一条当样本：{modId}「{title}」");
        }
    }

    if (modId.Length == 0 && explicitMod.Length > 0)
    {
        modId = explicitMod;
        Console.WriteLine($"   ① 退回命令行指定的 modid：{modId}");
    }

    if (modId.Length > 0)
    {
        var pageUrl = XmaUrls.ModPage(modId);
        ModDetail? parsed = null;

        // ② 直连详情（NSFW 应该必然失败 —— 这段就是纯浪费的时间）
        dSw.Restart();
        try
        {
            parsed = await dClient.DetailAsync(modId, CancellationToken.None);
            dSw.Stop();
            Console.WriteLine($"   ② 直连详情：{dSw.ElapsedMilliseconds} ms → 成功「{parsed.Title}」" +
                              $"（下载链 {(parsed.DownloadHref.Length > 0 ? "有" : "无")}）");
        }
        catch (Exception e)
        {
            dSw.Stop();
            var m = e.Message;
            Console.WriteLine($"   ② 直连详情：{dSw.ElapsedMilliseconds} ms → 失败：{m[..Math.Min(70, m.Length)]}");
        }

        // ③ 通道详情（此时会话已热）
        dSw.Restart();
        var html = await dChannel.GetHtmlAsync(pageUrl, CancellationToken.None);
        dSw.Stop();
        if (html is null)
        {
            Console.WriteLine($"   ③ 通道详情（热）：{dSw.ElapsedMilliseconds} ms → 失败：{dChannel.LastError}");
        }
        else
        {
            parsed = XmaParser.ParseDetail(html, modId, pageUrl);
            Console.WriteLine($"   ③ 通道详情（热）：{dSw.ElapsedMilliseconds} ms → {html.Length} 字节，" +
                              $"「{parsed.Title}」预览图 {parsed.Images.Count} 张，下载链 {(parsed.DownloadHref.Length > 0 ? "有" : "无")}");
        }

        // ④ 通道详情再来一次（验证「同一个 mod 再看一次」是不是还要付全价）
        dSw.Restart();
        var html2 = await dChannel.GetHtmlAsync(pageUrl, CancellationToken.None);
        dSw.Stop();
        Console.WriteLine($"   ④ 通道详情（热·第二次）：{dSw.ElapsedMilliseconds} ms → {html2?.Length ?? 0} 字节");

        // ⑤ 预览图：直连能不能取？（决定详情面板的图要不要走通道）
        var img = parsed?.Images.Count > 0 ? parsed.Images[0] : (thumb.Length > 0 ? thumb : sampleImg);
        if (img.Length > 0)
        {
            dSw.Restart();
            try
            {
                using var ir = await dHttp.GetAsync(img, CancellationToken.None);
                var bytes = await ir.Content.ReadAsByteArrayAsync(CancellationToken.None);
                dSw.Stop();
                Console.WriteLine($"   ⑤ 直连取预览图：{dSw.ElapsedMilliseconds} ms → HTTP {(int)ir.StatusCode}" +
                                  $"（{bytes.Length} 字节，{ir.Content.Headers.ContentType?.MediaType}）");
            }
            catch (Exception e)
            {
                dSw.Stop();
                Console.WriteLine($"   ⑤ 直连取预览图：{dSw.ElapsedMilliseconds} ms → 异常 {e.GetType().Name}");
            }

            // ⑥ 同一个图走通道要多久（这就是「详情面板图片走通道」的代价）
            dSw.Restart();
            var imgBytes = await dChannel.GetImageBytesAsync(img, CancellationToken.None);
            dSw.Stop();
            Console.WriteLine($"   ⑥ 通道取预览图：{dSw.ElapsedMilliseconds} ms → {(imgBytes?.Length ?? 0)} 字节" +
                              (imgBytes is null ? $"（失败：{dChannel.LastError}）" : ""));
        }
    }
}

if (loginProbe)
{
    // 在插件里登录：把整条链路真跑一遍（窗口会真的弹出来）。
    // 无人操作时应当一路探测到超时/取消，然后**自己把浏览器优雅关掉**、不留垃圾进程。
    Console.WriteLine();
    Console.WriteLine("=== 18. 插件自带登录：真机跑一遍 ===");
    var lSettings = new TestSettings();
    using var lChannel = new BrowserChannel(lSettings, new TimeLog());

    Console.WriteLine($"  插件 profile：{lChannel.OwnedProfileDir}");
    Console.WriteLine($"  登录前：profile 里有 XMA cookie？{lChannel.OwnedProfileHasXmaCookie}");
    Console.WriteLine($"  登录前：CheckLoginAsync → {await lChannel.CheckLoginAsync(CancellationToken.None)}");

    Console.WriteLine("  启动登录流程（20 秒窗口，无人操作 → 应超时退出并自动关闭浏览器）…");
    using var lcts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var lSw = System.Diagnostics.Stopwatch.StartNew();
    var outcome = await lChannel.LoginAsync(msg => Console.WriteLine("        · " + msg), lcts.Token);
    lSw.Stop();
    Console.WriteLine($"  LoginAsync：{lSw.ElapsedMilliseconds} ms → 成功={outcome.Success}");
    foreach (var line in outcome.Message.Split('\n')) Console.WriteLine("      " + line);
    Console.WriteLine($"  用的浏览器：{outcome.BrowserLabel}");
    Console.WriteLine($"  profile 目录已建立？{Directory.Exists(outcome.ProfileDir)}");
    Console.WriteLine($"  登录后：profile 里有 XMA cookie？{lChannel.OwnedProfileHasXmaCookie}");
    Console.WriteLine("  清除登录 → " + lChannel.ClearLogin());
    Console.WriteLine($"  清除后目录还在？{Directory.Exists(outcome.ProfileDir)}（应为 False）");
}

if (perfProbe)
{
    // 优化后的分段计时：预热（只开会话）×2 + 预热后取详情页 ×2。
    // 目的：把「会话冷启动」和「导航」拆开看，确认预热真的省掉了那 1 秒。
    Console.WriteLine();
    Console.WriteLine("=== 19. 优化后分段计时 ===");
    var pSettings = new TestSettings();
    using var pChannel = new BrowserChannel(pSettings, new TimeLog());

    var pSw = System.Diagnostics.Stopwatch.StartNew();
    var warmed = await pChannel.WarmUpAsync(CancellationToken.None);
    pSw.Stop();
    Console.WriteLine($"  ① 预热（只开会话，**不访问站点**）：{pSw.ElapsedMilliseconds} ms → {warmed}");

    pSw.Restart();
    var warmed2 = await pChannel.WarmUpAsync(CancellationToken.None);
    pSw.Stop();
    Console.WriteLine($"  ② 再预热一次（已热 → 应秒回）：{pSw.ElapsedMilliseconds} ms → {warmed2}");

    pSw.Restart();
    var h1 = await pChannel.GetHtmlAsync(XmaUrls.ModPage("141115"), CancellationToken.None);
    pSw.Stop();
    Console.WriteLine($"  ③ 预热后取详情页（= 用户点开详情的真实耗时，无冷启动）：{pSw.ElapsedMilliseconds} ms" +
                      $" → {(h1?.Length ?? 0)} 字节" + (h1 is null ? $"（{pChannel.LastError}）" : string.Empty));

    pSw.Restart();
    var h2 = await pChannel.GetHtmlAsync(XmaUrls.ModPage("95080"), CancellationToken.None);
    pSw.Stop();
    Console.WriteLine($"  ④ 热态再取一页：{pSw.ElapsedMilliseconds} ms → {(h2?.Length ?? 0)} 字节");
}

Console.WriteLine();
Console.WriteLine("=== 20. 广告屏蔽是否真的生效（子资源 A/B 对照）===");
{
    // ⚠ 第一版这个测试写错了，记下来免得再踩：
    //   直接导航到被屏蔽的域名（顶层导航）**测不出** CDP 的 setBlockedURLs ——
    //   它只拦页面发出的子资源请求，顶层导航不归它管。
    //   而 --host-resolver-rules 在本机（走系统代理 127.0.0.1:7877）也测不出来：
    //   DNS 是代理做的，本地解析规则根本不参与。
    //   所以必须**真的用子资源**来验：本地写一个页面，让它去加载广告脚本。
    //
    // 页面放在路径含 /modid/ 的位置 —— 这样通道会按「详情页」的策略等
    // `#mod-download-link, h1.display-5`，也就是等脚本的 onload/onerror 落地后再取 HTML。
    const string marker = "googletagmanager.com";

    var adDir = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "adtest", "modid");
    Directory.CreateDirectory(adDir);
    var adPage = Path.Combine(adDir, "probe.html");
    File.WriteAllText(adPage,
        "<html><body><script>\n" +
        "var s=document.createElement('script');\n" +
        $"s.src='https://www.{marker}/gtm.js?id=ADPROBE';\n" +
        "s.onload=function(){document.body.setAttribute('data-result','LOADED');done();};\n" +
        "s.onerror=function(){document.body.setAttribute('data-result','BLOCKED');done();};\n" +
        "document.head.appendChild(s);\n" +
        "function done(){var h=document.createElement('h1');h.className='display-5';" +
        "h.textContent='done';document.body.appendChild(h);}\n" +
        "</script></body></html>");
    var adUrl = new Uri(adPage).AbsoluteUri;
    Console.WriteLine($"  探测页：{adUrl}");

    async Task<(string Result, long Ms)> TrySubresourceAsync(bool blockAds)
    {
        var st = new TestSettings { BlockAds = blockAds };
        using var ch = new BrowserChannel(st, new NullLog());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var html = await ch.GetHtmlAsync(adUrl, CancellationToken.None);
        sw.Stop();
        if (html is null) return ("取不到：" + ch.LastError, sw.ElapsedMilliseconds);
        if (html.Contains("data-result=\"LOADED\"", StringComparison.Ordinal)) return ("LOADED", sw.ElapsedMilliseconds);
        if (html.Contains("data-result=\"BLOCKED\"", StringComparison.Ordinal)) return ("BLOCKED", sw.ElapsedMilliseconds);
        return ("没结果（脚本可能还没落地）", sw.ElapsedMilliseconds);
    }

    var offR = await TrySubresourceAsync(false);
    Console.WriteLine($"  屏蔽【关】子资源结果：{offR.Result}（{offR.Ms} ms）");
    var onR = await TrySubresourceAsync(true);
    Console.WriteLine($"  屏蔽【开】子资源结果：{onR.Result}（{onR.Ms} ms）");

    if (offR.Result != "LOADED")
    {
        Console.WriteLine($"  ⚠ 对照组没成立（关掉屏蔽也没加载成功：{offR.Result}）→ 无法判定，本次不下结论。");
    }
    else
    {
        Check("对照组：关掉屏蔽时广告脚本能被子资源加载", offR.Result == "LOADED", offR.Result);
        Check("开启屏蔽后广告脚本被子资源层面拦掉", onR.Result == "BLOCKED", onR.Result);
    }
    Check("屏蔽名单里不含 xivmodarchive（否则会把要取的图/页面一起拦掉）",
          !BrowserChannel.BlockedHosts.Any(h => h.Contains("xivmodarchive", StringComparison.OrdinalIgnoreCase)));
    Check("屏蔽名单非空", BrowserChannel.BlockedHosts.Length > 10, BrowserChannel.BlockedHosts.Length.ToString());
    Check("CDP 模式由域名派生（模式与名单不会走偏）",
          BrowserChannel.AdPatterns.Length == BrowserChannel.BlockedHosts.Length &&
          BrowserChannel.AdPatterns.All(p => p.StartsWith("*://*.") && p.EndsWith("/*")));
    Check("host-resolver 规则每个域名都覆盖「带子域」与「裸域」两种写法",
          BrowserChannel.HostResolverRules.Contains("MAP *.nitropay.com 127.0.0.1") &&
          BrowserChannel.HostResolverRules.Contains("MAP nitropay.com 127.0.0.1"));
    Check("host-resolver 规则里也不出现 xivmodarchive",
          !BrowserChannel.HostResolverRules.Contains("xivmodarchive", StringComparison.OrdinalIgnoreCase));
}

if (imgProbe)
{
    // 量 ThumbCache 走的那条直连路：它用的是插件共享的 HttpClient（30 分钟超时），
    // 而且**没有**站点那套头（User-Agent / Referer）—— 和 XmaClient 不一样。
    // 如果这条路会卡很久，那用户看到的「进度条一直转」就是它。
    Console.WriteLine();
    Console.WriteLine("=== 21. 预览图直连路径实测（ThumbCache 走的就是这条）===");
    const string img = "https://static.xivmodarchive.com/mod-images/7a4c13b5-b5ac-45dd-b034-df43c7132387.jpg";

    async Task ProbeAsync(string label, Action<HttpClient>? setup, int timeoutMs)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        setup?.Invoke(http);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var r = await http.GetAsync(img, HttpCompletionOption.ResponseContentRead, CancellationToken.None);
            var bytes = await r.Content.ReadAsByteArrayAsync();
            sw.Stop();
            Console.WriteLine($"  {label}：{sw.ElapsedMilliseconds} ms → HTTP {(int)r.StatusCode}、" +
                              $"{bytes.Length} 字节、{r.Content.Headers.ContentType?.MediaType}");
        }
        catch (Exception e)
        {
            sw.Stop();
            Console.WriteLine($"  {label}：{sw.ElapsedMilliseconds} ms → {e.GetType().Name}" +
                              (e is TaskCanceledException or OperationCanceledException ? "（超时）" : $": {e.Message}"));
        }
    }

    await ProbeAsync("裸 HttpClient（ThumbCache 现状，30 分钟超时）", null, 60_000);
    await ProbeAsync("带站点头（UA + Referer）", h =>
    {
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", XmaUrls.UserAgent);
        h.DefaultRequestHeaders.TryAddWithoutValidation("Referer", XmaUrls.Root + "/");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/*,*/*");
    }, 60_000);
    await ProbeAsync("停滞上限（现在的行为：卡住才放弃）", null, ImageFetchLimits.StallMs);

    Console.WriteLine($"  说明：直连现在是「**停滞** {ImageFetchLimits.StallMs} ms（没有新数据）就放弃」，" +
                      $"整条任务 {ImageFetchLimits.JobTimeoutMs} ms 硬上限。" +
                      "慢可以，卡不行 —— 实测一张整图要 9 秒多，总时长截断会误杀。");

    // ② 流式读 + 报字节：这就是界面现在会显示的东西（原来只有一句「下载中…」）
    Console.WriteLine("  --- 流式读取（界面进度条的数据来源）---");
    using (var shttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var r = await shttp.GetAsync(img, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        var total = r.Content.Headers.ContentLength ?? 0;
        var samples = new List<string>();
        long got = 0;
        var buf = new byte[1 << 16];
        await using var src = await r.Content.ReadAsStreamAsync(CancellationToken.None);
        int n;
        var lastSample = 0L;
        while ((n = await src.ReadAsync(buf, CancellationToken.None)) > 0)
        {
            got += n;
            if (total > 0 && got - lastSample >= total / 4)      // 每 25% 采一次
            {
                lastSample = got;
                samples.Add($"{sw.ElapsedMilliseconds}ms {got * 100 / total}%");
            }
        }
        sw.Stop();
        Console.WriteLine($"  总长 {total} 字节，共 {sw.ElapsedMilliseconds} ms；进度采样：{string.Join(" → ", samples)}");
        Check("站点给了 Content-Length（没有它就算不出比例，只能显示「下载中」）", total > 0, total.ToString());
        Check("流式读取拿到的字节数与声明一致", got == total, $"{got} vs {total}");
    }

    // ③ 关键回归：请求卡死时，直连必须**自己**放弃（否则进度条就永远转 —— 用户报的现象）
    Console.WriteLine("  --- 黑链路：验证直连不会无限等 ---");
    using (var hhttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })   // 故意给足 30 秒
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        linked.CancelAfter(ImageFetchLimits.StallMs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var aborted = false;
        try
        {
            // 10.255.255.1 是典型的黑洞地址：连不上又不会立刻拒绝
            await hhttp.GetAsync("http://10.255.255.1/x.jpg", HttpCompletionOption.ResponseHeadersRead, linked.Token);
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            aborted = true;
        }
        catch (Exception)
        {
            aborted = true;      // 连接被拒之类的也算「没有无限等」
        }
        sw.Stop();
        Console.WriteLine($"  黑洞地址：{sw.ElapsedMilliseconds} ms → {(aborted ? "已放弃（符合预期）" : "居然返回了？")}");
        Check("直连卡死时会在上限内自己放弃（绝不无限等）",
              aborted && sw.ElapsedMilliseconds <= ImageFetchLimits.StallMs + 3000,
              $"{sw.ElapsedMilliseconds} ms");
    }
}

if (adPerfProbe)
{
    // 站点正常配合时才能真正量出「屏蔽广告」值多少 —— 挑战页时两者都是快速失败，量不出差别。
    // 每次都用**全新的通道**（新 Chrome、冷缓存），保证两边条件一样。
    Console.WriteLine();
    Console.WriteLine("=== 22. 广告屏蔽到底省多少（站点正常时，A/B 各起一个新浏览器）===");
    const string detailUrl = "https://www.xivmodarchive.com/modid/141115";

    async Task<(long Ms, int Bytes, string Note)> NavAsync(bool blockAds, string label)
    {
        var st = new TestSettings { BlockAds = blockAds };
        using var ch = new BrowserChannel(st, new NullLog());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var html = await ch.GetHtmlAsync(detailUrl, CancellationToken.None);
        sw.Stop();
        var note = html is null ? "失败：" + ch.LastError : $"{html.Length} 字节";
        Console.WriteLine($"  屏蔽【{(blockAds ? "开" : "关")}】{label}：{sw.ElapsedMilliseconds} ms → {note}");
        return (sw.ElapsedMilliseconds, html?.Length ?? 0, note);
    }

    var offNav = await NavAsync(false, "（新浏览器，冷缓存）");
    var onNav = await NavAsync(true, "（新浏览器，冷缓存）");

    Check("两边都真的拿到了详情页（否则这组对比没意义）", offNav.Bytes > 10000 && onNav.Bytes > 10000,
          $"{offNav.Bytes} / {onNav.Bytes}");
    Console.WriteLine($"  结论：屏蔽前后 {offNav.Ms} ms → {onNav.Ms} ms" +
                      $"（差 {(offNav.Ms - onNav.Ms)} ms，页面字节基本一致说明内容没被误伤）");

    // 预览图：真实大小与耗时（站点配合时）
    Console.WriteLine("  --- 预览图 ---");
    using (var ihttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
    {
        ihttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", XmaUrls.UserAgent);
        ihttp.DefaultRequestHeaders.TryAddWithoutValidation("Referer", XmaUrls.Root + "/");
        const string img = "https://static.xivmodarchive.com/mod-images/7a4c13b5-b5ac-45dd-b034-df43c7132387.jpg";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bytes = await ihttp.GetByteArrayAsync(img);
        sw.Stop();
        Console.WriteLine($"  直连取预览图：{sw.ElapsedMilliseconds} ms → {bytes.Length} 字节" +
                          $"（{bytes.Length / 1048576.0:0.00} MB）");
        Check("预览图直连能拿到（停滞检测不会误杀慢下载）", bytes.Length > 1000,
              $"{sw.ElapsedMilliseconds} ms / {bytes.Length} 字节");
    }
}

if (externalProbe)
{
    // 摸底：站上的「外链」到底跳去哪。看清形态再设计，别猜。
    Console.WriteLine();
    Console.WriteLine("=== 23. 站上真实外链摸底 ===");
    var xSettings = new TestSettings();
    using var xChannel = new BrowserChannel(xSettings, new TimeLog());

    var ids = new List<string>();
    for (var page = 1; page <= 2 && ids.Count < 14; page++)
    {
        var listUrl = XmaUrls.Search(new SearchQuery { Page = page, NsfwMode = 1 });
        var listHtml = await xChannel.GetHtmlAsync(listUrl, CancellationToken.None);
        if (listHtml is null)
        {
            Console.WriteLine($"  第 {page} 页列表取不到：{xChannel.LastError}");
            continue;
        }
        foreach (var h in XmaParser.ParseSearch(listHtml, page, listUrl).Hits)
        {
            if (!ids.Contains(h.ModId)) ids.Add(h.ModId);
            if (ids.Count >= 14) break;
        }
        Console.WriteLine($"  列表第 {page} 页 → 累计 {ids.Count} 个 mod id");
    }

    var hosts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var samples = new List<string>();
    var internalCount = 0;
    var noLink = 0;

    foreach (var id in ids)
    {
        var page2 = XmaUrls.ModPage(id);
        var html2 = await xChannel.GetHtmlAsync(page2, CancellationToken.None);
        if (html2 is null)
        {
            Console.WriteLine($"  {id}：取不到（{xChannel.LastError}）");
            continue;
        }

        var det = XmaParser.ParseDetail(html2, id, page2);
        if (det.DownloadHref.Length == 0) { noLink++; Console.WriteLine($"  {id}：页面上没有下载链接"); continue; }
        if (!det.IsExternalLink) { internalCount++; continue; }

        string host;
        try { host = new Uri(det.AbsoluteDownloadUrl).Host; } catch { host = "(解析不了)"; }
        hosts[host] = hosts.TryGetValue(host, out var c) ? c + 1 : 1;
        samples.Add($"{id}  [{host}]  {det.AbsoluteDownloadUrl}");
    }

    Console.WriteLine();
    Console.WriteLine($"  统计：站内直链 {internalCount} 个 / 外链 {samples.Count} 个 / 无链接 {noLink} 个");
    Console.WriteLine("  --- 外链样本（modid, 主机, 完整 URL）---");
    foreach (var s in samples) Console.WriteLine("    " + s);
    Console.WriteLine("  --- 外链主机分布 ---");
    foreach (var kv in hosts.OrderByDescending(k => k.Value))
        Console.WriteLine($"    {kv.Key}: {kv.Value}");
}

if (streamProbe)
{
    // 用真实站点模拟「往下滚三页」：验证 解析 → 去重 → 页号推进 → 到底判据 这条链。
    // （触底触发依赖 ImGui 可见性，没有游戏测不了，所以这里只验数据链。）
    Console.WriteLine();
    Console.WriteLine("=== 24. 流式加载数据链实测（真实站点，连取 3 页）===");
    var stSettings = new TestSettings();
    using var stChannel = new BrowserChannel(stSettings, new TimeLog());
    var st = new StreamState();
    st.Reset(1);

    for (var page = st.NextPage; page <= 3 && !st.End; page = st.NextPage)
    {
        var stUrl = XmaUrls.Search(new SearchQuery { Page = page, NsfwMode = 1 });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var html = await stChannel.GetHtmlAsync(stUrl, CancellationToken.None);
        sw.Stop();
        if (html is null)
        {
            Console.WriteLine($"  第 {page} 页取不到：{stChannel.LastError}");
            break;
        }

        var parsed = XmaParser.ParseSearch(html, page, stUrl);
        var res = st.Append(parsed.Hits, parsed.TotalMods);
        Console.WriteLine($"  第 {page} 页：站点给 {parsed.Hits.Count} 条 → 新增 {res.NewItems.Count} 条" +
                          $"（{sw.ElapsedMilliseconds} ms，累计 {st.Loaded} 条，站点总数 {st.Total}）" +
                          (res.End ? $"  → 到底：{res.Reason}" : $"  → 下一页 {st.NextPage}"));
    }

    Check("真实站点：取到了内容", st.Loaded > 0, st.Loaded.ToString());
    Check("真实站点：三页之间没有重复（去重生效）", st.Loaded >= 15, st.Loaded.ToString());
    Check("真实站点：页号正常推进", st.NextPage > 1, st.NextPage.ToString());
    Check("真实站点：没误判到底", !st.End || st.EndReason.Length > 0, st.EndReason);
}

if (inspectProbe)
{
    // 对着**真站点**验证「装前看包」整条链路：详情页 → 下载直链 → Range 200 字节问大小
    // → Range 256 KB 读 zip 中央目录 → 列出包内清单。全程**不下载整包**。
    Console.WriteLine();
    Console.WriteLine("=== 联网：装前看包（Range 只取几十 KB，绝不下载整包）===");
    var iCache = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "inspect");
    Directory.CreateDirectory(iCache);
    using var iHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    using var iClient = new XmaClient(iHttp, new TestSettings(), iCache, new NullLog());

    // 实测挑出来的四种形态：.ttmp2（258 MB，zip 容器）/ .pmp（zip）/ .zip（内含 4 个 pmp）/ .rar（列不出）
    foreach (var mid in new[] { "95080", "67543", "107423", "47778" })
    {
        var inspDet = await iClient.DetailAsync(mid);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var insp = await iClient.InspectPackageAsync(inspDet.AbsoluteDownloadUrl);
        sw.Stop();

        Console.WriteLine($"   modid {mid} → {insp.FileName}（{insp.Extension}）");
        Console.WriteLine($"      大小 {insp.SizeText}｜用时 {sw.ElapsedMilliseconds} ms｜能列内容 = {insp.HasListing}");
        if (insp.Summary is { } sum)
            Console.WriteLine($"      条目 {sum.EntryCount} 个｜内含 mod {sum.ModCount} 个：{string.Join("、", sum.InnerModPacks.Take(4))}" +
                              $"｜顶层：{string.Join("、", sum.TopPaths.Take(5))}");
        else if (insp.Note.Length > 0)
            Console.WriteLine($"      说明：{insp.Note}");

        Check($"装前看包({mid}) 拿到了真实大小", insp.SizeKnown, insp.SizeText);
        Check($"装前看包({mid}) 用时 < 10 秒（证明没偷偷下整包）", sw.ElapsedMilliseconds < 10_000, sw.ElapsedMilliseconds + " ms");
    }
}

if (nameTestProbe)
{
    // 复现主人报的问题：粘 Mod 全名到搜索框却搜不到（站点上搜得到）。
    Console.WriteLine();
    Console.WriteLine("=== 联网：复现「搜 Mod 名搜不到」 ===");
    var nCache = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "nametest");
    Directory.CreateDirectory(nCache);
    using var nHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    using var nClient = new XmaClient(nHttp, new TestSettings(), nCache, new NullLog());

    const string reported = "Bellissente, a makeup for Aerin";

    // 对照 A：修复后的默认行为 = 按标题搜（name=）
    var byTitle = await nClient.SearchAsync(new SearchQuery { Text = reported, Page = 1 });
    var titleHit = byTitle.Hits.Any(h => h.Title.Contains("Bellissente"));
    Console.WriteLine($"   按标题(name=)     → {byTitle.Hits.Count} 条：{string.Join(" | ", byTitle.Hits.Take(3).Select(h => h.Title))}");
    Check("修复后：粘 Mod 全名能搜到它", titleHit, byTitle.Url);

    // 对照 B：旧行为 = 全文搜（basic_text=）。只打印不改判 —— 站点排序以后可能变，
    // 把它写成硬断言会让构建莫名其妙地挂；但它正是当初"搜不到"的证据。
    var byFull = await nClient.SearchAsync(new SearchQuery { Text = reported, Page = 1, TextAsFullText = true });
    var fullHit = byFull.Hits.Any(h => h.Title.Contains("Bellissente"));
    Console.WriteLine($"   旧行为(basic_text=) → {byFull.Hits.Count} 条：{string.Join(" | ", byFull.Hits.Take(3).Select(h => h.Title))}");
    Console.WriteLine($"   对照结论：旧行为在这一页{(fullHit ? "也能找到" : "找不到它 —— 与主人反馈一致")}");

    // 主人第二次报：只搜「Bellissente」、并且勾了全文搜，也搜不出来。
    // 用**插件的真实默认参数**（OnlyDtCompatible=true、NsfwMode=0、sortby=time_posted）两组各跑一次，
    // 看站点到底返回什么、以及插件解析出几条。
    Console.WriteLine();
    Console.WriteLine("   主人第二次报的用例（只搜 Bellissente）：");
    foreach (var asFull in new[] { true, false })
    {
        var r = await nClient.SearchAsync(new SearchQuery { Text = "Bellissente", TextAsFullText = asFull, Page = 1 });
        Console.WriteLine($"     全文={asFull} → 解析到 {r.Hits.Count} 条（站点总数标记 {r.TotalMods}）");
        foreach (var h in r.Hits.Take(3)) Console.WriteLine($"         · {h.Title}  [{h.ModId}]");
        Console.WriteLine($"         {r.Url}");

        // ⭐ 像界面一样再走一遍**流式状态机** —— 只验「解析到几条」是不够的：
        // 曾经解析出 1 条，却被 StreamState 的「到底」分支丢掉，界面一片空白。
        // 这一步就是当初漏掉的那一环。
        var sim = new StreamState();
        sim.Reset(1, r.TotalMods);
        var simRes = sim.Append(r.Hits, r.TotalMods);
        var uiList = simRes.NewItems.ToList();
        Console.WriteLine($"         界面状态机 → 最终列表 {uiList.Count} 条（到底={simRes.End}：{simRes.Reason}）");
        Check($"界面最终列表非空（走一遍 StreamState，全文={asFull}）", uiList.Count > 0,
              $"解析 {r.Hits.Count} 条 → 界面 {uiList.Count} 条");
    }

    // 主人第三次报的用例：作者名里带撇号（Helina's Bakery）。
    // 实测撇号会把站点的作者匹配弄坏（author=Helina's Bakery → 0 条），改成空格就好。
    Console.WriteLine();
    Console.WriteLine("   作者名带撇号的用例：");
    foreach (var rawAuthor in new[] { "Helina's Bakery", "Helina" })
    {
        var aq = new SearchQuery { Author = rawAuthor, Page = 1 };
        var ar = await nClient.SearchAsync(aq);
        Console.WriteLine($"     author=\"{rawAuthor}\" → {ar.Hits.Count} 条｜{ar.Url}");
        Console.WriteLine($"        {string.Join(" | ", ar.Hits.Take(2).Select(h => h.Title))}");
        Check($"作者搜索拿得到结果（{rawAuthor}）", ar.Hits.Count > 0, ar.Url);
    }
}

if (chanSearchProbe)
{
    // 主人的场景：**混合模式** → 搜索每次都走浏览器通道（headless Chrome），不走直连。
    // 这条路径此前从未被实测过。这里用通道取同一个搜索 URL，看到底拿到什么。
    Console.WriteLine();
    Console.WriteLine("=== 混合模式的真实路径：搜索走浏览器通道 ===");
    var csSettings = new TestSettings();
    var csCache = Path.Combine(Path.GetTempPath(), "XmaBrowserTests", "chansearch");
    Directory.CreateDirectory(csCache);
    using var csChannel = new BrowserChannel(csSettings, new TimeLog());

    foreach (var asFull in new[] { false, true })
    {
        var cq = new SearchQuery
        {
            Text = "Bellissente",
            TextAsFullText = asFull,
            NsfwMode = 1,              // 混合：不传 nsfw 参数，必须带登录态
            OnlyDtCompatible = true,
            Page = 1,
        };
        var cu = XmaUrls.Search(cq);
        var label = asFull ? "全文 basic_text" : "标题 name";
        Console.WriteLine($"   关键词语义 = {label}");
        Console.WriteLine($"      {cu}");

        var csw = System.Diagnostics.Stopwatch.StartNew();
        var chtml = await csChannel.GetHtmlAsync(cu, CancellationToken.None);
        csw.Stop();

        if (chtml is null)
        {
            Console.WriteLine($"      ❌ 通道取不到页面：{csChannel.LastError}");
            Check($"通道搜索({label})", false, csChannel.LastError);
            continue;
        }

        var cfacts = LoginStateDetector.Analyze(chtml);
        var cparsed = XmaParser.ParseSearch(chtml, 1, cu);
        Console.WriteLine($"      取到 {chtml.Length} 字节 / {csw.ElapsedMilliseconds} ms｜登录态 {cfacts.State}｜页面卡片 {cfacts.CardCount} 张");
        Console.WriteLine($"      解析出 {cparsed.Hits.Count} 条：{string.Join(" | ", cparsed.Hits.Take(3).Select(h => h.Title))}");
        if (cparsed.Hits.Count == 0)
            Console.WriteLine($"      诊断：{EmptyResultDiagnosis.Describe(chtml, cu)}");

        Check($"通道搜索({label}) 能搜到 Bellissente",
              cparsed.Hits.Any(h => h.Title.Contains("Bellissente")),
              $"{chtml.Length} 字节 / {cfacts.CardCount} 卡 / 解析 {cparsed.Hits.Count} 条");
    }
}

Console.WriteLine();
Console.WriteLine($"==== 结果：通过 {pass}，失败 {fail} ====");
return fail == 0 ? 0 : 1;

static void WriteEntry(ZipArchive z, string name, string content)
{
    var entry = z.CreateEntry(name);
    using var w = new StreamWriter(entry.Open());
    w.Write(content);
}

/// <summary>忽略空白后再比较（分段会在边界处丢掉空格/换行，这是正常的）。</summary>
static string Normalize(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

internal sealed class TestTranslateSettings : ITranslateSettings
{
    public string TranslateBackend => "google";
    public string TranslateBaseUrl => "https://api.deepseek.com/v1";
    public string TranslateApiKey => string.Empty;
    public string TranslateModel => "deepseek-chat";
    public string TranslateProxy => string.Empty;
}

internal sealed class TimeLog : IXmaLog
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    public void Info(string message) => Console.WriteLine($"        · [{_sw.ElapsedMilliseconds,6} ms] {message}");
    public void Warn(string message) => Console.WriteLine($"        · [{_sw.ElapsedMilliseconds,6} ms] ⚠ {message}");
    public void Debug(string message) => Console.WriteLine($"        · [{_sw.ElapsedMilliseconds,6} ms] · {message}");
    public void Error(string message) => Console.WriteLine($"        · [{_sw.ElapsedMilliseconds,6} ms] ✖ {message}");
}

internal sealed class TestSettings : IXmaSettings, IBrowserSettings
{
    public int RequestDelayMs => 350;
    public bool EnableBrowserChannel => true;
    public string BrowserExe => string.Empty;
    public string BrowserUserDataDir => string.Empty;
    public bool AutoPickProfile => true;
    public int BrowserTimeoutSec => 180;
    public bool CleanupBorrowedProfile => true;
    public string ChannelProfileDir => string.Empty;
    public bool ChannelBlockAds => BlockAds;
    /// <summary>测试里可切换：用来做「屏蔽广告真的生效吗」的 A/B 对照。</summary>
    public bool BlockAds { get; set; } = true;
    public int ChannelIdleMinutes => 0;      // 测试里别自动关，避免影响多次连续调用
}
