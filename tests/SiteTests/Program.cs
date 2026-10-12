using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
Check("含 basic_text（并已转义空格）", url.Contains("basic_text=hair%20style"), url);
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

internal sealed class TestSettings : IXmaSettings, IBrowserSettings
{
    public int RequestDelayMs => 350;
    public bool EnableBrowserChannel => true;
    public string BrowserExe => string.Empty;
    public string BrowserUserDataDir => string.Empty;
    public bool AutoPickProfile => true;
    public int BrowserTimeoutSec => 180;
    public bool CleanupBorrowedProfile => true;
}
