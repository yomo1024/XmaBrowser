using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using XmaBrowser.Browser;
using XmaBrowser.Diagnostics;
using XmaBrowser.Favorites;
using XmaBrowser.Install;
using XmaBrowser.Site;
using XmaBrowser.Translate;
using XmaBrowser.Ui;

namespace XmaBrowser.Windows;

public class MainWindow : Window
{
    private const string AdvancedId = "高级搜索###xma-advanced";
    private const string RepoUrl = "https://github.com/yomo1024/XmaBrowser";
    private const string SourceUrl = "https://raw.githubusercontent.com/yomo1024/XmaBrowser/main/pluginmaster.json";

    private static readonly Vector4 Dim = new(0.70f, 0.70f, 0.70f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.80f, 0.45f, 1f);
    private static readonly Vector4 Blue = new(0.55f, 0.75f, 0.98f, 1f);
    private static readonly Vector4 CardBg = new(0.16f, 0.16f, 0.18f, 1f);
    private static readonly Vector4 SelBg = new(0.20f, 0.28f, 0.44f, 1f);

    private readonly Plugin _plugin;
    private readonly SearchForm _form = new();
    private readonly string[] _sortLabels = SearchForm.Sorts.Select(s => s.Label).ToArray();

    // ---- 界面缩放（每帧从配置取；S() 用来把自己的像素常量一起缩）----
    private float _s = 1f;
    private const int UiStyleVars = 6;
    private bool _fontScaleProbed;
    private bool _fontScaleWorks = true;
    private bool _fontScaleApplied;

    /// <summary>按当前缩放折算一个像素值（列宽、缩略图、面板高度都用它）。</summary>
    private float S(float value) => value * _s;

    // ---- 浏览状态 ----
    private List<SearchHit> _hits = [];
    private int _page = 1;
    private int _jumpPage = 1;
    private int _totalMods;
    // 向下滚动流式加载：状态机（去重/到底判据）+ 在途标志 + 失败原因 + 自动触发预算
    private readonly StreamState _stream = new();
    private bool _streamBusy;
    private string _streamError = string.Empty;
    private int _streamAutoFired;
    private float _lastScrollY;
    private bool _listAtBottom;

    /// <summary>站点一页固定 15 条（实测），用于算总页数。</summary>
    private const int SitePageSize = 15;
    private string _status = "留空关键词直接搜 = 看最新发布";

    /// <summary>上一次搜索 0 条时的**原因**（来自 EmptyResultDiagnosis），有值就显示出来。</summary>
    private string _searchDiag = string.Empty;
    // 加载进度（分段预估 + 已用时间；纯逻辑类，可离线自测）
    private LoadProgress _searchProgress = LoadProgress.ForSearch();
    private LoadProgress _detailProgress = LoadProgress.ForDetail();
    private string _error = string.Empty;

    /// <summary>NSFW 模式走通道时，通道不可用的退回提示（显示在状态行下面）。</summary>
    private string _nsfwChannelWarn = string.Empty;

    // ---- NSFW 自检 ----
    private string _nsfwReport = string.Empty;
    private bool _nsfwChecking;

    // ---- 收藏 / 关注 ----
    /// <summary>点收藏条目的「详情」后自动切到浏览页（ImGui 的 SetSelected 只能在这一帧用）。</summary>
    private bool _focusBrowseTab;

    /// <summary>正在按作者搜（搜完要做「新品」对比）。</summary>
    private string _authorFollowPending = string.Empty;

    /// <summary>这次搜索里属于「关注作者的新品」的 modId（用来在列表里打标）。</summary>
    private HashSet<string> _authorNewIds = [];

    /// <summary>搜索结果短期缓存（翻页/重复搜秒开）。</summary>
    private readonly SearchCache _searchCache = new();

    /// <summary>直连被人机校验拦过之后的冷却记忆（这段时间直接用通道，不白等）。</summary>
    private readonly DirectBlockMemory _directBlocked = new();
    private bool _busy;
    private bool _viaChannel;
    private SearchHit? _selected;
    private ModDetail? _detail;
    private bool _detailBusy;

    // ---- 装前看包（只对当前详情生效；换 Mod 即作废）----
    private PackageInspection? _pkgProbe;
    private bool _pkgProbeBusy;
    private string _pkgProbeModId = string.Empty;

    /// <summary>
    /// 「装前看包」里用户对**包内各个 mod**的勾选（key = 内嵌包文件名，默认全选）。
    /// 只对当前这条 Mod 有效，换 Mod 就清空。
    /// </summary>
    private readonly Dictionary<string, bool> _pkgSel = new(StringComparer.OrdinalIgnoreCase);
    private int _previewIndex;

    // ---- 翻译状态 ----
    private bool _showOriginal;
    private string _translatedTitle = string.Empty;
    private string _translatedDesc = string.Empty;
    private bool _translating;
    private string _translateError = string.Empty;
    private CancellationTokenSource? _transCts;
    private CancellationTokenSource? _titleCts;
    private int _titleBusy;      // >0 = 正在批量翻译本页标题
    private int _titleDone;
    private int _titleTotal;
    private string _translateTestResult = string.Empty;
    private string _translatedForKey = string.Empty;

    // ---- 弹窗 ----
    private bool _advancedOpen;
    private bool _openAdvancedRequest;

    private string _browserReport = string.Empty;
    private CancellationTokenSource? _cts;

    public MainWindow(Plugin plugin) : base($"XMA Browser v{Plugin.PluginVersion}###XmaBrowserMain")
    {
        _plugin = plugin;
        _form.OnlyDtCompatible = plugin.Configuration.OnlyDtCompatible;
        _form.NsfwMode = plugin.Configuration.EffectiveNsfwMode;

        // 默认开小一点、也允许缩更小（以前最小 860×560，小屏上就顶满了）
        Size = new Vector2(1080, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(UiScale.Min * 1000f, UiScale.Min * 640f),   // 700×448
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        // 界面缩放：把字体与控件一起缩，只影响本窗口（不动全局 FontGlobalScale）。
        var pushed = PushUiScale(_plugin.Configuration.UiScale);
        try
        {
            DrawContents();
        }
        finally
        {
            PopUiScale(pushed);
        }
    }

    /// <summary>应用界面缩放：字体 + style 间距/内边距/滚动条，返回是否需要弹栈。</summary>
    private bool PushUiScale(float scale)
    {
        _s = UiScale.Clamp(scale);
        if (MathF.Abs(_s - 1f) < 0.001f) return false;

        var style = ImGui.GetStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, style.ItemSpacing * _s);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, style.FramePadding * _s);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, style.CellPadding * _s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, style.WindowPadding * _s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, style.ScrollbarSize * _s);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, style.GrabMinSize * _s);

        // 字体缩放：ImGui 1.92 起 SetWindowFontScale 进了「过时 API」，不同版本行为可能不同。
        // 不猜 —— 开局**实测一次**：调 0.5 看 GetFontSize() 有没有跟着变。
        if (!_fontScaleProbed)
        {
            _fontScaleProbed = true;
            var before = ImGui.GetFontSize();
            ImGui.SetWindowFontScale(0.5f);
            _fontScaleWorks = MathF.Abs(ImGui.GetFontSize() - before * 0.5f) < 0.5f;
            ImGui.SetWindowFontScale(1f);
            Plugin.Log.Information(_fontScaleWorks
                ? "[XmaBrowser] 界面缩放：SetWindowFontScale 可用（字体与控件一起缩）"
                : "[XmaBrowser] 界面缩放：本版 ImGui 的 SetWindowFontScale 无效，只缩间距与控件尺寸（字体无法缩）");
        }

        _fontScaleApplied = _fontScaleWorks;
        if (_fontScaleApplied) ImGui.SetWindowFontScale(_s);
        return true;
    }

    private void PopUiScale(bool pushed)
    {
        if (pushed)
        {
            if (_fontScaleApplied) ImGui.SetWindowFontScale(1f);
            ImGui.PopStyleVar(UiStyleVars);
        }
        _fontScaleApplied = false;
        _s = 1f;
    }

    private void DrawContents()
    {
        DrawTopBar();

        if (ImGui.BeginTabBar("xma-tabs"))
        {
            var browseFlags = _focusBrowseTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            _focusBrowseTab = false;
            if (ImGui.BeginTabItem("浏览", browseFlags))
            {
                DrawBrowseTab();
                ImGui.EndTabItem();
            }

            var jobs = _plugin.Jobs.Snapshot();
            var running = jobs.Count(j => !j.Finished);
            if (ImGui.BeginTabItem(running > 0 ? $"队列 ({running})" : "队列"))
            {
                DrawQueueTab();
                ImGui.EndTabItem();
            }

            var favCount = _plugin.Configuration.Favorites.Count;
            var authorCount = _plugin.Configuration.FollowedAuthors.Count;
            var newCount = _plugin.Configuration.FollowedAuthors.Sum(a => a.NewModIds.Count);
            var favLabel = (favCount, authorCount, newCount) switch
            {
                (0, 0, _) => "收藏·关注",
                (_, _, > 0) => $"收藏·关注 ({favCount}/{authorCount}) 新品 {newCount}",
                _ => $"收藏·关注 ({favCount}/{authorCount})",
            };
            if (ImGui.BeginTabItem(favLabel + "##fav"))
            {
                DrawFavoritesTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("设置"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("关于"))
            {
                DrawAboutTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        DrawAdvancedPopup();
    }

    // ==================================================================== 顶部

    private void DrawTopBar()
    {
        var penumbra = _plugin.Penumbra.Available
            ? $"Penumbra API {_plugin.Penumbra.ApiMajor}.{_plugin.Penumbra.ApiMinor} ✓"
            : "Penumbra 未连接 ✗";
        ImGui.TextColored(_plugin.Penumbra.Available ? Green : Red, penumbra);
        ImGui.SameLine();
        ImGui.TextColored(Dim, "·");
        ImGui.SameLine();
        ImGui.TextColored(Dim, $"站点 Mod 总数 {_totalMods}");
        ImGui.SameLine();
        ImGui.TextColored(Dim, "·");
        ImGui.SameLine();
        ImGui.TextColored(_viaChannel ? Yellow : Dim,
            _viaChannel ? $"本次通过浏览器通道读取（{_viaChannelReason}）" : "直连模式");
        if (_plugin.Channel.Enabled)
        {
            ImGui.SameLine();
            ImGui.TextColored(Dim, "· 浏览器通道已启用");
        }
        ImGui.Separator();
    }

    // ==================================================================== 浏览

    private void DrawBrowseTab()
    {
        DrawSearchBar();

        if (_error.Length > 0)
            ImGui.TextColored(Red, _error);
        else
            ImGui.TextColored(_busy ? Yellow : Green, _status);

        // 0 条时把原因说清楚：是站点拦了、没登录、还是站点改版导致一条都没解析出来。
        // 以前只显示「没有结果」，用户既不能自救、我也无从定位。
        if (_searchDiag.Length > 0)
            ImGui.TextColored(Yellow, "　↳ " + _searchDiag);

        if (_busy) DrawLoadProgress(_searchProgress, S(360f));

        if (_nsfwChannelWarn.Length > 0)
            ImGui.TextColored(Yellow, "⚠ " + _nsfwChannelWarn);

        var active = _form.ActiveConditions().ToList();
        if (active.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Blue, "  筛选中：" + string.Join("  ", active));
        }

        ImGui.Spacing();

        var avail = ImGui.GetContentRegionAvail();
        var listWidth = MathF.Max(S(320f), avail.X * UiScale.ClampSplit(_plugin.Configuration.BrowseSplit));

        ImGui.BeginChild("xma-browse-left", new Vector2(listWidth, 0));

        // 滚轮或滚动位置变化 = 用户又滑了一下 → 重置「自动加载」的预算。
        // 有了这个预算，即使可见性判断失效（一直认为到底了），也不会把站点翻穿。
        var wheelDelta = ImGui.GetIO().MouseWheel;
        var scrollNow = ImGui.GetScrollY();
        if (wheelDelta != 0f || MathF.Abs(scrollNow - _lastScrollY) > 1f)
        {
            _streamAutoFired = 0;
            _lastScrollY = scrollNow;
        }

        if (_plugin.Configuration.GridView) DrawResultGrid();
        else DrawResultList();
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("xma-browse-right", new Vector2(0, 0));
        DrawDetailPane();
        ImGui.EndChild();
    }

    private void DrawSearchBar()
    {
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(MathF.Max(S(200f), avail - S(330f)));
        ImGui.InputTextWithHint("##xma-kw", "关键词 / Mod 名（默认按标题精确搜）", ref _form.Text, 128);
        if (ImGui.IsItemDeactivatedAfterEdit()) StartSearch(1);

        ImGui.SameLine();
        ImGui.BeginDisabled(_busy);
        if (ImGui.Button("搜索", new Vector2(S(90f), 0))) StartSearch(1);
        ImGui.EndDisabled();

        ImGui.SameLine();
        var advLabel = _form.HasAdvanced ? "高级搜索 ●##xma-advbtn" : "高级搜索…##xma-advbtn";
        if (ImGui.Button(advLabel, new Vector2(S(120f), 0)))
        {
            _openAdvancedRequest = true;
            _advancedOpen = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("名称 / 作者 / 标签 / 种族 / 性别 / 类型 / 影响替换");

        ImGui.SameLine();
        ImGui.BeginDisabled(_busy);
        if (ImGui.Button("刷新", new Vector2(S(70f), 0))) StartSearch(_page);
        ImGui.EndDisabled();

        // 第二行：排序 / 过滤 / 翻页（不与上面挤在一起）
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("排序##xma-sort", ref _form.SortIndex, _sortLabels, _sortLabels.Length);

        ImGui.SameLine();
        ImGui.Checkbox("仅当前版本兼容", ref _form.OnlyDtCompatible);

        ImGui.SameLine();
        DrawNsfwCombo("##nsfw-main");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("站点是三态的（实测）：\n" +
                             "· 排除 NSFW ＝ 发 nsfw=false\n" +
                             "· 含 NSFW（混合）＝ 不传 nsfw 参数\n" +
                             "· 只看 NSFW ＝ 发 nsfw=true\n" +
                             "⚠ 后两种**必须带站点登录态**才有效：实测同一 URL「混合」——直连(未登录) 53281 条、\n" +
                             "浏览器通道(登录) 61787 条（= 53281 + 8506 条 NSFW）。\n" +
                             "所以选这两种时会自动走「浏览器通道」（借本机浏览器的站点登录），首次会慢一点。");

        ImGui.SameLine();
        ImGui.Checkbox("把关键词当标签搜", ref _form.KeywordAsTag);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾上后，「关键词」框里的内容按站点标签精确检索。");

        ImGui.SameLine();
        var favOnly = _plugin.Configuration.FavoritesOnlyFilter;
        if (ImGui.Checkbox("只看收藏", ref favOnly))
        {
            _plugin.Configuration.FavoritesOnlyFilter = favOnly;
            _plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"只显示本页里已收藏的条目（本地过滤，不联网）。\n已收藏 {_plugin.Configuration.Favorites.Count} 条。");

        // 第三行：**页码单独一行** + 视图切换 + 批量翻译标题。
        // （原来翻页按钮被 SetCursorPosX 推到过滤条件那一行的最右端，窗口一窄就跑到可视区外，
        //   看起来就像「只有 15 条、没法翻页」。）
        DrawPager("top");

        ImGui.SameLine();
        ImGui.TextColored(Dim, "｜视图");

        var grid = _plugin.Configuration.GridView;
        ImGui.SameLine();
        ImGui.BeginDisabled(_busy);
        if (ImGui.RadioButton("列表", !grid))
        {
            _plugin.Configuration.GridView = false;
            _plugin.Configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("宫格", grid))
        {
            _plugin.Configuration.GridView = true;
            _plugin.Configuration.Save();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextColored(Dim, "｜");
        ImGui.SameLine();
        DrawTitleTranslateButton();

        ImGui.Spacing();
    }

    /// <summary>NSFW 三态选择控件（主搜索栏与高级搜索弹窗共用）。</summary>
    private void DrawNsfwCombo(string id)
    {
        ImGui.SetNextItemWidth(160);
        ImGui.Combo($"内容##{id}", ref _form.NsfwMode, NsfwLabels, NsfwLabels.Length);
    }

    private static readonly string[] NsfwLabels = ["排除 NSFW", "含 NSFW（混合）", "只看 NSFW"];

    // ==================================================================== 收藏 / 关注

    /// <summary>切换一条 Mod 的收藏状态并落盘。</summary>
    private void ToggleFavorite(string modId, string title, string author, string type, string thumbUrl)
    {
        var cfg = _plugin.Configuration;
        var nowFav = FavoriteStore.Toggle(cfg.Favorites, modId, title, author, type, thumbUrl);
        cfg.Save();
        if (!nowFav) Plugin.Log.Information($"[XmaBrowser] 已取消收藏 {modId}（{title}）");
    }

    /// <summary>切换关注的作者并落盘。</summary>
    private void ToggleFollowAuthor(string author)
    {
        author = (author ?? string.Empty).Trim();
        if (author.Length == 0) return;

        var cfg = _plugin.Configuration;
        var nowFollowing = FavoriteStore.ToggleAuthor(cfg.FollowedAuthors, author);
        cfg.Save();
        Plugin.Log.Information($"[XmaBrowser] {(nowFollowing ? "已关注" : "已取消关注")}作者 {author}");
    }

    /// <summary>按作者搜他的全部作品（顺带对比「自上次以来新增了哪几条」）。</summary>
    private void OpenAuthorWorks(string author)
    {
        author = (author ?? string.Empty).Trim();
        if (author.Length == 0) return;

        _form.Reset();
        _form.Author = author;
        _form.SortIndex = 0;                                  // 最新发布
        _form.NsfwMode = _plugin.Configuration.EffectiveNsfwMode;
        _form.OnlyDtCompatible = false;                       // 看作者作品时别把旧版本兼容的过滤掉

        _authorFollowPending = author;
        _focusBrowseTab = true;
        StartSearch(1);
    }

    /// <summary>收藏页点条目 → 切到浏览页并加载它的详情。</summary>
    private void OpenFavoriteDetail(FavoriteMod fav)
    {
        _selected = new SearchHit
        {
            ModId = fav.ModId,
            Title = fav.Title,
            Author = fav.Author,
            Type = fav.Type,
            ThumbUrl = fav.ThumbUrl,
        };
        _previewIndex = 0;
        LoadDetail(_selected);
        _focusBrowseTab = true;
    }

    /// <summary>搜索完成后：如果这次是「关注作者」触发的，就算新品并落盘。</summary>
    private void AfterSearchForFollowedAuthor()
    {
        if (_authorFollowPending.Length == 0) return;

        var authorName = _authorFollowPending;
        _authorFollowPending = string.Empty;
        // 同样别原地 Clear/Add：渲染线程在 Contains 它。换成新集合再整体换引用。
        _authorNewIds = [];

        var cfg = _plugin.Configuration;
        var idx = FavoriteStore.IndexOfAuthor(cfg.FollowedAuthors, authorName);
        if (idx < 0) return;

        var fresh = FavoriteStore.DiffAndRemember(cfg.FollowedAuthors[idx], _hits.Select(h => h.ModId));
        _authorNewIds = new HashSet<string>(fresh, StringComparer.Ordinal);
        cfg.Save();

        _status += fresh.Count > 0
            ? $"　·　关注的作者「{authorName}」有 {fresh.Count} 条新品"
            : $"　·　「{authorName}」没有新品";
    }

    // ==================================================================== 收藏 / 关注 界面

    /// <summary>详情页的收藏 / 关注 / 看他作品 三个按钮。</summary>
    private void DrawFavoriteControls(ModDetail d, SearchHit hit)
    {
        var cfg = _plugin.Configuration;
        var thumb = d.Images.Count > 0 ? d.Images[0] : hit.ThumbUrl;
        var title = d.Title.Length > 0 ? d.Title : hit.Title;
        var author = (d.Author.Length > 0 ? d.Author : hit.Author).Trim();

        var isFav = FavoriteStore.IsFavorite(cfg.Favorites, d.ModId);
        if (ImGui.Button(isFav ? "★ 已收藏" : "☆ 收藏"))
            ToggleFavorite(d.ModId, title, author, d.Type, thumb);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("收藏到本地（离线也能在「收藏·关注」页看到、直接安装）。");

        ImGui.SameLine();
        ImGui.BeginDisabled(author.Length == 0);
        var following = FavoriteStore.IsFollowing(cfg.FollowedAuthors, author);
        if (ImGui.Button(following ? $"★ 已关注 {author}" : $"☆ 关注作者 {author}"))
            ToggleFollowAuthor(author);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("关注作者（本地保存）。关注后可在「收藏·关注」页一键看他全部作品，\n并标出「你上次看过之后新增」的条目。");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(author.Length == 0);
        if (ImGui.Button("看他全部作品")) OpenAuthorWorks(author);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("按作者搜他的全部作品（最新发布在前）。");

        ImGui.Spacing();
    }

    /// <summary>「收藏·关注」页：左边收藏列表，右边关注的作者。</summary>
    private void DrawFavoritesTab()
    {
        var cfg = _plugin.Configuration;
        ImGui.TextColored(Dim, $"收藏 {cfg.Favorites.Count} 条（本地保存，离线可看）　关注的作者 {cfg.FollowedAuthors.Count} 位");
        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.BeginChild("xma-fav-left", new Vector2(MathF.Max(S(360f), avail * 0.62f), 0));
        DrawFavoriteList();
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("xma-fav-right", new Vector2(0, 0));
        DrawFollowedAuthors();
        ImGui.EndChild();
    }

    private void DrawFavoriteList()
    {
        var cfg = _plugin.Configuration;
        ImGui.TextColored(Blue, "收藏的 Mod");
        if (cfg.Favorites.Count == 0)
        {
            ImGui.TextColored(Dim, "还没有收藏。在「浏览」页点每行最左边的 ☆，或详情页的「收藏」。");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable("xma-fav-table", 5, flags))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("图", ImGuiTableColumnFlags.WidthFixed, S(70f));
            ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("作者", ImGuiTableColumnFlags.WidthFixed, S(110f));
            ImGui.TableSetupColumn("收藏于", ImGuiTableColumnFlags.WidthFixed, S(96f));
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, S(108f));
            ImGui.TableHeadersRow();

            // 复制一份再遍历：按钮里会改集合
            foreach (var fav in cfg.Favorites.ToList())
            {
                ImGui.TableNextRow();
                ImGui.PushID("fav-" + fav.ModId);

                ImGui.TableNextColumn();
                var wrap = _plugin.Thumbs.Get(fav.ThumbUrl)?.GetWrapOrDefault();
                if (wrap is not null) ImGui.Image(wrap.Handle, Fit(wrap.Size, S(64f), S(40f)));
                else ImGui.Dummy(new Vector2(S(64f), S(40f)));

                ImGui.TableNextColumn();
                var zh = cfg.ShowTranslatedTitles ? _plugin.Translator.Cached(fav.Title) : null;
                if (ImGui.Selectable(zh is { Length: > 0 } ? zh : fav.Title, false))
                    OpenFavoriteDetail(fav);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{fav.Title}\n{fav.Author} · {fav.Type}\n站点 id {fav.ModId}\n点击在浏览页打开详情");

                ImGui.TableNextColumn();
                if (ImGui.SmallButton(fav.Author.Length > 0 ? fav.Author : "(无)"))
                    OpenAuthorWorks(fav.Author);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("看他全部作品");

                ImGui.TableNextColumn();
                ImGui.TextColored(Dim, fav.AddedUtc.ToLocalTime().ToString("yyyy-MM-dd"));

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("详情")) OpenFavoriteDetail(fav);
                ImGui.SameLine();
                if (ImGui.SmallButton("取消"))
                {
                    FavoriteStore.Unfavorite(cfg.Favorites, fav.ModId);
                    cfg.Save();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("取消收藏");

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawFollowedAuthors()
    {
        var cfg = _plugin.Configuration;
        ImGui.TextColored(Blue, "关注的作者");
        if (cfg.FollowedAuthors.Count == 0)
        {
            ImGui.TextColored(Dim, "还没有关注作者。在详情页点「☆ 关注作者 xxx」。");
            return;
        }

        foreach (var a in cfg.FollowedAuthors.ToList())
        {
            ImGui.PushID("fa-" + a.Name);
            ImGui.TextUnformatted(a.Name);
            if (a.NewModIds.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Green, $"新品 {a.NewModIds.Count}");
            }

            var last = a.LastCheckedUtc.Year > 2000 ? a.LastCheckedUtc.ToLocalTime().ToString("MM-dd HH:mm") : "还没看过";
            ImGui.TextColored(Dim, $"关注于 {a.AddedUtc.ToLocalTime():yyyy-MM-dd}　上次查看 {last}　已记录 {a.SeenModIds.Count} 条");

            if (ImGui.SmallButton("看他作品")) OpenAuthorWorks(a.Name);
            ImGui.SameLine();
            if (ImGui.SmallButton("已看过")) FavoriteStore.MarkAuthorRead(a);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("清掉「新品」标记（不改变已记录的条目）");
            ImGui.SameLine();
            if (ImGui.SmallButton("取消关注"))
            {
                FavoriteStore.Unfollow(cfg.FollowedAuthors, a.Name);
                cfg.Save();
            }

            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void DrawPager(string id)
    {
        var totalPages = _totalMods > 0 ? (_totalMods + SitePageSize - 1) / SitePageSize : 0;

        ImGui.BeginDisabled(_busy || _page <= 1);
        if (ImGui.Button($"◀ 上一页##{id}")) StartSearch(_page - 1);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextColored(Dim, totalPages > 0
            ? $"从第 {_page} 页起　已加载 {_hits.Count} 条 / 站点共 {_totalMods} 条（每页 {SitePageSize} 条）"
            : $"从第 {_page} 页起　已加载 {_hits.Count} 条");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("「下一页 ▶」= 从这一页重新往下加载（不是往后翻一页）。\n" +
                             "正常用法是往下滚动，列表底部会自动加载更多。");

        ImGui.SameLine();
        ImGui.BeginDisabled(_busy || _hits.Count == 0);
        if (ImGui.Button($"下一页 ▶##{id}")) StartSearch(_page + 1);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextColored(Dim, "跳到");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(64);
        ImGui.InputInt($"##jump-{id}", ref _jumpPage);
        ImGui.SameLine();
        ImGui.BeginDisabled(_busy);
        if (ImGui.Button($"跳转##{id}"))
            StartSearch(Math.Max(1, totalPages > 0 ? Math.Min(_jumpPage, totalPages) : _jumpPage));
        ImGui.EndDisabled();
    }

    /// <summary>「翻译本页标题」+ 进度（批量翻译只走这一条，不会每行都发请求）。</summary>
    private void DrawTitleTranslateButton()
    {
        if (_titleBusy > 0)
        {
            ImGui.ProgressBar(_titleTotal > 0 ? _titleDone / (float)_titleTotal : 0f, new Vector2(S(150f), 0),
                              $"{_titleDone}/{_titleTotal}");
            ImGui.SameLine();
            if (ImGui.Button("停止##title-tr")) _titleCts?.Cancel();
            return;
        }

        ImGui.BeginDisabled(_hits.Count == 0 || _busy);
        if (ImGui.Button("翻译本页标题")) TranslatePageTitles();
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把本页的英文标题翻成中文并缓存，列表/宫格里会显示中文名。\n" +
                             $"当前后端：{_plugin.Translator.BackendName}\n" +
                             "详情描述：在右侧点「翻译标题+描述」，或开着自动翻译。");
    }

    /// <summary>
    /// **在插件里登录 XIVModArchive**（关于页 / 详情失败处的按钮触发）。
    /// 会弹出一个看得见的浏览器窗口（插件自己的 profile），用户在里面过一次 Discord 授权。
    /// </summary>
    private void StartLogin()
    {
        if (_loginBusy) return;
        _loginBusy = true;
        _loginMessage = "正在准备…（会弹出一个浏览器窗口，请在里面完成 Discord 登录）";

        var channel = _plugin.Channel;
        Task.Run(async () =>
        {
            try
            {
                var outcome = await channel.LoginAsync(
                    msg => _loginMessage = msg, CancellationToken.None).ConfigureAwait(false);
                _loginMessage = outcome.Message;
                _loginOk = outcome.Success;
                foreach (var line in outcome.Message.Split('\n'))
                    Plugin.Log.Information("[XmaBrowser][登录] " + line.TrimEnd());
            }
            catch (Exception e)
            {
                _loginMessage = $"登录失败：{e.GetType().Name} {e.Message}";
                _loginOk = false;
                Plugin.Log.Warning("[XmaBrowser][登录] " + _loginMessage);
            }
            finally
            {
                _loginBusy = false;
            }
        });
    }

    /// <summary>测试插件自带的登录态是否还有效（cookie 在 ≠ 站点还认）。</summary>
    private void StartLoginCheck()
    {
        if (_loginBusy) return;
        _loginBusy = true;
        _loginMessage = "正在用插件自带的身份打开站点核对…（约 5 秒）";

        var channel = _plugin.Channel;
        Task.Run(async () =>
        {
            try
            {
                _loginMessage = await channel.CheckLoginAsync(CancellationToken.None).ConfigureAwait(false);
                _loginOk = _loginMessage.StartsWith("已登录", StringComparison.Ordinal);
                _ownedLoginCheckedUtc = DateTime.MinValue;
            }
            catch (Exception e)
            {
                _loginMessage = $"检查失败：{e.GetType().Name} {e.Message}";
                _loginOk = false;
            }
            finally
            {
                _loginBusy = false;
            }
        });
    }

    /// <summary>NSFW 通道 / 登录态自检（关于页按钮触发）。</summary>
    private void StartNsfwSelfCheck()
    {
        if (_nsfwChecking) return;
        _nsfwChecking = true;
        _nsfwReport = "自检中…（正在起浏览器通道并取三个模式的页面，约 10-30 秒）";

        var channel = _plugin.Channel;
        var cfg = _plugin.Configuration;
        Task.Run(async () =>
        {
            try
            {
                _nsfwReport = await NsfwDiagnostics.RunAsync(channel, cfg, CancellationToken.None).ConfigureAwait(false);
                foreach (var line in _nsfwReport.Split('\n'))
                    Plugin.Log.Information("[XmaBrowser][NSFW自检] " + line.TrimEnd());
            }
            catch (Exception e)
            {
                _nsfwReport = $"自检失败：{e.GetType().Name} {e.Message}";
                Plugin.Log.Warning("[XmaBrowser][NSFW自检] " + _nsfwReport);
            }
            finally
            {
                _nsfwChecking = false;
            }
        });
    }

    /// <summary>设置页的「测试翻译」：翻一句固定的英文并回报结果/错误（带上代理现状，好定位）。</summary>
    private void TestTranslation()
    {
        _translateTestResult = "测试中…";
        var tr = _plugin.Translator;
        var proxy = ProxyInfo.Describe();
        Task.Run(async () =>
        {
            try
            {
                var zh = await tr.TestAsync(CancellationToken.None).ConfigureAwait(false);
                _translateTestResult = $"✓ {zh}　（{tr.BackendName}；代理：{proxy}）";
            }
            catch (Exception e)
            {
                _translateTestResult = $"✗ {e.Message}　（{tr.BackendName}；代理：{proxy}）";
            }
        });
    }

    private void TranslatePageTitles()
    {
        if (_titleBusy > 0) return;

        _titleCts?.Cancel();
        _titleCts = new CancellationTokenSource();
        var ct = _titleCts.Token;

        var titles = _hits.Select(h => h.Title)
                          .Where(t => t.Length > 0)
                          .Distinct(StringComparer.Ordinal)
                          .ToList();

        _titleTotal = titles.Count;
        _titleDone = 0;
        _titleBusy = 1;
        _translateError = string.Empty;

        var tr = _plugin.Translator;
        Task.Run(async () =>
        {
            foreach (var t in titles)
            {
                if (ct.IsCancellationRequested) break;
                if (tr.Cached(t) is not null)
                {
                    _titleDone++;
                    continue;
                }

                try
                {
                    await tr.TranslateAsync(t, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    // 同一类错误别刷屏：报一条就停
                    _translateError = e.Message;
                    Plugin.Log.Warning($"[XmaBrowser] 批量翻译标题失败：{e.Message}");
                    break;
                }

                _titleDone++;
            }

            _titleBusy = 0;
        }, ct);
    }

    /// <summary>当前该显示的条目（受「只看收藏」本地过滤影响）。</summary>
    private List<SearchHit> VisibleHits()
    {
        if (!_plugin.Configuration.FavoritesOnlyFilter) return _hits;
        var favs = _plugin.Configuration.Favorites;
        return _hits.Where(h => FavoriteStore.IsFavorite(favs, h.ModId)).ToList();
    }

    /// <summary>
    /// 0 条时的出口。**标题里没有匹配 ≠ 站点上没有** —— 实测把作者名「Helina's Bakery」
    /// 当关键词按标题搜是 0 条（那是作者名，不在任何标题里），改全文搜则有 15 条。
    /// 与其让用户自己猜，不如直接给一个一键改全文的按钮。
    /// </summary>
    private void DrawEmptyActions()
    {
        if (_busy || _form.TextAsFullText) return;
        if (_form.Text.Trim().Length == 0) return;

        ImGui.Spacing();
        if (ImGui.Button("改用全文搜索（连描述一起）##fulltext-suggest"))
        {
            _form.TextAsFullText = true;
            StartSearch(1);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("标题里没有匹配时，把关键词改成**全文检索**（标题 + 描述）再搜一次。\n" +
                             "注意：全文搜是松散匹配，常见词会带出很多相关但未必完全对得上的 Mod。");
    }

    private void DrawResultList()
    {
        var hits = VisibleHits();
        if (hits.Count == 0)
        {
            _listAtBottom = false;      // 空列表时别让哨兵的旧值触发自动加载
            if (_hits.Count > 0 && _plugin.Configuration.FavoritesOnlyFilter)
                ImGui.TextColored(Dim, $"本页 {_hits.Count} 条里没有已收藏的 —— 取消下面的「只看收藏」看全部。");
            else
                ImGui.TextColored(Dim, _busy ? "搜索中…" : "还没有结果。敲关键词回车，或直接点「搜索」看最新发布。");
            DrawEmptyActions();
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        // 表格给**显式高度**（把底部状态行留出来）：不然表格会把外层 child 撑出滚动条，
        // 于是「外层一条 + 表格一条」两个滚动条同时出现。
        var tableH = MathF.Max(S(120f), ImGui.GetContentRegionAvail().Y - FooterReserveH());
        if (ImGui.BeginTable("xma-hits", 7, flags, new Vector2(0f, tableH)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            // ⭐ 列宽必须把**单元格内边距**算进去：列宽度 ≠ 内容区宽度，
            //    内容区 = 列宽 − 2×CellPadding.X。原来 ★ 列 30px 里塞 26px 按钮、
            //    图列 74px 里塞 68px 图片，内容区其实分别只有 22 / 66 —— 都会溢出。
            //    现在直接按「控件宽度 + 内边距」派生列宽，不再手写会打架的数字。
            var cellPadX = ImGui.GetStyle().CellPadding.X * 2f;
            ImGui.TableSetupColumn("★", ImGuiTableColumnFlags.WidthFixed, GridLayout.ColumnWidthFor(S(26f), cellPadX));
            ImGui.TableSetupColumn("图", ImGuiTableColumnFlags.WidthFixed, GridLayout.ColumnWidthFor(S(68f), cellPadX));
            ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("作者", ImGuiTableColumnFlags.WidthFixed, S(116f));
            ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthFixed, S(92f));
            ImGui.TableSetupColumn("浏览", ImGuiTableColumnFlags.WidthFixed, S(62f));
            ImGui.TableSetupColumn("标记", ImGuiTableColumnFlags.WidthFixed, S(52f));
            ImGui.TableHeadersRow();

            foreach (var hit in hits)
            {
                ImGui.TableNextRow();
                ImGui.PushID(hit.ModId);

                ImGui.TableNextColumn();
                var isFav = FavoriteStore.IsFavorite(_plugin.Configuration.Favorites, hit.ModId);
                if (ImGui.Button(isFav ? "★" : "☆", new Vector2(S(26f), 0)))
                    ToggleFavorite(hit.ModId, hit.Title, hit.Author, hit.Type, hit.ThumbUrl);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(isFav ? "取消收藏" : "收藏（本地保存，离线可看）");

                ImGui.TableNextColumn();
                var wrap = _plugin.Thumbs.Get(hit.ThumbUrl)?.GetWrapOrDefault();
                if (wrap is not null) ImGui.Image(wrap.Handle, Fit(wrap.Size, S(68f), S(42f)));
                else ImGui.Dummy(new Vector2(S(68f), S(42f)));

                ImGui.TableNextColumn();
                var zhTitle = _plugin.Configuration.ShowTranslatedTitles ? _plugin.Translator.Cached(hit.Title) : null;
                var rowLabel = zhTitle is { Length: > 0 } ? zhTitle : hit.Title;
                if (ImGui.Selectable(rowLabel, _selected?.ModId == hit.ModId, ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selected = hit;
                    _previewIndex = 0;
                    LoadDetail(hit);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{hit.Title}\n{hit.Author} · {hit.Type}\n点击查看详情");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(hit.Author);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(hit.Type);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(hit.Views > 0 ? hit.Views.ToString() : "-");

                ImGui.TableNextColumn();
                if (_authorNewIds.Contains(hit.ModId)) ImGui.TextColored(Green, "新品");
                else if (hit.IsNew) ImGui.TextColored(Green, "NEW");
                else if (hit.IsUpdated) ImGui.TextColored(Yellow, "UPD");

                ImGui.PopID();
            }

            // 哨兵行：只为了判断「列表末尾有没有出现在视野里」。
            // 列表用的是 ScrollY 表格（滚动发生在表格内部），所以用表格内元素的可见性最直接。
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Dummy(new Vector2(1f, 1f));
            _listAtBottom = ImGui.IsItemVisible();

            ImGui.EndTable();
        }

        DrawStreamFooter();
        MaybeAutoLoadMore();
    }

    // ==================================================================== 宫格视图

    private void DrawResultGrid()
    {
        var hits = VisibleHits();
        if (hits.Count == 0)
        {
            _listAtBottom = false;
            if (_hits.Count > 0 && _plugin.Configuration.FavoritesOnlyFilter)
                ImGui.TextColored(Dim, $"本页 {_hits.Count} 条里没有已收藏的 —— 取消「只看收藏」看全部。");
            else
                ImGui.TextColored(Dim, _busy ? "搜索中…" : "还没有结果。敲关键词回车，或直接点「搜索」看最新发布。");
            DrawEmptyActions();
            return;
        }

        var cfg = _plugin.Configuration;
        // ⭐ 列宽 ≠ 可用宽度：单元格内边距会吃掉两侧（默认 CellPadding.X=4 → 两边 8px）。
        //    卡片实际能用的宽度 = 列宽 − 单元内边距；不扣这一下，卡片就会横向溢出列，又冒出滚动条。
        var cellPadX = ImGui.GetStyle().CellPadding.X * 2f;
        var colW = MathF.Max(S(110f), cfg.GridCardWidth * _s);
        var cardW = MathF.Max(S(72f), colW - cellPadX);
        var sp = ImGui.GetStyle().ItemSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var cols = Math.Max(1, (int)((avail + sp) / (colW + sp)));

        // 同列表视图：表格给显式高度，避免外层 child 再冒一条滚动条
        var tableH = MathF.Max(S(160f), ImGui.GetContentRegionAvail().Y - FooterReserveH());
        if (ImGui.BeginTable("xma-grid", cols, ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
                             new Vector2(0f, tableH)))
        {
            // 列宽钉死成 colW：靠 ImGui 自动算宽度时，单元格内边距一叠加就可能溢出
            // （溢出的那些像素正是「图片旁边多一条滚动条」的来源）。
            for (var c = 0; c < cols; c++)
                ImGui.TableSetupColumn($"g{c}", ImGuiTableColumnFlags.WidthFixed, colW);

            for (var i = 0; i < hits.Count; i++)
            {
                if (i % cols == 0) ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawGridCard(hits[i], cardW);
            }

            // 哨兵行（同列表视图）：判断末尾有没有进视野
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Dummy(new Vector2(1f, 1f));
            _listAtBottom = ImGui.IsItemVisible();

            ImGui.EndTable();
        }

        DrawStreamFooter();
        MaybeAutoLoadMore();
    }

    /// <summary>宫格里的一张卡：缩略图 + 标题（有中文就用中文）+ 类型/作者，整块可点。</summary>
    private void DrawGridCard(SearchHit hit, float cardW)
    {
        var cfg = _plugin.Configuration;
        var selected = _selected?.ModId == hit.ModId;

        // ⭐ 尺寸全部按**真实**度量算，不再手写常量：
        //   ① 图片可用宽 = 卡片宽 − 子窗口内边距 − 子窗口边框。
        //      原来的 cardW-12 比真实内部宽度大（默认内边距 8 + 边框 1，翻倍后约 18），
        //      图片横向溢出 → 每张卡都长出横向滚动条；
        //   ② 卡片高 = 内部留白 + 取景框 + 一行按钮 + 一行文字 + 富余。
        //      原来的 imgH+50 装不下，又长出纵向滚动条。
        var style = ImGui.GetStyle();
        var padX = GridLayout.InnerPadX(style.WindowPadding.X, style.ChildBorderSize);
        var padY = GridLayout.InnerPadY(style.WindowPadding.Y, style.ChildBorderSize);
        var box = GridLayout.ThumbBox(cardW, padX);
        var cardH = GridLayout.CardHeight(padY, box.Y,
                                         ImGui.GetFrameHeightWithSpacing(),      // ★/☆ 那一行
                                         ImGui.GetTextLineHeightWithSpacing());  // 「类型 · 作者」那一行

        ImGui.PushID("grid-" + hit.ModId);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, selected ? SelBg : CardBg);
        // ⭐ 显式禁止滚动：即使上面的估算差一两个像素，也只会轻微裁切，绝不会冒出滚动条。
        ImGui.BeginChild("c", new Vector2(cardW, cardH), true,
                         ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        var wrap = _plugin.Thumbs.Get(hit.ThumbUrl)?.GetWrapOrDefault();
        if (wrap is not null)
        {
            // 等比缩放到取景框内 + 水平居中；高度不足时补空 —— 保证每张卡高度完全一致。
            var draw = GridLayout.Fit(wrap.Size, box.X, box.Y);
            if (draw.X < box.X) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (box.X - draw.X) * 0.5f);
            ImGui.Image(wrap.Handle, draw);
            if (draw.Y < box.Y) ImGui.Dummy(new Vector2(1f, box.Y - draw.Y));
        }
        else ImGui.Dummy(new Vector2(box.X, box.Y));

        var zh = cfg.ShowTranslatedTitles ? _plugin.Translator.Cached(hit.Title) : null;
        var title = zh is { Length: > 0 } ? zh : hit.Title;
        var isFav = FavoriteStore.IsFavorite(cfg.Favorites, hit.ModId);
        if (ImGui.SmallButton(isFav ? "★" : "☆"))
            ToggleFavorite(hit.ModId, hit.Title, hit.Author, hit.Type, hit.ThumbUrl);
        var starHovered = ImGui.IsItemHovered();
        if (starHovered) ImGui.SetTooltip(isFav ? "取消收藏" : "收藏（本地保存，离线可看）");

        ImGui.SameLine();
        ImGui.TextUnformatted(Clip(title, 30));
        ImGui.TextColored(Dim, Clip($"{hit.Type} · {hit.Author}", 26));
        if (_authorNewIds.Contains(hit.ModId))
        {
            ImGui.SameLine();
            ImGui.TextColored(Green, "新品");
        }
        else if (hit.IsNew)
        {
            ImGui.SameLine();
            ImGui.TextColored(Green, "NEW");
        }
        else if (hit.IsUpdated)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, "UPD");
        }

        // 悬停/点击必须在子窗口内部判定（EndChild 之后 IsWindowHovered 指的是父窗口）
        // 鼠标在 ★ 上时不算「点卡片」，否则点收藏会顺手把详情也切了。
        var hovered = ImGui.IsWindowHovered() && !starHovered;
        var clicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopID();

        if (clicked)
        {
            _selected = hit;
            _previewIndex = 0;
            LoadDetail(hit);
        }

        if (hovered)
            ImGui.SetTooltip($"{hit.Title}\n{hit.Author} · {hit.Type} · 浏览 {hit.Views}\n单击查看详情，右下角「安装到 Penumbra」安装");
    }

    /// <summary>字节数显示成 MB（站点图片动辄一两 MB，用 KB 反而看不清量级）。</summary>
    private static string MB(long bytes) => bytes <= 0 ? "?" : $"{bytes / 1048576.0:0.00} MB";

    /// <summary>在资源管理器里打开一个文件夹（诊断日志用）。</summary>
    private static void OpenFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch { /* 打不开就算了，不影响功能 */ }
    }

    /// <summary>
    /// 列表/宫格底部的状态行（原来这里是分页条）。
    /// 流式加载的状态 + **手动兜底**：自动触发依赖 ImGui 的可见性判断，
    /// 而我这边没有游戏、跑不了真界面，所以必须保留一个按钮 —— 万一自动没生效也能继续。
    /// </summary>
    private void DrawStreamFooter()
    {
        ImGui.Spacing();
        ImGui.Separator();

        if (_streamBusy)
        {
            ImGui.TextColored(Yellow, $"正在加载第 {_stream.NextPage} 页…");
            ImGui.SameLine();
            ImGui.TextColored(Dim, $"已加载 {_hits.Count} 条" +
                                   (_stream.Total > 0 ? $" / 站点共 {_stream.Total} 条" : string.Empty));
            return;
        }

        if (_streamError.Length > 0)
        {
            ImGui.TextColored(Red, "继续加载失败：" + _streamError);
            ImGui.SameLine();
            if (ImGui.Button("重试##stream-retry")) StartSearch(_stream.NextPage, append: true);
            return;
        }

        if (_stream.End)
        {
            ImGui.TextColored(Dim, $"到底了：{_stream.EndReason}（共 {_hits.Count} 条）");
            return;
        }

        if (ImGui.Button($"加载更多（下一页 {_stream.NextPage}）##stream-more"))
            StartSearch(_stream.NextPage, append: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("向下滚动到底会自动加载。\n这个按钮是兜底：万一自动触发没生效，你还能手动继续。");
        ImGui.SameLine();
        ImGui.TextColored(Dim, $"已加载 {_hits.Count} 条" +
                               (_stream.Total > 0 ? $" / 站点共 {_stream.Total} 条" : string.Empty));
    }

    /// <summary>一次滚动最多自动加载几页（防止可见性判断失效时把站点翻穿）。</summary>
    private const int StreamAutoLimit = 5;

    /// <summary>
    /// 触底自动加载。
    ///
    /// ⚠ 诚实说明：这段依赖 ImGui 的「可见性」在 ScrollY 表格里的行为，
    /// 而我这边没有游戏、跑不了真界面。所以加了两道保险，保证**不会失控**：
    ///   ① 手势预算：一次滚动最多自动加载 <see cref="StreamAutoLimit"/> 页；
    ///      任何滚轮/滚动动作都会重置预算。即使可见性判断一直为真，最多多下几页就停。
    ///   ② 底部永远有「加载更多」按钮，自动失效也能用。
    /// </summary>
    private void MaybeAutoLoadMore()
    {
        if (!_listAtBottom || _streamBusy || _busy || _stream.End || _streamError.Length > 0) return;
        if (_streamAutoFired >= StreamAutoLimit) return;

        _streamAutoFired++;
        StartSearch(_stream.NextPage, append: true);
    }

    /// <summary>按字符数截断（中英混排够用，不追求像素精确）。</summary>
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// 画一条加载进度条（分段模型给的进度 + 一行说明）。
    /// 只解析 DOM 之外的事情都跟用户感知无关，所以这里只呈现「还要多久」。
    /// </summary>
    private void DrawLoadProgress(LoadProgress p, float width = -1f)
    {
        if (!p.Running && !p.Failed) return;

        if (!p.Failed)
        {
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Blue);
            ImGui.ProgressBar(p.Fraction, new Vector2(width, S(14f)), $"{p.Fraction * 100f:0}%");
            ImGui.PopStyleColor();
        }
        ImGui.TextColored(p.Failed ? Red : Dim, p.Describe());
    }

    private void DrawDetailPane()
    {
        var hit = _selected;
        if (hit is null)
        {
            ImGui.TextColored(Dim, "← 左边选一条 Mod，这里显示详情与「安装到 Penumbra」");
            return;
        }

        // ⭐ 「已经加载好但不显示」的结构性修复：
        //    原来只要 _detailBusy 为真就一律不画数据 —— 哪怕 _detail 早就填好了。
        //    现在**有数据就画数据**：忙标志只在「数据还没到」时才有资格占屏。
        if (_detailBusy && _detail is null)
        {
            ImGui.TextColored(Yellow, $"正在读取 {hit.ModId} 的详情…");

            // 先把列表里那张缩略图顶上，给个视觉锚点 —— 别让人对着空白等
            if (hit.ThumbUrl.Length > 0)
            {
                var tw = _plugin.Thumbs.Get(hit.ThumbUrl)?.GetWrapOrDefault();
                if (tw is not null) ImGui.Image(tw.Handle, Fit(tw.Size, ImGui.GetContentRegionAvail().X, S(150f)));
            }
            DrawLoadProgress(_detailProgress, ImGui.GetContentRegionAvail().X);
            return;
        }

        var d = _detail;
        if (d is null)
        {
            ImGui.TextColored(Red, _error.Length > 0 ? _error : "详情读取失败。");
            if (_detailProgress.Failed) ImGui.TextColored(Dim, _detailProgress.Describe());
            ImGui.Spacing();

            // 读不到详情时，最可能的原因就是「需要登录态」—— 直接把登录入口放在这儿，
            // 不要让人跑到「关于」页去猜。
            if (_plugin.Channel.Enabled && !OwnedLoginCached())
            {
                ImGui.TextColored(Yellow, "这条内容可能需要登录态。在插件里登录一次，NSFW 的详情与下载就能看了：");
                ImGui.BeginDisabled(_loginBusy);
                if (ImGui.Button(_loginBusy ? "登录中…（请在弹出的浏览器窗口里完成 Discord 授权）"
                                            : "登录 XIVModArchive"))
                    StartLogin();
                ImGui.EndDisabled();
                if (_loginMessage.Length > 0)
                    ImGui.TextColored(_loginOk ? Green : Dim, _loginMessage);
            }
            else
            {
                ImGui.TextColored(Dim, "可以：换一条 / 稍后重试 / 到「关于」页点「NSFW 通道自检」看具体原因。");
            }
            return;
        }

        // 自动翻译（按 ModId 只触发一次，失败不重试，避免每帧重发请求）
        if (_plugin.Configuration.AutoTranslateDetail && !_translating &&
            !string.Equals(_translatedForKey, d.ModId, StringComparison.Ordinal))
        {
            _translatedForKey = d.ModId;
            StartTranslateDetail(d);
        }

        var zhTitle = _translatedTitle.Length > 0 ? _translatedTitle : _plugin.Translator.Cached(d.Title);
        var showTitle = !_showOriginal && zhTitle is { Length: > 0 } ? zhTitle : (d.Title.Length > 0 ? d.Title : hit.Title);
        ImGui.TextWrapped(showTitle);
        if (!_showOriginal && zhTitle is { Length: > 0 } && !string.Equals(zhTitle, d.Title, StringComparison.Ordinal))
            ImGui.TextColored(Dim, "原文：" + d.Title);

        ImGui.TextColored(Dim, $"作者 {d.Author}　类型 {d.Type}　站点 id {d.ModId}");
        ImGui.TextColored(Dim, $"最后更新 {Fmt(d.LastUpdateLocal)}　发布 {Fmt(d.ReleaseLocal)}");

        // 原页面链接：**所有 mod 都有**（站点 URL 可以直接拼出来，不依赖抓取、不会缺）。
        // 用途：分享给别人、或者你在浏览器里已登录时自己下载（NSFW 的站内 mod 尤其有用）。
        if (ImGui.SmallButton("打开原页面")) OpenExternal(d.PageUrl);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("在默认浏览器里打开这条 Mod 的站点原页面\n" + d.PageUrl);
        ImGui.SameLine();
        if (ImGui.SmallButton("复制原链接")) ImGui.SetClipboardText(d.PageUrl);
        if (d.AuthorPageUrl.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("作者页")) OpenExternal(d.AuthorPageUrl);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("看这位作者的全部作品\n" + d.AuthorPageUrl);
        }
        ImGui.TextWrapped("原页面：" + d.PageUrl);
        DrawFavoriteControls(d, hit);
        DrawTranslateControls(d);
        if (_detailViaChannel)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, $"　🌐 浏览器通道（{_detailChannelReason}）");
        }
        ImGui.Separator();

        // 这条 mod 没有预览图 → 进度到此为止（否则进度条会一直挂在「加载预览图」）
        if (d.Images.Count == 0 && _detailProgress.Running) _detailProgress.Complete();

        // ⭐ 预览图救济：一张图第一次没成时**自动带通道授权重来一次**（每个 mod 只自动来一次）。
        // 为什么需要：这张图很可能先被**列表**请求过 —— 列表只想直连（不许占通道，一页 15 张），
        // 而 NSFW 的图直连必然 403，于是被记成失败；等详情面板来要的时候已经在冷却期里，
        // 结果就是「进度条一直转、图出不来」，可切走再切回来又好了（主人实测到的现象）。
        if (d.Images.Count > 0)
        {
            var previewFirst = d.Images[0];
            if (!string.Equals(_previewRetryMod, d.ModId, StringComparison.Ordinal))
            {
                _previewRetryMod = d.ModId;
                _previewAutoRetried = 0;
                _previewTextureReloads = 0;
                _previewReadySince = DateTime.UtcNow;
            }

            var st0 = _plugin.Thumbs.Status(previewFirst);
            if (_previewAutoRetried == 0 && st0.StartsWith("失败", StringComparison.Ordinal))
            {
                _previewAutoRetried = 1;
                _plugin.Jobs.Note($"预览图第一次没成（{st0}）→ 自动带通道授权重试一次");
                _plugin.Thumbs.Retry(previewFirst, allowChannel: true);
            }
        }

        if (d.Images.Count > 0)
        {
            var idx = ((_previewIndex % d.Images.Count) + d.Images.Count) % d.Images.Count;
            var w = ImGui.GetContentRegionAvail().X;
            // 预览图：NSFW 的图直连会被 403 —— 允许它回退到浏览器通道。
            // 只对「用户正在看的这一张」开放通道（列表里 15 张缩略图不能一起涌进通道）。
            var previewUrl = d.Images[idx];
            var wrap = _plugin.Thumbs.Get(previewUrl, allowChannel: true)?.GetWrapOrDefault();
            if (wrap is not null)
            {
                ImGui.Image(wrap.Handle, Fit(wrap.Size, w, 230f));
                if (_detailProgress.Running) _detailProgress.Complete();
            }
            else
            {
                var st = _plugin.Thumbs.Status(previewUrl);
                var failed = st.StartsWith("失败", StringComparison.Ordinal);
                var cachedButNotReady = !failed && st.StartsWith("已缓存", StringComparison.Ordinal);

                // 全尺寸预览图是**整张大图**（本机实测一张 1496873 字节 ≈ 1.5 MB）。
                // 下载那几秒只按「阶段」画进度的话条子纹丝不动 —— 用户看到的就是「一直在加载、没成功」。
                var frac = _plugin.Thumbs.ImageProgress(previewUrl, out var got, out var total);
                var line = "全尺寸预览图：" + (st.Length > 0 ? st : "准备中") +
                           (frac >= 0f ? $"　{MB(got)} / {MB(total)}" : string.Empty);
                ImGui.TextColored(failed ? Dim : Yellow, line);

                // 先拿列表里那张小缩略图顶上（一般已经在缓存里，秒出）—— 别让人对着空白等
                var tw = hit.ThumbUrl.Length > 0 && !string.Equals(hit.ThumbUrl, previewUrl, StringComparison.Ordinal)
                    ? _plugin.Thumbs.Get(hit.ThumbUrl)?.GetWrapOrDefault()
                    : null;
                if (tw is not null) ImGui.Image(tw.Handle, Fit(tw.Size, w, S(150f)));

                if (failed)
                {
                    if (ImGui.Button("重试##prev-retry")) _plugin.Thumbs.Retry(previewUrl, allowChannel: true);
                    ImGui.SameLine();
                    ImGui.TextColored(Dim, "（还不行的话，到「关于」页复制诊断日志发我）");
                    if (_detailProgress.Running) _detailProgress.Fail(st);
                }
                else if (cachedButNotReady)
                {
                    // ⭐ 主人猜的就是这个状态：**文件其实已经在本地了，只是没显示出来**。
                    //    处理：自动重挂一次纹理（**不重下**那 1.4 MB）；等了 8 秒还没就绪
                    //    就明确报出来 + 给按钮，绝不留一个转不完的进度条。
                    var waitedMs = (long)(DateTime.UtcNow - _previewReadySince).TotalMilliseconds;
                    if (_previewTextureReloads == 0 && waitedMs > 1200)
                    {
                        _previewTextureReloads = 1;
                        _plugin.Jobs.Note($"预览图文件已在本地但纹理没就绪 → 自动重挂纹理：{previewUrl}");
                        _plugin.Thumbs.ReloadTexture(previewUrl);
                    }

                    if (_previewTextureReloads > 0 && waitedMs > 8000)
                    {
                        ImGui.TextColored(Dim, "预览图：文件已在本地，但纹理一直没就绪（可以重挂试试）");
                        ImGui.SameLine();
                        if (ImGui.Button("重新加载##prev-reload")) _plugin.Thumbs.ReloadTexture(previewUrl);
                        if (_detailProgress.Running) _detailProgress.Fail("纹理没就绪（文件已在本地）");
                    }
                    else
                    {
                        ImGui.TextColored(Dim, "预览图：已下载到本地，正在准备显示…");
                        if (_detailProgress.Running) _detailProgress.SetNote("准备显示预览图");
                    }
                }
                else if (frac >= 0f)
                {
                    // 真正的字节进度
                    ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Blue);
                    ImGui.ProgressBar(frac, new Vector2(w, S(14f)), $"{frac * 100f:0}%");
                    ImGui.PopStyleColor();
                }
                else if (_detailProgress.Running)
                {
                    DrawLoadProgress(_detailProgress, w);
                }
                else
                {
                    ImGui.Dummy(new Vector2(w, S(20f)));
                }
            }

            if (d.Images.Count > 1)
            {
                if (ImGui.Button("‹ 上一张")) _previewIndex = idx - 1;
                ImGui.SameLine();
                ImGui.TextColored(Dim, $"{idx + 1} / {d.Images.Count}");
                ImGui.SameLine();
                if (ImGui.Button("下一张 ›")) _previewIndex = idx + 1;
            }
            ImGui.Separator();
        }

        if (d.Tags.Count > 0)
            ImGui.TextWrapped("标签：" + string.Join("  ", d.Tags.Select(t => "#" + t)));
        if (d.Affects.Length > 0)
            ImGui.TextWrapped("影响/替换：" + d.Affects);
        if (d.Races.Length > 0)
            ImGui.TextWrapped("种族：" + d.Races);
        if (d.Genders.Length > 0)
            ImGui.TextWrapped("性别：" + d.Genders);

        ImGui.Spacing();
        ImGui.TextColored(Dim, "描述");
        ImGui.BeginChild("xma-desc", new Vector2(0, S(130f)), true);
        var zhDesc = _translatedDesc.Length > 0 ? _translatedDesc : _plugin.Translator.Cached(d.Description);
        if (!_showOriginal && zhDesc is { Length: > 0 } && !string.Equals(zhDesc, d.Description, StringComparison.Ordinal))
        {
            ImGui.TextColored(Blue, zhDesc);
            ImGui.Spacing();
            ImGui.TextColored(Dim, "—— 以下为原文 ——");
        }
        ImGui.TextWrapped(d.Description.Length > 0 ? d.Description : "（站点上没有描述）");
        ImGui.EndChild();

        ImGui.Separator();

        if (d.IsExternalLink)
        {
            // 主人定的策略：这种「作者把文件放在别处」的 mod，插件**不代下**，
            // 只把跳转链接给你，点一下用系统默认浏览器打开 —— 你自己下载、自己装。
            // 理由：那边通常要登录 / 选文件 / 过验证码，插件硬猜既不可靠也不安全。
            if (!string.Equals(_loggedExternalMod, d.ModId, StringComparison.Ordinal))
            {
                _loggedExternalMod = d.ModId;
                _plugin.Jobs.Note($"外链判定：{d.ModId}「{d.Title}」→ 外部站点 {d.DownloadHost}（不给一键安装）");
            }

            ImGui.TextColored(Yellow, "这条 Mod 的文件在作者的外部站点，插件不代下。");
            ImGui.TextWrapped("点下面用浏览器打开，下载好以后自己装进 Penumbra（那边一般要登录或选文件，所以交给你）。");
            ImGui.TextColored(Dim, $"跳转目标：{d.DownloadHost}");
            ImGui.TextWrapped(d.AbsoluteDownloadUrl);

            if (ImGui.Button("用默认浏览器打开", new Vector2(S(200f), 0)))
                OpenExternal(d.AbsoluteDownloadUrl);
            ImGui.SameLine();
            if (ImGui.Button("复制跳转链接")) ImGui.SetClipboardText(d.AbsoluteDownloadUrl);

            // 万一「外链」这个判据哪天判错了（例如站点把下载改走 CDN 域名，
            // 那会把所有 mod 都判成外链）—— 留一条不显眼的自救入口。
            ImGui.Spacing();
            if (_plugin.Penumbra.Available)
            {
                if (ImGui.SmallButton("判错了？仍然尝试直链下载并安装"))
                {
                    _plugin.Jobs.Note($"用户手动要求：对判定为外链的 {d.ModId} 仍然尝试直链下载：{d.AbsoluteDownloadUrl}");
                    var coverFallback = d.Images.Count > 0 ? d.Images[0] : hit.ThumbUrl;
                    _plugin.Jobs.Enqueue(d, coverFallback);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("正常情况用不到：外部站点的下载大多要登录或选文件，直连拿不到。\n" +
                                     "留这个口子是为了防「站点换了下载域名 → 所有 mod 被误判成外链」。");
            }
            else
            {
                ImGui.TextColored(Dim, "（Penumbra 未连接，无法尝试安装）");
            }
            return;
        }

        ImGui.TextWrapped("下载：" + d.FileNameHint);
        var ok = _plugin.Penumbra.Available;

        // 「装前看包」勾选的内嵌包：null = 全装；空列表 = 一个都没勾 → 停用安装
        var selectedPkgs = SelectedPackagesFor(d);
        var noneChecked = selectedPkgs is { Count: 0 };

        ImGui.BeginDisabled(!ok || noneChecked);
        if (ImGui.Button("安装到 Penumbra", new Vector2(S(180f), 0)))
        {
            var cover = d.Images.Count > 0 ? d.Images[0] : hit.ThumbUrl;
            _plugin.Jobs.Enqueue(d, cover, selectedPkgs);
        }
        ImGui.EndDisabled();

        if (noneChecked)
        {
            ImGui.SameLine();
            ImGui.TextColored(Red, "上面一个包都没勾");
        }
        else if (selectedPkgs is { Count: > 0 })
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, $"只装勾选的 {selectedPkgs.Count} 个包");
        }

        if (!ok)
        {
            ImGui.SameLine();
            ImGui.TextColored(Red, "Penumbra 未连接");
        }
        ImGui.SameLine();
        if (ImGui.Button("复制下载链接")) ImGui.SetClipboardText(d.AbsoluteDownloadUrl);

        DrawPackageProbe(d);
    }

    /// <summary>
    /// 装前看包：先看清「多大、里面是什么、装进去几个 mod」，再决定装不装。
    /// **不下载整包** —— 只发几次 Range 请求（实测约 0.25 MB / 2~4 秒，
    /// 对比下整包 48 MB / 95 秒）。
    /// </summary>
    private void DrawPackageProbe(ModDetail d)
    {
        // 换了一条 Mod → 上一条的探测结果作废（绝不能把 A 的包内容显示在 B 下面）
        if (!string.Equals(_pkgProbeModId, d.ModId, StringComparison.Ordinal))
        {
            _pkgProbe = null;
            _pkgProbeModId = string.Empty;
            _pkgSel.Clear();          // 勾选只对当前这条 Mod 有效
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(_pkgProbeBusy);
        if (ImGui.Button(_pkgProbeBusy ? "探测中…" : "查看包内内容")) StartPackageProbe(d);
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("不下载整包：只发几次 Range 请求（约 0.25 MB），读出真实大小与包内清单。\n" +
                             ".zip / .pmp / .ttmp2 能列出内容；.7z / .rar 只能看大小。");

        var p = _pkgProbe;
        if (p is null) return;

        ImGui.Spacing();
        ImGui.TextColored(Blue, $"包内内容 · {p.FileName}");
        ImGui.TextWrapped($"真实大小：{(p.SizeKnown ? p.SizeText : "未知")}");

        if (p.Summary is { } s)
        {
            ImGui.TextWrapped($"共 {s.EntryCount} 个条目，解压后约 {s.UncompressedText}");

            if (s.ModCount > 0)
            {
                ImGui.TextWrapped($"内含 {s.ModCount} 个 Mod —— 勾选要装哪些（默认全选）：");
                foreach (var name in s.InnerModPacks)
                {
                    if (!_pkgSel.ContainsKey(name)) _pkgSel[name] = true;      // 默认全选
                    var on = _pkgSel[name];
                    if (ImGui.Checkbox(name + "##pkgsel-" + name, ref on)) _pkgSel[name] = on;
                }

                var chosen = s.InnerModPacks.Count(n => _pkgSel.TryGetValue(n, out var v) && v);
                if (chosen == 0)
                    ImGui.TextColored(Red, "一个都没勾 → 安装按钮已停用（要么至少勾一个，要么点「全选」）");
                else if (chosen == s.ModCount)
                    ImGui.TextColored(Dim, $"已勾选全部 {chosen} 个 —— 「安装到 Penumbra」会把这 {chosen} 个都装上");
                else
                    ImGui.TextColored(Yellow, $"已勾选 {chosen} / {s.ModCount} —— 只会装勾选的这些");

                if (ImGui.SmallButton("全选##pkgsel-all")) { foreach (var n in s.InnerModPacks) _pkgSel[n] = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("全不选##pkgsel-none")) { foreach (var n in s.InnerModPacks) _pkgSel[n] = false; }
            }
            else
            {
                ImGui.TextColored(Dim, "（这个包本身就是一条 Mod，没有嵌套）");
            }

            if (s.TopPaths.Count > 0)
                ImGui.TextColored(Dim, "顶层：" + string.Join("　", s.TopPaths.Take(8)));
        }
        else if (p.Note.Length > 0)
        {
            ImGui.TextColored(Dim, p.Note);
        }
    }

    /// <summary>
    /// 当前这条 Mod 该装哪些内嵌包。
    ///   null = 全装（还没看过包内容，或勾选了全部 —— 保持老行为）
    ///   {}   = 一个都没勾 → 调用方应停用安装按钮
    ///   非空 = 只装这些（按文件名匹配）
    /// </summary>
    private List<string>? SelectedPackagesFor(ModDetail d)
    {
        if (!string.Equals(_pkgProbeModId, d.ModId, StringComparison.Ordinal)) return null;
        if (_pkgProbe?.Summary is not { ModCount: > 0 } s) return null;

        var chosen = s.InnerModPacks.Where(n => _pkgSel.TryGetValue(n, out var on) && on).ToList();
        return chosen.Count == s.InnerModPacks.Count ? null : chosen;
    }

    /// <summary>
    /// 装前看包：**整条链路（Range 请求 + 解析）都在后台**，绝不在渲染线程上跑。
    /// 失败绝不影响安装 —— 只把原因照实写出来。
    /// </summary>
    private void StartPackageProbe(ModDetail d)
    {
        if (_pkgProbeBusy) return;

        _pkgProbe = null;
        _pkgProbeBusy = true;
        _pkgProbeModId = d.ModId;

        var url = d.AbsoluteDownloadUrl;
        var modId = d.ModId;
        var client = _plugin.Site;

        _ = Task.Run(async () =>
        {
            PackageInspection result;
            try
            {
                result = await client.InspectPackageAsync(url).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                result = PackageInspection.Failed(string.Empty, string.Empty, "探测失败：" + e.Message);
            }

            _pkgProbeBusy = false;

            // 只有用户还停在这条 Mod 上才把结果放上去（换走了就丢掉，别串台）
            if (string.Equals(_pkgProbeModId, modId, StringComparison.Ordinal))
                _pkgProbe = result;      // 引用赋值是原子的：渲染线程只会看到「旧」或「新」
        });
    }

    // ==================================================================== 翻译

    /// <summary>详情里的翻译按钮 + 状态 + 原文切换。</summary>
    private void DrawTranslateControls(ModDetail d)
    {
        ImGui.BeginDisabled(_translating);
        if (ImGui.Button(_translating ? "翻译中…" : "翻译标题+描述"))
        {
            _translatedForKey = d.ModId;
            StartTranslateDetail(d);
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(_showOriginal ? "显示译文" : "显示原文"))
            _showOriginal = !_showOriginal;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"当前后端：{_plugin.Translator.BackendName}（设置 → 翻译 可切换）");

        if (_translateError.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextWrapped(_translateError);
        }
        else if (!_plugin.Translator.IsConfigured)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, "翻译后端未配置（设置 → 翻译）");
        }
    }

    /// <summary>翻译当前详情的标题 + 描述（后台跑，结果进缓存）。</summary>
    private void StartTranslateDetail(ModDetail d)
    {
        if (_translating) return;

        _transCts?.Cancel();
        _transCts = new CancellationTokenSource();
        var ct = _transCts.Token;

        _translating = true;
        _translateError = string.Empty;
        _translatedTitle = string.Empty;
        _translatedDesc = string.Empty;

        var tr = _plugin.Translator;
        var title = d.Title.Length > 0 ? d.Title : (_selected?.Title ?? string.Empty);
        var desc = d.Description;

        Task.Run(async () =>
        {
            try
            {
                if (title.Length > 0) _translatedTitle = await tr.TranslateAsync(title, ct).ConfigureAwait(false);
                if (desc.Length > 0) _translatedDesc = await tr.TranslateAsync(desc, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 切走了就算了
            }
            catch (Exception e)
            {
                _translateError = e.Message;
                Plugin.Log.Warning($"[XmaBrowser] 详情翻译失败：{e.Message}");
            }
            finally
            {
                _translating = false;
            }
        }, ct);
    }

    // ==================================================================== 队列

    private void DrawQueueTab()
    {
        var jobs = _plugin.Jobs.Snapshot();
        var running = jobs.Count(j => !j.Finished);
        ImGui.TextColored(Dim, $"共 {jobs.Count} 条，进行中 {running} 条（串行执行：一次只下一个/装一个）");
        ImGui.SameLine();
        if (ImGui.Button("清掉已完成")) _plugin.Jobs.ClearFinished();

        ImGui.Separator();

        if (jobs.Count == 0)
        {
            ImGui.TextColored(Dim, "队列是空的。到「浏览」页选一条 Mod → 「安装到 Penumbra」。");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable("xma-jobs", 5, flags))
        {
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, S(72f));
            ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("进度", ImGuiTableColumnFlags.WidthFixed, S(150f));
            ImGui.TableSetupColumn("信息", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, S(70f));
            ImGui.TableHeadersRow();

            foreach (var job in jobs)
            {
                ImGui.TableNextRow();
                ImGui.PushID(job.Id);

                ImGui.TableNextColumn();
                var color = job.State switch
                {
                    JobState.Done => Green,
                    JobState.Error => Red,
                    JobState.Cancelled => Dim,
                    _ => Yellow,
                };
                ImGui.TextColored(color, StateLabel(job.State));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(job.Title);
                if (job.Author.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Dim, "— " + job.Author);
                }

                ImGui.TableNextColumn();
                if (job.State == JobState.Downloading)
                    ImGui.ProgressBar(job.Percent / 100f, new Vector2(-1, 0), $"{job.Percent}%");
                else if (job.State == JobState.Done)
                    ImGui.TextColored(Green, "100%");
                else
                    ImGui.TextColored(Dim, "—");

                ImGui.TableNextColumn();
                if (job.Error.Length > 0) ImGui.TextWrapped(job.Error);
                else ImGui.TextWrapped(job.Message);

                ImGui.TableNextColumn();
                if (!job.Finished)
                {
                    if (ImGui.Button("取消")) _plugin.Jobs.Cancel(job.Id);
                }
                else
                {
                    if (job.UsedBrowserChannel)
                    {
                        ImGui.TextColored(Yellow, "🌐");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("这次下载走了浏览器通道：" + job.BrowserReason);
                        ImGui.SameLine();
                    }

                    // 本机 Penumbra 的 SetModPath 不可用 → 把「分组目标路径」备好，配合 Penumbra 的 Move Mod 用
                    if (job.GroupTargetPath.Length > 0)
                    {
                        if (ImGui.Button("复制分组路径##grp"))
                            ImGui.SetClipboardText(job.GroupTargetPath);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("复制：" + job.GroupTargetPath +
                                             "\n然后到 Penumbra 里右键这条 Mod → Move Mod → 粘贴 → 回车");

                        ImGui.SameLine();
                        if (ImGui.Button("聚焦##focus"))
                        {
                            var dir = job.ModDirName.Length > 0 ? job.ModDirName : job.Title;
                            _plugin.Penumbra.OpenModsTab(dir, job.Title);
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("在 Penumbra 里打开这条 Mod（若你这版实现了 OpenMainWindow）");
                    }
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private static string StateLabel(JobState s) => s switch
    {
        JobState.Queued => "排队",
        JobState.Downloading => "下载",
        JobState.Extracting => "解包",
        JobState.Installing => "安装",
        JobState.Enabling => "启用",
        JobState.Done => "完成",
        JobState.Error => "失败",
        JobState.Cancelled => "取消",
        _ => s.ToString(),
    };

    // ==================================================================== 设置

    private void DrawSettingsTab()
    {
        var cfg = _plugin.Configuration;
        var changed = false;

        ImGui.BeginChild("xma-settings-scroll");

        ImGui.TextColored(Blue, "界面");
        ImGui.Separator();

        // 界面缩放：字体与控件一起缩。用整数百分比，比 0.85× 这种写法直观。
        var scalePct = (int)MathF.Round(UiScale.Clamp(cfg.UiScale) * 100f);
        ImGui.TextUnformatted("界面缩放");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240);
        if (ImGui.SliderInt("##ui-scale", ref scalePct, (int)(UiScale.Min * 100), (int)(UiScale.Max * 100), "%d%%"))
        {
            cfg.UiScale = UiScale.Clamp(scalePct / 100f);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("字体与控件一起缩，**只影响本插件窗口**（改完立刻生效）。\n" +
                             "想改整个游戏/Dalamud 的界面缩放，用游戏里的 /xlsettings → 界面。\n" +
                             (_fontScaleWorks
                                 ? "（本机实测：本版 ImGui 支持字体缩放。）"
                                 : "⚠ 本机实测：这版 ImGui 的 SetWindowFontScale 无效，只能缩间距与控件，字体大小不变。"));

        ImGui.SameLine();
        ImGui.TextColored(Dim, $"当前 {UiScale.Describe(cfg.UiScale)}");
        ImGui.SameLine();
        if (ImGui.Button("恢复 100%"))
        {
            cfg.UiScale = UiScale.Default;
            changed = true;
        }

        // 浏览页左右比例
        var splitPct = (int)MathF.Round(UiScale.ClampSplit(cfg.BrowseSplit) * 100f);
        ImGui.TextUnformatted("浏览分栏");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240);
        if (ImGui.SliderInt("##ui-split", ref splitPct, (int)(UiScale.MinSplit * 100), (int)(UiScale.MaxSplit * 100), "左 %d%%"))
        {
            cfg.BrowseSplit = UiScale.ClampSplit(splitPct / 100f);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("浏览页里左边「搜索结果」占的宽度比例，右边是详情。调小 = 详情更宽。");
        ImGui.SameLine();
        ImGui.TextColored(Dim, $"（{
            ((int)MathF.Round(UiScale.ClampSplit(cfg.BrowseSplit) * 100f))}% 给结果列表）");

        ImGui.Spacing();
        ImGui.TextColored(Blue, "下载与安装");
        ImGui.Separator();
        var enable = cfg.EnableOnInstall;
        if (ImGui.Checkbox("装完自动在当前集合里启用", ref enable)) { cfg.EnableOnInstall = enable; changed = true; }
        var redraw = cfg.RedrawOnInstall;
        if (ImGui.Checkbox("装完触发重绘（立刻在游戏里可见）", ref redraw)) { cfg.RedrawOnInstall = redraw; changed = true; }
        ImGui.SameLine();
        var cleanup = cfg.CleanupAfterInstall;
        if (ImGui.Checkbox("装完删除下载的包", ref cleanup)) { cfg.CleanupAfterInstall = cleanup; changed = true; }

        var delay = cfg.RequestDelayMs;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("请求间隔（毫秒，礼貌抓站）", ref delay))
        {
            cfg.RequestDelayMs = Math.Clamp(delay, 0, 5000);
            changed = true;
        }
        ImGui.SameLine();
        ImGui.TextColored(Dim, "缓存目录：" + _plugin.Site.CacheDirectory);
        ImGui.SameLine();
        if (ImGui.Button("打开##cache")) OpenExternal(_plugin.Site.CacheDirectory);

        ImGui.Spacing();
        ImGui.TextColored(Blue, "浏览视图");
        ImGui.Separator();
        var gw = cfg.GridCardWidth;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("宫格卡片宽度", ref gw, 110, 260))
        {
            cfg.GridCardWidth = gw;
            changed = true;
        }
        ImGui.SameLine();
        ImGui.TextColored(Dim, "（浏览页右上可切「列表 / 宫格」）");

        ImGui.Spacing();
        ImGui.TextColored(Blue, "翻译（英文 Mod 标题/描述 → 中文）");
        ImGui.Separator();

        var backends = new[]
        {
            "谷歌翻译（免 Key，默认）",
            "有道公共接口（免 Key）",
            "腾讯交互翻译（免 Key）",
            "OpenAI 兼容（DeepSeek 等，需 Key）",
        };
        var currentBackend = (_plugin.Configuration.TranslateBackend ?? "google").Trim().ToLowerInvariant();
        var backendIdx = currentBackend switch
        {
            "youdao" => 1,
            "tencent" => 2,
            "openai" => 3,
            _ => 0,
        };
        ImGui.SetNextItemWidth(340);
        if (ImGui.Combo("后端##tr-backend", ref backendIdx, backends, backends.Length))
        {
            cfg.TranslateBackend = backendIdx switch
            {
                1 => "youdao",
                2 => "tencent",
                3 => "openai",
                _ => "google",
            };
            changed = true;
        }

        ImGui.TextUnformatted("代理　");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(340);
        var proxy = cfg.TranslateProxy;
        if (ImGui.InputTextWithHint("##tr-proxy", "留空 = 用系统代理（谷歌必需；如 http://127.0.0.1:7877）",
                                    ref proxy, 200))
        {
            cfg.TranslateProxy = proxy;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("插件跑在 .NET 里，**默认就走 Windows 系统代理** —— 系统代理开着的话这里留空即可。\n" +
                             "只有本机没系统代理、但你自己有本地代理客户端时，才需要手填（如 http://127.0.0.1:7890）。");

        // 一眼看清「插件实际会走哪儿」：这是看 .NET 的解析结果，不是猜的
        var detected = ProxyInfo.Describe();
        ImGui.SameLine();
        ImGui.TextColored(Dim, $"当前实际走：{detected}");

        if (cfg.TranslateProxy.Trim().Length == 0 && !ProxyInfo.HasProxy && backendIdx == 0)
        {
            ImGui.TextColored(Yellow, "⚠ 没检测到代理，谷歌在国内通常连不上 —— " +
                                      "请把翻墙软件的「系统代理」打开，或在这里手填它的本地地址；" +
                                      "也可以先换「有道」后端（国内直连可用）。");
        }

        if (backendIdx == 3)
        {
            ImGui.TextUnformatted("BaseUrl");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(340);
            var baseUrl = cfg.TranslateBaseUrl;
            if (ImGui.InputTextWithHint("##tr-base", "https://api.deepseek.com/v1", ref baseUrl, 200))
            {
                cfg.TranslateBaseUrl = baseUrl;
                changed = true;
            }

            ImGui.TextUnformatted("模型　");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200);
            var model = cfg.TranslateModel;
            if (ImGui.InputText("##tr-model", ref model, 80))
            {
                cfg.TranslateModel = model;
                changed = true;
            }

            ImGui.TextUnformatted("API Key");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(340);
            var key = cfg.TranslateApiKey;
            if (ImGui.InputText("##tr-key", ref key, 300, ImGuiInputTextFlags.Password))
            {
                cfg.TranslateApiKey = key;
                changed = true;
            }
            ImGui.SameLine();
            ImGui.TextColored(Dim, "只存在本地插件配置里");
        }
        else
        {
            ImGui.TextColored(Dim, "无需配置：直接用公共接口（长文本会自动按句分段），译文缓存在插件目录。");
        }

        var autoTr = cfg.AutoTranslateDetail;
        if (ImGui.Checkbox("打开详情时自动翻译标题+描述", ref autoTr))
        {
            cfg.AutoTranslateDetail = autoTr;
            changed = true;
        }
        ImGui.SameLine();
        var showTr = cfg.ShowTranslatedTitles;
        if (ImGui.Checkbox("列表/宫格显示中文标题（只用缓存）", ref showTr))
        {
            cfg.ShowTranslatedTitles = showTr;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("测试翻译")) TestTranslation();
        if (_translateTestResult.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(_translateTestResult.StartsWith("✓", StringComparison.Ordinal) ? Green : Red,
                              _translateTestResult);
        }

        ImGui.Spacing();
        ImGui.TextColored(Blue, "装进文件夹（Penumbra 的列表里就是可折叠的组）");
        ImGui.Separator();
        var group = cfg.GroupIntoFolders;
        if (ImGui.Checkbox("安装时把 Mod 归入文件夹", ref group)) { cfg.GroupIntoFolders = group; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Penumbra 的 mod 列表是文件系统树，文件夹就是折叠组。\n" +
                             "插件装完后会调 SetModPath 把 Mod 移进下面模板算出的文件夹。");

        ImGui.BeginDisabled(!cfg.GroupIntoFolders);
        ImGui.TextUnformatted("路径模板");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(300);
        var tpl = cfg.GroupTemplate;
        if (ImGui.InputTextWithHint("##grp-tpl", "如 {prefix}/{type}", ref tpl, 160))
        {
            cfg.GroupTemplate = tpl;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("可用占位符：{prefix} 前缀 / {type} 站点类型（Gear、Hair…）/ {author} 作者 / {modid} / {name}\n" +
                             "用 / 分层；留空 = 不分组。");

        ImGui.TextUnformatted("前缀");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        var pfx = cfg.GroupPrefix;
        if (ImGui.InputTextWithHint("##grp-pfx", "XMA", ref pfx, 40))
        {
            cfg.GroupPrefix = pfx;
            changed = true;
        }

        ImGui.TextColored(Dim, "效果预览：" + GroupPath.Describe(cfg.GroupTemplate, cfg.GroupPrefix));
        ImGui.TextColored(Dim, "提示：Penumbra 自带「默认导入文件夹」（右键文件夹 → Set Folder as...），" +
                               "那是它自己的全局设置；插件这个只管自己装进来的 Mod，可以按类型/作者分得更细。");
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextColored(Blue, "封面（Penumbra 里显示 Mod 预览图）");
        ImGui.Separator();
        var draw = cfg.DrawCovers;
        if (ImGui.Checkbox("在 Penumbra 的 mod 面板里画封面", ref draw)) { cfg.DrawCovers = draw; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("挂 Penumbra 的 PreSettingsTabBarDraw 事件自己画（Heliosphere 用的同一机制）。");

        var coverSize = cfg.CoverSize;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("封面高度（面板宽度 ×）", ref coverSize, 0.15f, 0.8f, "%.2f"))
        {
            cfg.CoverSize = Math.Clamp(coverSize, 0.1f, 1f);
            changed = true;
        }

        var fetchCover = cfg.FetchCoverOnInstall;
        if (ImGui.Checkbox("安装时自动下载封面", ref fetchCover)) { cfg.FetchCoverOnInstall = fetchCover; changed = true; }
        ImGui.SameLine();
        var injectCover = cfg.UseCoverInPackage;
        if (ImGui.Checkbox("把封面塞进安装包一起交付（最可靠）", ref injectCover))
        {
            cfg.UseCoverInPackage = injectCover;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Penumbra 只接受包文件；包内没有图 → 装出来就没有图。");

        if (_plugin.CoverDrawer.Active)
            ImGui.TextColored(Green, "封面绘制事件：已挂上");
        else
            ImGui.TextColored(Red, "封面绘制事件：没挂上（Penumbra 没装或版本不匹配）");

        ImGui.Spacing();
        ImGui.TextColored(Blue, "浏览器通道（NSFW / 被 Cloudflare 拦的内容）");
        ImGui.Separator();
        var chan = cfg.EnableBrowserChannel;
        if (ImGui.Checkbox("启用浏览器通道（NSFW / 需登录的内容）", ref chan))
        {
            cfg.EnableBrowserChannel = chan;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("需要登录态 / 被 Cloudflare 拦的内容交给浏览器去取。\n" +
                             "身份来源优先级：① 你在插件里登录过的（关于页「登录 XIVModArchive」）" +
                             "→ ② 本机已登录浏览器的 Cookies 副本。\n" +
                             "推荐用 ①：一次登录长期有效，而且插件能明确告诉你登没登。（详见「关于」页）");

        var autoPick = cfg.AutoPickProfile;
        if (ImGui.Checkbox("自动挑选可用浏览器", ref autoPick)) { cfg.AutoPickProfile = autoPick; changed = true; }

        var fallback = cfg.AutoFallbackToBrowser;
        if (ImGui.Checkbox("直连被拒时自动改用浏览器通道", ref fallback))
        {
            cfg.AutoFallbackToBrowser = fallback;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("站点做人机校验（Just a moment）时，**公开内容**也会直连被拒 ——\n" +
                             "开着：自动交给浏览器通道完成（更慢但能成）；\n" +
                             "关掉：直接报错，不做降级。");

        if (_browserReport.Length == 0)
        {
            _browserReport = string.Join("　", BrowserChannel.Detect().Select(c =>
                $"{c.Label}={(c.LooksUsable ? "可用" : (File.Exists(c.Exe) ? "已装无登录态" : "未装"))}"));
        }
        ImGui.TextColored(Dim, "检测到：" + _browserReport);

        var exe = cfg.BrowserExe;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##brow", "浏览器 exe 路径（留空 = 自动探测）", ref exe, 512))
        {
            cfg.BrowserExe = exe;
            changed = true;
        }

        var profileDir = cfg.ChannelProfileDir;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##ownprof", "插件自带的登录 profile 目录（留空 = 默认）", ref profileDir, 512))
        {
            cfg.ChannelProfileDir = profileDir;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("插件自己的浏览器 profile：在「关于」页点「登录 XIVModArchive」时登录态就存在这里，\n" +
                             "之后通道一直用它（这个目录**永不自动删除**）。\n" +
                             $"默认：{BrowserChannel.DefaultOwnedProfileDir}");

        var blockAds = cfg.ChannelBlockAds;
        if (ImGui.Checkbox("通道内屏蔽广告/统计脚本（加载更快）", ref blockAds))
        {
            cfg.ChannelBlockAds = blockAds;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("插件只读页面的 DOM（卡片/标题/下载链接），广告与统计脚本对它没用处，\n" +
                             "屏蔽掉能少发一堆请求、省点流量。\n" +
                             "⚠ 实测对「页面就绪时间」没有明显影响（5594 ms vs 5785 ms，噪声内）——\n" +
                             "所以别指望它提速；真正让加载变快的是浏览器预热与缓存。");

        ImGui.SameLine();
        var idle = cfg.ChannelIdleMinutes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("通道空闲关闭（分钟）", ref idle))
        {
            cfg.ChannelIdleMinutes = Math.Clamp(idle, 0, 120);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("headless 浏览器常驻要占上百 MB 内存，闲着就关掉；代价是下次用要重新启动（实测约 1 秒）。\n" +
                             "想让点开详情最快，把它设大一点（例如 15）—— 0 表示一直留着。");

        var timeout = cfg.BrowserTimeoutSec;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("通道超时（秒）", ref timeout))
        {
            cfg.BrowserTimeoutSec = Math.Clamp(timeout, 30, 900);
            changed = true;
        }
        ImGui.SameLine();
        var keepProfile = cfg.CleanupBorrowedProfile;
        if (ImGui.Checkbox("用完删除临时 profile（内含 cookie 副本）", ref keepProfile))
        {
            cfg.CleanupBorrowedProfile = keepProfile;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextColored(Blue, "缓存（占用与清理）");
        ImGui.Separator();

        var mb = cfg.ThumbCacheMb;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("预览图上限（MB）", ref mb))
        {
            cfg.ThumbCacheMb = Math.Clamp(mb, 16, 4096);
            changed = true;
        }

        ImGui.SameLine();
        var coverMb = cfg.CoverCacheMb;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("封面上限（MB）", ref coverMb))
        {
            cfg.CoverCacheMb = Math.Clamp(coverMb, 8, 2048);
            changed = true;
        }

        ImGui.SameLine();
        var trMb = cfg.TranslateCacheMb;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("翻译上限（MB）", ref trMb))
        {
            cfg.TranslateCacheMb = Math.Clamp(trMb, 8, 2048);
            changed = true;
        }

        var leftover = cfg.TempLeftoverHours;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("临时残留保留（小时）", ref leftover))
        {
            cfg.TempLeftoverHours = Math.Clamp(leftover, 1, 720);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("下载的安装包与解压目录：正常装完或失败都会立刻删；\n" +
                             "这条只兜底「插件崩在中间」这类残留。");

        // 手动点「立即清理」时，扫盘与删除都放后台（不然点一下要等它扫完才响应）
        ImGui.Spacing();
        if (ImGui.Button("立即清理缓存")) MaintainCaches(force: true);
        ImGui.SameLine();
        if (ImGui.Button("打开缓存文件夹")) OpenFolder(_plugin.CacheDir);
        ImGui.SameLine();
        ImGui.TextColored(Dim, _cacheMaintaining ? "正在清理…" : "搜索完会顺手自动跑一次维护");

        // 占用情况（每 3 秒算一次，且**在后台线程扫盘** —— 缓存目录可能有上千个文件，
        // 递归扫盘放在渲染线程上会直接掉帧）
        if (!_cacheReportBusy && (DateTime.UtcNow - _cacheReportUtc).TotalSeconds > 3)
        {
            _cacheReportUtc = DateTime.UtcNow;
            _cacheReportBusy = true;
            _ = Task.Run(() =>
            {
                try { _cacheReport = BuildCacheReport(); }
                catch { /* 忽略：显示不了占用不影响功能 */ }
                finally { _cacheReportBusy = false; }
            });
        }
        if (_cacheReport.Length > 0)
        {
            ImGui.BeginChild("xma-cache-report", new Vector2(0, S(96f)), true);
            ImGui.TextColored(Dim, _cacheReport);
            ImGui.EndChild();
        }

        if (_cacheMessage.Length > 0)
            ImGui.TextColored(Blue, _cacheMessage);

        ImGui.EndChild();

        if (changed) cfg.Save();
    }

    /// <summary>
    /// 缓存维护：各目录按上限淘汰 + 清掉临时目录里的陈年残留。
    /// 五分钟内重复调用会跳过（除非 force），因为要扫盘。
    /// </summary>
    private void MaintainCaches(bool force = false)
    {
        if (_cacheMaintaining) return;
        if (!force && (DateTime.UtcNow - _lastCacheMaintainUtc).TotalMinutes < 5) return;
        _lastCacheMaintainUtc = DateTime.UtcNow;
        _cacheMaintaining = true;

        // ⭐ 整块搬到后台线程：这是**递归扫盘 + 删文件 + 写日志文件**，
        // 放在渲染线程上点一下「立即清理」就会先卡住界面。
        var cfg = _plugin.Configuration;
        var dirs = new List<(string Name, string Path, long LimitBytes)>
        {
            ("预览图", _plugin.ThumbsDir, Math.Max(16, cfg.ThumbCacheMb) * 1048576L),
            ("封面图", _plugin.CoversDir, Math.Max(8, cfg.CoverCacheMb) * 1048576L),
            ("翻译缓存", _plugin.TranslateDir, Math.Max(8, cfg.TranslateCacheMb) * 1048576L),
        };
        var tempDir = _plugin.TempDir;
        var age = TimeSpan.FromHours(Math.Max(1, cfg.TempLeftoverHours));
        var jobs = _plugin.Jobs;

        _ = Task.Run(() =>
        {
            try
            {
                var report = CacheMaintenance.Maintain(dirs, tempDir, age, DateTime.UtcNow);
                if (report != "缓存都在上限内，没动")
                {
                    jobs.Note("缓存维护：" + report);
                    _cacheMessage = $"{DateTime.Now:HH:mm:ss} {report}";
                }
                else if (force)
                {
                    _cacheMessage = $"{DateTime.Now:HH:mm:ss} 缓存都在上限内，没动";
                }
                _cacheReportUtc = DateTime.MinValue;   // 让占用显示马上刷新
            }
            catch (Exception e)
            {
                _cacheMessage = $"缓存维护出错：{e.GetType().Name}";
            }
            finally
            {
                _cacheMaintaining = false;
            }
        });
    }

    /// <summary>各缓存目录的占用与上限（给设置页显示 —— 别让用户去猜占了多少）。</summary>
    private string BuildCacheReport()
    {
        var cfg = _plugin.Configuration;
        var rows = new List<CacheUsage>
        {
            MakeUsage("预览图  ", _plugin.ThumbsDir, Math.Max(16, cfg.ThumbCacheMb) * 1048576L),
            MakeUsage("封面图  ", _plugin.CoversDir, Math.Max(8, cfg.CoverCacheMb) * 1048576L),
            MakeUsage("翻译缓存", _plugin.TranslateDir, Math.Max(8, cfg.TranslateCacheMb) * 1048576L),
            MakeUsage("临时目录", _plugin.TempDir, 0),
        };
        var total = rows.Sum(r => r.Bytes);
        return string.Join("\n", rows.Select(r => "  " + r.Describe())) +
               $"\n  合计：{CacheUsage.Mb(total)}　（缓存根目录：{_plugin.CacheDir}）";
    }

    private static CacheUsage MakeUsage(string name, string dir, long limitBytes)
    {
        var (bytes, files) = CacheMaintenance.Measure(dir);
        return new CacheUsage(name, dir, bytes, files, limitBytes);
    }

    // ==================================================================== 关于

    private void DrawAboutTab()
    {
        ImGui.TextColored(Blue, $"XMA Browser v{Plugin.PluginVersion}");
        ImGui.TextWrapped("在游戏里逛 XIVModArchive，点一下把 Mod 装进 Penumbra 并即时生效。");
        ImGui.Separator();

        ImGui.TextColored(Dim, "公开内容直连站点；NSFW / 被 Cloudflare 拦的内容走浏览器通道。");
        ImGui.TextColored(Dim, "插件只读站点公开数据，不上传任何信息；借来的临时 profile 默认用完即删，插件自带的登录 profile 只在你点「清除」时删除。");
        ImGui.Spacing();

        ImGui.TextUnformatted("仓库 / 更新源");
        ImGui.SameLine();
        if (ImGui.Button("打开仓库")) OpenExternal(RepoUrl);
        ImGui.SameLine();
        if (ImGui.Button("复制插件源地址")) ImGui.SetClipboardText(SourceUrl);
        ImGui.TextColored(Dim, SourceUrl);

        ImGui.Spacing();
        ImGui.TextUnformatted("Penumbra");
        ImGui.SameLine();
        ImGui.TextColored(_plugin.Penumbra.Available ? Green : Red,
            _plugin.Penumbra.Available
                ? $"API {_plugin.Penumbra.ApiMajor}.{_plugin.Penumbra.ApiMinor}"
                : "未连接");
        if (_plugin.Penumbra.Available)
        {
            ImGui.TextColored(Dim, "Mod 目录：" + _plugin.Penumbra.ModDirectory);
            if (ImGui.Button("打开 Mod 目录")) OpenExternal(_plugin.Penumbra.ModDirectory);
            ImGui.SameLine();
            if (ImGui.Button("重绘一次")) _plugin.Penumbra.Redraw();
        }

        ImGui.Spacing();
        ImGui.TextColored(Dim, "已知限制：外链 Mod（Patreon 等）没有直链，只能「在浏览器打开」；.7z/.rar 暂不支持。");

        ImGui.Spacing();
        ImGui.TextColored(Blue, "诊断：Mod 路径与分组");
        ImGui.Separator();

        if (_plugin.Jobs.SetModPathNoOp)
        {
            ImGui.TextColored(Red, "⚠ 本机 Penumbra 的 SetModPath 不生效（返回 Success 但路径不变）—— 自动归组已停用。");
            ImGui.TextWrapped("可用替代：在 Penumbra 的 mod 列表里右键任意文件夹 → Set Folder as... → " +
                              "Set Default Import Folder，之后新装的 Mod 会被 Penumbra 自己放进那个文件夹；" +
                              "或右键某条 Mod → Move Mod，手动把它的路径写成 XMA/Gear/名字。");
        }
        ImGui.BeginDisabled(_diagBusy);
        if (ImGui.Button("读取所有 Mod 的真实路径（并写日志）"))
        {
            // ⚠ Penumbra 的 IPC 会一直等到框架线程回话。原来这是在渲染线程上直接调的 ——
            // 点一下就会冻住界面（Mod 多的时候尤其明显）。现在整块丢后台。
            _diagBusy = true;
            _diagResult = "读取中…（Penumbra IPC 可能要几秒）";
            var pen = _plugin.Penumbra;
            _ = Task.Run(() =>
            {
                try
                {
                    var mods = pen.Mods().ToList();
                    var lines = pen.DumpPaths();
                    foreach (var line in lines)
                        Plugin.Log.Information("[XmaBrowser][诊断] " + line);
                    // 整体换引用：渲染线程可能正在遍历它们，别原地改
                    _diagMods = mods;
                    _diagLines = lines;
                    _diagIndex = 0;
                    _diagResult = $"读到 {lines.Count} 条（已写入日志）";
                }
                catch (Exception e)
                {
                    _diagResult = $"❌ 读取失败：{e.GetType().Name} {e.Message}";
                }
                finally { _diagBusy = false; }
            });
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("用 Penumbra 的 GetModPath 读回每个 Mod 的真实路径（含文件夹），\n" +
                             "并写入 /xllog —— 分组没生效时看这个最直接。\n" +
                             "（读取在后台线程跑，界面不会卡）");

        if (_diagLines.Count > 0)
        {
            ImGui.BeginChild("xma-diag-list", new Vector2(0, 120), true);
            foreach (var line in _diagLines)
                ImGui.TextWrapped(line);
            ImGui.EndChild();

            var labels = _diagMods.Select(kv => kv.Key + "  →  " + kv.Value).ToArray();
            if (labels.Length > 0)
            {
                _diagIndex = Math.Clamp(_diagIndex, 0, labels.Length - 1);
                ImGui.SetNextItemWidth(420);
                ImGui.Combo("##xma-diag-pick", ref _diagIndex, labels, labels.Length);
                ImGui.SameLine();

                var pick = Math.Clamp(_diagIndex, 0, _diagMods.Count - 1);
                var selDir = _diagMods[pick].Key;
                var selName = _diagMods[pick].Value;

                ImGui.BeginDisabled(_diagBusy);
                if (ImGui.Button("按模板归入分组"))
                {
                    _diagBusy = true;
                    _diagResult = "归组中…";
                    var cfg2 = _plugin.Configuration;
                    // 手动归组时没有站点类型信息（只有安装时才带）→ {type} 会算成 Other
                    var folder = GroupPath.BuildFolder(cfg2.GroupTemplate, cfg2.GroupPrefix, string.Empty,
                                                       string.Empty, string.Empty, selName);
                    var jobs = _plugin.Jobs;
                    var pen2 = _plugin.Penumbra;
                    var d = selDir;
                    var n = selName;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var (ok, msg, report) = jobs.GroupOneDetailed(d, n, folder);
                            var lines = pen2.DumpPaths();
                            foreach (var line in lines)
                                Plugin.Log.Information("[XmaBrowser][诊断] " + line);
                            _diagReport = report;
                            _diagLines = lines;      // 整体换引用
                            _diagResult = (ok ? "✅ " : "❌ ") + msg;
                        }
                        catch (Exception e)
                        {
                            _diagResult = $"❌ {e.GetType().Name}: {e.Message}";
                        }
                        finally { _diagBusy = false; }
                    });
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("对选中的 Mod 走一遍归组（会先读回真实路径核对）。\n" +
                                     "注意：手动归组没有站点类型，模板里的 {type} 会是 Other。\n" +
                                     "（归组在后台线程跑，界面不会卡）");

                ImGui.SameLine();
                ImGui.BeginDisabled(_diagBusy);
                if (ImGui.Button("归组自检（IPC 探针 + 移动/还原实测）"))
                {
                    _diagBusy = true;
                    _diagResult = "自检中…（要试多种路径写法，可能十几秒）";
                    var jobs2 = _plugin.Jobs;
                    var pen3 = _plugin.Penumbra;
                    var d2 = selDir;
                    var n2 = selName;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var rep = jobs2.RunPathWriteExperiment(d2, n2);
                            var lines = pen3.DumpPaths();
                            foreach (var line in rep.Split('\n'))
                                Plugin.Log.Information("[XmaBrowser][深入自检] " + line.TrimEnd());
                            _diagReport = rep;
                            _diagLines = lines;      // 整体换引用
                            _diagResult = "已跑完路径写法实验，见下方报告";
                        }
                        catch (Exception e)
                        {
                            _diagResult = $"❌ {e.GetType().Name}: {e.Message}";
                        }
                        finally { _diagBusy = false; }
                    });
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("在选中的 Mod 上依次试各种路径写法（改名 / 单段 / 正斜杠 / 反斜杠 / 全路径），\n" +
                                     "每种都读回真实结果，最后尽量还原。\n" +
                                     "用来判断「SetModPath 回 Success 却不动」到底是哪种写法才能生效。");
            }
        }

        if (_diagResult.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(_diagResult.StartsWith("❌") ? Red : Dim, _diagResult);
        }

        if (_diagReport.Length > 0)
        {
            ImGui.Spacing();
            if (ImGui.Button("复制自检报告"))
                ImGui.SetClipboardText(_diagReport);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("把这份报告发给开发者即可定位（含每一步的调用结果与读回路径）。");
            ImGui.SameLine();
            ImGui.TextColored(Dim, "自检报告（复制给我）");
            ImGui.BeginChild("xma-diag-report", new Vector2(0, S(130f)), true);
            ImGui.TextWrapped(_diagReport);
            ImGui.EndChild();
        }

        // ---- 站点登录（插件自己的浏览器 profile）----
        // 站点对 NSFW 的门控是「客户端身份」，而登录只有 Discord OAuth 一条路（没有密码表单）。
        // 原来只能复制本机浏览器的 cookies —— 借不到（没登录过 / cookie 锁着 / 过期了）就完全没辙，
        // 而且插件无从知道到底借到了什么。现在插件自带一个 profile：在这里登录一次，长期复用。
        ImGui.Spacing();
        ImGui.TextColored(Blue, "站点登录（NSFW 内容需要）");
        ImGui.Separator();

        if (OwnedLoginCached())
            ImGui.TextColored(Green, "✔ 插件里已有 XIVModArchive 登录态，NSFW 内容直接用这个身份");
        else
            ImGui.TextColored(Yellow, "✖ 插件里还没登录 —— NSFW 的详情与下载会取不到");

        ImGui.BeginDisabled(_loginBusy);
        if (ImGui.Button(_loginBusy ? "登录中…（请在浏览器窗口里完成 Discord 授权）"
                                    : "登录 XIVModArchive（会弹出浏览器窗口）"))
            StartLogin();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("弹出的是**插件自己的**浏览器窗口，profile 存在插件目录里，不碰你平时用的浏览器。\n" +
                             "在里面点「Log In with Discord」完成授权即可 —— 插件会自动检测到并关掉窗口。");
        ImGui.SameLine();
        ImGui.BeginDisabled(_loginBusy);
        if (ImGui.Button("测试登录状态")) StartLoginCheck();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("用插件自带的身份打开站点核对一次。\n" +
                             "cookie 还在 ≠ 登录还有效（过期 / 被站点踢了 / Discord 撤销授权时，cookie 都还在），\n" +
                             "所以要以站点给的页面为准。");
        ImGui.SameLine();
        if (ImGui.Button("清除插件里的登录"))
        {
            _loginMessage = _plugin.Channel.ClearLogin();
            _loginOk = false;
            _ownedLoginCheckedUtc = DateTime.MinValue;   // 强制重算
        }
        if (_loginMessage.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(_loginOk ? Green : Dim, _loginMessage);
        }
        ImGui.TextColored(Dim, "插件 profile：" + _plugin.Channel.OwnedProfileDir);

        // ---- NSFW 通道 / 站点登录态自检 ----
        // 站点只在**登录态**下才把 NSFW 并进「混合」列表（实测 直连 53281 / 登录态 61787 = 53281+8506），
        // 而通道是复制本机浏览器的 cookies 起的 —— 挑错浏览器就等于没登录。
        ImGui.Spacing();
        ImGui.TextColored(Blue, "NSFW / 站点登录态");
        ImGui.Separator();
        ImGui.BeginDisabled(_nsfwChecking);
        if (ImGui.Button(_nsfwChecking ? "自检中…（要起浏览器通道，约 10-30 秒）" : "NSFW 通道自检（登录态）"))
            StartNsfwSelfCheck();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("查：通道借的是哪个浏览器 / profile、它有没有登录 XIVModArchive、\n" +
                             "三态各拿到多少条 —— 用来定位「选了混合却看不到 NSFW」。");
        if (_nsfwReport.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("复制##nsfw-copy"))
                ImGui.SetClipboardText(_nsfwReport);
            ImGui.SameLine();
            ImGui.TextColored(Dim, "把这份发我");
            ImGui.BeginChild("xma-nsfw-report", new Vector2(0, S(170f)), true);
            ImGui.TextWrapped(_nsfwReport);
            ImGui.EndChild();
        }

        // ---- 插件自己的诊断日志（主人找不到 dalamud.log，所以这里直接给）----
        var diagLog = _plugin.Jobs.DiagLog;
        ImGui.Spacing();
        if (ImGui.Button("复制诊断日志"))
            ImGui.SetClipboardText(string.Join("\n", diagLog));
        ImGui.SameLine();
        if (ImGui.Button("复制全部（报告+日志）"))
            ImGui.SetClipboardText(_diagReport + "\n---- 日志 ----\n" + string.Join("\n", diagLog));
        ImGui.SameLine();
        if (ImGui.Button("打开日志文件夹"))
        {
            var dir = _plugin.Jobs.DiagDirectory;
            if (dir.Length > 0) OpenFolder(dir);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("诊断日志同时写在文件里（" + _plugin.Jobs.DiagFile + "）——\n" +
                             "远程排查时直接把那个文件发我就行，里面有每一步的耗时。");
        ImGui.SameLine();
        ImGui.TextColored(Dim, $"诊断日志（最近 {diagLog.Count} 条；同样的内容也在 /xllog 里）");

        ImGui.BeginChild("xma-diag-log", new Vector2(0, 150), true);
        if (diagLog.Count == 0)
        {
            ImGui.TextColored(Dim, "（还没有记录。装一个 Mod、或点上面的「按模板归入分组」，这里就会写进来）");
        }
        else
        {
            foreach (var line in diagLog)
                ImGui.TextUnformatted(line);

            if (diagLog.Count != _diagLogSeen)
            {
                ImGui.SetScrollHereY(1f);
                _diagLogSeen = diagLog.Count;
            }
        }
        ImGui.EndChild();
    }

    // ==================================================================== 高级搜索

    private void DrawAdvancedPopup()
    {
        if (_openAdvancedRequest)
        {
            ImGui.OpenPopup(AdvancedId);
            _openAdvancedRequest = false;
        }

        ImGui.SetNextWindowSize(new Vector2(S(520f), 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(AdvancedId, ref _advancedOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        // 弹窗是**独立窗口**：style 间距随主窗口一起缩了，但字体缩放是「每窗口」的，要再设一次。
        if (MathF.Abs(_s - 1f) > 0.001f) ImGui.SetWindowFontScale(_s);

        ImGui.TextColored(Dim, "留空 = 不看这个条件（除「关键词」外都是站点的精确检索项）");
        ImGui.Separator();

        // ---- 关键词的检索语义（对应主工具条那个「关键词 / Mod 名」框）----
        ImGui.Checkbox("关键词按全文搜（连描述一起）", ref _form.TextAsFullText);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "默认**关** = 按标题搜（站点的 name=：标题里每个词都要命中）。\n" +
                "粘一个 Mod 全名进去就能精确找到它 —— 推荐保持关闭。\n\n" +
                "勾上 = 按全文搜（站点的 basic_text=：标题 + 描述，松散匹配）。\n" +
                "常见词会把你要的 Mod 淹掉：实测搜「Bellissente, a makeup for Aerin」全名，\n" +
                "全文搜返回一堆别的 makeup 且翻几页都找不到它，按标题搜则是第 1 条。");
        ImGui.TextColored(Dim, TextSearch.Describe(_form.TextAsFullText));
        ImGui.Separator();

        var w = 330f;
        ImGui.TextUnformatted("名称"); ImGui.SameLine();
        ImGui.SetNextItemWidth(w);
        ImGui.InputText("##f-name", ref _form.Name, 128);

        ImGui.TextUnformatted("作者"); ImGui.SameLine();
        ImGui.SetNextItemWidth(w);
        ImGui.InputText("##f-author", ref _form.Author, 128);

        ImGui.TextUnformatted("标签"); ImGui.SameLine();
        ImGui.SetNextItemWidth(w);
        ImGui.InputTextWithHint("##f-tags", "多个用逗号分隔", ref _form.Tags, 256);

        ImGui.TextUnformatted("种族"); ImGui.SameLine();
        ImGui.SetNextItemWidth(w);
        ImGui.InputTextWithHint("##f-races", "如 Midlander / Viera", ref _form.Races, 128);

        ImGui.TextUnformatted("性别"); ImGui.SameLine();
        ImGui.SetNextItemWidth(w);
        ImGui.InputTextWithHint("##f-genders", "如 female / male / unisex", ref _form.Genders, 64);

        ImGui.TextUnformatted("影响/替换"); ImGui.SameLine();
        ImGui.SetNextItemWidth(w);
        ImGui.InputTextWithHint("##f-affects", "如 All PC eyes but Hrothgar", ref _form.Affects, 128);

        ImGui.TextUnformatted("类型 id"); ImGui.SameLine();
        ImGui.SetNextItemWidth(w);
        ImGui.InputTextWithHint("##f-types", "逗号分隔的数字，留空 = 全部", ref _form.Types, 64);

        ImGui.Separator();
        ImGui.SetNextItemWidth(180);
        ImGui.Combo("排序##f-sort", ref _form.SortIndex, _sortLabels, _sortLabels.Length);
        ImGui.SameLine();
        ImGui.Checkbox("仅当前版本兼容", ref _form.OnlyDtCompatible);
        ImGui.SameLine();
        DrawNsfwCombo("##nsfw-adv");

        ImGui.Separator();

        ImGui.BeginDisabled(_busy);
        if (ImGui.Button("搜索", new Vector2(140, 0)))
        {
            _advancedOpen = false;
            ImGui.CloseCurrentPopup();
            StartSearch(1);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("重置", new Vector2(110, 0))) _form.Reset();
        ImGui.SameLine();
        if (ImGui.Button("关闭", new Vector2(110, 0)))
        {
            _advancedOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ==================================================================== 逻辑

    /// <summary>
    /// 把一次搜索结果落到界面状态上（**缓存命中与真请求共用**）。
    /// 顺带跑「关注作者的新品对比」与缩略图缓存清理。
    /// </summary>
    private void ApplySearchResult(SearchResult result, int page, bool fromCache)
    {
        // 首屏：重置流式状态（清空去重集合），第一页也走同一条追加逻辑 ——
        // 这样「去重 / 到底」的判据只有一份，首屏和后续加载不会两套代码走偏。
        _stream.Reset(page, result.TotalMods);
        var first = _stream.Append(result.Hits, result.TotalMods);
        _hits = first.NewItems.ToList();
        _totalMods = result.TotalMods;
        _selected = null;
        _detail = null;
        _searchProgress.Complete();
        _searchDiag = _hits.Count == 0 ? result.Diagnostic : string.Empty;
        _status = _hits.Count == 0
            ? (fromCache ? "这一页没有结果（缓存）" : "这一页没有结果 —— 换个关键词，或放宽筛选")
            : $"已加载 {_hits.Count} 条 / 站点共 {_totalMods} 条" + (fromCache ? "（缓存，秒开）" : string.Empty);
        if (first.End) _plugin.Jobs.Note($"流式加载：首屏就到底了（{first.Reason}）");

        // 如果是「关注作者 → 看他作品」触发的，顺手算一下自上次以来的新品
        AfterSearchForFollowedAuthor();
        // 顺手跑一次缓存维护（各目录按上限淘汰 + 清陈年临时残留），频次限制在 5 分钟一次
        MaintainCaches();
    }

    /// <summary>向下滚动加载的更后一页：**追加**，绝不清空已有结果。</summary>
    private void ApplyAppend(SearchResult result, int page, bool fromCache)
    {
        var res = _stream.Append(result.Hits, result.TotalMods);
        if (res.NewItems.Count > 0)
        {
            // ⚠ 绝不能 _hits.AddRange(...)：这个赋值发生在**后台线程**，
            // 而渲染线程每帧都在 foreach 同一个 List —— 原地改会抛
            // "Collection was modified; enumeration operation may not execute"（真会崩）。
            // 换成「另建新列表 + 整体换引用」：引用赋值是原子的，正在被枚举的旧列表不受影响。
            var old = _hits;
            _hits = StreamState.Merge(old, res.NewItems);
        }
        if (result.TotalMods > 0) _totalMods = result.TotalMods;
        _status = $"已加载 {_hits.Count} 条 / 站点共 {_totalMods} 条" +
                  (fromCache ? "（缓存，秒开）" : string.Empty);
        _plugin.Jobs.Note($"流式加载第 {page} 页：本页 {result.Hits.Count} 条 → 新增 {res.NewItems.Count} 条" +
                          (res.End ? $"；到底（{res.Reason}）" : string.Empty));
    }

    private void StartSearch(int page, bool append = false)
    {
        if (append)
        {
            // 追加：绝不动 _busy（那是首屏搜索的状态，动了界面会整块变「搜索中」）
            if (_streamBusy || _busy || _stream.End || page < 1) return;
            _streamBusy = true;
            _streamError = string.Empty;
        }
        else
        {
            if (_busy || page < 1) return;
            _page = page;
            _jumpPage = page;
            _stream.Reset(page);          // 新一轮流式加载从这一页开始
            _streamBusy = false;
            _streamError = string.Empty;
            _streamAutoFired = 0;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        // 搜索也加总看门狗：到点就放弃并说明，别让人无限等
        _cts.CancelAfter(TimeSpan.FromSeconds(FetchLimits.SearchTimeoutSeconds));
        var ct = _cts.Token;

        if (append)
        {
            _status = $"正在加载更多（第 {page} 页）…";
        }
        else
        {
            _busy = true;
            _error = string.Empty;
            _viaChannel = false;
            _status = $"搜索中…（第 {page} 页）";
            _searchProgress = LoadProgress.ForSearch($"第 {page} 页");
            _searchProgress.Begin();
        }
        var nsfwMode = _form.NsfwMode;

        var query = _form.ToQuery(page);
        var client = _plugin.Site;
        var channel = _plugin.Channel;
        var cfg = _plugin.Configuration;

        Task.Run(async () =>
        {
            try
            {
                SearchResult result;
                var pageUrl = XmaUrls.Search(query);

                // 短期缓存：翻回上一页 / 手滑重复搜同一条件时秒开（TTL 3 分钟，键是完整 URL）
                if (_searchCache.TryGet(pageUrl, out var cached))
                {
                    _searchProgress.Enter(0, "命中缓存");
                    // ⚠ 这里**不要截断 URL**：曾经只显示前 80 字符，主人看到的 `…&author=He`
                    //    被误读成「搜索被截断成 He」。日志的用途就是让人核对实际请求了什么。
                    _plugin.Jobs.Note($"搜索缓存命中：{pageUrl}");
                    if (append) ApplyAppend(cached, page, fromCache: true);
                    else ApplySearchResult(cached, page, fromCache: true);
                    return;
                }

                // NSFW 模式必须优先走浏览器通道（带站点登录态）：
                // 实测同一 URL「混合（不传 nsfw 参数）」——直连（未登录）总数 53281（纯普通内容），
                // 带登录态的浏览器通道总数 61787 = 53281 + 8506（普通 + 全是 NSFW）。
                // 也就是说**站点只在登录态下才把 NSFW 并进列表**；直连再多试也没用。
                var channelFirst = SearchRouting.MustUseBrowserChannel(_form.NsfwMode, channel.Enabled, cfg.AutoFallbackToBrowser)
                                   // 直连刚被 Cloudflare 挑战过 → 这段时间别先撞一次直连（实测白等 1-2 秒）
                                   || (channel.Enabled && cfg.AutoFallbackToBrowser && _directBlocked.IsBlocked(DateTime.UtcNow));
                _nsfwChannelWarn = string.Empty;
                if (_directBlocked.IsBlocked(DateTime.UtcNow))
                    Plugin.Log.Information("[XmaBrowser] " + _directBlocked.Describe(DateTime.UtcNow));

                if (channelFirst)
                {
                    _searchProgress.Enter(1, "浏览器通道");
                    var chSw = System.Diagnostics.Stopwatch.StartNew();
                    var chHtml = await channel.GetHtmlAsync(pageUrl, ct).ConfigureAwait(false);
                    chSw.Stop();
                    var viaMs = chSw.ElapsedMilliseconds;

                    if (chHtml is { Length: > 0 } chHtmlOk)
                    {
                        _searchProgress.Enter(2, "解析");
                        var parseSw = System.Diagnostics.Stopwatch.StartNew();
                        result = XmaParser.ParseSearch(chHtmlOk, query.Page, pageUrl);
                        parseSw.Stop();
                        _viaChannel = true;
                        _viaChannelReason = _form.NsfwMode == 2 ? "只看 NSFW（需登录态）" : "NSFW 混合（需登录态）";

                        // 顺手判一下登录态：没登录就等于白走通道，直接把原因写出来（别只显示「没有结果」）
                        var facts = LoginStateDetector.Analyze(chHtmlOk);
                        if (facts.State == XmaLoginState.LoggedOut)
                            _nsfwChannelWarn = $"浏览器通道用的是「{channel.LastUsedBrowser?.Label ?? "未知"}」，" +
                                               "但它**没有登录 XIVModArchive** → 现在拿不到 NSFW 条目。\n" +
                                               "去「设置」把「浏览器程序 / 用户数据目录」指到已登录站点的那个浏览器（或先在那个浏览器里登录），" +
                                               "然后在「关于」页点「NSFW 通道自检」确认。";
                        else if (facts.State == XmaLoginState.CloudflareChallenge)
                            _nsfwChannelWarn = "通道取到的是 Cloudflare 挑战页（不是真页面）→ 稍后重试；NSFW 可能不完整。";

                        // ⭐ 通道拿到 0 条（实测过的两种成因：headless 被 Cloudflare 挑战、通道没登录）时，
                        // **再试一次直连**。混合模式下直连拿不到 NSFW，但「有 SFW 结果 + 说明缺什么」
                        // 远好过「一条都没有却不知道为什么」。直连也 0 条时才保留通道的诊断。
                        if (result.Hits.Count == 0)
                        {
                            var channelWhy = EmptyResultDiagnosis.Describe(chHtmlOk, pageUrl);
                            result.Diagnostic = channelWhy + "（走的是浏览器通道）";

                            if (SearchRouting.ShouldFallbackToDirect(result.Hits.Count, cfg.AutoFallbackToBrowser))
                            {
                                _searchProgress.Enter(2, "通道 0 条 → 直连兜底");
                                try
                                {
                                    var viaDirect = await client.SearchAsync(query, ct).ConfigureAwait(false);
                                    if (viaDirect.Hits.Count > 0)
                                    {
                                        viaDirect.Diagnostic = string.Empty;
                                        result = viaDirect;
                                        _viaChannel = false;
                                        _viaChannelReason = string.Empty;
                                        _nsfwChannelWarn = "浏览器通道这次没取到结果，已自动改用直连 —— " +
                                                           "本次**可能缺 NSFW 条目**。\n" +
                                                           "通道那边的原因：" + channelWhy + "\n" +
                                                           "去「关于」页点「NSFW 通道自检」确认通道是否已登录站点、是否被人机校验。";
                                        _plugin.Jobs.Note($"搜索：通道 0 条 → 直连兜底拿到 {result.Hits.Count} 条");
                                    }
                                    else
                                    {
                                        _plugin.Jobs.Note("搜索：通道与直连都是 0 条 —— 站点确实没有匹配");
                                    }
                                }
                                catch (Exception fbEx)
                                {
                                    // 直连也不行：保留通道的诊断，别把原因丢掉
                                    _plugin.Jobs.Note("搜索：通道 0 条 → 直连兜底也失败：" + fbEx.Message);
                                }
                            }
                        }

                        _plugin.Jobs.Note($"搜索（通道）取页面 {viaMs} ms + 解析 {parseSw.ElapsedMilliseconds} ms → {result.Hits.Count} 条｜{pageUrl}");
                    }
                    else
                    {
                        // 通道不可用就别卡住：退回直连（普通内容照常看，NSFW 会缺）
                        _searchProgress.Enter(1, "直连（通道失败）");
                        var dSw = System.Diagnostics.Stopwatch.StartNew();
                        result = await client.SearchAsync(query, ct).ConfigureAwait(false);
                        dSw.Stop();
                        _nsfwChannelWarn = "浏览器通道取不到页面（" + channel.LastError + "），已退回直连 —— NSFW 条目不会显示；" +
                                           "检查设置里的浏览器通道 / 浏览器是否已登录站点。";
                        _plugin.Jobs.Note($"搜索（通道失败 → 直连 {dSw.ElapsedMilliseconds} ms）：{channel.LastError}");
                    }
                }
                else
                {
                    try
                    {
                        _searchProgress.Enter(1, "直连");
                        var dSw = System.Diagnostics.Stopwatch.StartNew();
                        result = await client.SearchAsync(query, ct).ConfigureAwait(false);
                        dSw.Stop();
                        _directBlocked.MarkHealthy();     // 直连通了 → 不用再绕
                        _plugin.Jobs.Note($"搜索（直连）{dSw.ElapsedMilliseconds} ms → {result.Hits.Count} 条（总数 {result.TotalMods}）｜{pageUrl}");
                    }
                    catch (XmaAccessException directDenied) when (channel.Enabled && cfg.AutoFallbackToBrowser)
                    {
                        // 直连被拒：人机校验就记一笔冷却（接下来直接用通道，别再白撞）
                        if (directDenied.Reason == XmaBlockReason.CloudflareChallenge)
                            _directBlocked.MarkBlocked(DateTime.UtcNow);
                        _plugin.Jobs.Note($"搜索直连被拒（{directDenied.Reason}）→ 改走浏览器通道");
                        _searchProgress.SetNote("直连被拒 → 通道");
                        // 直连被拒（Cloudflare 挑战 / 需要登录态）→ 交给浏览器通道重试一次
                        var html = await channel.GetHtmlAsync(pageUrl, ct).ConfigureAwait(false);
                        if (html is null || html.Length == 0)
                            throw new InvalidOperationException(directDenied.Message + "；浏览器通道也没取到：" + channel.LastError);

                        _searchProgress.Enter(2, "解析");
                        result = XmaParser.ParseSearch(html, query.Page, pageUrl);
                        if (result.Hits.Count == 0)
                            result.Diagnostic = EmptyResultDiagnosis.Describe(html, pageUrl) + "（直连被拒后走的浏览器通道）";
                        _viaChannel = true;
                        _viaChannelReason = XmaBlockClassifier.ShortLabel(directDenied.Reason);
                    }
                }

                _searchCache.Put(pageUrl, result);
                if (append) ApplyAppend(result, page, fromCache: false);
                else ApplySearchResult(result, page, fromCache: false);

                if (!append)
                {
                    // ⭐ 搜完就顺手把浏览器预热好：NSFW 模式下接下来点开的每个详情、每张预览图都要走通道，
                    // 提前把会话建好（实测省约 1 秒），点开就不用再等。**不访问站点**，所以没有额外请求。
                    if (nsfwMode != 0 && channel.Enabled)
                        _ = Task.Run(() => channel.WarmUpAsync());

                    cfg.OnlyDtCompatible = _form.OnlyDtCompatible;
                    cfg.NsfwMode = _form.NsfwMode;
                    cfg.Save();
                }
            }
            catch (XmaAccessException e)
            {
                if (append) AppendFailed(e.Message);
                else
                {
                    _error = e.Message;
                    _status = "被站点拒绝";
                    _searchProgress.Fail(e.Message);
                }
            }
            catch (OperationCanceledException)
            {
                if (append) AppendFailed("加载被取消或超时");
                else
                {
                    _status = $"已取消或超时（超过 {FetchLimits.SearchTimeoutSeconds} 秒）";
                    _searchProgress.Fail(_status);
                }
            }
            catch (Exception e)
            {
                if (append) AppendFailed($"{e.GetType().Name}: {e.Message}");
                else
                {
                    _error = $"{e.GetType().Name}: {e.Message}";
                    _status = "搜索失败";
                    _searchProgress.Fail(_error);
                }
            }
            finally
            {
                if (append) _streamBusy = false;
                else _busy = false;
            }
        }, ct);
    }

    /// <summary>
    /// 追加失败：**保留已有结果**，只在底部状态行里说明并给「重试」。
    /// 不能因为第 8 页失败就把看了半天的前 7 页清掉。
    /// </summary>
    private void AppendFailed(string why)
    {
        _streamError = why;
        _status = $"已加载 {_hits.Count} 条（继续加载失败，可重试）";
        _plugin.Jobs.Note($"流式加载第 {_stream.NextPage} 页失败：{why}");
    }

    private void LoadDetail(SearchHit hit)
    {
        _detailBusy = true;
        _detail = null;
        _detailViaChannel = false;
        _error = string.Empty;

        // ⭐ 整条详情的**总看门狗**：到点就放弃并把原因写出来。
        // 没有它的时候，会话启动 + 重试相乘可以拖到一分钟，用户只能盯着不动的进度条 ——
        // 「加载了 1 分钟还没出来、切换一下秒开（走缓存）」就是这么来的。
        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource(TimeSpan.FromSeconds(FetchLimits.DetailTimeoutSeconds));
        var detailCt = _detailCts.Token;

        // 换了一条 Mod：把上一条的翻译清掉（正在跑的那个也取消）
        _transCts?.Cancel();
        _translating = false;
        _translatedTitle = string.Empty;
        _translatedDesc = string.Empty;
        _translateError = string.Empty;

        var client = _plugin.Site;
        var channel = _plugin.Channel;
        var cfg = _plugin.Configuration;
        var modId = hit.ModId;
        var pageUrl = XmaUrls.ModPage(modId);
        var nsfwMode = _form.NsfwMode;

        // 进度从这一刻开始算（含缓存判断）
        _detailProgress = LoadProgress.ForDetail(hit.Title);
        _detailProgress.Begin();

        // 详情短期缓存：同一条 mod 在几分钟内再看就直接用。
        // 为什么需要：NSFW 详情只能走通道（实测 2.8–3.6 秒，还要跟别人的通道操作排队），
        // 而看 Mod 的习惯就是「点进去 → 回列表 → 再点回来」，每次都重来就特别慢。
        if (_detailCache.TryGet(modId, out var cachedDetail))
        {
            _detail = cachedDetail;
            _detailViaChannel = false;
            _detailChannelReason = "短缓存";
            _detailBusy = false;
            _detailProgress.Enter(0, "命中缓存");
            _detailProgress.Complete();
            _plugin.Jobs.Note($"详情命中短缓存：{cachedDetail.Title}");
            return;
        }

        Task.Run(async () =>
        {
            // 详情：**先直连**（快，通常 1 秒内），只有直连拿不到「下载直链」时才回退浏览器通道。
            // 原来 NSFW 模式下是无条件通道优先 —— 每个详情都要等一次 headless 浏览器，慢得离谱。
            // 站点只在登录态下才给 NSFW 的下载直链，所以判据是「解析出来的 DownloadHref 是否为空」。
            var canFallBack = channel.Enabled && cfg.AutoFallbackToBrowser;
            // 直连刚被人机校验拦过 → 详情也别先撞一次（实测白等约 1 秒）
            var directBlocked = canFallBack && _directBlocked.IsBlocked(DateTime.UtcNow);
            // 「只看 NSFW」模式下**每条结果都是 NSFW**。而 NSFW 的详情只有带登录态（浏览器通道）
            // 才看得见 —— 直连必然被拒。所以这种模式下别再白撞直连（实测每次白等 936 ms）。
            var nsfwOnly = nsfwMode == 2;
            // 混合模式下：这条 mod 之前已经被证实「需要登录态」→ 也别再撞
            var knownNeedsLogin = canFallBack && _needsLogin.Needs(modId, DateTime.UtcNow);
            var skipDirect = directBlocked || nsfwOnly || knownNeedsLogin;

            // ⭐ 混合模式是「先直连、不行再走通道」。直连要 1 秒左右，等它失败再开会话等于白等这 1 秒 ——
            // 所以**并行**把浏览器预热起来（预热只开浏览器、不访问站点，没有副作用）。
            if (canFallBack && !skipDirect)
            {
                _detailProgress.Enter(1, "预热浏览器");
                _ = Task.Run(() => channel.WarmUpAsync());
            }

            if (!skipDirect)
            {
                _detailProgress.Enter(2, "直连");
                var dSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var parsed = await client.DetailAsync(modId, detailCt).ConfigureAwait(false);
                    dSw.Stop();

                    if (parsed.Title.Length > 0 && (parsed.DownloadHref.Length > 0 || !canFallBack))
                    {
                        _detail = parsed;
                        _directBlocked.MarkHealthy();
                        _detailCache.Put(modId, parsed);
                        _plugin.Jobs.Note($"详情（直连）{dSw.ElapsedMilliseconds} ms：{parsed.Title}");
                        _detailProgress.Enter(4, "预览图");
                        return;
                    }

                    // 直连拿到了页面但没有下载链 → 多半是 NSFW 的「登录后可见」空壳页，走通道
                    _plugin.Jobs.Note($"详情直连 {dSw.ElapsedMilliseconds} ms 但没拿到下载链 → 改走浏览器通道");
                }
                catch (XmaAccessException denied) when (canFallBack)
                {
                    dSw.Stop();
                    if (denied.Reason == XmaBlockReason.CloudflareChallenge)
                        _directBlocked.MarkBlocked(DateTime.UtcNow);
                    else if (denied.Reason == XmaBlockReason.NeedsLogin)
                        _needsLogin.Mark(modId, DateTime.UtcNow);   // 这条 mod 需要登录态，下次别再白撞
                    _plugin.Jobs.Note($"详情直连被拒（{denied.Reason}，{dSw.ElapsedMilliseconds} ms）→ 改走浏览器通道");
                }
                catch (OperationCanceledException)
                {
                    // 直连被总看门狗掐掉：别让它把整条流程带崩，往下交给统一的收尾处理
                    _plugin.Jobs.Note($"详情直连被取消/超时（{dSw.ElapsedMilliseconds} ms）");
                }
                catch (Exception e)
                {
                    // ⭐ 必须兜住**所有**异常类型：这里逃出去一个异常，就不会走到下面那个
                    //    finally，_detailBusy 会永远停在 true —— 界面就永远显示「正在读取…」，
                    //    而用户看到的就是「第一次一直转、第二次打开才出来」。
                    _plugin.Jobs.Note($"详情直连出错（{e.GetType().Name}，{dSw.ElapsedMilliseconds} ms）→ 改走浏览器通道");
                }
            }
            else
            {
                _plugin.Jobs.Note(nsfwOnly
                    ? "详情：「只看 NSFW」模式 → 直接走浏览器通道（直连看不到 NSFW 详情）"
                    : knownNeedsLogin
                        ? "详情：这条 Mod 已被证实需要登录态 → 直接走浏览器通道"
                        : "详情：直连在冷却期内，直接走浏览器通道");
            }

            if (canFallBack)
            {
                var cSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    _detailProgress.Enter(1, "准备浏览器");
                    var html = await channel.GetHtmlAsync(pageUrl, detailCt).ConfigureAwait(false);
                    cSw.Stop();
                    _detailProgress.Enter(2, "浏览器通道");
                    if (html is { Length: > 0 } htmlOk)
                    {
                        _detailProgress.Enter(3, "解析");
                        var parsed = XmaParser.ParseDetail(htmlOk, modId, pageUrl);
                        if (parsed.Title.Length > 0 || parsed.DownloadHref.Length > 0)
                        {
                            _detail = parsed;
                            _detailViaChannel = true;
                            _detailChannelReason = nsfwMode != 0 ? "NSFW 模式（需登录态）" : "直连被拒";
                            if (parsed.Title.Length > 0) _detailCache.Put(modId, parsed);
                            _plugin.Jobs.Note($"详情（通道）{cSw.ElapsedMilliseconds} ms：{parsed.Title}");
                            _detailProgress.Enter(4, "预览图");
                            return;
                        }
                    }
                    Plugin.Log.Warning("[XmaBrowser] 通道读详情没解析出内容：" + channel.LastError);
                }
                catch (Exception e)
                {
                    Plugin.Log.Warning($"[XmaBrowser] 浏览器通道读详情失败：{e.Message}");
                }
            }

            try
            {
                _detailProgress.Enter(2, "直连");
                _detail = await client.DetailAsync(modId, detailCt).ConfigureAwait(false);
                if (_detail.Title.Length > 0 || _detail.DownloadHref.Length > 0)
                {
                    _detailProgress.Enter(3, "预览图");
                    return;
                }
            }
            catch (XmaAccessException directDenied) when (channel.Enabled && cfg.AutoFallbackToBrowser)
            {
                try
                {
                    _detailProgress.Enter(1, "准备浏览器");
                    var html = await channel.GetHtmlAsync(pageUrl, detailCt).ConfigureAwait(false);
                    if (html is null || html.Length == 0)
                    {
                        _error = "浏览器通道也没取到页面：" + channel.LastError;
                    }
                    else
                    {
                        _detailProgress.Enter(3, "解析");
                        var parsed = XmaParser.ParseDetail(html, modId, pageUrl);
                        if (parsed.Title.Length == 0 && parsed.DownloadHref.Length == 0)
                            _error = "取到的页面解析不出内容（站点结构可能变了）";
                        else
                        {
                            _detail = parsed;
                            _detailViaChannel = true;
                            _detailChannelReason = XmaBlockClassifier.ShortLabel(directDenied.Reason);
                            _detailProgress.Enter(4, "预览图");
                        }
                    }
                }
                catch (Exception e)
                {
                    _error = $"浏览器通道失败：{e.GetType().Name} {e.Message}";
                }
            }
            catch (XmaAccessException e)
            {
                _error = e.Message + (_plugin.Configuration.AutoFallbackToBrowser
                    ? "（可在「设置」里开启浏览器通道）"
                    : "（「直连被拒时自动走浏览器通道」在设置里是关掉的）");
            }
            catch (OperationCanceledException)
            {
                _error = $"读详情超过 {FetchLimits.DetailTimeoutSeconds} 秒就放弃了（哪一段慢见「关于」页的诊断日志，也可以点重试）。";
                _plugin.Jobs.Note($"详情超时：{modId} —— {FetchLimits.DetailTimeoutSeconds} 秒预算用尽，已放弃");
            }
            catch (Exception e)
            {
                _error = $"读详情失败：{e.GetType().Name}: {e.Message}";
            }
            finally
            {
                _detailBusy = false;
                // 没拿到详情就把进度条标成失败（别让它一直转）；
                // 拿到了就停在「加载预览图」，由绘制那边在图到位时收尾 Complete。
                if (_detail is null) _detailProgress.Fail(_error.Length > 0 ? _error : "没取到详情");
            }
        });
    }

    private CancellationTokenSource? _detailCts;
    private bool _detailViaChannel;
    private string _detailChannelReason = string.Empty;
    // 详情短期缓存 + 「这条 mod 需要登录态」记忆（都是纯逻辑类，能离线自测）
    private readonly DetailCache _detailCache = new();
    private readonly NeedsLoginMemory _needsLogin = new();
    // 在插件里登录 XIVModArchive
    private bool _loginBusy;
    private bool _loginOk;
    private string _loginMessage = string.Empty;
    // 「插件里有没有登录态」要扫 cookie 库，不能每帧扫 —— 缓存 2 秒
    private bool _ownedLoginState;
    private DateTime _ownedLoginCheckedUtc = DateTime.MinValue;
    // 预览图自动救济：每个 mod 视图只自动重试一次（之后交给「重试」按钮）
    private string _previewRetryMod = string.Empty;
    private int _previewAutoRetried;
    // 「文件已在本地但纹理没就绪」的自动重挂（每个 mod 只一次）
    private int _previewTextureReloads;
    private DateTime _previewReadySince = DateTime.MinValue;
    // 缓存维护：上次跑的时间 + 占用报告 + 结果提示
    private DateTime _lastCacheMaintainUtc = DateTime.MinValue;
    private DateTime _cacheReportUtc = DateTime.MinValue;
    private string _cacheReport = string.Empty;
    private string _cacheMessage = string.Empty;
    // 后台任务在途标志：扫盘/读 cookie 库/读 Penumbra 这类同步 IO 绝不能放在渲染线程上
    private volatile bool _cacheReportBusy;
    private volatile bool _ownedLoginBusy;
    private volatile bool _diagBusy;
    private volatile bool _cacheMaintaining;
    // 外链判定只往诊断日志写一次（不然每帧都写会刷屏）
    private string _loggedExternalMod = string.Empty;

    /// <summary>插件 profile 里有没有 XIVModArchive 登录态（结果缓存 2 秒；**扫描放后台线程**）。</summary>
    private bool OwnedLoginCached()
    {
        // ⚠ 这个检查要打开 cookie 库文件扫最多 1 MB。原来每 2 秒在渲染线程上做一次 ——
        // 属于「每两秒一次的小卡顿」，现在改成后台扫描、界面只读结果。
        if (!_ownedLoginBusy && (DateTime.UtcNow - _ownedLoginCheckedUtc).TotalSeconds >= 2)
        {
            _ownedLoginCheckedUtc = DateTime.UtcNow;
            _ownedLoginBusy = true;
            var ch = _plugin.Channel;
            _ = Task.Run(() =>
            {
                try { _ownedLoginState = ch.OwnedProfileHasXmaCookie; }
                catch { _ownedLoginState = false; }
                finally { _ownedLoginBusy = false; }
            });
        }
        return _ownedLoginState;
    }

    private string _viaChannelReason = string.Empty;
    private List<string> _diagLines = [];
    private List<KeyValuePair<string, string>> _diagMods = [];
    private int _diagIndex;
    private string _diagResult = string.Empty;
    private string _diagReport = string.Empty;
    private int _diagLogSeen;

    private static string Fmt(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm") ?? "-";

    /// <summary>
    /// 等比缩放（contain）：结果两个方向都不超过给的上限，保持宽高比。
    /// 实现在 <see cref="GridLayout.Fit"/>（纯函数，可离线断言「绝不超出容器」这条不变量）。
    /// </summary>
    private static Vector2 Fit(Vector2 src, float maxW, float maxH) => GridLayout.Fit(src, maxW, maxH);

    /// <summary>底部状态行要占的高度：表格必须给它留出空间，否则外层 child 会长出滚动条（双滚动条）。</summary>
    private static float FooterReserveH()
        => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 3f + 8f;

    private static void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* 忽略 */ }
    }

    public void DisposeCancel() => _cts?.Cancel();
}
