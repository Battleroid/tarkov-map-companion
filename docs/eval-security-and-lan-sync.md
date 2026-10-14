# 深度评估报告：安全风险审计 + 局域网网页同步方案

> 项目：tarkov-map-companion-self-improv（Battleroid/tarkov-map-companion 汉化分支）
> 日期：2026-10-13 · 基线：master@d84e569 + 本地 4 commits · .NET 8 / Avalonia 11.3.19 / SkiaSharp 2.88.9

---

# 第一部分：安全风险评估

## 结论先行

**整体安全姿态良好，无高危（🔴）问题。** 项目防御意识明显强于同类工具：网络层有总开关、截图删除有三重闸门、反序列化全部为 System.Text.Json 具体类型（无多态）、组队通信全程 AES-GCM 且日志脱敏。未发现后门、隐蔽上传、危险反序列化或供应链污染。

值得跟进的是 **3 个 🟡 中低危项** 和若干 🔵 建议项。

## 风险清单

| 级别 | 位置 | 问题 | 说明 |
|---|---|---|---|
| 🟡 | `Views/MainWindow.axaml.cs` L2664 `OpenUrl` | 远程数据 → ShellExecute | `task.WikiLink` 来自 tarkov.dev 任务 JSON（网络可刷新数据），直接 `Process.Start(UseShellExecute=true)`。上游数据投毒或磁盘缓存被篡改时，用户点击后可打开任意 scheme 的 URL。需用户点击，不直接等于 RCE。**修复：加 `http/https` scheme 白名单。** |
| 🟡 | `Party/PartySession.cs` L477 | 组队监听 `0.0.0.0:24601` | 主持队伍时绑定全部网卡。属功能必需（注释已说明），密码学实现正确（PBKDF2-100k + AES-GCM 帧认证、64KB 帧上限、归属取自连接而非载荷）。仅用户主动主持时暴露，可接受。48-bit secret 对离线爆破理论抵抗有限，但明文价值极低（几秒前的游戏坐标），实际威胁模型下合理。 |
| 🟡 | SkiaSharp 2.88.9（传递依赖） | 2.88 分支已停止维护 | CVE-2023-4863（libwebp 堆溢出，在野利用）已在 2.88.6 修复，**当前版本免疫**。但后续原生库 CVE 只在 3.x 修；升级路径被 Avalonia 11.3 阻塞（`Directory.Build.props` 已如实记录）。属已知技术债，建议纳入发版 checklist 跟踪。 |
| 🔵 | `GameLog/LogTail.cs` L100 | 单次读取无上限 | `new byte[stream.Length - _offset]`，两次扫描间日志暴涨时可致大分配。实践中扫描频繁，风险低。建议加 16MB 分块上限。 |
| 🔵 | `Party/PortMapper.cs` L50-55 | 公网 IP 回显给第三方 | UPnP 失败时向 `checkip.amazonaws.com`/`api.ipify.org`/`icanhazip.com` 发 GET 探测公网 IP。仅用户主动主持队伍时发生，不发送数据。隐私上可接受，建议在设置中说明。 |
| 🔵 | 仓库卫生 | `src/` 下 273 个 DLL | 全部为 `bin/` 构建输出，非 vendored 第三方二进制，供应链面干净。若被提交进 git 建议清理（卫生问题，非安全问题）。 |

## 已验证的良好实践（无需处理）

- **网络行为收敛**：运行时外联仅限 tarkov.dev 系域名（地图数据/图标/瓦片），`AllowNetwork` 总开关可一键完全离线；`Tools/Fetch*.cs` 仅命令行开发工具。
- **无遥测/无上传**：无任何分析、崩溃上报；设置文件 `%APPDATA%\TarkovMapCompanion\settings.json` 不含凭据。
- **截图删除三重闸门**：`ScreenshotCuller` = CullMode 默认 Off → `IsInsideFolder` 防 `..`/符号链接逃逸 → 文件名必须匹配截图正则；删除默认走回收站。
- **缓存路径防遍历**：`AssetCache` 用 URL 的 SHA-256 前缀做目录，`ItemIconStore.LooksLikeAnId` 限定 24 位小写十六进制。
- **组队协议设计正确**：AEAD 认证、字段截断、防冒充（归属取自连接）、日志 IP 打码（`.x`）、会话指纹用 SHA-256 前缀而非会话码本身。
- **无危险反序列化**：全仓无 Newtonsoft / TypeNameHandling / BinaryFormatter；gzip 快照为构建期内嵌受信资源。
- **UPnP XML 解析**：`XDocument.Parse` 默认禁 DTD，无 XXE。
- **publish/release 脚本**：版本号参数有正则校验防 MSBuild 属性注入。

---

# 第二部分：局域网网页同步功能实现方案

## 需求

把桌面程序当前显示的地图（含玩家位置/朝向、轨迹、路线、撤离点引导线、POI、任务标记、队友等）同步到同局域网安卓平板，**浏览器直接访问网页**，无需安装 App。

## 方案选型

| 方案 | 思路 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| **A. 内嵌 HTTP 服务 + SVG/JSON 推送**（推荐） | 程序内嵌 Web 服务，平板浏览器打开页面；地图底图复用已缓存的 SVG，状态以 JSON 经 WebSocket 推送，浏览器端 Canvas 绘制覆盖层 | 平板端可自由缩放/平移；流量极小（状态仅 KB 级）；底图矢量清晰；无外部依赖（离线可用） | 需要在浏览器端重写一套覆盖层渲染（约 300-500 行 JS） | ✅ 推荐 |
| B. 服务端渲染帧推送（MJPEG/WebP） | 复用现有 SkiaSharp 管线把 `MapCanvas` 画面编码为帧流推送 | 画面与桌面像素级一致；客户端极简 | 平板无法本地缩放（只能看）；持续编码 CPU 开销；每帧数百 KB | 可作 Phase 0 快速验证，不宜作终态 |
| C. 独立 Companion Web App / 小程序 | 平板端独立应用，经局域网 API 拉数据 | 体验最好 | 开发量是 A 的 3-5 倍，违背"直接访问网页"诉求 | ❌ |

## 推荐架构（方案 A）

```
┌───────────────────────────── 桌面程序 ─────────────────────────────┐
│  MapSession（现有，不动）                                            │
│    │ 事件：FixApplied / MapChanged / WaypointsChanged / ...          │
│    ▼                                                                │
│  WebSync/WebSyncServer.cs（新增）                                    │
│    ├─ Kestrel 监听 0.0.0.0:8765（仅 LAN，可选访问令牌）               │
│    ├─ StateBroadcaster：订阅 MapSession 事件 → 节流(2~5Hz) → WS 广播 │
│    ├─ GET /            → 内嵌 HTML/JS（EmbeddedResource，无 CDN）     │
│    ├─ GET /api/state   → 全量快照 JSON（source-gen 序列化）           │
│    ├─ GET /map.svg     → AssetCache 已缓存 SVG + SvgLayerFilter      │
│    │                     按当前楼层过滤后输出（楼层过滤逻辑直接复用！）  │
│    ├─ GET /tiles/...   → 仅 3 张无 SVG 地图：代理 AssetCache 瓦片     │
│    └─ WS  /ws          → 增量推送（位置/路线/楼层/换图/队伍）          │
└────────────────────────────────────────────────────────────────────┘
            │ LAN (HTTP + WebSocket)
            ▼
┌──────────── 安卓平板 Chrome ────────────┐
│ 单页 HTML：                              │
│  · 内联 <svg> 底图 + 顶层 <canvas> 覆盖层 │
│  · Pointer Events 实现双指缩放/拖拽       │
│  · 楼层切换按钮（映射 MapFloor.Name）     │
│  · WS 断线自动重连 + /api/state 全量恢复  │
└──────────────────────────────────────────┘
```

## 关键设计点

### 1. Web 服务器选型：Kestrel，而非 HttpListener

- `HttpListener` 绑定非 localhost 前缀需要 urlacl 注册或管理员权限——对桌面工具是不可接受的 UX。
- **Kestrel 无需任何系统配置**。通过 `<FrameworkReference Include="Microsoft.AspNetCore.App" />` 引入，单文件自包含发布照常工作（体积 +15~25MB，可接受）。
- 自带 WebSocket 支持、并发处理、请求超时，省去手写 HTTP 协议层。

### 2. 底图复用现有资产管线（零新增下载逻辑）

- 13 张地图中 10 张有 SVG：`SvgMapSource` 已经过 `AssetCache` 把 SVG 缓存到 `%LOCALAPPDATA%\TarkovMapCompanion\cache`。**服务端直接读缓存文件，用现有 `SvgLayerFilter.Filter(svgText, baseLayerId, activeFloors)` 按楼层过滤后输出**——楼层过滤逻辑一行都不用重写。
- 3 张纯瓦片地图：`/tiles/{map}/{z}/{x}/{y}.png` 端点从 `AssetCache` 取已缓存瓦片转发；平板端按 `GameMap` 的 `TileSize/MinZoomLevel/MaxZoomLevel` 拼接。
- 浏览器端坐标换算所需的全部参数（`MapProjection` 的仿射系数、`BaseRect`、`SvgBaseRect`、缩放）随 `/api/state` 下发，投影公式照搬 `MapProjection.ToBase`（约 15 行 JS）。

### 3. 状态模型与推送协议

快照 JSON（`/api/state`，WS 首帧也发全量）：

```jsonc
{
  "map": { "id": "shoreline", "displayName": "海岸线", "projection": {...},
           "baseRect": {...}, "floors": ["Ground_Level", ...], "activeFloors": [...] },
  "player":  { "x": 123.4, "z": -45.6, "yaw": 270, "floor": "Ground_Level", "trail": [...] },
  "extract": { "targetId": "...", "guideFrom": {...}, "guideTo": {...} },
  "waypoints": [...], "pois": [...], "quests": [...], "annotations": [...],
  "peers": [...], "pings": [...], "exitAvailability": {...}, "inRaid": true
}
```

- 数据源全部来自 `MapSession` 已公开的属性与 Overlay 快照（`Overlays` 各实例本就在锁内派发快照，线程安全已解决——`CreateImageSourceAsync` 的小地图就是这套"第二消费者"先例）。
- 订阅事件：`FixApplied`（节流 2Hz）、`MapChanged`、`WaypointsChanged`、`SelectionChanged`、`PoisChanged`、`ExitAvailabilityChanged`、`AnnotationsChanged`、`QuestsChanged`、`Party.Changed`、`PingAdded`、`RaidStateChanged`。
- 玩家所在楼层：复用 `MapFloor.Covers(position)` 判定，推送 `player.floor`，平板端可自动切到玩家所在楼层（可选开关）。

### 4. 安全设计（呼应第一部分，新服务不引入新风险）

| 措施 | 说明 |
|---|---|
| 默认关闭 | 设置项 `WebSyncEnabled=false`，首次开启弹说明 + Windows 防火墙提示 |
| 访问令牌 | 默认生成随机 8 位令牌，URL 形如 `http://192.168.x.x:8765/?key=Ab3f9Kq2`；服务端校验 query，不匹配返回 404（不泄露服务存在性） |
| 仅 LAN | 不做 UPnP/端口映射（**明确不复用 `PortMapper`**）；UI 显示本机局域网 IP + 二维码（平板扫码直达） |
| 只读 API | 全部端点仅 GET/WS 订阅，无任何写操作、无路径参数拼接（地图 id 白名单校验，防目录遍历） |
| WS 帧上限 | 复用组队协议同款思路：客户端帧上限 4KB（客户端只需发心跳） |
| 输出编码 | SVG 输出设置 `Content-Type: image/svg+xml` + `X-Content-Type-Options: nosniff`；状态 JSON 不含任何本机路径 |

### 5. 平板端页面（无构建工具、无 CDN）

- 单 HTML + 内联 JS（<600 行），作为 `EmbeddedResource` 打进程序集，保证完全离线可用。
- 缩放/平移：Pointer Events + CSS `transform` 作用于 SVG 与 Canvas 的公共容器；覆盖层 Canvas 按 base 坐标系绘制后与底图同变换。
- 覆盖层绘制优先级与桌面端一致：热力图（可选）→ POI → 注释 → 任务 → 路线 → 引导线 → 队友 → Ping → 玩家。
- 竖屏/横屏自适应；`?key=` 令牌存入 `sessionStorage` 避免每次手输。

## 实施计划（4 个里程碑）

| 里程碑 | 内容 | 验收标准 | 预估工作量 |
|---|---|---|---|
| M1 服务骨架 | `FrameworkReference`、`WebSyncServer`、`/api/state` 快照 + source-gen JSON、设置项 + 防火墙说明 | `dotnet test` 全绿；浏览器访问 `/api/state` 返回当前地图与玩家 JSON | ~1 天 |
| M2 底图供给 | `/map.svg`（复用 `SvgLayerFilter` + `AssetCache`）、`/tiles/...` 代理、投影参数下发 | 平板浏览器正确显示 10 张 SVG 图 + 3 张瓦片图，可缩放 | ~1 天 |
| M3 实时同步 | WS 推送 + 事件订阅 + 节流；平板端覆盖层渲染（玩家/轨迹/路线/引导线/POI/队友/Ping）；断线重连 | 游戏内截图后 ≤1s 平板位置更新；换图/切楼层同步；断网恢复自动重连 | ~2 天 |
| M4 体验与收尾 | 令牌 + 二维码、玩家楼层自动跟随、偏好设置 UI、本地化键（en/zh 同步）、测试补齐 | 设置页可开关/复制链接/扫码；`StringsEn.json`/`StringsZh.json` 键集一致 | ~1 天 |

**测试策略**：StateBroadcaster 序列化与节流逻辑进 xunit（复用现有 `SetMapForTesting` 等测试钩子）；HTTP 层用 Kestrel `TestServer` 或起真实端口做集成测试；参照现有 656 测试的模式，新增约 30-50 个用例。

## 风险与开放问题

1. **体积增加**：Kestrel 使单文件发布 +15~25MB。若不可接受，备选是手写基于 `TcpListener` 的极简 HTTP/WebSocket 服务（~400 行，无新依赖），维护成本换体积。建议先接受体积。
2. **SVG 浏览器兼容性**：tarkov.dev 的 SVG 在桌面 Skia 渲染正常，Chrome 移动端对 SVG 支持良好，风险低；M2 即可验证。
3. **多客户端**：WS 广播天然支持多台平板/手机同时看，无需额外工作。
4. **与 Party 功能的关系**：二者正交——Party 是跨公网多人协作（加密 P2P），WebSync 是 LAN 只读镜像。不要复用 Party 协议。

---

## 决策点（待确认）

1. **方案 A（SVG+JSON 推送）确认？** 还是先做方案 B 帧推送快速验证（半天）再转 A？
2. **Kestrel 体积 +15~25MB 可接受？** 还是要零依赖手写迷你 HTTP 服务？
3. **访问令牌默认开启** 还是纯 LAN 裸奔（更便捷但同网络任何人可看你在哪）？
4. 优先级：先修第一部分 🟡 的 `OpenUrl` scheme 白名单（10 分钟改动），还是直接开 WebSync？
