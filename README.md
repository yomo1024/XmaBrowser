# XMA Browser —— 在游戏里逛 XIV Mod Archive

把一个 Dalamud 插件装进游戏，**在游戏内搜索/浏览 XIVModArchive，点一下就把 Mod 装进 Penumbra 并即时生效**。
不再需要切出游戏 → 开管理器网页 → 点安装 → 回游戏确认。

> 与「FFXIV XMA Manager」三件套**互不依赖**：这是独立插件，不读写管理器的数据，也不需要管理器在运行。

---

## 功能

| 功能 | 说明 |
|---|---|
| 分页签界面 | **浏览 / 队列 / 设置 / 关于** 四个页签，各管一摊，后续加功能有地方放 |
| 搜索 / 浏览 | 关键词 + **高级搜索弹窗**（名称 / 作者 / 标签 / 种族 / 性别 / 类型 id / 影响替换）、排序（最新发布 / 最近更新 / 今日最多浏览）、仅当前版本兼容、显示 NSFW、翻页 |
| 详情 | 大图轮播、作者、类型、标签、影响/替换、种族/性别、更新时间、描述 |
| 一键安装 | 下载 → 解析包 → 装进 Penumbra → （可选）在当前集合启用 → 重绘立刻可见 |
| **封面** | 安装时自动取封面（详情页大图优先，退回列表缩略图）：**塞进包一起交付** + **在 Penumbra 的 mod 面板里画出来**（挂 `PreSettingsTabBarDraw`，与 Heliosphere 同机制） |
| 安装队列 | 独立页签，显示状态 / 进度条 / 信息 / 取消；串行执行（礼貌抓站） |
| **容器支持** | **zip / 7z / rar / tar** 都能装，且会先「看清里面是什么」再决定怎么装（见下） |
| 缩略图 | 列表缩略图 + 详情大图，磁盘缓存可配上限、自动淘汰最久未用 |
| 设置 | 装完是否自动启用/重绘/删包、请求间隔、封面、浏览器通道、缩略图上限，集中在「设置」页签 |

命令：`/xma` 打开窗口。

### 压缩包怎么处理（zip / 7z / rar / tar 同一套判断）

容器里「有几个包、有没有包」跟格式无关，所以判断逻辑只有一份，按优先级：

| 容器里的情况 | 处理 |
|---|---|
| 本身就是 `.pmp/.pcp/.ttmp/.ttmp2` | 直接 `InstallMod` |
| 里面有 **1 个或多个** Mod 包（哪怕藏在多层子目录里） | 全部解出来，**逐个装** |
| 里面没有包，但**根上**就是 Mod 结构（有 `meta.json`/`default_mod.json`，或有 `files/`、有素材文件） | 整包作为 **1 个 Mod** 登记 |
| 里面没有包，但**几个顶层子目录各自是一个 Mod**（每个含 meta / files / 素材） | **每个子目录单独登记成一条 Mod**（不混成一坨） |
| 里面既没包也没 Mod 结构（纯说明书 / 素材包） | 明确报错，并**列出顶层内容**告诉你为什么装不了 |

> 队列里会写明这次按哪种方式处理的（例如「包内发现 3 个 Mod 包（逐个安装）」）。

### 更新日志

- **v0.2.1** —— 支持 **7z / rar / tar**（SharpCompress 1.0.0，≥0.48.0 以避开 zip slip 类漏洞告警）；包解析改为**容器无关 + 结构探测**，处理上面那张表的全部情况，「不像 Mod」时给出可读原因。
- **v0.2.0** —— 界面改分页签（浏览/队列/设置/关于）；搜索重做（新增高级搜索弹窗）；**封面**：随包注入 + 在 Penumbra 面板里绘制；站点对直连做 Cloudflare 挑战时自动降级到浏览器通道。
- v0.1.0 —— 首个版本：浏览 / 搜索 / 详情 / 一键装进 Penumbra + 浏览器通道处理 NSFW。


---

## 两条通道（这是本插件最关键的设计）

实测结论（本机验证，不是推测）：

| 内容 | 直连 HttpClient | 浏览器通道 |
|---|---|---|
| 公开（SFW）搜索 / 详情 | ✅ 200 | — |
| 公开（SFW）下载 | ✅ 206 + PK 头 | — |
| **NSFW 详情页** | ❌ 403 | ✅ 200 |
| **NSFW 下载** | ❌ 404 | ✅ 实测拿到 30.8 MB 的 `.ttmp2`（PK 头） |

也就是说：**NSFW 卡的不是"登录/cookie"，而是 Cloudflare 认不认你是浏览器**。
所以：

- **公开内容**走直连 HttpClient（快、有进度、可取消）
- **NSFW 等需要登录态的内容**交给**本机浏览器**去取 —— 你**不需要在插件里做任何登录操作**

### 浏览器通道怎么工作（以及它的边界）

1. 找到本机已安装的浏览器（Chrome / Edge / CentBrowser / Brave）里**已登录过 XIVModArchive** 的那个；
2. 把它的 `Cookies` + `Local State` **复制**到插件自己的临时 profile（浏览器开着也能复制）；
3. 用**同一个浏览器 exe** 起一个 `--headless=new` 实例（无窗口），通过 CDP 让浏览器自己去取页面 / 下载文件；
4. 用完关掉进程，按设置**删除临时 profile（里面有 cookie 副本）**。

为什么不让插件自己解密 cookie：Chrome 127+ 起 cookie 是 **App-Bound 加密（v20）**，外部程序用 DPAPI 解密必然失败（实测 `InvalidTag`）。走"复制 profile + 同 exe 启动"就是让**浏览器替我们解密**，我们只通过 CDP 读结果。

**边界**：如果浏览器里从没登录过 XMA，NSFW 自然取不到（这时插件会明确告诉你）。

---

## 安装

### 方式一：开发者插件（本地）

把 `bin/Release/` 整个目录拷到：

```
%APPDATA%\XIVLauncher\devPlugins\XmaBrowser\
```

（区域版启动器是 `XIVLauncherCN`）然后 `/xlplugins` → Dev Plugins → 启用 **XMA Browser**。

### 方式二：自定义插件源（推荐，能在线更新）

Dalamud → 设置 → 实验性 → **自定义插件源** → 粘贴（主地址）：

```
https://raw.githubusercontent.com/yomo1024/XmaBrowser/main/pluginmaster.json
```

主地址不通时用备选（下载走 jsdelivr，国内多数网络更快）：

```
https://cdn.jsdelivr.net/gh/yomo1024/XmaBrowser@main/pluginmaster-cdn.json
```

然后在插件列表里搜 **XMA Browser** 安装即可。

> **注意**：加了源之后如果列表里压根看不到这个插件，说明清单没取到（不是安装失败）。
> 用那台机器的浏览器打开上面的地址：能看到 JSON 文本 = 网络没问题（换个源重试）；打不开 = 网络到不了，改用「方式一」本地安装。
>
> jsdelivr 对**新建仓库的新路径**会先返回 404（负缓存），要 `https://purge.jsdelivr.net/gh/yomo1024/XmaBrowser@main/<文件名>` 刷一下才生效 —— 这就是主地址用 raw 的原因。

---

## 已知限制

- **外链 Mod 不能一键装**：作者把文件放在 Patreon / 自己的站点时没有直链，插件只能「在浏览器打开」。
- 压缩包的判断靠「结构特征」（meta 文件 / `files/` 目录 / 素材扩展名）。如果某个包的写法很另类（既无标记也无素材），会被判成「不像 Mod」并提示原因 —— 这时把它转成 `.pmp` 再装即可。
- **站点改版会让解析失效**：解析层有真实 HTML 样本的回归测试，改版时先看哪条断言挂了。
- 站点同一页固定 15 条，翻页用站点的 `page` 参数。

---

## 开发 / 构建 / 测试

```bash
# 构建（本机没装游戏也能编；DALAMUD_HOME 指向 Dalamud dev 引用库）
export DALAMUD_HOME='E:\Hermes\dalamud-dev'
dotnet build -c Release XmaBrowser.csproj          # 验收口径：0 警告 0 错误

# 无游戏自测（解析层用真实 HTML 样本 + 包解析，不联网）
cd tests/SiteTests && dotnet run -c Release

# 联网实测：SFW 直连 + NSFW 浏览器通道 + 真实下载
cd tests/SiteTests && dotnet run -c Release -- live
```

测试项目**直接 Include 插件的源码**（`Site/`、`Install/PackageResolver.cs`、`Browser/`），所以测的就是真东西；
站点层与浏览器通道都通过窄接口（`IXmaSettings` / `IBrowserSettings` / `IXmaLog`）与 Dalamud 解耦，才能脱离游戏跑。

### 目录

```
XmaBrowser/
  Plugin.cs                 生命周期、/xma 命令、组件接线
  Configuration.cs          设置（实现 IXmaSettings / IBrowserSettings）
  Site/                     站点层（不依赖 Dalamud，可单测）
    XmaClient.cs            HTTP（含实测过的请求头配方）、节流、下载（带进度）
    XmaParser.cs            页面解析（选择器都照着真实 DOM 写）
    XmaModels.cs            模型 + 搜索 URL 拼装 + 时间解析
  Install/
    PenumbraBridge.cs       Penumbra IPC 封装（类型化订阅器，全部切 framework 线程）
    PackageResolver.cs      包解析：内层 .pmp/.ttmp2 → InstallMod；松散文件 → AddMod（含 Zip Slip 防护）
    InstallQueue.cs         安装队列：下载 → 解析 → 安装 → 启用 → 重绘
  Browser/                  浏览器通道（不依赖 Dalamud，可单测）
    BrowserChannel.cs       借 profile + headless 启动 + 取页面/下载
    CdpClient.cs            极简 CDP 客户端（ClientWebSocket）
  Ui/ThumbCache.cs          缩略图磁盘缓存 + 纹理缓存 + 淘汰
  Windows/MainWindow.cs     游戏内界面
  tests/SiteTests/          无游戏自测（含 samples/ 真实 HTML 样本）
```

---

## 隐私与安全

- 插件只**读**站点公开数据；除下载 Mod 包外不向站点发送任何内容。
- 浏览器通道复制的是**你本机浏览器自己的** cookie 文件，只用于让浏览器取 NSFW 内容；
  **不会被插件上传、不会被写入日志**（代码里没有打印 cookie 的地方）。
- 临时 profile 默认用完即删（设置里可关）。
- 全流程不回传任何数据到第三方。

## 许可与致谢

清洁室实现：接口配方（搜索参数、下载链接选择器、`/private/` 直链形态）来自对站点的公开实测，
未复制 `Noevain/ModArchiveBrowser`（AGPL-3.0）的代码。

感谢 Penumbra、Dalamud、XIVModArchive 以及它们各自的作者。
