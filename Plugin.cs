using System;
using System.IO;
using System.Net.Http;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XmaBrowser.Site;
using XmaBrowser.Install;
using XmaBrowser.Browser;
using XmaBrowser.Cover;
using XmaBrowser.Ui;
using XmaBrowser.Translate;
using XmaBrowser.Windows;

namespace XmaBrowser;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "XMA Browser";

    /// <summary>插件版本（取自程序集，即 csproj 的 &lt;Version&gt;）。</summary>
    public static string PluginVersion => typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private const string CommandName = "/xma";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    public Configuration Configuration { get; }
    public XmaClient Site { get; }
    public PenumbraBridge Penumbra { get; }
    public BrowserChannel Channel { get; }
    public InstallQueue Jobs { get; }
    public ThumbCache Thumbs { get; }
    public CoverFetcher Covers { get; }
    public PenumbraCoverDrawer CoverDrawer { get; }
    public Translator Translator { get; }
    public HttpClient Http => _http;

    // 各类缓存目录（缓存维护与设置页显示占用要用）
    public string CacheDir { get; }
    public string ThumbsDir { get; }
    public string CoversDir { get; }
    public string TempDir { get; }
    public string TranslateDir { get; }

    public readonly WindowSystem WindowSystem = new("XmaBrowser");
    private readonly MainWindow _window;
    private readonly HttpClient _http;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Migrate()) Configuration.Save();   // 老的 bool「显示 NSFW」→ 三态

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        var cacheDir = string.IsNullOrWhiteSpace(Configuration.CacheDirectory)
            ? Path.Combine(PluginInterface.ConfigDirectory.FullName, "downloads")
            : Configuration.CacheDirectory;
        Directory.CreateDirectory(cacheDir);
        CacheDir = cacheDir;
        ThumbsDir = Path.Combine(cacheDir, "thumbs");
        CoversDir = Path.Combine(cacheDir, "covers");
        TempDir = Path.Combine(cacheDir, "temp");
        TranslateDir = Path.Combine(PluginInterface.ConfigDirectory.FullName, "translate");

        Site = new XmaClient(_http, Configuration, cacheDir, new DalamudLogAdapter(Log));

        var logAdapter = new DalamudLogAdapter(Log);
        Penumbra = new PenumbraBridge(PluginInterface, Framework, Log);
        Channel = new BrowserChannel(Configuration, logAdapter);
        Thumbs = new ThumbCache(_http, TextureProvider, Log, Path.Combine(cacheDir, "thumbs"),
                                Configuration.ThumbCacheMb, Channel);
        Covers = new CoverFetcher(Site, Channel, logAdapter, Path.Combine(cacheDir, "covers"));
        Jobs = new InstallQueue(Site, Penumbra, Configuration, Channel, Covers, logAdapter,
                                Path.Combine(cacheDir, "temp"),
                                msg => ChatGui.Print(msg));
        // 取图过程接进「关于」页的诊断日志：这类问题必须能拿到真实过程，不能靠猜
        Thumbs.Diag = msg => Jobs.Note(msg);
        // 通道每一步的耗时也进同一份日志（"卡在哪一段"要能直接看出来）
        Channel.Diag = msg => Jobs.Note(msg);
        // 日志同时落盘，方便主人直接把文件发过来
        Jobs.DiagFile = Path.Combine(PluginInterface.ConfigDirectory.FullName, "logs",
                                     $"diag-{DateTime.Now:yyyyMMdd}.log");
        CoverDrawer = new PenumbraCoverDrawer(PluginInterface, Configuration, Penumbra, TextureProvider, Log);
        Translator = new Translator(Configuration, Path.Combine(PluginInterface.ConfigDirectory.FullName, "translate"),
                                    msg => Log.Debug("[XmaBrowser] " + msg));
        CoverInjector.CleanupOld();     // 清掉上次注入留在 %TEMP% 的包

        _window = new MainWindow(this);
        WindowSystem.AddWindow(_window);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 XMA Browser 窗口（在游戏里逛 XIV Mod Archive）",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

        Log.Information($"[XmaBrowser] 已加载 v{PluginVersion}，缓存目录 {cacheDir}");
        ChatGui.Print($"[XMA Browser] 已加载。用 {CommandName} 打开窗口。");
    }

    private void OnCommand(string command, string args) => _window.Toggle();

    public void ToggleMainUi() => _window.Toggle();

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        CommandManager.RemoveHandler(CommandName);

        Site.Dispose();
        Jobs.Dispose();
        Thumbs.Dispose();
        CoverDrawer.Dispose();
        Translator.Dispose();
        Channel.Dispose();
        Penumbra.Dispose();
        _http.Dispose();
        WindowSystem.RemoveAllWindows();
    }
}
