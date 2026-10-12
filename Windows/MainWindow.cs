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

namespace XmaBrowser.Windows;

public class MainWindow : Window
{
    private static readonly (string Label, string Value)[] Sorts =
    [
        ("最新发布", "time_published"),
        ("最近更新", "time_edited"),
        ("今日最多浏览", "views_today"),
    ];

    private static readonly Vector4 Dim = new(0.7f, 0.7f, 0.7f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.8f, 0.45f, 1f);

    private readonly Plugin _plugin;
    private readonly string[] _sortLabels = Sorts.Select(s => s.Label).ToArray();

    // 列表状态（后台线程写、绘制线程读：整个列表引用一次性替换，避免边遍历边改）
    private List<SearchHit> _hits = [];
    private string _query = string.Empty;
    private int _sortIndex;
    private bool _onlyDt;
    private bool _nsfw;
    private bool _useTags;
    private int _page = 1;
    private int _totalMods;
    private string _status = "留空搜索 = 看最新发布";
    private string _error = string.Empty;
    private bool _busy;

    private SearchHit? _selected;
    private ModDetail? _detail;
    private bool _detailBusy;
    private bool _detailViaChannel;
    private int _previewIndex;
    private string _browserReport = string.Empty;
    private CancellationTokenSource? _cts;

    public MainWindow(Plugin plugin) : base($"XMA Browser v{Plugin.PluginVersion}###XmaBrowserMain")
    {
        _plugin = plugin;
        _onlyDt = plugin.Configuration.OnlyDtCompatible;
        _nsfw = plugin.Configuration.ShowNsfw;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(780, 540),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawHeader();
        DrawSearchBar();
        ImGui.Separator();
        DrawSettings();

        var avail = ImGui.GetContentRegionAvail();
        var bodyH = MathF.Max(200f, avail.Y - 140f);
        var leftW = MathF.Max(360f, avail.X * 0.52f);

        ImGui.BeginChild("xma-list", new Vector2(leftW, bodyH));
        DrawList();
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("xma-detail", new Vector2(0, bodyH));
        DrawDetail();
        ImGui.EndChild();

        ImGui.Separator();
        DrawQueue();
    }

    private void DrawQueue()
    {
        var jobs = _plugin.Jobs.Snapshot();
        var running = jobs.Count(j => !j.Finished);
        ImGui.TextColored(Dim, $"安装队列：{jobs.Count} 条（进行中 {running}）");
        ImGui.SameLine();
        if (ImGui.Button("清掉已完成")) _plugin.Jobs.ClearFinished();

        if (jobs.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Dim, "—— 空。选一条 Mod → 「安装到 Penumbra」");
            return;
        }

        foreach (var job in jobs.TakeLast(5).ToArray())
        {
            ImGui.PushID(job.Id);

            var color = job.State switch
            {
                JobState.Done => Green,
                JobState.Error => Red,
                JobState.Cancelled => Dim,
                _ => Yellow,
            };

            ImGui.TextColored(color, $"[{StateLabel(job.State)}]");
            ImGui.SameLine();
            ImGui.TextUnformatted(job.Title);

            if (job.Message.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Dim, job.Message);
            }
            if (job.UsedBrowserChannel)
            {
                ImGui.SameLine();
                ImGui.TextColored(Yellow, "🌐浏览器通道");
            }
            if (job.Error.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Red, job.Error);
            }
            if (!job.Finished)
            {
                ImGui.SameLine();
                if (ImGui.Button("取消")) _plugin.Jobs.Cancel(job.Id);
            }

            if (job.State == JobState.Downloading)
                ImGui.ProgressBar(job.Percent / 100f, new Vector2(-1, 4), "");

            ImGui.PopID();
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

    private void DrawHeader()
    {
        ImGui.TextColored(Dim, $"v{Plugin.PluginVersion}   站点共 {_totalMods} 个 Mod   第 {_page} 页   " +
                               (_plugin.Site.CacheDirectory.Length > 0 ? "缓存：" + _plugin.Site.CacheDirectory : ""));
        if (_error.Length > 0)
            ImGui.TextColored(Red, _error);
        else
            ImGui.TextColored(_busy ? Yellow : Green, _status);
    }

    private void DrawSearchBar()
    {
        ImGui.TextUnformatted("关键词");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputText("##q", ref _query, 128);
        if (ImGui.IsItemDeactivatedAfterEdit())
            StartSearch(1);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        ImGui.Combo("##sort", ref _sortIndex, _sortLabels, _sortLabels.Length);

        ImGui.SameLine();
        ImGui.Checkbox("标签精确", ref _useTags);
        ImGui.SameLine();
        ImGui.Checkbox("仅当前版本兼容", ref _onlyDt);
        ImGui.SameLine();
        ImGui.Checkbox("显示 NSFW", ref _nsfw);

        ImGui.SameLine();
        if (_busy)
        {
            ImGui.BeginDisabled();
            ImGui.Button("搜索中…");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("搜索"))
        {
            StartSearch(1);
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(_busy || _page <= 1);
        if (ImGui.Button("◀ 上一页")) StartSearch(_page - 1);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(_busy || _hits.Count == 0);
        if (ImGui.Button("下一页 ▶")) StartSearch(_page + 1);
        ImGui.EndDisabled();
    }

    private void DrawList()
    {
        if (_hits.Count == 0)
        {
            ImGui.TextColored(Dim, _busy ? "搜索中…" : "还没有结果。敲关键词后回车，或直接点「搜索」看最新发布。");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                      ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable("xma-hits", 6, flags))
        {
            ImGui.TableSetupColumn("图", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("作者", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("浏览", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("标记", ImGuiTableColumnFlags.WidthFixed, 56);
            ImGui.TableHeadersRow();

            foreach (var hit in _hits)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                var wrap = _plugin.Thumbs.Get(hit.ThumbUrl)?.GetWrapOrDefault();
                if (wrap is not null)
                    ImGui.Image(wrap.Handle, Fit(wrap.Size, 64f, 40f));
                else
                    ImGui.Dummy(new Vector2(64, 40));

                ImGui.TableNextColumn();
                var label = $"{hit.Title}##{hit.ModId}";
                if (ImGui.Selectable(label, _selected?.ModId == hit.ModId))
                {
                    _selected = hit;
                    _previewIndex = 0;
                    LoadDetail(hit);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(hit.Author);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(hit.Type);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(hit.Views > 0 ? hit.Views.ToString() : "-");

                ImGui.TableNextColumn();
                if (hit.IsNew) ImGui.TextColored(Green, "NEW");
                else if (hit.IsUpdated) ImGui.TextColored(Yellow, "UPD");
                else ImGui.TextUnformatted("");
            }

            ImGui.EndTable();
        }
    }

    private void DrawDetail()
    {
        var d = _detail;
        if (_selected is null)
        {
            ImGui.TextColored(Dim, "左边选一条 Mod，这里显示详情与「安装到 Penumbra」。");
            return;
        }

        if (_detailBusy)
        {
            ImGui.TextColored(Yellow, $"正在读取 {_selected.ModId} 的详情…");
            return;
        }

        if (d is null)
        {
            ImGui.TextColored(Red, "详情读取失败。");
            return;
        }

        ImGui.TextWrapped(d.Title.Length > 0 ? d.Title : _selected.Title);
        ImGui.TextColored(Dim, $"作者 {d.Author}   类型 {d.Type}   站点 id {d.ModId}");
        ImGui.TextColored(Dim, $"最后更新 {Fmt(d.LastUpdateLocal)}   发布 {Fmt(d.ReleaseLocal)}   浏览 {_selected.Views}");
        ImGui.Separator();

        // 预览图（大图）——取自站点 openImage() 的图片地址，走缩略图缓存
        if (d.Images.Count > 0)
        {
            var idx = ((_previewIndex % d.Images.Count) + d.Images.Count) % d.Images.Count;
            var w = MathF.Max(160f, ImGui.GetContentRegionAvail().X);
            var wrap = _plugin.Thumbs.Get(d.Images[idx])?.GetWrapOrDefault();
            if (wrap is not null)
                ImGui.Image(wrap.Handle, Fit(wrap.Size, w, 240f));
            else
                ImGui.Dummy(new Vector2(w, 40));

            if (d.Images.Count > 1)
            {
                if (ImGui.Button("‹")) _previewIndex = idx - 1;
                ImGui.SameLine();
                ImGui.TextColored(Dim, $"{idx + 1} / {d.Images.Count}");
                ImGui.SameLine();
                if (ImGui.Button("›")) _previewIndex = idx + 1;
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
        ImGui.TextColored(Dim, $"预览图 {d.Images.Count} 张");

        ImGui.Separator();
        ImGui.TextUnformatted("描述");
        ImGui.BeginChild("xma-desc", new Vector2(0, 140), true);
        ImGui.TextWrapped(d.Description.Length > 0 ? d.Description : "（站点上没有描述）");
        ImGui.EndChild();

        ImGui.Separator();

        if (d.IsExternalLink)
        {
            ImGui.TextColored(Yellow, "这条 Mod 的文件在外部站点（作者外链），插件拿不到直链，没法一键安装。");
            if (ImGui.Button("在浏览器打开")) OpenExternal(d.AbsoluteDownloadUrl);
            ImGui.SameLine();
            if (ImGui.Button("复制外链")) ImGui.SetClipboardText(d.AbsoluteDownloadUrl);
        }
        else
        {
            ImGui.TextWrapped("下载：" + d.FileNameHint);
            var penumbraOk = _plugin.Penumbra.Available;
            ImGui.BeginDisabled(!penumbraOk);
            if (ImGui.Button("安装到 Penumbra")) _plugin.Jobs.Enqueue(d);
            ImGui.EndDisabled();
            if (!penumbraOk)
            {
                ImGui.SameLine();
                ImGui.TextColored(Red, "Penumbra 未连接");
            }
            ImGui.SameLine();
            if (ImGui.Button("复制下载链接")) ImGui.SetClipboardText(d.AbsoluteDownloadUrl);
            if (_detailViaChannel)
            {
                ImGui.SameLine();
                ImGui.TextColored(Yellow, "🌐 走浏览器通道读取");
            }
        }
    }

    private void StartSearch(int page)
    {
        if (_busy || page < 1) return;

        _page = page;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _busy = true;
        _error = string.Empty;
        _status = $"搜索中…（第 {page} 页）";

        var q = new SearchQuery
        {
            Page = page,
            OnlyDtCompatible = _onlyDt,
            Nsfw = _nsfw,
            SortBy = Sorts[_sortIndex].Value,
            SortOrder = "desc",
        };
        var keyword = _query.Trim();
        if (keyword.Length > 0)
        {
            if (_useTags) q.Tags = keyword;
            else q.Text = keyword;
        }

        var client = _plugin.Site;
        var cfg = _plugin.Configuration;

        Task.Run(async () =>
        {
            try
            {
                var result = await client.SearchAsync(q, ct).ConfigureAwait(false);
                _hits = result.Hits;
                _totalMods = result.TotalMods;
                _selected = null;
                _detail = null;
                _status = result.Hits.Count == 0 ? "这一页没有结果" : $"第 {page} 页，共 {result.Hits.Count} 条";
                _plugin.Thumbs.Trim();
                cfg.OnlyDtCompatible = _onlyDt;
                cfg.ShowNsfw = _nsfw;
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

        var client = _plugin.Site;
        var channel = _plugin.Channel;
        var modId = hit.ModId;

        Task.Run(async () =>
        {
            try
            {
                _detail = await client.DetailAsync(modId).ConfigureAwait(false);
            }
            catch (XmaAccessException directDenied)
            {
                // 直连被 403（NSFW 等需要登录态的内容）→ 交给浏览器通道再试一次
                if (!channel.Enabled)
                {
                    _error = directDenied.Message;
                }
                else
                {
                    try
                    {
                        var html = await channel.GetHtmlAsync(XmaUrls.ModPage(modId)).ConfigureAwait(false);
                        if (html is null || html.Length == 0)
                        {
                            _error = "浏览器通道也没取到页面：" + channel.LastError;
                        }
                        else
                        {
                            var parsed = XmaParser.ParseDetail(html, modId, XmaUrls.ModPage(modId));
                            if (parsed.Title.Length == 0 && parsed.DownloadHref.Length == 0)
                                _error = "浏览器通道取到的页面解析不出内容（可能站点改了结构）";
                            else
                            {
                                _detail = parsed;
                                _detailViaChannel = true;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _error = $"浏览器通道失败：{e.GetType().Name} {e.Message}";
                    }
                }
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

    private static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 忽略 */ }
    }

    private static string Fmt(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm") ?? "-";

    private static Vector2 Fit(Vector2 src, float maxW, float maxH)
    {
        if (src.X <= 0 || src.Y <= 0) return new Vector2(maxW, maxH);
        var k = MathF.Min(maxW / src.X, maxH / src.Y);
        return new Vector2(src.X * k, src.Y * k);
    }

    private void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("设置##xma-settings")) return;

        var cfg = _plugin.Configuration;
        var changed = false;

        ImGui.TextColored(Dim, "下载与安装");
        var enable = cfg.EnableOnInstall;
        if (ImGui.Checkbox("装完自动在当前集合启用", ref enable)) { cfg.EnableOnInstall = enable; changed = true; }
        var redraw = cfg.RedrawOnInstall;
        if (ImGui.Checkbox("装完触发重绘（立刻可见）", ref redraw)) { cfg.RedrawOnInstall = redraw; changed = true; }
        ImGui.SameLine();
        var cleanup = cfg.CleanupAfterInstall;
        if (ImGui.Checkbox("装完删除下载的包", ref cleanup)) { cfg.CleanupAfterInstall = cleanup; changed = true; }

        var delay = cfg.RequestDelayMs;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("请求间隔（毫秒，礼貌抓站）", ref delay))
        {
            cfg.RequestDelayMs = Math.Clamp(delay, 0, 5000);
            changed = true;
        }

        ImGui.TextColored(Dim, "缓存目录：" + _plugin.Site.CacheDirectory);

        ImGui.Separator();
        ImGui.TextColored(Dim, "浏览器通道（NSFW / 需要登录态的内容）");
        var chan = cfg.EnableBrowserChannel;
        if (ImGui.Checkbox("启用浏览器通道（借用本机浏览器的登录态）", ref chan))
        {
            cfg.EnableBrowserChannel = chan;
            changed = true;
        }
        ImGui.SameLine();
        var autoPick = cfg.AutoPickProfile;
        if (ImGui.Checkbox("自动挑选可用浏览器", ref autoPick)) { cfg.AutoPickProfile = autoPick; changed = true; }

        if (_browserReport.Length == 0)
        {
            var list = BrowserChannel.Detect();
            _browserReport = string.Join("　", list.Select(c => $"{c.Label}={(c.LooksUsable ? "可用" : (File.Exists(c.Exe) ? "已装无登录态" : "未装"))}"));
        }
        ImGui.TextColored(Dim, "检测到：" + _browserReport);

        var exe = cfg.BrowserExe;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("浏览器路径（留空=自动）##xma-brow", ref exe, 512))
        {
            cfg.BrowserExe = exe;
            changed = true;
        }

        var timeout = cfg.BrowserTimeoutSec;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("通道超时（秒）", ref timeout))
        {
            cfg.BrowserTimeoutSec = Math.Clamp(timeout, 30, 900);
            changed = true;
        }
        ImGui.SameLine();
        var keepProfile = cfg.CleanupBorrowedProfile;
        if (ImGui.Checkbox("用完删除临时 profile（含 cookie 副本）", ref keepProfile))
        {
            cfg.CleanupBorrowedProfile = keepProfile;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextColored(Dim, "缩略图");
        var mb = cfg.ThumbCacheMb;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("缓存上限（MB）", ref mb))
        {
            cfg.ThumbCacheMb = Math.Clamp(mb, 16, 4096);
            changed = true;
        }

        if (changed) cfg.Save();
        ImGui.Separator();
    }

    public void DisposeCancel() => _cts?.Cancel();
}
