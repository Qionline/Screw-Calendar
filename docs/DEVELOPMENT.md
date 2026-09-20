# Screw Calendar 开发文档

螺丝日历是一个基于 .NET 8 和 WPF 的 Windows 桌面日历。它支持经典、极简、紧凑三种外观，提供本机 Markdown 待办、透明度、主题色、中英文切换、置顶、锁定、开机启动、系统托盘和 JSON 导入导出。

## 快速了解

- 技术栈：C#、.NET 8、WPF，少量 Windows Forms 用于系统托盘、屏幕信息和文件选择窗口。
- 目标平台：Windows x64。
- 发布形式：自包含单文件免安装程序 `ScrewCalendar.exe`。
- 正式用户数据：`%LocalAppData%\ScrewCalendar\data`。
- 错误日志：`%LocalAppData%\ScrewCalendar\logs\app.log`。
- 当前语言：简体中文、English。
- 节假日：内置 2025—2026 年数据兜底，并从 `holiday-cn` 静默更新当前查看年份及下一年；节气范围为 2025—2026 年。
- 可在设置中开启底部 Markdown 待办；每个日期一份文档，常驻区另有一份文档。

关闭主窗口时程序会隐藏到系统托盘，不会立即结束进程。需要完全退出时，请右键托盘图标并选择“退出”。

## 项目结构

```text
ScrewCalendar/
├─ App.xaml / App.xaml.cs       应用入口、全局资源、单实例与生命周期
├─ MainWindow.cs                主窗口骨架、日历渲染、主题与共享视觉逻辑
├─ Models/                      日历状态与窗口几何模型
├─ Calendar/                    日期、Markdown 文档处理与固定布局常量
├─ Controls/                    Markdown 可视块编辑器等业务控件
├─ Services/                    日历数据、日志和节假日服务
├─ Views/                       设置、Markdown 编辑、弹窗、数据和窗口行为的 partial 文件
├─ Resources/Data/              节假日、调休与节气数据
├─ Shared/DesktopWidgets.Toolkit/
│  ├─ Windows/                  窗口层级、托盘、启动、单实例、位置和弹窗行为
│  ├─ Themes/                   可复用控件模板与默认主题色板
│  └─ README.md                 公共库接入、API 示例与回归清单
├─ locales/                     zh-CN.json 与 en-US.json
├─ tests/ScrewCalendar.Tests/   无第三方测试框架的自动化回归测试
├─ artifacts/publish/           最终单文件发布目录
├─ docs/                        用户指南与开发文档
│  └─ RELEASING.md              版本、标签和 GitHub Release 流程
├─ .github/                     CI、依赖更新、Issue 与 PR 模板
├─ UI_STYLE_CONTRACT.md         已确认 UI 尺寸和回归约束
├─ CONTRIBUTING.md              贡献流程与合并要求
├─ THIRD_PARTY_NOTICES.md       第三方依赖、数据来源与许可证
├─ NuGet.Config                 NuGet 官方源配置
└─ ScrewCalendar.csproj         主项目配置
```

`bin/`、`obj/` 和测试项目中的同名目录都是可重新生成的编译中间产物。不要把临时构建目录放在项目根目录，也不要创建 `build-fixed` 之类的替代输出目录。

## 数据规则

程序不把用户 Markdown 待办放在发布目录或 `bin` 目录。正式文件为：

```text
%LocalAppData%\ScrewCalendar\data\calendar.json
%LocalAppData%\ScrewCalendar\data\calendar.json.bak
%LocalAppData%\ScrewCalendar\data\holiday-cache\{year}.json
```

- `calendar.json` 是版本 3 状态，包括常驻 Markdown、按日期保存的 Markdown、主题、语言、行数和窗口设置。
- 当前项目尚未上线，不保留版本 2 日程兼容和迁移代码；旧版本 JSON 会被校验拒绝。
- 通过 `Ctrl+V` 粘贴或拖入的图片会编码为 PNG，以内容 SHA-256 哈希命名并复制到 `%LocalAppData%\ScrewCalendar\data\assets`；Markdown 只使用新的 `assets/...` 相对路径，删除原图不会影响待办中的图片。
- `calendar.json.bak` 是最近一次有效主文件的备份。
- `holiday-cache` 保存通过格式与年份校验的 `holiday-cn` 原始年度 JSON。成功缓存后 24 小时内不重复下载，写入使用临时文件原子替换；下载失败时继续使用已有缓存或内置数据，并在下次启动时重试。
- 主文件损坏或校验失败时，程序会读取备份并修复主文件。
- 保存采用临时文件替换，且不会用已经损坏的主文件覆盖有效备份。
- 发布输出中不得附带真实用户数据。
- JSON 导出只包含 Markdown 和设置，不内嵌本地图片；跨电脑复制图片时还需同时复制数据目录下的 `assets` 文件夹。

导入时会校验版本、日期行数、透明度、枚举值、窗口数据、日期键、文档数量和 Markdown 长度，异常会记录到日志并拒绝覆盖当前数据。

## 待办列表使用规则

- 在设置中勾选“显示底部待办列表”后，待办区域显示在日历下方。
- 每个日期只保存一份 Markdown；常驻区另存一份 Markdown，文档中的任务列表表达多项待办。
- 常驻待办始终显示；底部日期待办固定显示今天的数据，不跟随上方日历的点击或翻页。
- 今天存在非空 Markdown 时显示常驻/今日双栏；今天为空时常驻区占满整行，并提供“编辑今日”入口。
- 原有新增加号、铅笔和逐条删除已取消；点击待办正文、空状态或“编辑今日”后直接在待办面板内编辑。
- 底部内联编辑器提供正文、H1—H3、列表、任务工具和最右侧图标保存按钮；点击文本域任意空白位置都可以继续编辑。
- 从日期格打开的编辑窗口由入口决定目标，不能在窗口内切换常驻或其他日期，避免覆盖另一份文档。
- 编辑器使用普通可视块模式；工具栏提供正文、H1—H3、列表和任务操作，不显示单独的图片按钮。
- 普通文本与标题不显示左侧 `H1`、`H2`、`H3` 标记，只有列表和任务块显示对应标记；编辑器以单一 WPF `RichTextBox`/`FlowDocument` 处理跨段落拖选、`Ctrl+A`、复制、剪切、删除和输入替换。
- 列表或任务段落按 Enter 新建同类型、同缩进的段落，任务新段落默认未勾选；复制、剪切和粘贴使用 Markdown 适配保持源语法。
- 底部渲染后的正文可以选择复制，`- [ ]` / `- [x]` 可直接点击并回写 Markdown；日期格只显示最多三行紧凑摘要。
- 设置与 Markdown 编辑窗口采用非模态交互，打开期间主日历和待办仍可操作；设置按钮再次点击会关闭设置窗口。
- 编辑今日 Markdown 时，底部今日待办实时显示草稿；关闭不保存会恢复原内容。
- 图片可通过 `Ctrl+V` 粘贴或从资源管理器拖入；应用会先复制归档到本机 assets 目录，显示时按容器宽度自适应。
- 编辑器会将图片插入当前文本块之后并自动滚动到图片；截图位图与复制的图片文件使用同一存档流程。
- 日历右下角拖动柄只缩放日历；待办面板底边拖动柄只改变待办高度。
- 设置页提供日历整体大小和待办高度滑块，并保留对应拖动操作提示。
- 待办面板按日历当前实际显示宽度居中，不直接使用窗口宽度；行数增多导致日历缩小时，待办会同步收窄。
- 拖动日历右下角进行等比缩放时，待办宽度实时同步，待办高度保持不变。
- 紧凑模式下，待办左右边缘与日期内容区域保持固定比例；经典和极简模式与完整日历卡片对齐。

## 窗口层级与置顶

- 三种视觉模式使用相同的窗口层级规则，视觉风格不再影响窗口层级。
- 开启“置顶”后，日历保持在普通窗口之上；全屏应用或游戏覆盖整个显示器时临时让出顶层，退出全屏后自动恢复。
- 关闭“置顶”后，日历固定处于桌面层之上、普通应用窗口之下；点击未遮挡区域也不会把日历抬到其他窗口前方。
- Windows 10 使用任务栏最右侧“显示桌面”或 Win+D 时，三种模式都保持显示，不执行最小化后的 `Hide()`。
- 托盘菜单中的“显示/隐藏”仍是用户主动控制显示状态的入口，不受桌面层级保护影响。
- 窗口层级等通用能力来自 `Shared/DesktopWidgets.Toolkit`，复用方式见其独立 README。

## UI 开发规范

任何涉及日历网格、工具栏、星期栏、输入框、滚动区域或弹窗的修改，都必须在动手前和完成后完整阅读 [UI_STYLE_CONTRACT.md](../UI_STYLE_CONTRACT.md)。不能只以“编译成功”作为 UI 修改完成标准。

核心固定值统一定义在 `Calendar/CalendarLayout.cs`：

| 项目 | 固定值 |
| --- | ---: |
| 日历卡片宽度 | 886px |
| 日期网格左右内容边距 | 16px |
| 日期网格与日期 holder 宽度 | 854px |
| 经典/极简工具栏与星期栏外层宽度 | 878px |
| 紧凑工具栏与星期栏外层宽度 | 866px |
| 日期方块宽度 | 118px |
| 日期方块四周 Margin | 2px |
| 日期列宽 | 122px |
| 日期方块高度 | 105px |
| 月份标签高度 | 22px |
| 待办面板默认高度 | 220px |
| 待办面板最小/最大高度 | 140px / 520px |
| 窗口最小宽度/高度 | 300px / 280px |
| 通用弹窗最外层内边距 | 左右 16px、上下 14px |
| 设置窗口内边距 | 左 20px、右 16px、上下 14px |
| 设置窗口滚动条左间距 | 12px |
| 日历可见行数 | 最小 5 行、最大 10 行 |

开发时遵守以下规则：

1. 固定布局值直接使用常量，不在运行时做百分比宽度计算。
2. 修改日期方块时，同时检查顶部工具栏、星期栏、月份标签和左右边距。
3. 首行月份标签只能增加行高，不能挤压日期方块。
4. 跨项目通用控件模板放入 `Shared/DesktopWidgets.Toolkit/Themes/Controls.xaml`；仅属于日历业务的样式才留在主项目。
5. 主题相关颜色使用动态资源或既有主题方法，新增 UI 必须兼容浅色、深色和用户主题色。
6. 不要把 `DropShadowEffect` 等位图效果挂在包含文字的根容器上，以免 Win11 模式文字模糊。
7. WPF 不是浏览器 CSS：涉及 `ControlTemplate`、`Track`、焦点状态和命中测试时，必须同时验证模板结构和运行时交互。
8. 不得为完成局部需求恢复此前已经确认的间距、尺寸或窗口结构。
9. 日历 Viewbox 与底部待办面板必须保持独立：右下角缩放日历，底边只调整待办高度。
10. 透明度必须同时作用于三种模式的日期表面、月份标签、Markdown 摘要和待办面板背景。
11. 弹窗必须按正常前台窗口显示；主窗口处于桌面底层时，不能把所属弹窗一起压回桌面层。

## 代码开发规范

- 日历数据模型只放在 `Models/`，日期、Markdown 文档处理与布局计算放在 `Calendar/`，日历专属持久化与资源服务放在 `Services/`。
- 可跨桌面组件复用的 Windows 能力和 UI 模板放入 `Shared/DesktopWidgets.Toolkit/`，不能重新复制回主窗口或业务服务。
- `MainWindow.cs` 保留窗口骨架和日历主体；独立窗口或职责放入 `Views/MainWindow.*.cs`。
- 多处使用的控件、状态修改或数据处理必须提取复用，避免复制事件处理代码。
- 用户可见文本必须进入 `locales/zh-CN.json` 和 `locales/en-US.json`，两个文件的键必须保持一致。
- `Resources/Data/calendar-data.json` 保存内置节假日兜底和静态节气；远端节假日更新由 `CalendarMetadataService` 与 `HolidayUpdateService` 负责，不要重新硬编码进 C#。
- 文件、注册表、资源加载和序列化失败必须记录日志；不能无说明地吞掉异常。
- 不直接删除或覆盖用户数据。迁移、清理和发布前先确认准确路径。
- 不把生成文件放进源码目录的临时文件夹；正式发布统一进入 `artifacts/publish/`。
- 修改范围外的用户文件和设置保持不变。

## 首次配置开发环境

安装：

1. Windows 10 或 Windows 11 x64。
2. 安装不低于 `global.json` 基线版本的 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。仅安装 Runtime 无法编译；项目允许在同一 .NET 8 系列内滚动到更高的 feature band。
3. 可选：Visual Studio 2022，并启用“.NET 桌面开发”工作负载。

进入项目根目录后恢复依赖：

```powershell
dotnet restore .\ScrewCalendar.sln --configfile .\NuGet.Config --locked-mode
```

如果新电脑无法访问 NuGet，请先确认能够访问 `https://api.nuget.org/v3/index.json`，并检查代理、防火墙及用户目录下的 NuGet 配置权限。

## 日常开发流程

建议每次需求按以下顺序处理：

1. 阅读本开发文档；如果涉及 UI，再完整阅读根目录的 `UI_STYLE_CONTRACT.md`。
2. 确认需求影响的模型、服务、视图、资源和双语文本，避免只修改局部组件。
3. 修改源码，不直接编辑 `bin`、`obj` 或 `artifacts/publish` 中的生成文件。
4. 运行自动化测试。
5. 执行 Release 构建。
6. UI 需求按契约逐项回归三种风格、明暗主题、主题色和弹窗状态。
7. 发布到 `artifacts/publish`。
8. 发布前检查输出中没有 `data/calendar.json` 等真实用户数据。

## 测试

运行全部自动化测试：

```powershell
dotnet run --project .\tests\ScrewCalendar.Tests\ScrewCalendar.Tests.csproj -c Release --no-restore
```

当前测试覆盖：

- 星期列和跨月日期行计算。
- 日历固定布局契约。
- 日历缩放与待办高度的独立尺寸计算。
- 高行数受屏幕高度限制时，待办宽度同步与隐藏待办不放大日历的尺寸回归。
- 主数据保存与读取。
- 主文件损坏后的备份恢复。
- 版本、日期键、Markdown 长度和界面设置的导入校验。
- 版本 3 Markdown 存档校验、备份恢复和按日期持久化。
- Markdown 标题、列表、任务解析与任务状态源码回写。
- 图片内容哈希命名、重复图片复用，以及删除原始文件后的存档加载。
- 日历行数上限以及底部面板只读取今天的日期 Markdown。
- 中英文翻译键完整性。
- 节假日和节气资源加载。
- 远端节假日 JSON 校验、合并和缓存写入。
- 应用版本、SDK 基线和发布包许可证文件契约。

新增数据规则、布局常量、本地化键或年份数据时，必须同步增加或更新测试。

## 编译与发布

Release 编译：

```powershell
dotnet build .\ScrewCalendar.csproj -c Release --no-restore
```

编译输出主要位于：

```text
bin\Release\net8.0-windows\win-x64\
```

它用于开发验证，可能包含 DLL、PDB、依赖文件和运行时资源，不等同于最终交付目录。

生成自包含单文件发布：

```powershell
dotnet publish .\ScrewCalendar.csproj -c Release --no-restore -o .\artifacts\publish
```

最终可交付程序：

```text
artifacts\publish\ScrewCalendar.exe
```

`artifacts/publish` 是整理后的免安装发布目录，其中包含 PDB、本地化 JSON、日历数据资源、项目许可证、README 和第三方声明。PDB 可在正式分发时不提供，但开发留档建议保留。正式版本使用 `scripts/Release.ps1` 执行与 CI 一致的检查和标签推送；完整流程见 [RELEASING.md](RELEASING.md)。

## 发布前人工检查

至少验证以下项目：

- 程序启动、单实例和托盘退出。
- 点击关闭按钮后进入托盘，而不是误以为进程残留。
- 月份前后翻页和“回到今天”。
- 日期 Markdown 与常驻 Markdown 的新建、可视块编辑、清空和保存。
- 可视块编辑器的标题标记隐藏、列表/任务标记保留，以及单一 RichTextBox 的跨段落选择和替换操作。
- 列表/任务按 Enter 的类型继承，以及 Markdown 复制、剪切和粘贴的 `- [ ]` / `- [x]` 语法保留。
- 编辑器中通过 `Ctrl+V` 和文件拖入导入图片，且不再显示图片工具栏按钮。
- 设置窗口和 Markdown 编辑窗口的位置、拖动、关闭按钮与滚动条。
- 关闭置顶且主窗口被部分遮挡时，从日历打开的设置和 Markdown 编辑窗口首次显示即位于普通窗口前方。
- 经典、极简、紧凑三种风格。
- 三种风格在“显示桌面”后都保持可见；关闭置顶时点击日历不会覆盖普通窗口。
- 开启置顶时普通窗口不能覆盖日历，全屏应用可以覆盖且退出全屏后恢复置顶。
- 浅色、深色、全部主题色和透明度。
- 三种模式下日期项、月份标签、Markdown 摘要和待办面板的透明度同步变化。
- 锁定、置顶和开机启动。
- 简体中文和 English 切换。
- JSON 导入导出。
- 输入框默认、聚焦、禁用和多行状态。
- 待办面板开关、常驻/今日布局、Markdown 复选框回写、图片粘贴和两种独立缩放。
- 关闭置顶后，底部 Markdown 编辑器仍能获得键盘焦点并保存输入；保存后主窗口恢复桌面层行为。
- 主文件损坏后的备份恢复。
- 发布目录不存在用户 `data` 文件夹。
- 项目中没有 `build-fixed` 或其他临时发布目录。

## 换电脑继续开发

1. 复制整个项目源码目录，但可以不复制 `bin/`、`obj/`、测试输出和 `artifacts/publish/`。
2. 如需带走 Markdown 待办，在旧电脑设置中导出 JSON；如使用了图片，还要复制正式数据目录中的 `assets` 文件夹。
3. 在新电脑安装不低于 `global.json` 基线版本的 .NET 8 SDK。
4. 进入项目根目录，执行带 `--locked-mode` 的解决方案还原命令。
5. 运行自动化测试，确认环境正常。
6. 执行 Release 构建和发布命令。
7. 启动新程序，在设置中导入之前导出的 JSON。
8. 开始 UI 工作前重新阅读根目录的 `UI_STYLE_CONTRACT.md`。

## 当前构建产物

- 开发编译输出：`bin/Release/net8.0-windows/win-x64/`
- 最终免安装发布输出：`artifacts/publish/`
- 正式用户数据：`%LocalAppData%\ScrewCalendar\data/`
- 日志：`%LocalAppData%\ScrewCalendar\logs/app.log`

发布目录可以重新生成；用户数据目录不能随意清理。
