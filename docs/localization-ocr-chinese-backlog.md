# 本地化与 OCR 中文适配

> 项目：tarkov-map-companion-self-improv · 记录日期：2026-10-14 · 状态：**评估完成；C2 字符串已外部核实，仍等待截图实测后实施**
> 基线：`master`（`bd8dd1a`，默认语言改动已合并）。B 系列已于 2026-10-14 重做评估并大幅扩范围，见「B 系列 · OCR 中文支持」。

---

## 结论先行

2026-10-14 把客户端默认语言从英文改成简体中文，改了 3 处：`Settings/AppSettings.cs:470` 的 `Language` 字段默认值、同文件 `Normalize()` 的枚举兜底 `Settings/AppSettings.cs:618-619`、`Localization/Localizer.cs:38` 的静态初始值。**只影响新装 / 没有 settings.json 的用户**，已有配置保持原值。

这次改动把两条「拿显示翻译当匹配基准」的潜伏路径从边缘情况推成了默认路径：`MapSession.ExtractByName` 的是**真 bug**，`MapPoi.SubtitleLabel` 的是**真实退化**；同病根还有一处诊断输出。这三处**已修**（见「已完成的修复」台账），不再作为待办。

剩下的问题分两类：

- **A 系列 — 契约卫生**：`Details` 的注释承诺「英文是 canonical、给 OCR 用」，但 `PoiBuilder` 有若干处在构建时就把 `Loc(...)` 烘进 `Details`。**当前无可见症状**（详情行按语言择一显示，wiki 中文 overlay 覆盖完整）。成本低，但**与 B 系列无耦合**（见「修正一」）。
- **B 系列 — OCR 中文支持（主体）**：游戏客户端设中文时，截图是中文，而整条 OCR 链路的四个环节都只认英文。**既有缺口，与默认语言改动无关。** 2026-10-14 复核后范围从「匹配侧」扩到「全链路」，并已定下两项决策。

## 已完成的修复（本轮台账，不再是待办）

| 位置 | 症状 | 改法 | 回归测试 |
|---|---|---|---|
| `MapSession.cs:906-909`（`ExtractByName`） | 用 `_mapData.Translate(key)`（随语言变化的显示翻译）去和恒为英文的 `MapPoi.Name` 比较；默认中文下 fallback 永远匹配不上，已保存撤离点在 id 失配时无法恢复 | 改为 `TranslateEn(key)` | 新增 `SavedExtractRestoreTests.ASavedIdThatNoPoiCarriesIsRestoredByItsEnglishNameUnderChinese`（该路径此前**零覆盖**） |
| `MapPoi.cs:212-222`（`SubtitleLabel`） | 用 `MapNames.Display(destination)`（本地化显示名）做 `Name.Contains(...)` 判断；默认中文下基准变中文、`Name` 是英文，永不包含 | 包含判断改用 `MapNames.EnglishFor(destination)`；显示文本仍用 `Display` | `ExitRowTests.ATransitWhoseNameSaysWhereItGoesGetsNoSubtitle` 改成中英双语 Theory |
| `Tools/FetchWiki.cs:107-110` | 同类病根：拿显示翻译当 key 与英文 wiki 名对账，中文默认下覆盖率诊断显示成接近 0 匹配（数据本身不受影响，仅诊断输出） | 改为 `store.TranslateEn(extract.Name)` | 无独立测试（`--fetch-wiki` 一次性诊断输出） |
| `Views/PreferencesWindow.axaml.cs:72-73`（`ShowCategory`） | `CategoryList.SelectedIndex == -1` 时六个页面全部 `IsVisible = false`，面板全空 | 加 `if (index < 0) index = 0;` 守卫 | 无（只加守卫，未重构） |

## A 系列 — 契约卫生（与 B 系列无耦合，可后置）

> **修正一（原记录有误）**：原文称「A1 是 B1 的前置」，理由是「一旦 OCR 开始消费 `Details` 就会变成真 bug」。复核结论：**OCR 匹配路径只消费 `MapPoi.Name`（`Vision/ExitAvailability.cs:94`）和 `PanelRow.Name`，不消费 `Details`。** `MapSession.ExtractByName` 走的是 id → localization key，也不碰 `Details`。因此 A 系列是独立的契约债务，不阻塞 B 系列，也不应压在 B 系列之前。

### A1 — `Details` 在中文下被烘入中文，破坏「英文列表是 canonical」的契约

**证据**（`AddLine(english, chinese)` 的双参数写法才是正确形态）：

| 位置 | 代码 | 问题 |
|---|---|---|
| `Data/PoiBuilder.cs:231-235` | `void AddLine(string english, string? chinese = null)` → `detailsZh.Add(string.IsNullOrWhiteSpace(chinese) ? english : chinese!)` | 单参数时 `chinese` 为 null，回落把**同一个值**同时写进 `Details` 和 `DetailsZh` |
| `Data/PoiBuilder.cs:271-275` | `AddLine(LocEnglish("Poi.Needs", …), Loc("Poi.Needs", …))` | 正确形态 |
| `Data/PoiBuilder.cs:281-285` | `AddLine(LocEnglish("Poi.AlwaysOpen", …), Loc("Poi.AlwaysOpen", …))` | 正确形态 |
| `Data/PoiBuilder.cs:255-257` | `AddLine(… ? Loc("Poi.ActivatedBy", switchName) : Loc("Poi.RequiresSwitch"))` | **只传一个参数**，`Loc` 结果进了 `Details` |
| `Data/PoiBuilder.cs:290` | `AddLine(Localizer.Loc("Poi.NotAlwaysOpen"));` | 同上 |
| `Data/PoiBuilder.cs:296` | `AddLine(Localizer.Loc("Poi.SingleUse"));` | 同上 |
| `Data/PoiBuilder.cs:303` | `AddLine(Localizer.Loc("Poi.Coop"));` | 同上 |
| `Data/PoiBuilder.cs:254`（注释） | 「The wrapper is localized at build time; the Chinese list mirrors it exactly」 | 注释确认：构建时语言同时决定两个列表 |

- **改法**：那 4 处补成双参数，英文用 `Localizer.LocEnglish(...)`（`Localization/Localizer.cs:79,96`），中文用 `Localizer.Loc(...)`。
- **验证**：`LanguageScope(ChineseSimplified)` 下构建带 switch / co-op 的撤离点，断言 `Details` 为英文、`DetailsZh` 为中文；扩展 `tests/TarkovMapCompanion.Tests/PoiTests.cs`。

#### A4 — 同样的烘焙还发生在 `AddLocks` / `AddBossZones`，且完全没有 `DetailsZh`

| 位置 | 代码 |
|---|---|
| `Data/PoiBuilder.cs:542` → `:551` | `details.Add(Localizer.Loc("Poi.NeedsPower"))` → `Details = details`，**无 `DetailsZh` 赋值** |
| `Data/PoiBuilder.cs:470` | `chance = Localizer.Loc("Poi.SpawnChanceMap", …)` |
| `Data/PoiBuilder.cs:474` | `zoneChance = Localizer.Loc("Poi.SpawnChanceZone", …)` |
| `Data/PoiBuilder.cs:476,486` | `details = { chance, zoneChance }` → `Details = details`，无 `DetailsZh` |

与 A1 同形态，只是走直接 `details.Add` 而非 `AddLine`。改法/验证同 A1，对 Lock 与 BossZone 各加一条断言。

### A2 — transit 的 `Details` 没有 `DetailsZh`，靠「构建时烘焙」歪打正着

| 位置 | 代码 |
|---|---|
| `Data/PoiBuilder.cs:424` | `var details = new List<string>();` |
| `Data/PoiBuilder.cs:427` | `details.Add(Localizer.Loc("Poi.LeadsTo", MapNames.Display(destination)));` — 构建时语言被烘进 `Details` |
| `Data/PoiBuilder.cs:430` | `details.Add(transit.Conditions);` — 原始英文串，未本地化 |
| `Data/PoiBuilder.cs:432-444` | 只赋 `Details = details`（`:442`），**没有 `DetailsZh`** |

`DetailLines`（`Data/MapPoi.cs:120-141`）在 `DetailsZh` 缺失时回落到 `Details[i]`，平时因烘焙语言等于界面语言而看不出来。风险窗口是「POI 构建时语言 ≠ 界面语言」——语言改动要求重启（`Localization/Localizer.cs:23-25`），所以当下不触发。

- **改法**：`Poi.LeadsTo` 用 `LocEnglish`/`Loc` 成对；`transit.Conditions` 按 `AddLine` 回落规则复制一份英文。
- **验证**：`LanguageScope(ChineseSimplified)` 下构造 transit，断言 `DetailLines[0]` 为中文，切英文后为英文。

### A3 — `MapPoi` 里「Name 恒为英文」的注释不准确

`Data/MapPoi.cs:74-75` 声称 `Name` 恒英文且供 OCR 匹配。实际只有 **extract / transit / loot container / stationary weapon / boss zone / 有名字的 switch 与 BTR** 成立；不成立的是 **locks（`PoiBuilder.cs:548` 回落分支）、spawn（`:634-650`）、hazard（`:674-686`）、无名字的 switch（`:668-670`）、无名字的 BTR（`:604`）** —— 这些回落标签的 `Name` 是构建时 `Loc` 结果（中文）。

- **改法（保守）**：把注释收窄为「主路径 POI 的 `Name` 恒英文；部分标签类 POI 的 `Name` 是构建时 `Loc` 结果」。若要所有 `Name` 恒英文属更大改动，另行决定。
- **验证**：中文语言下构建 spawn / hazard / lock，断言其 `Name` 语言，把事实钉住。

## B 系列 — OCR 中文支持（主体）

### 复核结论：原文的 B1 定义不完整

原记录把问题定义为「候选集只有英文名 + CJK 阈值不可迁移」（匹配侧）。**实测后确认这是第 4 环**，中文下前 3 环已经断了，匹配根本不会被调用到。真正的范围是一条 4 环链路：

| # | 环节 | 位置 | 中文下的行为 |
|---|---|---|---|
| **C1** | OCR 引擎语言 | `Vision/WindowsOcrTextReader.cs:52` | **硬编码 `en-US`**（注释还写着「Prefer English: the extract names we match against are the English ones」）。中文面板用英文引擎识别 → 乱码或空。`Ocr.NoLanguagePack`（`StringsEn/Zh.json:215`）文案也写死「添加英语」 |
| **C2** | 面板存在性判定 | `Vision/ExtractPanelParser.cs:141`、`:227-229` | `:140-142` 只要没有一行匹配 `^(EXTRACT\|TRANSIT\|EXFIL\|EXIT)…` 就**直接 `return NotFound`**；header 判据是 `normalized.Contains("extraction point")` / `Contains("stay")`，纯英文。→ `PanelFound=false` → `Resolve` 在 `ExitAvailability.cs:90` 立刻返回 null |
| **C3** | 候选集 | `Vision/ExitAvailability.cs:93-96` | 只有 `Normalize(p.Name)`，即英文 |
| **C3b** | **canonical 映射陷阱** | `ExitAvailability.cs:110` + `:43` | 见下 |
| **C4** | 阈值 | `Vision/NameMatcher.cs:32,35` | `floor=0.78` / `margin=0.08` 不可直接迁移 |
| **C5** | 未识别上报 | `ExitAvailability.cs:54`、`ExtractPanelParser.cs:266` | `LooksLikeAnExitRow` 依赖 `Kind` / `HasIdKeyword`，二者来自英文 id 关键词，因此中文下「这行读不出来」也不会被上报 |

#### C2 中文面板字符串（2026-10-14 外部核实，非推断）

来源：游戏自身的语言表，由 SPT（Single Player Tarkov）服务器仓库分发——`project/assets/database/locales/global/en.json` 与 `global/ch.json`（<https://github.com/sp-tarkov/server>）。这是**游戏自己的本地化数据**，不是社区转述，因此下述字符串可用；但**「id 列如何拼装」这一层仍是推理**，见末尾标注。

| 用途 | 英文 | 简体中文 |
|---|---|---|
| 列表 header（`FindEp`） | `Find an extraction point` | **找到撤离点** |
| 进行中·区内（`StayInEpInside`） | `Stay in the extraction point` | **停留在撤离点内** |
| 进行中·区外（`StayInEpOutside`） | `Get back to the extraction point` | **回到撤离点内** |
| PMC id token（`EXFIL`） | `EXFIL` | **撤离点** |
| Scav id token（`Exit`） | `EXIT` | **退出** |
| Transit id token（`FAC_TRANSIT_12` 等） | `TRANSIT01` | **目的地01** |
| 撤离倒计时（`Extraction in {0:F1}`） | `Extraction in {0:F1}` | **撤离倒计时 {0:F1}** |
| Transit 倒计时（`Transition in {0:F1}`） | `Transition in {0:F1}` | **{0:F1} 后转移** |

**对 C2 的三条直接结论：**

1. **id 列 token 必须双语化**（置信度：`目的地` 高；`撤离点`/`退出` 中高）。英文分支 `EXTRACT|TRANSIT|EXFIL|EXIT` 对应的中文分支是 `撤离点|退出|目的地`。注意 `TRANSIT01` 的中文值是 `目的地01`——**中文比英文短**（3 字 vs 8 字），但同样带半宽数字。
2. **header 判据的共享子串是 `撤离点`**（正对英文的 `extraction point`），**否决信号是 `停留`/`回到`**（正对英文的 `stay`）。`ExtractPanelParser.cs:227-231` 的两个 `Contains` 都要成对扩。
3. **标点形态不同**：中文串用**半宽数字**（`目的地01`、`ZB-013`）但**全角括号与冒号**（`Scav检查站（合作撤离点）`、`解密：`）。现有 `NameMatcher.Normalize` 会把全角标点一律折叠成空格（`char.IsLetterOrDigit('（')` 为 false），所以**点名侧的归一化反而安全**；要小心的是 header 的 `Contains`（见下）。

**C2 新增的一个坑（由代码确认，不在原记录里）：`Contains` 对中文会被空格打断。** `Normalize` 保留汉字（`char.IsLetterOrDigit('撤')` 为 true）但把空白折叠成**单个空格**。若 OCR 把 header 返回成 `找 到 撤 离 点`（引擎是否在汉字间插空格不确定），`normalized.Contains("找到撤离点")` 会失败。**中文分支不能用朴素 `Contains` 裸串**，要么比对「去掉全部空白」的形态，要么对 CN 单独做一次去空格归一化。这条必须在 `ExtractPanelParserTests` 里用带空格的输入钉住。

**id 列形态（2026-10-14 已由图像实证）**：找到多张可读的中文客户端面板截图，来源游侠网 ali213《逃离塔科夫》撤离点怎么看（<https://gl.ali213.net/glpic/2020/02/26/…>，2020-02-26）。所有可读样本中，左侧 id 列一律是 **`撤离点01`、`撤离点02`… `撤离点06`**——**「token + 半宽两位序号」**，与语言表推出的结构一致。面板为三列：`撤离点NN │ 本地化点名 │ 计时`（`??:??:??` 或 `0:00:26`），可带需求子行（实测形如 `你需要: 绳索`、`交付卢布(3000)`）。

> **⚠️ 这些截图的版本是 EFT 0.12.2（2020-02），距今约 6 年。** 详见下方「P0 素材的适用边界」——**它们能验证 C2，但绝不能用来定 C4 阈值。**

> 附带一个有价值的性质：现有正则要求 token 后必须跟 1-4 个数字类字符再遇空白/行尾（`ExtractPanelParser.cs:109` 的 lookahead）。中文分支沿用这一结构即可——裸 `撤离点`（如 header 尾部 `找到撤离点` 本身）因后面不跟数字而不会被误判成 id 行，这个安全性质天然继承。

> **仍无图像实证的两项**（仅有语言表背书，置信度仍高）：transit 的 `目的地01` 与 Scav 的 `退出01`——检索到的中文面板样本**全部是 PMC 局**，没有 transit 行，也没有 Scav 局。

#### 截图里的中文点名 vs 本仓库 `translationsZh`（2026-10-14 交叉验证）

把上面截图中读到的点名与 `Data/Snapshots/mapdata.json.gz` 的 `translationsZh` 逐条比对：

| 截图（0.12.2） | 仓库 `translationsZh` | 仓库英文 | 结论 |
|---|---|---|---|
| 通往海关的路 | 通往海关的路 | Road to Customs | **逐字吻合** |
| 码头的船 | 码头的船 | Pier Boat | **逐字吻合** |
| 临时指挥所 | 临时指挥所 | CCP Temporary | **逐字吻合** |
| 岩石通道 | 岩石通道 | Rock Passage | **逐字吻合** |
| RUAF大门 | RUAF大门 | RUAF Gate | **逐字吻合** |
| ZB-016 / ZB-014 | ZB-016 / ZB-014 | （原样） | **逐字吻合** |
| UN路障 | UN路障 | UN Roadblock | **逐字吻合** |
| 郊区 | 郊区 | Outskirts | **逐字吻合** |
| 南部载具撤离点 / 工厂大门 / 峭壁斜坡 | **当前 Woods 数据中无此三名** | — | 2020 年旧 Woods 布局 |

**结论：凡是今天仍存在的撤离点，中文名与游戏内完全一致。** 三处分歧全部是 6 年间的地图改动（`峭壁斜坡` 现在是 Shoreline 的 `Cliff Descent`），**不是翻译漂移**。

这条验证的价值在于它正面确认了 C3 的前提：**把 `NameZh` 纳入候选集是可行的，它就是游戏实际显示的文字。** 否则整个 C3 建立在「仓库里的中文名与游戏里一致」这个未经验证的假设上。

#### P0 素材的适用边界（重要）

检索到的可读中文面板截图**全部是 EFT 0.12.2（2020-02）**。检索范围包括游侠网、百度贴吧（403 反爬）、百度经验、ge330、Steam 社区截图墙（含一张确认是中文客户端 1.2.0.0 的战局结算画面）、Bilibili、搜狗；小黑盒/知乎/小红书/微博未取到可直达图片路径。

**因此这些素材的用途必须分开：**

- **✅ 可用于 C2** —— 验证的是**字符串形态**（`撤离点01` 的拼装方式、`找到撤离点` 的 header、`撤离点NN │ 名称 │ 计时` 的三列布局、全角/半角混排）。这些是**面板 UI 契约**，六年未必改版，且已与 1.2.0.0 中文客户端截图间接交叉（该客户端确为中文）。
- **❌ 不可用于 C4** —— 阈值是针对**今天的撤离点集合**测的。拿 2020 年的截图跑 `--read-exits`，`南部载具撤离点`/`工厂大门`/`峭壁斜坡` 在当前数据里不存在，必然 unmatched。**这是数据版本漂移造成的假阴性，失败形态与「中文匹配写错了」完全一致** —— 一旦拿去调 floor/margin，会把阈值调到一个完全错误的值上。

**结论：P0 拆成两半。** C2 的字符串形态**已经不需要等真机了**；C4 的阈值**仍然需要 1.0+ 的当前版本中文客户端截图**，社区素材无法替代。

> 附带风险待验：截图里计时写作 `0:00:26`（半角冒号），但语言表中 UI 标签用全角 `：`（如 `解密：`）。`TrailingTimer`（`ExtractPanelParser.cs:126-128`）的字符类只含半角 `[:.;]`。若某些版本渲染成全角 `：`，倒计时不会被剥离，会污染点名。**该正则是否需要同时接受全角，待当前版本截图确认。**

#### C3b 是最容易骗过测试的陷阱

```csharp
// ExitAvailability.cs:109-110
if (Best(row, candidates) is { } match)
    matched.Add(match.NormalizedName);   // ← 存的是「匹配到的候选串」

// ExitAvailability.cs:43
public bool Includes(MapPoi poi) => _names.Contains(NameMatcher.Normalize(poi.Name));  // ← 查的是英文 Name
```

候选集现在是扁平 `HashSet<string>`，`NameMatch.NormalizedName` 只能表达「哪个候选赢了」，无法表达「赢的那个候选对应哪个 POI」。**直接把中文名加进 `candidates` 是不够的**：中文命中会被存成 `17号仓库`，而 `Includes` 拿英文 `p.Name` 去查，**对每个 POI 都返回 false**。

失败形态是「OCR 读出中文了、面板也认出来了、但地图上撤离点一个都没点亮」——功能看起来静默失效，而 `dotnet test` 完全可能通过。**C3 的正确形态是把候选改成「表层串 → 英文 canonical」的映射，`matched` 仍只存英文 canonical，`Includes` 契约不变。**

### 实测数据（2026-10-14，非推断）

**中文名覆盖率 100%** —— `Data/Snapshots/mapdata.json.gz` 的 `translationsZh` 覆盖全部 **195/195** 个 extract + transit key，17 张图逐张 100%（customs 32/32、woods 24/24、streets 22/22、shoreline 23/23、terminal 等 0 个 POI）。原记录担心的「上游中文名质量」**不是覆盖问题**，只是已记录的少数错 key（`Gate Near Medblock` → `17号仓库`、`Reserve Checkpoint` → `Scav检查站`）。候选侧不缺数据。

**中文名长度分布（决定了阈值）**：

```
≤4 字: 52/195 = 27%    ≤5 字: 90/195 = 46%    ≤8 字: 129/195 = 66%
最坏一对「不同」候选: 0.833（woods 的 zb 016 / zb 014）
1 字扰动的自相似度: 中位数 0.857，p1 0.667，最小 0.500
```

含义：`floor=0.78` 下，4 字名错 1 字 = 0.75（漏），3 字名错 1 字 = 0.67（漏）——**近三分之一的中文撤离点只要认错一个字就整个失效**。`margin=0.08` 尚可存活（最近的不同候选对 0.833，满分命中时 margin 0.167 > 0.08）。但若为接住短名错误把 floor 降到 ~0.70，`8号门` 被读成 `3号门`（0.667）就从「漏报」变成「误标」——**这个取舍必须由真实截图实测钉死，不先猜数值。**

**`NameMatcher.Normalize` 对 CJK 成立，原记录的决策点 2 可以关掉** —— `char.IsLetterOrDigit('仓')` 为 true、`char.ToLowerInvariant` 对汉字是空操作、汉字间无空格所以不涉及分词。「按字母数字切词 + 单空格分隔」在无空格文本上只是不产生分隔符。**不需要设计 CJK 分词策略，保持现状。** 唯一要注意的是 `Levenshtein`（`:136-161`）按 UTF-16 码元比较，对汉字等价于「按字」编辑距离——这恰好是合理的粒度。

### 已定决策（2026-10-14）

1. **引擎语言选择 = 新增独立设置项**（偏好设置中让用户选「游戏客户端语言」），**不跟随 App 界面语言**。理由：游戏客户端语言与本 App 界面语言是两个独立设置，用户可能不一致；跟随界面语言会让不一致的用户直接识别失败。
   - 代价：多一个用户需要理解并正确配置的开关；P1 需要加 `AppSettings` 字段 + `Normalize()` 兜底 + 偏好面板控件（注意 `PreferencesWindow.axaml` 的 `Name` 是 load-bearing，见 `AGENTS.md`）。
2. **P0 实测素材 = 用户提供真实中文客户端截图**（拉起撤离面板的那一帧），若干张更好（覆盖不同分辨率与不同撤离点数）。仓库当前**没有任何图片 fixture**，`docs/screenshot-dark.png` 是界面截图不是游戏画面。
   - 待确认的具体点：id 列是「token + 序号」（`撤离点01`）还是本地化点名，以及倒计时在面板上的实际形态（见 C2 一节末尾）。**其余 C1/C2 字符串已于 2026-10-14 经游戏语言表核实**（来源见 C2 一节），不再是推断。

### 实施计划

| 阶段 | 内容 | 验证方式 |
|---|---|---|
| **P0** | 收截图，实测：OCR 原始输出形态、id 列是 `EXFIL01` 还是中文、header 实际文案、实际字错率 → 据此定 C4 阈值 | `--read-exits <png> <map>` 先记录当前 0 命中基线 |
| **P1**（C1） | `WindowsOcrTextReader` 按所选语言创建引擎（含回退链）；`Ocr.*` 文案泛化为不写死英语；新增设置项与两语 key | 截图上输出非乱码文本；`LocalizationKeyTests`；`SettingsKeyTests` |
| **P2**（C2 + C5） | `ExtractPanelParser` 的 id 列正则加 `撤离点\|退出\|目的地` 分支、header 判据扩为 `撤离点`/`停留`/`回到`，并处理「OCR 在汉字间插空格」的 `Contains` 失效（见 C2 一节），使 `PanelFound` / `Kind` / `HasIdKeyword` / `LooksLikeAnExitRow` 在中文下成立 | `ExtractPanelParserTests` 新增中文捕获文本用例，含**带空格**的 header 输入 |
| **P3**（C3 + C3b） | **核心改动**：`ExitAvailability` 候选从 `HashSet<string>` 改为「表层串 → 英文 canonical」映射；`matched` 仍只存英文，`Includes` 契约不变 | `ExitAvailabilityTests`：中文读法点亮正确 POI，且 `Includes` 对其英文 `Name` 返回 true（**这条是防 C3b 陷阱的回归钉子**） |
| **P4**（C4） | `NameMatcher` 按读法语种分别取 floor；数值由 P0 实测决定 | 单测钉住两条：「4 字名错 1 字仍命中」与「8号门 ≠ 3号门」 |
| **P5** | 更新本文与 `AGENTS.md` 契约注记；`scripts\publish.ps1` 重跑 | `dotnet test` 绿 + 发布版手测 |

**排序理由**：P3 是唯一有架构含量的改动，P1 / P2 / P4 都只是加分支。P4 必须排在 P0 之后——阈值是这张表里唯一不能先猜的数。

### 明确不做

- 不改 `NameMatcher.Normalize` / `Levenshtein` 的算法（实测表明对 CJK 成立）。
- 不动 `PoiBuilder` 里 `Details` 的 `Loc`/`LocEnglish` 混用重构（更大范围的数据不变量重构，用户已明确划界）。
- 不动 `ExtractNotesTests.cs` 的测试卫生问题（用户已叫停）。
- 不为 `zh-Hans` 语言包缺失做自动下载或引导安装，只做文案指引。

## 相关文档

- 渲染性能专题：`docs/render-performance-backlog.md`
- 安全 + WebSync 评估：`docs/eval-security-and-lan-sync.md`
- WebSync 实现规格：`docs/websync-implementation-spec.md`