# 本地化与 OCR 中文适配（待启动）

> 项目：tarkov-map-companion-self-improv · 记录日期：2026-10-14 · 状态：**仅记录，尚未实施**
> 基线：分支 `websync`。本文是专题启动时的输入，不是已完成的工作。

---

## 结论先行

2026-10-14 把客户端默认语言从英文改成简体中文，改了 3 处：`Settings/AppSettings.cs:470` 的 `Language` 字段默认值、同文件 `Normalize()` 的枚举兜底 `Settings/AppSettings.cs:618-619`、`Localization/Localizer.cs:38` 的静态初始值。**只影响新装 / 没有 settings.json 的用户**，已有配置保持原值。

这次改动把两条「拿显示翻译当匹配基准」的潜伏路径从边缘情况推成了默认路径：`MapSession.ExtractByName` 的是**真 bug**，`MapPoi.SubtitleLabel` 的是**真实退化**；同病根还有一处诊断输出。这三处**已修**（见「已完成的修复」台账），不再作为待办。

剩下的问题分两类：

- **契约类（A 系列）**：`MapPoi.Details` 的注释承诺「英文是 canonical、给 OCR 用」，但 `PoiBuilder` 有若干处在**构建时**就把 `Loc(...)`（显示翻译）烘进 `Details`，中文运行时列表里存的就是中文。**当前无可见症状**——详情行按语言从 `Details`/`DetailsZh` 择一显示（`Data/MapPoi.cs:120-141`），且 wiki 中文 overlay 覆盖完整（`extract-notes.zh.json` 57 条全部含 `requirementZh`，主 `extract-notes.json` 的 57 条 requirement 都能匹配到中文，回落 0 条），用户确认看不出问题。**但一旦 OCR 开始消费 `Details`（注释正是这么承诺的），就会变成真 bug。** 先修契约：成本低，且是 B1 的前置。
- **功能缺口（B1，主要前瞻项）**：OCR 的候选与匹配全部走英文 `MapPoi.Name`，所以**游戏客户端设成中文时，截图是中文、OCR 出中文，匹配英文候选命中率≈0**。这是**既有缺口，与本次默认语言改动无关**（oracle 复核时明确指出）。它是本专题的主体，但需要先做几个决策。

**建议顺序**：A1（含核实中新发现的 A4）→ B1 决策 → B1 实现 → A2 → A3。

## 已完成的修复（本轮台账，不再是待办）

| 位置 | 症状 | 改法 | 回归测试 |
|---|---|---|---|
| `MapSession.cs:906-909`（`ExtractByName`） | 用 `_mapData.Translate(key)`（随语言变化的显示翻译）去和恒为英文的 `MapPoi.Name` 比较；默认中文下 fallback 永远匹配不上，已保存撤离点在 id 失配时无法恢复 | 改为 `TranslateEn(key)` | 新增 `SavedExtractRestoreTests.ASavedIdThatNoPoiCarriesIsRestoredByItsEnglishNameUnderChinese`（该路径此前**零覆盖**） |
| `MapPoi.cs:212-222`（`SubtitleLabel`） | 用 `MapNames.Display(destination)`（本地化显示名）做 `Name.Contains(...)` 判断「名字里已说明去向就不再显示副标题」；默认中文下基准变中文、`Name` 是英文，永不包含，所有 transit 行多出冗余副标题 | 包含判断改用 `MapNames.EnglishFor(destination)`；显示文本仍用 `Display` 以保持本地化 | `ExitRowTests.ATransitWhoseNameSaysWhereItGoesGetsNoSubtitle` 改成中英双语 Theory |
| `Tools/FetchWiki.cs:107-110` | 同类病根：拿显示翻译当 key 与英文 wiki 名对账，中文默认下覆盖率诊断显示成接近 0 匹配。（**数据本身不受影响**，写盘的 `document.Maps` 来自抓取；只是诊断输出） | 改为 `store.TranslateEn(extract.Name)` | 无独立测试（仅 `--fetch-wiki` 工具的一次性诊断输出） |
| `Views/PreferencesWindow.axaml.cs:72-73`（`ShowCategory`） | `CategoryList.SelectedIndex == -1` 时六个页面全部 `IsVisible = false`，面板全空 | 加 `if (index < 0) index = 0;` 守卫 | 无（只加守卫，未重构） |

## 仍遗留的问题

> 排列顺序 = 处理优先级；编号沿用评审的 A/B 分组，便于对照。

### P1 · A1 — `Details` 在中文下被烘入中文，破坏「英文列表是 canonical」的契约

**证据**（`AddLine(english, chinese)` 的双参数写法才是正确形态）：

| 位置 | 代码 | 问题 |
|---|---|---|
| `Data/PoiBuilder.cs:231-235` | `void AddLine(string english, string? chinese = null)` → `detailsZh.Add(string.IsNullOrWhiteSpace(chinese) ? english : chinese!)` | 单参数时 `chinese` 为 null，回落把**同一个值**同时写进 `Details` 和 `DetailsZh` |
| `Data/PoiBuilder.cs:271-275` | `AddLine(LocEnglish("Poi.Needs", …), Loc("Poi.Needs", …))` | 正确：英文进 `Details`、中文进 `DetailsZh` |
| `Data/PoiBuilder.cs:281-285` | `AddLine(LocEnglish("Poi.AlwaysOpen", …), Loc("Poi.AlwaysOpen", …))` | 正确 |
| `Data/PoiBuilder.cs:255-257` | `AddLine(… ? Loc("Poi.ActivatedBy", switchName) : Loc("Poi.RequiresSwitch"))` | **只传一个参数**，`Loc` 结果（中文界面下即中文）进了 `Details` |
| `Data/PoiBuilder.cs:290` | `AddLine(Localizer.Loc("Poi.NotAlwaysOpen"));` | 同上 |
| `Data/PoiBuilder.cs:296` | `AddLine(Localizer.Loc("Poi.SingleUse"));` | 同上 |
| `Data/PoiBuilder.cs:303` | `AddLine(Localizer.Loc("Poi.Coop"));` | 同上 |
| `Data/PoiBuilder.cs:254`（注释） | 「The wrapper is localized at build time; the Chinese list mirrors it exactly」 | 注释本身确认了该行为：构建时语言同时决定两个列表 |

- **改法**：把那 4 处（`255-257`、`290`、`296`、`303`）补成双参数，英文用 `Localizer.LocEnglish(...)`（定义见 `Localization/Localizer.cs:79,96`），中文用 `Localizer.Loc(...)`。
- **预期**：`Details` 恢复恒英文；中文显示仍由 `DetailLines`（`Data/MapPoi.cs:120-141`）从 `DetailsZh` 取。
- **验证**：在 `LanguageScope(ChineseSimplified)` 下构建一条带 switch / co-op 的撤离点，断言 `Details` 各项为英文、`DetailsZh` 各项为中文（或至少断言 `Details[i]` 与 `DetailsZh[i]` 语种不同）；扩展 `tests/TarkovMapCompanion.Tests/PoiTests.cs`。

#### A4（核实中新发现）— 同样的 `Details` 烘焙还发生在 `AddLocks` / `AddBossZones`，且完全没有 `DetailsZh`

| 位置 | 代码 | 问题 |
|---|---|---|
| `Data/PoiBuilder.cs:542` → `:551` | `details.Add(Localizer.Loc("Poi.NeedsPower"))` → `Details = details` | 单个 `Loc` 串直接进 `Details`，**无 `DetailsZh` 赋值** |
| `Data/PoiBuilder.cs:470` | `chance = Localizer.Loc("Poi.SpawnChanceMap", …)` | 同上 |
| `Data/PoiBuilder.cs:474` | `zoneChance = Localizer.Loc("Poi.SpawnChanceZone", …)` | 同上 |
| `Data/PoiBuilder.cs:476,486` | `details = { chance, zoneChance }` → `Details = details` | 无 `DetailsZh` |

这与 A2 是同一形态（`Details` 有值、`DetailsZh` 缺失），只是走直接 `details.Add` 而非 `AddLine`，所以不在 A1 的 4 处之列。改法/预期/验证同 A1，对 Lock 与 BossZone 各加一条断言。

### P2 · B1 — OCR 只认英文名，中文游戏客户端无法匹配（主要前瞻项）

**证据**：

| 位置 | 说明 |
|---|---|
| `Vision/NameMatcher.cs:41-68` | `Normalize`：`char.IsLetterOrDigit` 保留 + `char.ToLowerInvariant` 小写化；标点/空白折叠成单个空格 |
| `Vision/ExitAvailability.cs:93-96` | 候选集 = `exits.Select(p => NameMatcher.Normalize(p.Name))`；`p.Name` 对撤离点恒英文 |
| `Vision/ExitAvailability.cs:43` | `Includes(MapPoi poi) => _names.Contains(NameMatcher.Normalize(poi.Name))` |
| `Vision/ExitAvailability.cs:133-150` | `Best(...)` 对每个 OCR 行读法调 `NameMatcher.Match(reading, candidates)` |
| `MapSession.cs:1596` | 运行时唯一入口 `ExitAvailability.Resolve(reading, exits, …)` |
| `Rendering/PoiOverlay.cs:242` | 用 `availability.Includes(poi)` 决定撤离点是否淡化 |
| `Views/MainWindow.axaml.cs:1000` | 用 `availability?.Includes(poi)` 写 `poi.AvailableThisRaid` |
| `Data/MapPoi.cs:74-75` | 注释明确 `Name` 恒英文、**供 OCR 匹配** |

**问题链**：游戏客户端设中文 → 撤离面板截图是中文 → OCR 出中文 → 与英文候选做 `Similarity`（`Vision/NameMatcher.cs:71-81`，`1 - Levenshtein/长度`），跨脚本时几乎全是等价替换，相似度≈0 → `Resolve` 因 `matched.Count == 0` 返回 null（`Vision/ExitAvailability.cs:119-120`）→ 「本场可用撤离点」功能整体失效（不会误标，而是完全不生效）。

**待决策（这些是待决策点，不是已定方案）：**

1. **候选集形态**：是否把 `MapPoi.NameZh`（`Data/MapPoi.cs:77`）纳入候选，还是维持英文单语、另建一张中文候选表？纳入会影响 B1 的重定义范围。
2. **`NameMatcher.Normalize` 对 CJK 是否成立**（`Vision/NameMatcher.cs:41-68`）：小写化对汉字是空操作；「按字母数字切词 + 单空格分隔」在无空格分词的汉字上会把整名连成一串；`Levenshtein`（`Vision/NameMatcher.cs:136-161`）按 UTF-16 码元比较，对汉字等价于「按字」编辑距离，是否足以区分同图近名要实测。
3. **上游中文名的质量**：`AGENTS.md:115-120` 已记录上游 json.tarkov.dev / wiki 抓取带**真实缺陷**（Trailer Park 两条同名不同中文名的记录、`Gate Near Medblock` → `17号仓库`、`Reserve Checkpoint` → `Scav检查站` 中英不一致等）。中文名来自同一条链路，其质量需单独核，不能默认可信。
4. **契约重定义**：若最终支持双语 OCR，「英文列表是 OCR canonical」这条契约本身要重新定义，并**与 A1 一起处理**（否则 `Details` 里中英混存的问题会在 OCR 消费时同时爆）。

- **改法**：先产出决策记录，再按决策实现（候选集、Normalize 的 CJK 策略、阈值 `DefaultFloor`/`DefaultMargin` 见 `Vision/NameMatcher.cs:32,35`）。
- **预期**：中文客户端下撤离点可用性功能可用，且不把「读不出」误报成「不可用」。
- **验证**：用 `dotnet run --project src/TarkovMapCompanion -c Release -- --read-exits <截图.png> <map>`（`Tools/ReadExits.cs`，其中 `:98-120` 打印 resolution 段与 AVAILABLE 数）对一张真实中文客户端截图跑，先记录当前 0 命中的基线，再对照英文客户端截图应收敛到同量级。

### P3 · A2 — transit 的 `Details` 没有 `DetailsZh`，靠「构建时烘焙」歪打正着

**证据**：

| 位置 | 代码 | 问题 |
|---|---|---|
| `Data/PoiBuilder.cs:424` | `var details = new List<string>();` | — |
| `Data/PoiBuilder.cs:427` | `details.Add(Localizer.Loc("Poi.LeadsTo", MapNames.Display(destination)));` | 构建时语言被烘进 `Details` |
| `Data/PoiBuilder.cs:430` | `details.Add(transit.Conditions);` | 原始数据串（英文），未本地化 |
| `Data/PoiBuilder.cs:432-444` | 对象初始化只赋 `Details = details`（`:442`） | **没有 `DetailsZh`** |

`MapPoi.DetailLines`（`Data/MapPoi.cs:120-141`）在 `DetailsZh` 缺失时回落到 `Details[i]`；由于 `:427` 的烘焙语言通常等于界面语言，平时看不出来。风险窗口是「POI 构建时的语言 ≠ 界面语言」——目前语言改动要求**重启**（`Localization/Localizer.cs:23-25`、`Views/PreferencesWindow.axaml.cs:479-483`），所以**当下不触发**；一旦出现语言热切换、或 POI 缓存跨语言复用，就会显示错语言。

- **改法**：给 transit 补 `DetailsZh`：`Poi.LeadsTo` 用 `LocEnglish`/`Loc` 成对；`transit.Conditions` 若无中文，按 `AddLine` 的回落规则复制一份英文（`Data/PoiBuilder.cs:231-235`）。
- **预期**：transit 详情行与界面语言解耦。
- **验证**：`LanguageScope(ChineseSimplified)` 下构造 transit，断言 `DetailLines[0]` 为中文；切到英文后为英文。

### P4 · A3 — `MapPoi` 里「Name 恒为英文」的注释不准确

**证据**：

| 位置 | 代码 | `Name` 实际语言 |
|---|---|---|
| `Data/MapPoi.cs:74-75` | 注释：`<see cref="Name"/> stays English and is what OCR matches against` | 声称恒英文 |
| `Data/PoiBuilder.cs:548` | locks：`Name = string.IsNullOrWhiteSpace(l.LockType) ? Localizer.Loc("Poi.Locked") : Capitalize(l.LockType)` | 回落分支是**烘焙中文** |
| `Data/PoiBuilder.cs:634-650`（`DescribeSpawn`，赋值在 `:458`） | spawn：整条 `Localizer.Loc(...)` | **烘焙中文**（`NameZh` 未设） |
| `Data/PoiBuilder.cs:674-686`（`PrettifyHazard`，赋值在 `:524`） | hazard：`Localizer.Loc(...)`（`:680-684`） | **烘焙中文**（`NameZh` 未设） |
| `Data/PoiBuilder.cs:657-671`（`DescribeSwitch`） | switch：正常分支 `:666` 返回 `TranslatePair` 的英文；回落分支 `:668-670` 是 `Localizer.Loc(...)` | 回落分支**烘焙中文** |
| `Data/PoiBuilder.cs:603-605` | BTR：空白名时 `:604` `Localizer.Loc("Poi.BtrStop")`，否则 `:605` `TranslatePair`（英文） | 空白名分支**烘焙中文** |
| `Data/PoiBuilder.cs:309-319` / `:432-444` / `:563-569` / `:583-589` | extract / transit / loot container / stationary weapon 用 `TranslatePair` | 英文（正确） |
| `Data/PoiBuilder.cs:469` + `Data/MapDataStore.cs:273-274` | boss zone 用 `MobName`，而 `MobName` 明确恒英文 | 英文（正确） |

**结论**：「`Name` 恒英文」只对 **extract / transit / loot container / stationary weapon / boss zone / 有名字的 switch 与 BTR** 成立；**不成立**的是 **locks / spawn / hazard / 无名字的 switch / 无名字的 BTR** 这些回落标签。

- **改法**：把 `Data/MapPoi.cs:74-75` 的注释收窄为「主路径 POI 的 `Name` 恒英文；部分标签类 POI（lock/spawn/hazard/回落 switch/BTR）的 `Name` 是构建时 `Loc` 结果」。若希望所有 `Name` 都恒英文，则把这些回落也改成 `LocEnglish` + 相应 `NameZh`（属较大改动，另行决定）。
- **预期**：注释与真实行为一致，避免后续据此写出错误假设的代码。
- **验证**：加一条「中文语言下构建 spawn / hazard / lock，断言其 `Name` 语言」的测试，把事实钉住（若选择保留现状，就断言它们确实是中文，作为**已知行为**）。

### 备注（核实中新发现）· `GameMap.DisplayName` 是启动时按语言烘焙的隐式依赖

| 位置 | 说明 |
|---|---|
| `Maps/GameMap.cs:51` | `DisplayName = MapNames.Display(group.NormalizedName)`，**构造时**按当前语言定值 |
| `Maps/MapCatalog.cs:121` | 目录按 `DisplayName` 排序（`string.Compare(..., OrdinalIgnoreCase)`） |

用途纯显示（地图选择器、状态栏、About），且语言改动要求重启，所以**当前不是 bug**。仅作记录：任何引入「语言热切换」或「跨语言复用 `GameMap`」的改动都要重估这里。

## 明确不做 / 需先确认

- **A1/A2 的范围以「补双参数、不改行为」为限**；是否顺势让所有 POI 的 `Name` 恒英文属更大改动，用户另行决定。
- **不动 `PoiBuilder.cs` 里 `Details` 的 `Loc`/`LocEnglish` 混用重构**（用户明确说这是更大的数据不变量重构）。
- **不动 `ExtractNotesTests.cs` 的测试卫生问题**（用户已叫停）。
- **未验证项**：中文 OCR 的实际识别质量、`NameMatcher` 在 CJK 上的 floor/margin 阈值，都需要真实中文客户端截图实测（本文只有代码级推断）。

## 建议的下一步（按顺序，每步附可执行验证）

1. **A1 + A4**（低风险，为 B1 铺路）：把 4 处单参数 `AddLine` 与 2 处直接 `details.Add` 补成中英双列表。
   - 验证：`dotnet test`，新增「中文语言下 `Details` 为英文」的 `PoiTests` 断言；`dotnet build` 保持 0 warning 0 error。
2. **B1 决策**：先回答上文 4 个待决策点，产出一页决策记录再动代码。
   - 验证：拿一张真实中文客户端截图跑 `--read-exits`，记录当前 `resolution` 段 AVAILABLE 数的 0 命中基线。
3. **B1 实现**：按决策实现中文候选匹配。
   - 验证：同一条 `--read-exits` 命令，中文截图的 AVAILABLE 数应与英文截图同量级；补中英双语匹配单测。
4. **A2**：给 transit 补 `DetailsZh`。
   - 验证：transit 详情行的语言随界面切换。
5. **A3**：订正 `Data/MapPoi.cs:74-75` 注释（若同时决定让 `Name` 恒英文，则一并改回落标签）。
   - 验证：构建期语言测试。

## 相关文档

- 渲染性能专题：`docs/render-performance-backlog.md`
- 安全 + WebSync 评估：`docs/eval-security-and-lan-sync.md`
- WebSync 实现规格：`docs/websync-implementation-spec.md`
