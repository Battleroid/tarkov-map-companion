# 渲染性能优化专题（待启动）

> 项目：tarkov-map-companion-self-improv · 记录日期：2026-10-13 · 状态：**仅记录，尚未实施**
> 基线：`master`。本文是专题启动时的输入，不是已完成的工作。

---

## 结论先行

程序**空闲时开销≈0**（事件驱动重绘，无动画即停表），但**单帧绘制成本很高**：本机实测 15~43 ms/帧（headless Skia CPU 光栅，1600×1100）。

关键判断：**只要地图在动——平移、缩放、路线箭头动画、跟随缓动——就会稳定占用接近一个 CPU 核心**。EFT 是 CPU bound，所以低核数机器上会体现为游戏微卡顿。WebSync 与此无关（其开销 <0.5% 单核），不要把它当作优化对象。

## 实测基线（本机，2026-10-13）

复现命令：

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"; $env:PATH="$env:USERPROFILE\.dotnet;$env:PATH"
dotnet run --project src/TarkovMapCompanion -c Release -- --render-test <map> $env:TEMP\rt.png 1600 1100 [floors] [bare|marks|quests]
```

| 场景 | 首帧（含解析） | 稳态 | 备注 |
|---|---|---|---|
| shoreline（1162 POI，默认设置） | 935 ms | **43.13 ms/帧** | 默认 `LootContainer=false`，可见约 400 个 |
| shoreline `bare`（仅底图无标记/热图） | 295 ms | **14.82 ms/帧** | → 9 个覆盖层合计 ≈ 28 ms |
| the-lab（瓦片渲染，661 POI） | 119 ms | **33.72 ms/帧** |瓦片路径不比 SVG 便宜 |

> 注意事项：RenderTest 是 headless **纯 CPU 光栅**，不含合成器 present 开销；真机 Avalonia 默认也是 Skia 路径，且 SVG 快照那条明确是 `SKSurface.Create`（CPU）。项目内无任何渲染模式覆盖（`Program.cs:134-137`）。

## 负载场景

| 场景 | UI 线程占用 | 证据 |
|---|---|---|
| 空闲 | ≈0 | `MapClock` 构造即停止，仅事件唤醒（`Rendering/MapClock.cs:55-113`） |
| 常规 raid（每张截图一次重绘） | 平均 1~5% | 每张截图 1~2 次 43 ms 重绘 |
| 平移/缩放地图 | **100%（约 20 fps 上限）** | 输入 60 Hz 触发重绘，每帧 43 ms |
| ≥2 路径点 + 箭头动画 | **≈86% 单核，持续** | `AnimateArrows` 默认 true → `MapClock` 20 fps 常驻（`Rendering/WaypointOverlay.cs:81,84`） |
| 跟随玩家缓动 | 每张截图 ≈ 0.6 s CPU | 250 ms 缓动 × 16 ms 定时器 ≈ 15 帧 × 43 ms（`Rendering/MapCanvas.cs:245-248`） |

缓动/缩放期间还会触发 `SvgMapSource.RebuildSnapshot`——缩放变化 >0.7% 即整块 CPU 重光栅化视口×1.35 区域（`Rendering/SvgMapSource.cs:183-263`），单帧可远超 43 ms。

## 非渲染开销（默认几乎全关）

默认 `ReadExitsFromScreenshots=false`、`ReadGameLog=false`、`CullMode=Off`（`Settings/AppSettings.cs:107,187,208`），所以 OCR、日志尾读、截图回收都不在默认路径上。

- **常开**：每 2 秒全量枚举截图目录 + 逐文件正则 + 排序（`Screenshots/ScreenshotWatcher.cs:21,150-185`），成本随磁盘截图数线性增长，与游戏共用同一块盘。
- **可选但重**：每张截图一次全图解码 + WinRT OCR，注释估 ~25 ms，1440p software bitmap 约 7~16 MB，且**无并发限流**（`MapSession.cs:1441`）；日志路径每 2 s 扫整个 Logs 树元数据。
- **日志**：`Log.Write` 全局锁 + `File.AppendAllText` 同步写盘（`Diagnostics/Log.cs:36-43`）。

## 内存上限

| 项 | 上限 | 位置 |
|---|---|---|
| 瓦片位图 | 400 张 ≈ 100 MB | `Rendering/TileMapSource.cs:31` |
| SVG 视口快照 | 8192² × 4 B = **256 MB**（1600×1100 窗口实际约 12 MB） | `Rendering/SvgMapSource.cs:30,222` |
| SVG 解析产物 | **无上限**，每个 base/floor 组合一个 SKPicture 常驻 | `Rendering/SvgMapSource.cs:36,177` |
| 小地图 | 第二份**不共享**的 image source（第二份快照 + 第二份 picture 字典） | `Views/MinimapWindow.axaml.cs:23` |
| 热图 | 512² float + color +缓存图 ≈ 3 MB | `Rendering/HeatmapOverlay.cs:49` |

典型总占用 200~400 MB。

---

## 优化项（按收益排序）

每项都标了「证据 / 改法 / 预期 / 验证」，启动专题时按此顺序做，每步用 `--render-test` 前后对比数字。

### P1 · 箭头动画常驻 20 fps（最大单项收益）

- **证据**：`WaypointOverlay.Advance()` 在 `Count >= 2` 时恒返回 true（`Rendering/WaypointOverlay.cs:84`），`AnimateArrows` 默认 true（`:81`）；`MapClock` 因此永不停表（`Rendering/MapClock.cs:103-113`）。
- **改法**：把「窗口非最小化」（`MapClock.Suspended` 已有，`Views/MainWindow.axaml.cs:522-526`）扩展为「窗口无焦点 / 屏幕保护 / 玩家静止 N 秒」；或降到 10 fps；或路线未变化时冻结相位。
- **预期**：去掉路径点后从 ~86% 单核降到 ~0。**这是唯一一个能把持续占用归零的改动。**
- **验证**：`dotnet test` 中 `MapClock` 的 `ShouldKeepRunning` 规则测试需同步更新；再加一个「失焦时不请求帧」的测试。

### P2 · 覆盖层每帧成本（28 ms → 目标 <10 ms）

- **证据**：每个 POI 每帧新建 `SKPath` 菱形，文字被测量两次（`Rendering/PoiOverlay.cs:251,341`）；`LabelPlacer` 每帧重算且 `Fits` 线性扫已占位矩形，≈ O(n²)（`Rendering/LabelPlacer.cs:62,175-190`）；`QuestOverlay`/`WaypointOverlay`/`PingOverlay` 每帧各有 `SKPath`/paint 分配与 `ToString()`。
- **改法**：POI 路径与文字测量结果按 poi id + 缩放档位缓存；缩放低于阈值不画标签；`LabelPlacer` 占位缓存跨帧复用（仅在 overlay 集合变化时失效）；把每帧 LINQ/`ToArray()`（`MapCanvas.cs:429,431`、`PlayerOverlay.cs:171`、`WaypointOverlay.cs:128`、`PeerOverlay.cs:156-165`）换成复用缓冲。
- **预期**：覆盖层成本减半，`bare` 与全量帧时差从 28 ms 降到 ~10 ms。
- **验证**：`--render-test` 对比 `bare` 与全量的差值。

### P3 · 拖拽/缩放期间的整块重光栅

- **证据**：`SnapshotCovers` 对任何 >0.7% 的缩放变化返回 false（`Rendering/SvgMapSource.cs:192`），缩放动画每帧触发 `RebuildSnapshot`；`SnapshotMargin=0.35`（`:27`）。
- **改法**：放宽缩放阈值（放大允许软化，或按 zoom level 量化）；缩放进行中降级到低分辨率快照、停手后再补高清。
- **预期**：消除拖拽缩放时的长帧。
- **验证**：给 `SvgMapSource` 补「连续缩放时重光栅次数」的测试。

### P4 · 内存收敛

- `_picturesByLayerKey` 加 LRU 上限（`Rendering/SvgMapSource.cs:36,177`）；小地图窗口与主窗口共享同一 `IMapImageSource`（`Views/MinimapWindow.axaml.cs:23-25`）；`RebuildSnapshot` 旧图及时 `Dispose`（已做，确认无泄漏即可）。
- **预期**：常驻内存降 50~150 MB。

### P5 · 后台小开销（顺手做，不单独立项）

- `StateBroadcaster.Broadcast` 在 `_clients` 为空时直接 return（`WebSync/StateBroadcaster.cs:748`），并在 `OnFixApplied` 同样短路（`:616`）——消除关闭/无客户端时的全部无谓快照构建与序列化。
- `WebSyncServer.Update` 仅在 `enabled` 时懒启动 worker 线程（`WebSync/WebSyncServer.cs:182-190`）。
- 瓦片加进程内小缓存或 `ETag`（`WebSync/WebSyncServer.cs:858` 每请求一次磁盘读 + SHA256 + `CreateDirectory`）。
- 截图目录枚举：文件数与最新写入时间未变则跳过正则解析与排序（`Screenshots/ScreenshotWatcher.cs:150-185`）。
- OCR 加信号量（当前无并发限流，`MapSession.cs:1441`）。
- `LogTail` 单次读取加 16 MB 分块上限（`GameLog/LogTail.cs:100`，与既有安全评估的 🔵 项同一条）。

## 明确不做 / 需先确认

- **不改默认行为语义**：任何「少画东西」的优化都要有设置开关兜底，不能让用户默认看不到撤离点。
- **未验证项**：真机合成器 present 开销、4K 分辨率下的帧时、实际游戏内 CPU 占用曲线（需要真实 raid + 采样）。专题启动时建议先补一次真机测量（`Get-Counter '\Process(TarkovMapCompanion)\% Processor Time'` 或任务管理器采样），因为上面的 43 ms 是 headless 上界估计。
- OCR 与日志尾读的实测成本本文只有代码级推断，专题里如涉及需实测。

## 相关文档

- 安全 + WebSync 评估：`docs/eval-security-and-lan-sync.md`
- WebSync 实现规格：`docs/websync-implementation-spec.md`