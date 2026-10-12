using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using XmaBrowser.Browser;
using XmaBrowser.Cover;
using XmaBrowser.Install;
using XmaBrowser.Site;

// 站点层 / 包解析层 / 浏览器通道的无游戏自测。
//   离线：dotnet run -c Release                  （用 samples/ 里的真实 HTML，不联网）
//   联网：dotnet run -c Release -- live          （真站点：SFW 直连 + NSFW 浏览器通道 + 真实下载）
var live = args.Contains("live");
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
var urlNsfw = XmaUrls.Search(new SearchQuery { Nsfw = true, OnlyDtCompatible = false });
Check("NSFW + 不限兼容性时参数正确", urlNsfw.Contains("nsfw=true") && !urlNsfw.Contains("dt_compat"), urlNsfw);

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

Console.WriteLine();
Console.WriteLine($"==== 结果：通过 {pass}，失败 {fail} ====");
return fail == 0 ? 0 : 1;

static void WriteEntry(ZipArchive z, string name, string content)
{
    var entry = z.CreateEntry(name);
    using var w = new StreamWriter(entry.Open());
    w.Write(content);
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
