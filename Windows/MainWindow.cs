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

    // ---- 浏览状态 ----
    private List<SearchHit> _hits = [];
    private int _page = 1;
    private int _jumpPage = 1;
    private int _totalMods;

    /// <summary>站点一页固定 15 条（实测），用于算总页数。</summary>
    private const int SitePageSize = 15;
    private string _status = "留空关键词直接搜 = 看最新发布";
    private string _error = string.Empty;
    private bool _busy;
    private bool _viaChannel;
    private SearchHit? _selected;
    private ModDetail? _detail;
    private bool _detailBusy;
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
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawTopBar();

        if (ImGui.BeginTabBar("xma-tabs"))
        {
            if (ImGui.BeginTabItem("浏览"))
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

        var active = _form.ActiveConditions().ToList();
        if (active.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Blue, "  筛选中：" + string.Join("  ", active));
        }

        ImGui.Spacing();

        var avail = ImGui.GetContentRegionAvail();
        var listWidth = MathF.Max(380f, avail.X * 0.5f);

        ImGui.BeginChild("xma-browse-left", new Vector2(listWidth, 0));
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
        ImGui.SetNextItemWidth(MathF.Max(200f, avail - 330f));
        ImGui.InputTextWithHint("##xma-kw", "关键词（留空 = 不看关键词）", ref _form.Text, 128);
        if (ImGui.IsItemDeactivatedAfterEdit()) StartSearch(1);

        ImGui.SameLine();
        ImGui.BeginDisabled(_busy);
        if (ImGui.Button("搜索", new Vector2(90, 0))) StartSearch(1);
        ImGui.EndDisabled();

        ImGui.SameLine();
        var advLabel = _form.HasAdvanced ? "高级搜索 ●##xma-advbtn" : "高级搜索…##xma-advbtn";
        if (ImGui.Button(advLabel, new Vector2(120, 0)))
        {
            _openAdvancedRequest = true;
            _advancedOpen = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("名称 / 作者 / 标签 / 种族 / 性别 / 类型 / 影响替换");

        ImGui.SameLine();
        ImGui.BeginDisabled(_busy);
        if (ImGui.Button("刷新", new Vector2(70, 0))) StartSearch(_page);
        ImGui.EndDisabled();

        // 第二行：排序 / 过滤 / 翻页（不与上面挤在一起）
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("排序##xma-sort", ref _form.SortIndex, _sortLabels, _sortLabels.Length);

        ImGui.SameLine();
        ImGui.Checkbox("仅当前版本兼容", ref _form.OnlyDtCompatible);

        ImGui.SameLine();
        DrawNsfwCombo("##nsfw-main");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("站点是**三态**（实测）：\n" +
                             "· 排除 NSFW ＝ 发 nsfw=false\n" +
                             "· 含 NSFW（混合）＝ 不传 nsfw 参数（NSFW 与普通内容都返回）\n" +
                             "· 只看 NSFW ＝ 发 nsfw=true\n" +
                             "看/下载 NSFW 需要站点登录态，会走浏览器通道（借用本机浏览器的登录）。");

        ImGui.SameLine();
        ImGui.Checkbox("把关键词当标签搜", ref _form.KeywordAsTag);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾上后，「关键词」框里的内容按站点标签精确检索。");

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

    /// <summary>页码控制条。单独一行，并在结果区底部再放一份（滚动到下面不用跑回顶部翻页）。</summary>
    private void DrawPager(string id)
    {
        var totalPages = _totalMods > 0 ? (_totalMods + SitePageSize - 1) / SitePageSize : 0;

        ImGui.BeginDisabled(_busy || _page <= 1);
        if (ImGui.Button($"◀ 上一页##{id}")) StartSearch(_page - 1);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextColored(Dim, totalPages > 0
            ? $"第 {_page} / {totalPages} 页　共 {_totalMods} 条（站点每页 {SitePageSize} 条）"
            : $"第 {_page} 页");

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
            ImGui.ProgressBar(_titleTotal > 0 ? _titleDone / (float)_titleTotal : 0f, new Vector2(150, 0),
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

    private void DrawResultList()
    {
        if (_hits.Count == 0)
        {
            ImGui.TextColored(Dim, _busy ? "搜索中…" : "还没有结果。敲关键词回车，或直接点「搜索」看最新发布。");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                     ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable("xma-hits", 6, flags))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("图", ImGuiTableColumnFlags.WidthFixed, 74);
            ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("作者", ImGuiTableColumnFlags.WidthFixed, 116);
            ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthFixed, 92);
            ImGui.TableSetupColumn("浏览", ImGuiTableColumnFlags.WidthFixed, 62);
            ImGui.TableSetupColumn("标记", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableHeadersRow();

            foreach (var hit in _hits)
            {
                ImGui.TableNextRow();
                ImGui.PushID(hit.ModId);

                ImGui.TableNextColumn();
                var wrap = _plugin.Thumbs.Get(hit.ThumbUrl)?.GetWrapOrDefault();
                if (wrap is not null) ImGui.Image(wrap.Handle, Fit(wrap.Size, 68f, 42f));
                else ImGui.Dummy(new Vector2(68, 42));

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
                if (hit.IsNew) ImGui.TextColored(Green, "NEW");
                else if (hit.IsUpdated) ImGui.TextColored(Yellow, "UPD");

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        DrawPager("bottom");
    }

    // ==================================================================== 宫格视图

    private void DrawResultGrid()
    {
        if (_hits.Count == 0)
        {
            ImGui.TextColored(Dim, _busy ? "搜索中…" : "还没有结果。敲关键词回车，或直接点「搜索」看最新发布。");
            return;
        }

        var cfg = _plugin.Configuration;
        var cardW = MathF.Max(120f, cfg.GridCardWidth * ImGui.GetIO().FontGlobalScale);
        var sp = ImGui.GetStyle().ItemSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var cols = Math.Max(1, (int)((avail + sp) / (cardW + sp)));

        if (ImGui.BeginTable("xma-grid", cols, ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
        {
            for (var i = 0; i < _hits.Count; i++)
            {
                if (i % cols == 0) ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawGridCard(_hits[i], cardW);
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        DrawPager("grid-bottom");
    }

    /// <summary>宫格里的一张卡：缩略图 + 标题（有中文就用中文）+ 类型/作者，整块可点。</summary>
    private void DrawGridCard(SearchHit hit, float cardW)
    {
        var cfg = _plugin.Configuration;
        var selected = _selected?.ModId == hit.ModId;
        var imgW = cardW - 12f;
        var imgH = imgW * 0.56f;
        var cardH = imgH + 50f;

        ImGui.PushID("grid-" + hit.ModId);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, selected ? SelBg : CardBg);
        ImGui.BeginChild("c", new Vector2(cardW, cardH), true);

        var wrap = _plugin.Thumbs.Get(hit.ThumbUrl)?.GetWrapOrDefault();
        if (wrap is not null) ImGui.Image(wrap.Handle, Fit(wrap.Size, imgW, imgH));
        else ImGui.Dummy(new Vector2(imgW, imgH));

        var zh = cfg.ShowTranslatedTitles ? _plugin.Translator.Cached(hit.Title) : null;
        var title = zh is { Length: > 0 } ? zh : hit.Title;
        ImGui.TextUnformatted(Clip(title, 32));
        ImGui.TextColored(Dim, Clip($"{hit.Type} · {hit.Author}", 26));
        if (hit.IsNew)
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
        var hovered = ImGui.IsWindowHovered();
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

    /// <summary>按字符数截断（中英混排够用，不追求像素精确）。</summary>
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void DrawDetailPane()
    {
        var hit = _selected;
        if (hit is null)
        {
            ImGui.TextColored(Dim, "← 左边选一条 Mod，这里显示详情与「安装到 Penumbra」");
            return;
        }

        if (_detailBusy)
        {
            ImGui.TextColored(Yellow, $"正在读取 {hit.ModId} 的详情…");
            return;
        }

        var d = _detail;
        if (d is null)
        {
            ImGui.TextColored(Red, "详情读取失败，可以：换一条 / 开浏览器通道 / 稍后重试。");
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
        DrawTranslateControls(d);
        if (_detailViaChannel)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, $"　🌐 浏览器通道（{_detailChannelReason}）");
        }
        ImGui.Separator();

        if (d.Images.Count > 0)
        {
            var idx = ((_previewIndex % d.Images.Count) + d.Images.Count) % d.Images.Count;
            var w = ImGui.GetContentRegionAvail().X;
            var wrap = _plugin.Thumbs.Get(d.Images[idx])?.GetWrapOrDefault();
            if (wrap is not null) ImGui.Image(wrap.Handle, Fit(wrap.Size, w, 230f));
            else ImGui.Dummy(new Vector2(w, 30));

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
        ImGui.BeginChild("xma-desc", new Vector2(0, 130), true);
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
            ImGui.TextColored(Yellow, "这条 Mod 的文件在外部站点（作者外链），拿不到直链，没法一键安装。");
            if (ImGui.Button("在浏览器打开")) OpenExternal(d.AbsoluteDownloadUrl);
            ImGui.SameLine();
            if (ImGui.Button("复制外链")) ImGui.SetClipboardText(d.AbsoluteDownloadUrl);
            return;
        }

        ImGui.TextWrapped("下载：" + d.FileNameHint);
        var ok = _plugin.Penumbra.Available;
        ImGui.BeginDisabled(!ok);
        if (ImGui.Button("安装到 Penumbra", new Vector2(180, 0)))
        {
            var cover = d.Images.Count > 0 ? d.Images[0] : hit.ThumbUrl;
            _plugin.Jobs.Enqueue(d, cover);
        }
        ImGui.EndDisabled();
        if (!ok)
        {
            ImGui.SameLine();
            ImGui.TextColored(Red, "Penumbra 未连接");
        }
        ImGui.SameLine();
        if (ImGui.Button("复制下载链接")) ImGui.SetClipboardText(d.AbsoluteDownloadUrl);
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
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("进度", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("信息", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 70);
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
        if (ImGui.Checkbox("启用浏览器通道（借用本机浏览器的登录态）", ref chan))
        {
            cfg.EnableBrowserChannel = chan;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把本机已登录浏览器的 Cookies + Local State 复制到临时 profile，\n用同一个浏览器 exe 起无窗口实例，经 CDP 取页面/图片/文件。你不需要在插件里登录。");

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
        ImGui.TextColored(Blue, "缩略图缓存");
        ImGui.Separator();
        var mb = cfg.ThumbCacheMb;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("磁盘上限（MB）", ref mb))
        {
            cfg.ThumbCacheMb = Math.Clamp(mb, 16, 4096);
            changed = true;
        }

        ImGui.EndChild();

        if (changed) cfg.Save();
    }

    // ==================================================================== 关于

    private void DrawAboutTab()
    {
        ImGui.TextColored(Blue, $"XMA Browser v{Plugin.PluginVersion}");
        ImGui.TextWrapped("在游戏里逛 XIVModArchive，点一下把 Mod 装进 Penumbra 并即时生效。");
        ImGui.Separator();

        ImGui.TextColored(Dim, "公开内容直连站点；NSFW / 被 Cloudflare 拦的内容走浏览器通道（借用本机浏览器登录态）。");
        ImGui.TextColored(Dim, "插件只读站点公开数据，不上传任何信息；临时 profile 默认用完即删。");
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
        if (ImGui.Button("读取所有 Mod 的真实路径（并写日志）"))
        {
            _diagMods = _plugin.Penumbra.Mods().ToList();
            _diagLines = _plugin.Penumbra.DumpPaths();
            foreach (var line in _diagLines)
                Plugin.Log.Information("[XmaBrowser][诊断] " + line);
            _diagResult = $"读到 {_diagLines.Count} 条（已写入日志）";
            _diagIndex = 0;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("用 Penumbra 的 GetModPath 读回每个 Mod 的真实路径（含文件夹），\n" +
                             "并写入 /xllog —— 分组没生效时看这个最直接。");

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
                if (ImGui.Button("按模板归入分组"))
                {
                    var cfg = _plugin.Configuration;
                    var dir = _diagMods[_diagIndex].Key;
                    var name = _diagMods[_diagIndex].Value;
                    // 手动归组时没有站点类型信息（只有安装时才带）→ {type} 会算成 Other
                    var folder = GroupPath.BuildFolder(cfg.GroupTemplate, cfg.GroupPrefix, string.Empty, string.Empty,
                                                       string.Empty, name);
                    var (ok, msg, report) = _plugin.Jobs.GroupOneDetailed(dir, name, folder);
                    _diagResult = (ok ? "✅ " : "❌ ") + msg;
                    _diagReport = report;
                    _diagLines = _plugin.Penumbra.DumpPaths();
                    foreach (var line in _diagLines)
                        Plugin.Log.Information("[XmaBrowser][诊断] " + line);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("对选中的 Mod 走一遍归组（会先读回真实路径核对）。\n" +
                                     "注意：手动归组没有站点类型，模板里的 {type} 会是 Other。");

                ImGui.SameLine();
                if (ImGui.Button("归组自检（IPC 探针 + 移动/还原实测）"))
                {
                    var dir = _diagMods[_diagIndex].Key;
                    var name = _diagMods[_diagIndex].Value;
                    _diagReport = _plugin.Jobs.RunPathWriteExperiment(dir, name);
                    _diagResult = "已跑完路径写法实验，见下方报告";
                    _diagLines = _plugin.Penumbra.DumpPaths();
                    foreach (var line in _diagReport.Split('\n'))
                        Plugin.Log.Information("[XmaBrowser][深入自检] " + line.TrimEnd());
                }
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
            ImGui.BeginChild("xma-diag-report", new Vector2(0, 130), true);
            ImGui.TextWrapped(_diagReport);
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

        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(AdvancedId, ref _advancedOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextColored(Dim, "留空 = 不看这个条件（除「关键词」外都是站点的精确检索项）");
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

    private void StartSearch(int page)
    {
        if (_busy || page < 1) return;

        _page = page;
        _jumpPage = page;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _busy = true;
        _error = string.Empty;
        _viaChannel = false;
        _status = $"搜索中…（第 {page} 页）";

        var query = _form.ToQuery(page);
        var client = _plugin.Site;
        var channel = _plugin.Channel;
        var cfg = _plugin.Configuration;

        Task.Run(async () =>
        {
            try
            {
                SearchResult result;
                try
                {
                    result = await client.SearchAsync(query, ct).ConfigureAwait(false);
                }
                catch (XmaAccessException directDenied) when (channel.Enabled && cfg.AutoFallbackToBrowser)
                {
                    // 直连被拒（Cloudflare 挑战 / 需要登录态）→ 交给浏览器通道重试一次
                    var html = await channel.GetHtmlAsync(XmaUrls.Search(query), ct).ConfigureAwait(false);
                    if (html is null || html.Length == 0)
                        throw new InvalidOperationException(directDenied.Message + "；浏览器通道也没取到：" + channel.LastError);

                    result = XmaParser.ParseSearch(html, query.Page, XmaUrls.Search(query));
                    _viaChannel = true;
                    _viaChannelReason = XmaBlockClassifier.ShortLabel(directDenied.Reason);
                }

                _hits = result.Hits;
                _totalMods = result.TotalMods;
                _selected = null;
                _detail = null;
                _status = result.Hits.Count == 0
                    ? "这一页没有结果 —— 换个关键词，或放宽筛选"
                    : $"第 {page} 页，本页 {result.Hits.Count} 条";
                _plugin.Thumbs.Trim();

                cfg.OnlyDtCompatible = _form.OnlyDtCompatible;
                cfg.NsfwMode = _form.NsfwMode;
                cfg.Save();
            }
            catch (XmaAccessException e)
            {
                _error = e.Message;
                _status = "被站点拒绝";
            }
            catch (OperationCanceledException)
            {
                _status = "已取消";
            }
            catch (Exception e)
            {
                _error = $"{e.GetType().Name}: {e.Message}";
                _status = "搜索失败";
            }
            finally
            {
                _busy = false;
            }
        }, ct);
    }

    private void LoadDetail(SearchHit hit)
    {
        _detailBusy = true;
        _detail = null;
        _detailViaChannel = false;
        _error = string.Empty;

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

        Task.Run(async () =>
        {
            try
            {
                _detail = await client.DetailAsync(modId).ConfigureAwait(false);
            }
            catch (XmaAccessException directDenied) when (channel.Enabled && cfg.AutoFallbackToBrowser)
            {
                try
                {
                    var html = await channel.GetHtmlAsync(pageUrl).ConfigureAwait(false);
                    if (html is null || html.Length == 0)
                    {
                        _error = "浏览器通道也没取到页面：" + channel.LastError;
                    }
                    else
                    {
                        var parsed = XmaParser.ParseDetail(html, modId, pageUrl);
                        if (parsed.Title.Length == 0 && parsed.DownloadHref.Length == 0)
                            _error = "取到的页面解析不出内容（站点结构可能变了）";
                        else
                        {
                            _detail = parsed;
                            _detailViaChannel = true;
                            _detailChannelReason = XmaBlockClassifier.ShortLabel(directDenied.Reason);
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
            catch (Exception e)
            {
                _error = $"读详情失败：{e.GetType().Name}: {e.Message}";
            }
            finally
            {
                _detailBusy = false;
            }
        });
    }

    private bool _detailViaChannel;
    private string _detailChannelReason = string.Empty;
    private string _viaChannelReason = string.Empty;
    private List<string> _diagLines = [];
    private List<KeyValuePair<string, string>> _diagMods = [];
    private int _diagIndex;
    private string _diagResult = string.Empty;
    private string _diagReport = string.Empty;
    private int _diagLogSeen;

    private static string Fmt(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm") ?? "-";

    private static Vector2 Fit(Vector2 src, float maxW, float maxH)
    {
        if (src.X <= 0 || src.Y <= 0) return new Vector2(maxW, maxH);
        var k = MathF.Min(maxW / src.X, maxH / src.Y);
        return new Vector2(src.X * k, src.Y * k);
    }

    private static void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* 忽略 */ }
    }

    public void DisposeCancel() => _cts?.Cancel();
}
