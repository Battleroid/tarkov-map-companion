# WebSync 局域网同步功能 — 实现规格书（AI 开发指令）

> **状态：已于分支 `websync` 全部实现并交付（2026-10-14）。本文档保留为设计依据与决策记录，
> 不再是待执行指令。** 现役状态见 `AGENTS.md` 的 Current state 一节；给终端用户的说明见
> `README.md` 的 "On a tablet, over the LAN" 一段。文中若干处规格在实现中被实测证据修正，
> 这些修正已就地写回（§4.1 两类路线、§4.2 线程与退订契约、§5.2 投影旋转、§5.4.1 Kestrel 启停、
> §6.2 瓦片缓存 category、§9 体积判据），**以就地修正后的文本为准**，不要照着最初的假设重写。
> 基线测试数已从 656 增至 716。
>
> 历史基线：项目 tarkov-map-companion-self-improv · .NET 8 (`net8.0-windows10.0.19041.0`) ·
> Avalonia 11.3.19 · SkiaSharp 2.88.9 · 立项日期：2026-10-13

---

## 实现后补记（2026-10-14，真机验证暴露的问题）

规格书写成时，客户端只有纸面设计。真机上暴露并已修复的问题，按"不在本文档正文中、但会误导下一个人"的顺序补记如下——**它们全部属于客户端 `Assets/index.html`，服务端不受影响**：

- 客户端 HTML 是内嵌资源、URL 恒定，早期 `/` 端点**没有发任何缓存头**，浏览器启发式缓存，
  导致多轮客户端修复在手机上完全看不到效果。现在 `/` 发 `Cache-Control: no-store, must-revalidate`
  + `Pragma: no-cache`。**改动"没效果"时先查这一条和发布产物，再去读绘制代码。**
- `position:fixed; inset:0` 按**布局视口**定尺寸，而 `innerWidth/innerHeight` 报的是**可视视口**。
  手机上地址栏让两者不等，canvas 被按布局视口显示、却用可视视口算出的后备存储与
  `setTransform(dpr,…)` 绘制，于是**标记被拉伸而 DOM 底图不被拉伸**。现在 `#stage` 与 `#cv`
  一律由 `measure()` 按实测 `vw/vh` 定尺寸。桌面永远复现不了。
- 解析 SVG 文档时用 `document.importNode` 是**复制**、`appendChild` 是**移动**；
  `while(root.firstChild) f.appendChild(document.importNode(...))` 永不终止，会冻死主线程——
  定时器随之停摆，连超时都不会触发。现在用 `appendChild` 排空文档。
- Canvas 2D 状态是累积的，`save()/restore()` 成对也可能泄漏 `globalAlpha/lineWidth/font/fillStyle`。
  标记层现全部包在 `scoped()` 里，`draw()` 每帧先重置变换与绘制基线。
- 标记半径原本直接照抄桌面端**绝对 CSS 像素**（`PoiOverlay` 是 1920px 宽窗口上的值），
  在 366px 手机上相对大约放大 5 倍。现在按 `clamp(vw/1000, 0.6, 1.0)` 换算。
- 局域网地址**不能**按 RFC1918 网段排序：虚拟网卡（WSL/Hyper-V）同样分到私网地址，会压过真实
  WiFi。现在按"同子网的 IPv4 网关是否存在"分层，网关在同子网=真实局域网，优先级最高。
- **客户端 JavaScript 至今没有任何自动化测试。** 716 个测试全部覆盖服务端；上面几条本该被拦住。
  `?selfcheck=1` 是唯一的自动化守卫，且很薄。
- headless Chrome **不等于手机**：`--window-size=390,844 --force-device-scale-factor=2` 产出
  780x1688 截图，但页面实际 `innerWidth=491`，截图静默裁掉了页面，会制造"底图被裁"这类假象。
  任何视觉结论前先从页面里读 `innerWidth/innerHeight/devicePixelRatio` 核对。

---

---

## 0. 环境前置（每次会话必做）

.NET 8 SDK 是用户本地安装、**不在 PATH 上**。所有 dotnet 命令前必须：

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
$env:PATH="$env:USERPROFILE\.dotnet;$env:PATH"
```

`global.json` 钉 8.0.421（`rollForward: latestFeature`），不能删。

验证命令：

```powershell
dotnet test            # 必须保持 656+ 全绿（新增测试后数字增加）
dotnet run --project src/TarkovMapCompanion
scripts\publish.ps1    # 单文件发布，最后必须验证仍可用
```

---

## 1. 已确认决策（不可变更）

| # | 决策 |
|---|---|
| 1 | 方案 A：内嵌 Web 服务 + SVG 底图 + JSON/WebSocket 状态推送；平板浏览器直接访问，不做帧推送、不做独立 App |
| 2 | Web 服务器用 **Kestrel**（`FrameworkReference Microsoft.AspNetCore.App`），接受单文件发布体积 +15~25MB；不用 HttpListener（需 urlacl/管理员），不手写迷你 HTTP |
| 3 | 访问令牌**默认开启**；设置页显示完整 URL + **二维码**，平板扫码直达 |

## 2. 项目硬性约定（违反即返工）

1. **本地化键是契约**。`Localization/StringsEn.json` 与 `StringsZh.json` 键集必须完全一致。所有用户可见文案走 `Localizer.Loc(key)`；中英二选一只允许出现在 `Localizer.Pick(english, chinese)` 内，调用点不得自行判断语言。
2. **只用 System.Text.Json**。禁止引入 Newtonsoft。网络/磁盘 JSON 一律 source-generated context（参照 `Party/PartyProtocol.cs` L231-233 的既有模式）。
3. **`.gitattributes` 承重**：`*.sln`/`*.bat`/`*.ps1` CRLF，其余一律 LF。新增文件按此执行。
4. **PowerShell 会破坏 UTF-8**。读/比对 JSON 用 `python`，不要 `ConvertFrom-Json`。
5. **不读内存、不 hook 游戏进程、不上传任何数据**——这是项目的立身之本。WebSync 是**只读镜像**：所有端点仅 GET + WebSocket 订阅，禁止任何写操作 API。
6. 新增设置字段加在 `Settings/AppSettings.cs`（跟随现有注释风格：XML 文档注释 + 默认值）；持久化经 `Settings/SettingsStore.cs`（temp+move 原子写，已存在，勿改）。
7. 测试放 `tests/TarkovMapCompanion.Tests/`，xunit；需要内部成员时项目已有 `InternalsVisibleTo`。

## 3. 功能定义

桌面程序内嵌一个默认关闭的 Web 服务。用户在设置里开启后，程序监听 `0.0.0.0:{port}`
（默认 8765，仅局域网使用，**禁止**任何 UPnP/端口映射/公网暴露）。同一局域网的平板/手机
浏览器打开 `http://{局域网IP}:{port}/?key={令牌}`，即可看到与桌面端一致的地图：底图、
玩家位置/朝向/轨迹、路线航点、撤离点引导线、POI/撤离点可用性、任务标记、注释、队友与
Ping。状态变化 ≤1s 内同步到浏览器。

### 总体架构

```
MapSession（现有，不改其职责）
  │  事件：FixApplied / MapChanged / WaypointsChanged / SelectionChanged /
  │        PoisChanged / ExitAvailabilityChanged / AnnotationsChanged /
  │        QuestsChanged / RaidStateChanged / PingAdded
  ▼
WebSync/StateBroadcaster（新增）—— 订阅事件、节流、构造快照、向 WS 客户端广播
  ▼
WebSync/WebSyncServer（新增，Kestrel）
  GET  /                    → 内嵌单页客户端（EmbeddedResource，无 CDN）
  GET  /api/state           → 全量快照 JSON（含地图描述与投影参数）
  GET  /map/{mapId}.svg     → AssetCache 缓存 SVG + SvgLayerFilter 楼层过滤
  GET  /tiles/{mapId}/{z}/{x}/{y}.png → AssetCache 瓦片代理（仅无 SVG 的地图）
  WS   /ws                  → 首帧全量快照，随后增量推送
  全部端点（除静态页外的 API/WS）要求 ?key= 令牌校验，不匹配返回 404
```

## 4. 关键既有接口（实现时直接复用，禁止重写）

| 用途 | 类型/成员 | 位置 |
|---|---|---|
| 当前地图/事件总线 | `MapSession.CurrentMap`、`MapSession.Overlays`、`MapSession.QuestProgressFromLog`，事件见 §3 架构图 | `MapSession.cs` |
| 地图元数据 | `GameMap`：`Key/NormalizedName/DisplayName/Projection/BaseRect/SvgBaseRect/SvgUrl/**BaseSvgLayerId**/BaseTilePathTemplate/TileSize/MinZoomLevel/MaxZoomLevel/Floors/HasSvg/HasTiles`（`BaseSvgLayerId` 是 `string?`，public）；`MapFloor` 是 positional record：`Name/**SvgLayerId**/TilePathTemplate/ShownByDefault/Extents`，外加 `Covers(GamePosition)`。**注意 `MapFloor.Name` 与 `MapFloor.SvgLayerId` 是两个命名空间**（见 §6.1） | `Maps/GameMap.cs` |
| 坐标投影 | `MapProjection.ToBase(GamePosition)` / `ToBase(double, double)`；仿射系数是 6 个**私有标量** `_cos/_sin/_a/_b/_c/_d`（**没有** `transform` 矩阵字段，`transform` 只是构造参数），**需在 §5.2 扩展只读访问器**；`MapProjection.CoordinateRotationDegrees`；`MapProjection.ScreenAngleDegrees(yaw)` | `Maps/MapProjection.cs` |
| 资产缓存 | `AssetCache.GetStringAsync(url, category, ct)` / `GetAsync(...)` / `IsCached` / `PathFor`；`AllowNetwork`；`Root` 是 `init`-only 可注入（测试用 `new AssetCache { Root = tmp }`）。**⚠️ tile 的 category 不是裸 `"tiles"`，而是 `Path.Combine("tiles", map.NormalizedName)`**（SVG 才是裸 `"svg"`）——必须与 `TileMapSource.QueueFetch` 一致，否则平板端不复用桌面端已缓存的瓦片，离线时取不到。**AssetCache 不实现 `IDisposable`**，内部自建的 `HttpClient` 永不释放——不要试图 dispose 它 | `Data/AssetCache.cs` |
| SVG 楼层过滤 | `SvgLayerFilter.Filter(string svgText, string? baseLayerId, IReadOnlyCollection<string> extraLayerIds, bool includeBase = true)`——**第 3 参是"额外图层 id"集合，不是 activeFloorNames；第 4 参才是 includeBase** | `Rendering/SvgLayerFilter.cs` |
| 玩家状态 | `PlayerOverlay.Current`（`PlayerFix`）、`PlayerOverlay.History`（轨迹，最多 TrailLength=12）、`ActiveFloors` | `Rendering/PlayerOverlay.cs` |
| 路线 | `WaypointOverlay.Waypoints`（`Waypoint{Position,Base,Number,Visited}`）、`WaypointOverlay.Next`、`SetSharedRoutes` 的 `SharedRoute{Owner,Map,Color,Points}` | `Rendering/WaypointOverlay.cs` |
| 引导线 | `ExtractLineOverlay.Target`（`MapPoi`）、`.Waypoint`、`.PlayerPosition`、`.PlayerYawDegrees`、`.GuideBase`、`.DistanceMeters`、`.RelativeBearingDegrees`（后三者为可空） | `Rendering/ExtractLineOverlay.cs` |
| POI | `PoiOverlay.Pois` / `Visible`（`IDictionary<PoiKind,bool>`）/ `Availability`；`MapPoi{Id,Name,NameZh,Kind,Position,Base,Elevation,IsConditional,IsSingleUse,AvailableThisRaid}` | `Rendering/PoiOverlay.cs`、`Data/MapPoi.cs` |
| 任务 | `QuestOverlay.Marks`（`QuestMark` record：`TaskId/TaskName/ObjectiveId/Description/SKColor Color/GamePosition Position/int Index/bool OneOf/IReadOnlyList<(double X,double Z)> Outline`）。**⚠️ 唯一不返回快照的 Overlay**：`Marks` 在锁内直接返回 live `_marks`（其他 Overlay 都在锁内 copy）——见 §4.1 线程契约 | `Rendering/QuestOverlay.cs` |
| 注释 | `AnnotationStore`（事件 `MapSession.AnnotationsChanged`）；`MapAnnotation{Id,Map,X,Z,Text,Author}` | `Data/AnnotationStore.cs` |
| 队伍 | `PartySession.Peers`（`PartyPeer{Name,Map,Position,Yaw,HasPosition,IsSelf,Color,LatencyMs,...}`）、**`PartySession.Routes` 是 `IReadOnlyList<PeerRoute>`，而 `PeerRoute{Name,Map,List<RoutePoint> Points}`——没有 `Color`，它不是 `SharedRoute`**；事件 `Changed`/`RoutesChanged`/`PingReceived`（`EventHandler<PeerPosition>`） | `Party/PartySession.cs` |
| Ping | `MapPing{required Name, required Map, required GamePosition Position, required SKColor Color, DateTime PlacedUtc, double AgeSeconds}`，事件 `MapSession.PingAdded` | `Rendering/PingOverlay.cs` |
| 本地化 | `Localizer.Loc(key)` / `Loc(key, params object[])` / `Pick(en, zh)`；键是**扁平点分字符串**（`About.AppName`），不是嵌套对象 | `Localization/Localizer.cs` |
| 应用路径 | `AppPaths.ConfigDirectory` / `AppPaths.CacheDirectory` / `AppPaths.TileCacheDirectory`（三者都会 EnsureDirectory） | `Settings/SettingsStore.cs` |
| 瓦片模板 | `TileMapSource.ResolveTemplate(IReadOnlyCollection<string> activeFloorNames, bool includeBase)` 是 **private**，无法直接复用（见 §6.2）；模板展开逻辑：`template.Replace("{z}",...).Replace("{x}",...).Replace("{y}",...)`；楼层模板 `MapFloor.TilePathTemplate` | `Rendering/TileMapSource.cs` |

### 4.1 两类路线数据源不同，不要混

| DTO 字段 | 数据源 | 类型 | 备注 |
|---|---|---|---|
| `sharedRoutes` | `WaypointOverlay` 经 `SetSharedRoutes()` 写入 | `SharedRoute(string Owner, string Map, SKColor Color, IReadOnlyList<GamePosition> Points)` | 桌面端产出的**已着色**共享路线，与语言无关，`Owner` 可直接下发 |
| 队友路线 | `PartySession.Routes` | `PeerRoute(string Name, string Map, List<RoutePoint> Points)` | **无 Color**；客户端需自己按 `PartyPeer.Color` 上色，且 `RoutePoint` 与 `GamePosition` 不是同一类型 |

两者都要出现在快照里，但**序列化路径与颜色来源不同**，不要图省事统一成 `SharedRoute`。

### 4.2 Overlay 获取、线程契约与退订纪律

**获取方式**：`MapSession` 已经暴露了强类型 public 属性——`Player` / `Pois` / `ExtractLine` / `Waypoints` / `Party` / `Peers` / `Pings` / `Quests` / `Annotations` / `Heatmap`。**直接用这些属性，不要遍历 `Overlays` 按类型查找**（`Overlays` 每次访问都会 new 一个新数组）。本节不需要任何"参照 MainWindow 接线方式"的额外设计。

**线程契约（实现必须遵守）**：

1. `MapSession` 的所有事件在 **watcher / socket 后台线程**触发，UI 侧靠 `Dispatcher.UIThread.Post` 回到主线程。StateBroadcaster 同样运行在后台线程，**禁止把任何工作 marshal 回 UI 线程**（Kestrel 与 Avalonia dispatcher 混用是 §11 死锁风险的来源）。
2. 除 `QuestOverlay.Marks` 外，所有 Overlay 的只读属性都在私有 `_gate` 锁内**复制**后再返回 —— 多个读者安全。
   **`QuestOverlay.Marks` 返回 live 集合**。StateBroadcaster 在后台线程枚举它会与 UI 绘制线程竞争（仓库已有 `PlayerOverlayConcurrencyTests` 关注这条）。二选一，并在测试里锁住：
   - 首选（零侵入）：StateBroadcaster 读取后**立刻 copy 成新 list/record** 再用，禁止持有跨越 await；
   - 备选（侵入既有代码，需单开 commit）：把 `Marks` 改为返回快照。若选此项，必须在提交说明里点明它改了既有行为。
   **禁止**持有任何 Overlay 的锁，也禁止为了取快照而重入绘制逻辑。
3. **退订**：`MapSession.Dispose()` 只退订它自己内部的 handler，**不会清理外部订阅者**。StateBroadcaster 必须自行保存所有 handler 引用、在 Dispose 时逐个 `-=`。否则反复开关偏好设置窗口 / 重开 MinimapWindow 会泄漏订阅并持有已死 WebSocket。参照 `MinimapWindow.Teardown()` 的退订写法。

---

## 5. 里程碑 M1 — 服务骨架与状态快照

### 5.1 项目文件改动

`src/TarkovMapCompanion/TarkovMapCompanion.csproj` 增加：

```xml
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>
```

同时把 `WebSync/Assets/**` 以 `EmbeddedResource` 嵌入（逻辑名前缀 `TarkovMapCompanion.WebSync.Assets.`）。

**验证**：`dotnet build` 通过；`scripts/publish.ps1` 产物可运行，确认自包含单文件能把 ASP.NET Core 框架打进去，**并把体积增量写进提交说明**（当前基线 **50.6 MB**，ASP.NET Core 8 共享框架 27.2 MB 经 `EnableCompressionInSingleFile` 压缩后预计 +8~15 MB，落在 ~60~65 MB。这是**预期内**的，不要试图压到 30MB —— 见 §11）。

**EmbeddedResource 的坑（既有 csproj 已有先例）**：文件名里出现 `.zh.` 这类**看似文化的片段**时，MSBuild 会把文件路由进 satellite assembly 而不是嵌入，运行时查找返回空。参照 csproj 里 `extract-notes.zh.json` 的处理（`<WithCulture>false</WithCulture>` + 显式 `<LogicalName>`）。`index.html` 不触发此规则，但如果将来拆成 `index.en.html` / `index.zh.html` **必须**加同样的 override。逻辑名前缀沿用 `TarkovMapCompanion.WebSync.Assets.`。

**不得改动的既有发布参数**：`scripts/publish.ps1` 与 `.github/workflows/{ci,release}.yml` 目前统一使用 `-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`，**没有** `PublishTrimmed` / `PublishReadyToRun`。ASP.NET Core 与自包含单文件是官方支持的组合，这三个开关不受影响 —— **本功能不得改动这些发布参数**。另注：`publish.ps1` 不跑冒烟测试（CI 才跑 `--render-test`），体积也没有任何门禁。

### 5.2 `MapProjection` 只读扩展（最小侵入）

为把投影参数下发给浏览器，`MapProjection` 新增**只读**属性（不改既有行为、不改任何现有方法）：

```csharp
/// <summary>
/// Affine coefficients of ToBase, exposed so WebSync clients can reproject in JS.
/// Mirrors ToBase exactly — see the formula below. Read-only.
/// </summary>
public MapProjectionAffine Affine => ...
```

**真实实现是"先旋转、再缩放、平移"，不是通用仿射矩阵。** 现有私有字段是 6 个标量（**不存在 `transform` 矩阵字段**，`transform` 只是构造参数名）：

```csharp
private readonly double _cos, _sin, _a, _b, _c, _d;

public MapPoint ToBase(double gameX, double gameZ)
{
    var rx = gameX * _cos - gameZ * _sin;
    var ry = gameX * _sin + gameZ * _cos;
    return new MapPoint(_a * rx + _b, -_c * ry + _d);
}
```

因此下发给浏览器的参数集**必须**是 `{ cos, sin, a, b, c, d, coordinateRotationDegrees }`，客户端复刻为：

```js
const rx = x * cos - z * sin;
const ry = x * sin + z * cos;
const bx = a * rx + b;
const by = -c * ry + d;
```

⚠️ 两处必须一起下发，缺一即错：

- `CoordinateRotationDegrees`（构造入参，public 可读）不导出，客户端无从校核。
- **朝向不要在客户端重算。** `ScreenAngleDegrees(yaw)` 内部还含一步翻转：

  ```csharp
  var angle = yawDegrees + CoordinateRotationDegrees;
  if (Math.Abs(CoordinateRotationDegrees % 180.0) > 1e-9) angle += 180.0;
  return Normalize360(angle);
  ```

  快照里已预计算 `player.screenAngleDegrees`（§5.5），客户端**直接用这个值画朝向**，自己再算会反向 180°。

- 快照字段名沿用 §5.5 的小写 `a/b/c/d/tx/ty` 会与真实语义冲突 —— §5.5 的 `projection` 对象必须改名为 `{ "cos":…, "sin":…, "a":…, "b":…, "c":…, "d":…, "coordinateRotationDegrees":… }`，并去掉 `tx/ty`（`_b/_d` 已经是平移项）。

#### 5.2.1 两个 Overlay 只读访问器（同样是最小侵入）

§5.5 的快照有两个字段**无法**从既有公共接口取到，只能新增只读访问器；否则 StateBroadcaster 会去复制 `MapSession` 的派生逻辑，产生漂移（原实现就是这么写的，已纠正）：

| 新增 | 位置 | 说明 |
|---|---|---|
| `public IReadOnlyList<SharedRoute> SharedRoutes { get; }` | `Rendering/WaypointOverlay.cs` | 返回 `SetSharedRoutes` 写入的共享路线**拷贝**（锁内 copy，语义照抄同文件既有的 `Waypoints` / `Next`）。不新增此项就只能复刻 `MapSession.RebuildSharedRoutes`，一旦后者改动 WebSync 静默走偏 |
| `public IReadOnlyList<MapPing> Snapshot()` | `Rendering/PingOverlay.cs` | 返回当前 ping 的拷贝（照 `PlayerOverlay.History` 的锁内 copy 模式）。`PingOverlay._pings` 原本没有任何读取器 |

两条都是**只读**、不改任何既有成员行为、不动绘制。与 §5.2 的 `MapProjection.Affine` 同属"只读扩展"豁免（§10）。

### 5.3 新增设置（`Settings/AppSettings.cs`）

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `WebSyncEnabled` | bool | `false` | 总开关 |
| `WebSyncPort` | int | `8765` | 监听端口，合法性校验 1024-65535 |
| `WebSyncAccessToken` | string | `""` | 空 = 未生成；首次开启时用 `RandomNumberGenerator` 生成 8 位 Crockford base32（不含 0/O/1/I/L），持久化 |
| `WebSyncFollowPlayerFloor` | bool | `true` | 平板端是否自动切到玩家所在楼层 |
| `WebSyncShowPois` / `WebSyncShowQuests` / `WebSyncShowAnnotations` | bool | `true` | 下发内容过滤 |

**必须在 M1 同提交里落地的三件事**：

1. **端口范围校验写进 `Normalize()`，不能只靠 UI。** `SettingsStore.Load()` 会调 `settings.Migrate()` + `settings.Normalize()`（`Normalize()` 在 Save 时也会走）。新增的字段要在这两个钩子里被处理 —— 端口夹到 1024-65535，越界时回落到 8765。`AppSettings` 的默认值是普通 C# 属性初始化器，**没有** `[DataMember]`、**没有**手维护属性清单，整个类型已注册在一个 `SettingsJsonContext` 上，所以加字段不需要改任何序列化注册。
2. **本地化键同提交（§8.3 的清单在 M1 就全量加进两个 JSON）。** 既有 `tests/.../LocalizationKeyTests.cs` 会扫描 `src/**/*.cs` 里的 `Loc("字面量")` 和所有 `*.axaml` 里的 `{loc:Tr ...}`，断言每个键都存在于 `StringsEn.json`；另一条断言 `StringsZh.json` 不得有 English 没有的键。**WebSync 代码一旦落地而键没同步，`dotnet test` 立即红。** 所以本地化清单属于 M1，不是 M4。
   （该测试只认字面量，认不出 `Loc(variable)` / `Loc(nameof(...))`；所以 WebSync 一律写字面量键。网页内联中文 HTML 不受此检查约束，属预期。）
3. **前向兼容是"静默丢弃"，不是报错。** 未开 `UnmappedMemberHandling.Disallow`，旧版本读到新版本的键会静默忽略、且保存时按旧类型重新序列化丢失。后果具体化：用户用新 exe 生成令牌后回退旧 exe，WebSync 配置会被抹掉（重新开启即可）。这是可接受的，但要在 §8.4 的 README 里写一句。

### 5.4 新增文件

```
src/TarkovMapCompanion/WebSync/
  WebSyncServer.cs        # Kestrel 宿主：启动/停止、端点注册、令牌中间件
  WebSyncState.cs         # 全部 DTO + [JsonSourceGenerationOptions] + JsonSerializerContext
  StateBroadcaster.cs     # 事件订阅、节流、快照构造、WS 客户端注册表
  LanAddress.cs           # 枚举本机局域网 IPv4（优先 10/8、172.16/12、192.168/16）
```

**WebSyncServer 要求**：
- `WebApplication.CreateSlimBuilder()`；`builder.WebHost.UseUrls($"http://0.0.0.0:{port}")`；把默认日志级别压到 Warning（避免 Kestrel 噪声进 app.log，走 `Diagnostics/Log.cs` 记启动/停止/错误）。
- 生命周期：由 `MapSession` 持有（它是进程期编排器，构造于 `MainWindow` 构造函数，由 `MainWindow.Closing` → `MapSession.Dispose()` 确定性地释放）；`WebSyncEnabled` 变更时启停；端口占用时捕获 `IOException`，经 `MapSession.Status`（`event EventHandler<string>`）报告，**不得崩溃**。
- **停止路径必须防挂起（本功能唯一会拖垮既有行为的风险，详见 §5.4.1）**。
- 令牌中间件：除 `GET /` 与 `GET /assets/*` 外，所有请求（含 WS 升级）必须带匹配 `?key=`；不匹配返回 **404**（不返回 401/403，不暴露服务存在性）。`GET /` 无令牌时返回一个仅提示「链接缺少 key」的最小页面。
- 所有 `{mapId}` 参数白名单校验（`MapCatalog` 的 `Key` 集合），拒绝路径拼接，防目录遍历。
- 响应头统一 `X-Content-Type-Options: nosniff`；API 响应 `Cache-Control: no-store`。

**StateBroadcaster 要求**：
- 订阅 §3 列出的全部事件。`FixApplied` 节流至 2Hz（500ms 合并，取最新）；其余事件即时触发对应增量。
- 快照构造全部在调用线程内从 Overlay 公共只读属性读取（线程契约与 `QuestOverlay.Marks` 的 live 集合例外见 **§4.2**，禁止加锁重入绘制逻辑）。
- 维护 `ConcurrentDictionary<Guid, WebSocket>` 客户端表；发送失败即摘除并 dispose。
- 玩家楼层用 `MapFloor.Covers(position)` 判定（取 `CurrentMap.Floors` 中第一个 Covers 为 true 的 `Name`，无则 null）。
- **StateBroadcaster 实现 `IDisposable`，保存全部事件 handler 引用并逐个退订**（`MapSession.Dispose()` 不清理外部订阅者 —— 见 §4.2 第 3 条）。

#### 5.4.1 生命周期与停止路径（定案，必读）

既有退出链路**全程同步**：`MainWindow.Closing` → `_session.Dispose()`。`App` 没有 `OnExit` / `IAsyncDisposable`，`ShutdownRequested` 只用来持久化窗口位置。而 Kestrel 的 build/start/stop 是异步的，且 `WebApplication` 会捕获 ambient `SynchronizationContext`。**在 UI 线程上 `.GetAwaiter().GetResult()` 阻塞 host API 会让窗口关不掉** —— 这是本功能唯一可能拖垮既有行为的路径。定案如下：

**A. 线程模型**
- 所有 host 的 `Build()` / `StartAsync()` / `StopAsync()` 都在**一个没有 `SynchronizationContext` 的专用 worker 线程**上执行（自建线程并 `SetSynchronizationContext(null)`，或 `Task.Run` 到线程池但显式确保不捕获 UI 上下文）。绝不在 Avalonia dispatcher 线程上调用任何 host API。
- worker 后面挂一个**串行化的 desired-state reconciler**：外部只提交"期望状态（enabled/disabled + 端口 + 令牌）"，由 reconciler 顺序地收敛实际状态。它必须能抵御**快速开关**（1 秒内 off→on）而不产生两个 host、不泄漏一个孤儿 host、不重启已 dispose 的 host。

**B. 启动不阻塞 UI**
- `Update(desiredState)` 从 UI 线程调用时**只入队并立即返回**，不做任何等待。端口 bind 可能耗时也可能抛异常，都不能发生在 UI 线程上。
- 绑定失败的异常类型**已实测**：只有 `StartAsync()` 抛，`Build()` 不抛；类型是 `System.IO.IOException`（内层 `AddressInUseException` ← `SocketException(10048)`）。**catch `IOException`、在 `StartAsync` 外围**。捕获后经 `MapSession.Status`（`event EventHandler<string>`）报告，**不得崩溃**。

**C. 停止（关键顺序）**
1. `builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2))`。
2. `MapSession.Dispose()` 里的顺序**固定为**：`StateBroadcaster.Dispose()`（退订 handler + `Abort()` 所有 WebSocket，全部同步且快）→ `_webSync.Stop(2s)` → 既有 teardown（watcher / Party / imageSource）。**WS 必须先 abort 再 stop**：实测（带一个已连接 WS）不 abort 时 `StopAsync` 耗 30s（默认）/ 2s（超时 2s），**先 abort 再 stop 只要 ~1ms**。
3. `Stop` 内部用 `Task.Wait(2s)` 之类的**有界**等待；超时即放弃并记 Warning，监听 socket 随进程退出由 OS 回收。
4. **`App.OnExit` 兜底已取消**：实测证明返回 `Main` **不会**被运行中的 host 拖延（进程在 READY 后 6-7ms 内退出），所以**不需要**改 `App` 生命周期。host 线程是后台线程，无需显式 stop 也能退出——我们仍显式 stop 只是为了释放端口与优雅关闭。

**D. 接线点（三处，不动 `MainWindow.Closing` / `App`）**
1. `MapSession` 构造函数**末尾**构造 `WebSyncServer`（**不启动**）。
2. `MapSession.StartAsync()` 里调用一次 `ApplyWebSyncSettings()`；偏好设置页的 WebSync 控件也调用它。
3. `MapSession.Dispose()` 里按 C 的顺序 teardown。

**E. 验收**
开启 WebSync（含一个已连接的平板 WS）后反复开关该开关、然后正常关闭主窗口，**进程必须在 2 秒内退出**。此场景要有测试覆盖（至少覆盖"停止 ≤2s"这一半；进程退出用 §8.4 实测清单）。

**F. 遥测**
`WebSyncServer` 暴露 `BoundPort` 供测试读取端口；端口 0 时从 `IServerAddressesFeature` 解析实际端口，**解析格式以探针第 5 项实测为准**。

### 5.5 快照 JSON Schema（`/api/state` 与 WS 首帧共用，source-gen 序列化）

```jsonc
{
  "version": 1,
  "generatedAt": "2026-10-13T10:00:00Z",
  "inRaid": true,
  "map": {
    "key": "shoreline", "displayName": "海岸线",
    "imagery": { "type": "svg", "url": "/map/shoreline.svg" }
    // 或 { "type": "tiles", "urlTemplate": "/tiles/shoreline/{z}/{x}/{y}.png",
    //        "tileSize": 256, "minZoom": 14, "maxZoom": 17 }
    ,
    // ⚠️ 语义以 §5.2 为准：这是"先旋转再缩放"，不是通用仿射矩阵。tx/ty 不存在。
    "projection": { "cos": 1.0, "sin": 0.0, "a": 1.0, "b": 0.0, "c": 1.0, "d": 0.0,
                    "coordinateRotationDegrees": 0.0 },
    "baseRect": { "x": 0, "y": 0, "w": 1000, "h": 1000 },
    "svgBaseRect": { "x": 0, "y": 0, "w": 1000, "h": 1000 },
    "floors": [{ "name": "Ground_Level" }],          // MapFloor.Name 列表
    "activeFloors": ["Ground_Level"]                  // 桌面端当前激活楼层
  },
  "player": {
    "x": 123.4, "z": -45.6, "yawDegrees": 270.0,
    "screenAngleDegrees": 270.0,                       // 预计算，客户端免投影旋转
    "floor": "Ground_Level",
    "trail": [{ "x": 1.0, "z": 2.0 }, ...]             // PlayerOverlay.History
  },
  "extract": {                                          // ExtractLineOverlay，无目标则 null
    "targetId": "...", "targetName": "码头",
    "guideBase": { "x": 1.0, "y": 2.0 },               // GuideBase（base 坐标）
    "distanceMeters": 123.0, "relativeBearingDegrees": 45.0
  },
  "waypoints": [{ "n": 1, "x": 1.0, "z": 2.0, "visited": false }],   // 游戏坐标
  "sharedRoutes": [{ "owner": "队友A", "color": "#RRGGBB", "points": [{ "x": 1.0, "z": 2.0 }] }],  // WaypointOverlay 的 SharedRoute，自带 SKColor
  "peerRoutes":   [{ "name": "队友B", "points": [{ "x": 1.0, "z": 2.0 }] }],                       // PartySession.Routes 的 PeerRoute，无 Color，客户端按 PartyPeer.Color 上色
  "pois": [{ "id": "...", "kind": "Extract", "name": "码头",           // name 用 Localizer.Pick 后的 DisplayName
             "x": 1.0, "z": 2.0, "available": true, "conditional": false,
             "singleUse": false, "elevation": [0.0, 3.0] }],
  "quests": [{ "label": "...", "x": 1.0, "z": 2.0 }],                  // QuestMark 展开
  "annotations": [{ "id": "...", "x": 1.0, "z": 2.0, "text": "...", "author": "..." }],
  "peers": [{ "name": "...", "x": 1.0, "z": 2.0, "yawDegrees": 90.0, "color": "#RRGGBB" }],
  "pings": [{ "x": 1.0, "z": 2.0, "at": "..." }],
  "settings": { "followPlayerFloor": true }
}
```

规则：坐标一律给**游戏坐标**（x/z）；base 坐标仅 `extract.guideBase`（因引导线逻辑复杂，直接给结果）。字段缺失语义 = null 或空数组，客户端必须容忍未知字段（向前兼容）。

### 5.6 WS 增量消息

```jsonc
{ "type": "player",  "player": { ... } }              // 节流 2Hz
{ "type": "map",     "snapshot": { ...全量... } }      // MapChanged：直接重发全量
{ "type": "route",   "waypoints": [...], "sharedRoutes": [...], "peerRoutes": [...], "extract": {...} }  // ⚠️ 两类路线不同源，见 §4.1
{ "type": "pois",    "pois": [...], "quests": [...] }
{ "type": "meta",    "annotations": [...], "peers": [...], "pings": [...] }
{ "type": "raid",    "inRaid": false }
```

### 5.7 M1 测试（新增 `tests/.../WebSyncTests.cs` 等）

- DTO 序列化往返（source-gen context 不丢字段、 camelCase 命名）。
- 令牌中间件：带/不带/错误 key 三种请求的响应码（404 语义）。
- StateBroadcaster：注入假 MapSession（参照既有测试里 `SetMapForTesting` 类钩子），验证节流（多发 FixApplied 只出一次广播）与快照字段映射。
- `LanAddress`：返回地址属于私有网段。
- 端口冲突时不抛异常、走 Status 事件。

---

## 6. 里程碑 M2 — 底图供给

### 6.1 `GET /map/{mapId}.svg?floors=A,B&base=true`

1. `MapCatalog` 按 `Key` 取 `GameMap`；无 `HasSvg` → 404。
2. `AssetCache.GetStringAsync(map.SvgUrl, "svg", ct)` 取缓存 SVG 文本（未缓存则下载；`AssetCache.AllowNetwork=false` 且未缓存 → 503 + 提示文案）。
3. ⚠️ **`MapFloor.Name` 与 `MapFloor.SvgLayerId` 是两个不同的命名空间** —— 楼层显示名是 `"Ground_Level"` 这类可读串，SVG 里的图层 `id` 是另一套（可为空 `string?`）。`SvgLayerFilter` 匹配的是**图层 id，不是楼层名**。
   因此传入的集合必须由 `MapFloor.SvgLayerId` 映射而来并**剔除 null**：

   ```csharp
   var layerIds = floors.Where(f => f.SvgLayerId is not null)
                        .Select(f => f.SvgLayerId!)
                        .ToArray();
   var svg = SvgLayerFilter.Filter(svgText, map.BaseSvgLayerId, layerIds, includeBase: base);
   ```

   **把 `MapFloor.Name` 直接当第 3 参传是错的**（照现文字实现会导致楼层过滤静默失效 —— 平板端所有楼层叠在一起）。这条要有测试（见 §6.3）。
4. 结果按 `{mapId}|{base}|{排序后的layerIds}|{includeBase}` 做内存缓存（`ConcurrentDictionary`，换图时无需清理，进程内驻留；总条数 ≤ 13图 × 少量楼层组合）。
5. 响应 `Content-Type: image/svg+xml; charset=utf-8` + nosniff + `Cache-Control: no-store`（SVG 内容随楼层参数变，URL 已含参数，可允许私有缓存 5 分钟，自行权衡并在注释说明选择）。

### 6.2 `GET /tiles/{mapId}/{z}/{x}/{y}.png`

1. 白名单校验 mapId；`HasTiles` 否则 404。
2. `z` 必须在 `[MinZoomLevel, MaxZoomLevel]`；`x/y` 非负且 `< 2^z`（超出 → 404）。
3. 楼层模板选择**必须与 `TileMapSource.ResolveTemplate` 行为一致**，而该方法是 **`private` 的，无法直接复用**。二选一：
   - 首选（零侵入）：在 `Rendering/TileMapSource.cs` 上加一个 `internal` 的薄包装把既有 private 逻辑暴露出来，复用它以保证不漂移。**这是对既有文件的侵入，必须在规格里显式声明并在提交说明里点明。**
   - 备选：在 `WebSyncServer` 里照 `ResolveTemplate` 逻辑重写，并在测试里锁住一致性。

   既有逻辑（`TileMapSource.cs:197`）：返回**第一个**满足 `TilePathTemplate is not null && activeFloors.Contains(floor.Name)` 的 `MapFloor.TilePathTemplate`；否则 `includeBase` 为真时返回 `GameMap.BaseTilePathTemplate`，否则 null。注意这里匹配的是 `floor.Name`（**不是** `SvgLayerId`——SVG 与瓦片两套命名空间的区别见 §6.1）。
4. `template.Replace("{z}",...).Replace("{x}",...).Replace("{y}",...)` 得 URL → `AssetCache.GetAsync(url, Path.Combine("tiles", map.NormalizedName), ct)` → 字节转发（**category 必须带 map 名**，与 `TileMapSource.QueueFetch` 一致，否则不复用桌面端缓存、离线取不到；见 §4 资产缓存行）。`Content-Type` 探测首字节魔数（RIFF…WEBP → `image/webp`，否则 `image/png`）。
5. 404/加载失败返回 1×1 透明 PNG，避免浏览器控制台噪声。

### 6.3 M2 测试

- SVG 端点：构造内存 AssetCache（`AssetCache.Root` 是 `init`-only，用 `new AssetCache { Root = tmpDir }` 注入；注意**现有测试从未用过这条路径**，等于新写），验证楼层过滤被调用、404/503 分支。
  **必测**：`SvgLayerId` 与 `Name` 不同时，过滤走的是 `SvgLayerId`（照 §6.1 修好后，此测试应能证明旧写法会错）。
- 瓦片端点：z 越界、mapId 非白名单、模板替换结果与 `TileMapSource` 行为一致（对照既有 `TileMapSourceTests` 风格；若走 §6.2 的 `internal` 包装，此测试直接断言包装与渲染路径同源）。
- HTTP 集成测试：起真实 Kestrel，**端口传 0 让系统分配**——CI runner 是共享机器，硬编码 8765 会撞端口导致 flaky。
- StateBroadcaster 退订测试：Dispose 后再触发 `FixApplied` / `PoisChanged`，断言无广播、无异常（对应 §4.2 第 3 条）。
- 退出测试：开启 WebSync → 触发停止 → 断言 **2 秒内**完成（对应 §5.4.1）。

---

## 7. 里程碑 M3 — WebSocket 实时推送与网页客户端

### 7.1 `WS /ws`

- `HttpContext.WebSockets.IsWebSocketRequest` 检查 + 令牌中间件 → `AcceptWebSocketAsync()`。
- 连接即注册进 StateBroadcaster 客户端表，发送全量快照，随后收增量。
- 收方向只接受心跳/文本 pong；**客户端帧上限 4KB**，超限关闭（1009）。
- 服务端每 30s ping；60s 无响应摘除。
- 断线由客户端负责重连（指数退避 1s→2s→…→max 15s），重连成功后以全量快照重同步。

### 7.2 网页客户端（`WebSync/Assets/index.html`，单文件内联 JS+CSS，< 600 行）

硬性要求：
- **零外部引用**（无 CDN、无网络字体），离线可用；以 EmbeddedResource 嵌入。
- 中文 UI（桌面端汉化项目的镜像，文案直接内联中文即可，不做多语言）。
- 移动端触控：Pointer Events 实现单指拖拽、双指捏合缩放（`touch-action: none`）；缩放围绕双指中心。
- 深色底（`#1a1d21` 系），地图容器全屏，顶部浮层显示图名/连接状态，底部楼层切换胶囊按钮。

渲染模型：
1. 取 `?key=` 存 `sessionStorage`，后续所有 fetch/WS 自动附加。
2. 首屏 `GET /api/state` → 依 `map.imagery.type`：
   - `svg`：`GET /map/{key}.svg?floors={activeFloors}` → 解析为内联 `<svg>` 放入容器；读取其 `viewBox` 作为 base 坐标显示范围，结合 `svgBaseRect` 与 `projection` 建立 game→base→SVG 两级换算。**换算函数必须照 §5.2 的"先旋转再缩放"实现（约 15 行 JS），不是通用仿射矩阵**：
     ```js
     function gameToBase(x, z, p) {
       const rx = x * p.cos - z * p.sin;
       const ry = x * p.sin + z * p.cos;
       return { x: p.a * rx + p.b, y: -p.c * ry + p.d };
     }
     ```
     **朝向直接用 `player.screenAngleDegrees`，客户端不重算**（否则会差 180°）。
   - `tiles`：建一个绝对定位瓦片层（按 minZoom 起步，支持 zoom 级别切换），同坐标换算。
3. 顶层 `<canvas>`（设备像素比自适应）绘制覆盖层，与底图共用 CSS transform。绘制优先级从低到高：POI → 注释 → 任务 → sharedRoutes → waypoints → 引导线（虚线 + 距离/方位角标签）→ peers → pings（扩散圈动画，3s 淡出）→ trail → 玩家（三角朝向标 + 脉冲圈）。
4. 楼层：按钮列表来自 `map.floors`；`settings.followPlayerFloor=true` 时收到 `player.floor` 变化自动切换（重新请求对应 floors 的 SVG / 换瓦片模板），手动点击后暂停自动跟随 30s。
5. POI 不可用的（`available=false`）降透明度 0.4；条件/一次性撤离点加角标。
6. 连接状态指示：绿点=已连接，黄点=重连中；断线横幅提示。

### 7.3 M3 测试

- WS 集成测试：真实 Kestrel 回环 + `ClientWebSocket`，验证首帧全量、增量到达、无 key 拒绝、4KB 帧上限。
- 增量消息 schema 与 §5.6 一致（序列化快照测试）。
- 客户端 JS 的坐标换算：抽出纯函数（文件顶部 `function gameToBase(...)`），在 C# 测试里无法跑 JS——**改为在规格里给出 3 组手工算好的输入/期望值对照表**，实现后人工用浏览器 console 核对（写在 `WebSync/Assets/coords-check.md` 注释块中）。

---

## 8. 里程碑 M4 — 设置 UI、二维码与收尾

### 8.1 偏好设置页（`Views/PreferencesWindow.axaml{,.cs}`）

新增「局域网同步」分区（**版式与既有「队伍」分区一致**，先看 Party 分区怎么写再动手）：
- 启用开关（`WebSyncEnabled`）——开启时提示：「首次开启 Windows 可能弹出防火墙提示，请允许专用网络」。
- 端口输入（`WebSyncPort`，1024-65535 校验）。
- 完整访问 URL 只读框：`http://{LanAddress 首选地址}:{port}/?key={token}` + 「复制」按钮。
- 二维码图片（内容为同一 URL）+ 「重新生成令牌」按钮（重新生成后二维码与 URL 即时刷新；旧客户端自然失效）。
- 三个内容开关（POI/任务/注释下发）+ 「平板自动跟随玩家楼层」开关。

### 8.2 二维码实现

- 新增 NuGet：**QRCoder**（MIT、零依赖、纯托管；选定版本写入 csproj 并在 `NOTICE.md` 追加归属，跟随既有第三方归属格式）。
- 用法：`QRCodeGenerator.CreateQrCode(url, ECCLevel.M)` → `PngByteQRCode` → byte[] → `Avalonia.Media.Imaging.Bitmap`（MemoryStream）。**禁止**引入带 Skia 依赖的二维码库（与 SkiaSharp 钉版冲突，`Directory.Build.props` 有明确注释警告）。

### 8.3 本地化键（**已在 M1 落地**，此处仅作清单存档）

> 按 §5.3 第 2 条，以下键必须与 M1 的 WebSync 代码**同提交**进入 `StringsEn.json` 与 `StringsZh.json`，否则 `LocalizationKeyTests` 会红。这不是 M4 的活。

```
WebSync.Section / WebSync.Enable / WebSync.EnableHint / WebSync.Port / WebSync.PortInvalid /
WebSync.Url / WebSync.Copy / WebSync.Copied / WebSync.RegenerateToken / WebSync.TokenRegenerated /
WebSync.QrHint / WebSync.FollowPlayerFloor / WebSync.ShowPois / WebSync.ShowQuests /
WebSync.ShowAnnotations / WebSync.StatusRunning / WebSync.StatusStopped / WebSync.StatusPortInUse /
WebSync.ClientHintNeedKey
```

### 8.4 收尾

- `README.md` 增加 WebSync 段落（中英各一，跟随现有双语结构）；**必须写明**：令牌是明文存在本地配置里的局域网访问凭证、只读镜像不上传任何数据、回退到旧版本会导致 WebSync 配置被丢弃。`AGENTS.md` 的 Current state 更新。
- `scripts/publish.ps1` 产物实测：启动 → 开启 WebSync → 手机浏览器扫码访问 → 验证换图/移动/楼层 → **开启状态下关闭主窗口，确认进程 2 秒内退出**。
- 记录单文件体积增量（基线 50.6MB → 实测值）到提交说明。
- 全部测试通过；新增测试预计 30-50 个。
- `NOTICE.md`：ASP.NET Core / Kestrel 是微软 MIT 框架、非内嵌源码，**不强制加条目**；QRCoder 需要加（§8.2）。

---

## 9. 验收标准汇总（Definition of Done）

1. `dotnet test` 全绿（基线 656 + 新增）。
2. 设置页开启 WebSync 后，同网段安卓平板 Chrome 扫码 3s 内看到当前地图。
3. 游戏内截图 → 平板位置/朝向 ≤1s 更新；切图、切楼层、路线增删、撤离点选择同步。
4. 无 key 或错 key 访问任何 API/WS 一律 404；`/` 无 key 仅见提示页。
5. 关闭开关后端口立即释放；端口被占时程序不崩溃且 UI 有明确状态。
6. 断网（`AllowNetwork=false`）时：已缓存的地图照常可用，未缓存地图给出明确 503 提示，程序其余功能不受影响。
7. 单文件发布产物体积增量 **≤ 25MB**（基线 50.6MB → 预计 ~60~65MB，§5.1/§11 已说明这是 ASP.NET Core 的固有成本；**不设 30MB 上限的绝对值判据**），功能同上。
8. 开启 WebSync 后正常关闭主窗口，进程 **2 秒内**退出（§5.4.1）。
9. `scripts/publish.ps1` 与两个 workflow 的发布参数与本功能之前逐字一致（§5.1）。

## 10. 明确不做（Out of Scope）

- ❌ 任何写操作 API（平板反向标注、改路线、选撤离点）——本轮只读镜像。
- ❌ UPnP / 公网访问 / DDNS / 复用 `Party/PortMapper.cs`。
- ❌ 用户账号体系、多用户权限分级。
- ❌ 与 Party 协议合并或复用其帧格式（Party 是加密 P2P 协作，WebSync 是 LAN 只读广播，正交）。
- ❌ 帧推送/MJPEG/服务端渲染截图流。
- ❌ 修改 `MapSession` 既有事件语义或 Overlay 绘制逻辑（只允许 §5.2 / §5.2.1 的只读扩展：`MapProjection.Affine`、`WaypointOverlay.SharedRoutes`、`PingOverlay.Snapshot()`）。

## 11. 风险与回退

| 风险 | 应对 |
|---|---|
| 单文件体积增大 | **已实测**：基线 50.6MB，ASP.NET Core 8 共享框架 27.2MB，经 `EnableCompressionInSingleFile` 压缩后预计 +8~15MB → ~60~65MB。这是方案 A 的固有成本，决策已在 §1 记录，**不再以体积为回退门槛**。若将来确实要压：`PublishTrimmed` 不可用（Avalonia 反射多），只能改用 `Microsoft.AspNetCore.App` 的裁剪子集或换 HttpListener（需 urlacl/管理员）——**两者都要重开决策，不在本轮范围** |
| **同步 Dispose 阻塞异步 Kestrel 导致程序关不掉** | §5.4.1：Kestrel 跑独立后台线程、停止用 2s 有界等待、UI 线程不做无界阻塞。DoD 第 8 条专项验收。这是本功能对既有行为**唯一的高危接触面** |
| 楼层过滤用错命名空间（`Name` vs `SvgLayerId`） | §6.1 已明确并给出正确写法，§6.3 有专门测试锁住 |
| 旧版 exe 读到新 WebSync 配置键 | 静默丢弃（未开 `UnmappedMemberHandling.Disallow`），表现为回退后 WebSync 配置丢失、重新开启即可。§5.3 第 3 条 + README 注明 |
| tarkov.dev SVG 在移动端 Chrome 渲染异常 | M2 即实测 13 张图；异常则对该图回退 tiles（`/api/state` 的 imagery 描述已支持双类型，服务端按图选择） |
| Windows 防火墙拦首次监听 | UI 文案预告 + 文档给出 `netsh advfirewall` 手动命令；**程序不得自动改防火墙规则** |
| 多客户端广播背压 | 发送设 2s 超时，慢客户端直接摘除（宁可断不可堵，客户端会自动重连） |
| 令牌泄露 | 设置页一键重新生成即失效旧令牌；令牌不出现在 app.log（参照 Party 的脱敏传统） |

## 12. 提交纪律

- 每个里程碑一个 commit（或一组小 commit），信息格式跟随仓库既有风格。
- 不夹带与本功能无关的改动；发现既有 bug 另开 commit。
- 本地化两文件同提交；漏键 = 返工。
