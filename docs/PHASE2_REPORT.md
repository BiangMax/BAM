# Phase 2 桌面界面验收报告

**2026-11-25 · 工程和视觉验收通过。** 所有者已审核 Phase 2 工程报告与九张实际界面截图，并通过 Phase 2.1 视觉精修验收。当前桌面界面作为后续设计基线，检查点为 `phase-2-complete`。上一稳定检查点为 `phase-1-complete`。

## 已交付

| 界面 | 实现 |
|---|---|
| 主窗口 | 默认 1200×800 DIP，最小目标 800×540 DIP。48 DIP 的 WPF UI 标题栏带 BAM 原创标志和系统窗口按钮。导航由 188 DIP 折叠为 64 DIP，图标保留中文工具提示和无障碍名称。高缩放下初次启动按显示器工作区限制尺寸。状态栏有已知错误时可跳转对应日志。 |
| 总览 | 软件进程计数、游戏任务检测覆盖、CPU 和工作集。最近 120 秒图表切换 CPU 或 MiB，空值和采样断档断线。脚本卡和有界最近事件跟随可用宽度排布。 |
| 脚本管理 | 管理清单和选中软件详情，显示路径、工作目录、启动参数、版本和能力。发现、定位与进程控制入口呈禁用状态，并提供原因。 |
| 运行日志 | 来源和级别筛选、搜索、自动滚动、回收式虚拟列表、事件详情及复制。列表最多保留 2,000 条。演示来源可以触发可辨识的读取错误和筛选空状态。 |
| 设置 | 深色、浅色、跟随 Windows 应用模式、减少动画、关闭至托盘、关于和 GitHub 链接。尚待接入的采样及脚本配置以状态文字呈现。关于与状态栏取实际程序集信息版本 `0.1.0-alpha.1`。 |
| 托盘 | NotifyIcon 提供双击恢复、打开总览、当前监控状态和退出。关闭至托盘默认为关，首次启用后收起时请求 Windows 显示提示。托盘和最小化复用单实例激活路径。 |

`src/BAM.Desktop/Themes` 集中保存深浅色和高对比度调色板、字体、边距、交互时长及控件模板。应用先加载 WPF UI 字典，再用 BAM 的语义 Brush 和 `DynamicResource` 提供界面配色。原生托盘菜单打开时读取当前主题的颜色，高对比度使用系统菜单色。Segoe UI Variable 优先回退至 Microsoft YaHei UI。窗体材质仅在支持且启用透明效果时请求 Mica，工作区使用实色以保持对比度。

`Controls` 包含流式布局、指标、状态、空状态、菜单按钮和 WPF Drawing 图表。CPU 轴固定为 0–100%，工作集轴从 0 开始并以 MiB 显示。图表通过采样点更新，鼠标及方向键可查看采样详情。资源历史保留最近 120 秒、最多 121 点。总览卸载、收起或窗口最小化后停止定时器，恢复时读取可用历史并取新点。页面及按下反馈使用短时淡入，高对比、系统禁用动画和 BAM 减少动画偏好触发即时反馈。

`Views` 和 `ViewModels` 承担界面、导航、筛选和显示。软件进程与游戏任务使用现有 Core 枚举独立表达。BAM 通过本地原创资产显示程序品牌。MaaEnd、MAA、BBchannel 目前使用同一套 Fluent 占位图标。正常设置存放在 `%LocalAppData%/BAM/settings.json`，采用串行原子写入和上一版本备份，演示偏好仅保留于内存。

### 新增依赖和工程文件

| 组件 | 版本及范围 |
|---|---|
| WPF-UI | 4.3.0。Desktop 使用其 `FluentWindow`、`TitleBar`、主题、图标和基础控件资源。 |
| WPF-UI.Abstractions | 4.3.0，由 WPF-UI 传递引入。 |
| CommunityToolkit.Mvvm | 8.4.2。Desktop 的属性通知和命令经 .NET SDK 10.0.404 编译验证。 |
| WinForms NotifyIcon、System.Drawing | .NET 10 Windows Desktop 共享框架自带。 |
| BAM.Desktop.Tests | 新增的 Windows 桌面测试项目，复用现有集中管理的 xUnit 与 VSTest 版本。 |

集中版本文件和新增或变动的锁文件已经同步。Core 源码和 Phase 1 核心测试保留原有内容。Adapters 和 Infrastructure 随真实接入建立。

## 演示隔离与启动

正常发布的 `artifacts/publish/win-x64/BAM.exe` 使用空态提供器，软件计数明确区分未知已启动数和已知管理数，资源未知显示为破折号。演示构建必须设置 `EnableBamDemo=true`，运行时显式附带 `--demo`。演示编译输出独立于正式构建，合成提供器和截图入口仅存在于演示程序集。启用后标题栏持续显示「演示模式」。图表、状态、版本和日志均为合成样例。

```powershell
dotnet restore BAM.slnx --locked-mode
dotnet build BAM.slnx -c Release --no-restore
dotnet test BAM.slnx -c Release --no-build --no-restore
dotnet publish src/BAM.Desktop/BAM.Desktop.csproj -c Release -r win-x64 --self-contained false --no-build --no-restore -o artifacts/publish/win-x64
dotnet publish src/BAM.Desktop/BAM.Desktop.csproj -c Release -r win-x64 --self-contained false --no-restore -p:EnableBamDemo=true -o artifacts/publish/demo-win-x64
& .\artifacts\publish\win-x64\BAM.exe
# 退出前一个 BAM 实例后启动演示
& .\artifacts\publish\demo-win-x64\BAM.exe --demo
```

同一用户的两个入口共享单实例机制。从正式版切换到演示前，请先退出现有实例。

## 实际截图与视觉复核

本地截图位于 Git 忽略的 `artifacts/screenshots/`。通过启动实际 WPF 进程并运行 PowerShell 7 烟测生成，烟测使用匹配的 Windows Desktop UI Automation 组件。独立演示构建中的 WPF 渲染器采集客户区视觉树，含自绘标题栏。DWM 材质、桌面、其他应用、原生弹出菜单和窗口阴影位于采集范围之外。

| 截图 | 场景 |
|---|---|
| `01-dashboard-dark.png` | 深色总览，四项摘要、CPU 曲线及三列卡片 |
| `02-dashboard-light.png` | 浅色总览 |
| `03-dashboard-narrow.png` | 800×540 DIP，折叠导航和两列摘要 |
| `04-scripts.png` | 脚本管理和软件详情 |
| `05-logs.png` | 运行日志及详情 |
| `06-settings.png` | 设置和关于 |
| `07-dashboard-memory.png` | 工作集图表 |
| `08-empty.png` | 演示构建未传 `--demo` 时的空态提供器。正式发布版用独立烟测确认相同的界面空态。 |
| `09-narrow-cards.png` | 800×540 DIP 向下滚动后的单列卡片 |

根据首轮截图压缩了图表和设置分组，使首屏可见三张脚本卡的主操作与设置页关于区。调整了标题栏图标尺寸、日志正文与选中行的对比、禁用按钮边框、窄屏图表高度，以及淡色主题的分层和图表留白。复核了更新后的实际运行截图。

```powershell
pwsh -File scripts/Test-Desktop.ps1 -Output Build
pwsh -File scripts/Test-Desktop.ps1 -Output Publish
pwsh -File scripts/Test-Desktop.ps1 -Output Demo -Capture
pwsh -File scripts/Test-Desktop.ps1 -Output DemoEmpty -Capture
```

## 验证结果

| 验收项 | 结果 |
|---|---|
| 环境及还原 | Windows 11 x64、SDK 10.0.404、Windows Desktop 10.0.15。四项目锁定还原通过。 |
| Release 构建和单测 | 0 警告、0 错误。21 项 Phase 1 核心测试及 6 项桌面测试通过，合计 27 项。新增测试覆盖历史环绕、乱序和缺样、设置写入顺序与备份、未来 schema、演示偏好隔离。结果见 `artifacts/test-results/phase2/`。 |
| 桌面烟测 | 正式构建与目录发布均实际启动、最小化、由次实例恢复同一 HWND、正常退出并重新启动。正式入口附加 `--demo` 仍显示空态。 |
| 页面和主题 | UI Automation 完成导航、CPU/内存切换、脚本选择、状态栏错误跳转、日志来源/级别/搜索及空态与错误提示、自动滚动开关、深色/浅色/跟随系统选择、减少动画开关。补测了脚本卡菜单打开详情和筛选日志。深浅截图均已复核。 |
| 响应式与 DPI | 实测 144 DPI（150%）下 1200×800、920×730 和 800×540 DIP，检查标题、折叠导航、三列/两列/单列卡片和窄屏两列摘要。窗口最大化和恢复通过，原生命中测试对最大化按钮返回 `HTMAXBUTTON`。 |
| 托盘与单实例 | 演示设置开启关闭至托盘后，关闭隐藏主窗口，重复启动恢复同一 HWND；关闭该偏好后正常退出。恢复最小化保留 Phase 1 回归。测试进程已清理。 |
| win-x64 发布 | WPF 共享框架依赖的目录式发布。PE 为 AMD64，应用清单为 `asInvoker`、`uiAccess=false` 和 `PerMonitorV2`。 |
| 发布隐私 | 两个发布目录各含 10 个文件。检查 UTF-8、UTF-16LE 和 JSON 转义后的工作区、用户和本地第三方安装路径等标记。发现 Core 的 PDB 编译选项元数据保留本机路径后，将调试符号移出发布文件清单。复检两个目录均为 0 命中。 |

## 待实测、范围和接入

- **已实现、待实测。** 100%、125%、200% 和混合 DPI 显示器。高对比度和 Windows 关闭透明效果时的原生材质行为。Windows 主题在 BAM 运行期间随系统设置切换的广播路径。Snap 悬浮面板、系统菜单、完整键盘流程和读屏体验仍需交互验收。当前环境无法通过 Shell 获取托盘图标位置，所以任务栏上的实际双击和菜单路径仍待验收。测试使用当前系统设置。
- **阶段差异。** 120 秒绘图采用带轻填充的线段和断档，通过单独的单位切换呈现。最近事件在默认宽屏卡片下方按需滚动。日志以最新优先排列，自动跟随回到首条，浏览较旧的行时暂停。安装/能力错误场景、应用真实图标、日志脱敏导出、持续日志流和长期性能预算还缺少实际数据源或对应验收设备。
- **后续阶段。** 安装与快捷方式发现、真实图标与版本读取、资源采样、软件及窗口控制、任务证据、采集和导出、远控、自动调度及发布安装机制按路线图推进。
- **Phase 3 接口。** 在 App 组合根用真实快照实现替换 `IDesktopData`。来自 Core 的进程与任务快照继续独立。资源提供器返回已确认主进程的时间与数值，缺样保留空值。真实发现、图标提取与日志 I/O 置于 Infrastructure，第三方能力归 Adapters。实时变化接入时应使用安装 ID 和不可变快照更新页面，而非以显示名绑定实例。

## 最终工程复验与代码审查

- 对照 `phase-1-complete` 审查累计变更。Core 源码、既有 21 项核心测试及单实例同步实现保持原有行为。审查记录为 P0 0 项、P1 0 项、P2 2 项、P3 1 项。[后续事项](ROADMAP.md#代码审查后续项)
- 四项目重新锁定还原。首次在受限环境中连接 NuGet 遇 TLS 凭据错误，使用获准的网络环境按原锁定配置重试后通过。Release 构建 0 警告、0 错误，27 项测试全部通过。
- Build、Publish、Demo、DemoEmpty 四种烟测均通过。演示版在 144 DPI（150%）下复测页面导航、日志筛选、深浅及跟随系统选项、缩放、最大化还原、收起托盘及次实例恢复同一 HWND。正式入口附加 `--demo` 后仍为空态。复测使用既有视觉证据，验证进程终检为 0。
- 两份 win-x64 框架依赖发布各 10 个文件。AMD64、`asInvoker`、`uiAccess=false` 和 `PerMonitorV2` 核验通过。仅演示程序集包含合成来源及截图入口。UTF-8、UTF-16LE 与转义路径检查均为 0 命中。
- 下一阶段为通用进程管理。真实第三方安装发现、图标提取、进程监控和控制仍待接入。BBchannel 采用轻量适配，游戏任务深度控制留到 Phase 4。其他 DPI、高对比度、原生交互和长期性能验证见[路线图](ROADMAP.md#phase-3-接手与待验证事项)。

## Phase 2.1 视觉精修

**2026-11-25 · 视觉验收通过。** 精修集中在桌面布局、语义样式和截图烟测。

| 项目 | 前后对照 |
|---|---|
| 默认总览 | 原首屏在完整三张脚本卡之后结束。调整标题上方留白、摘要内边距、图表高度和区块间距后，1200×800 DIP 的深浅总览均可见完整三卡及前两条最近事件。CPU 与内存仍各自使用单一纵轴，断档、时间刻度和详情仍可读。 |
| 窄屏总览 | 原首屏在资源图表中结束。800×540 DIP 的两列摘要下方现在先显示管理入口与首张完整脚本卡，之后按顺序滚动查看其余卡片和图表。单列卡将资源指标和按内容取宽的操作组放在同一行。 |
| 字体和脚本卡 | 标题与主指标使用集中管理的 Medium 字重，导航和按钮以 Regular 为主。脚本卡将软件进程和游戏任务分别并排呈现，版本在身份栏内对齐，三项资源指标保持同一基线。禁用操作继续显示原因。 |
| 脚本管理 | 左侧清单与右侧详情按内容取高，版本集中在身份栏，分隔线组织安装信息、管理能力和操作。下方留作页面空间。 |
| 控件 | 下拉框左侧小点来自内部 ToggleButton 继承 WPF UI 左对齐后收缩的边框。修正后，日志页和设置页的选框均有完整轮廓及点击区。弱化静态卡片边框，细化资源分段选择器及滚动条默认、悬停与拖动状态。 |

### 实际截图

Phase 2 的九张基线截图位于 `artifacts/screenshots/`，其 SHA-256 摘要与本轮开始时逐一核对一致。Phase 2.1 的九张截图单独保存在 Git 忽略的 `artifacts/screenshots/phase-2.1/`。两组均为实际 BAM 窗口的 WPF 客户区截图。

| 场景 | Phase 2 | Phase 2.1 |
|---|---|---|
| 深色总览 | `01-dashboard-dark.png` | `phase-2.1/01-dashboard-dark.png` |
| 浅色总览 | `02-dashboard-light.png` | `phase-2.1/02-dashboard-light.png` |
| 窄屏总览 | `03-dashboard-narrow.png` | `phase-2.1/03-dashboard-narrow.png` |
| 窄屏脚本卡 | `09-narrow-cards.png` | `phase-2.1/09-narrow-cards.png` |
| 脚本管理 | `04-scripts.png` | `phase-2.1/04-scripts.png` |
| 运行日志 | `05-logs.png` | `phase-2.1/05-logs.png` |
| 设置 | `06-settings.png` | `phase-2.1/06-settings.png` |
| 内存图表与数据空态 | `07-dashboard-memory.png` · `08-empty.png` | `phase-2.1/07-dashboard-memory.png` · `phase-2.1/08-empty.png` |

### 工程验证

- 四项目 NuGet 锁定还原通过。Release 构建 0 警告、0 错误。原 21 项 Core 测试及 6 项 Desktop 测试全部通过。结果见 `artifacts/test-results/phase-2.1/`。
- 正式 win-x64 框架依赖目录发布和演示发布运行通过。正式入口仍显示数据空态。烟测通过页面导航、CPU 与内存切换、脚本选择、日志筛选、三种主题选项、减少动画、窗口缩放、最小化及托盘恢复。新增 UI Automation 断言验证 800×540 DIP 下首张卡片及管理入口位于可视区，图表位于其后。
- 两种发布各含 10 个运行文件，AMD64 清单及演示编译隔离检查通过，本机路径和私有文件标记复检零命中。烟测结束后相关 BAM 验证进程为 0。
- 在本机 144 DPI（150%）下实测默认、两列过渡和最小目标尺寸。已逐页检查对齐、文字层级和可用空间。其他 DPI 组合、高对比度及原生任务栏和窗口合成细节需要对应环境复验。
- 精修后的界面作为后续开发基线。真实第三方图标及进程管理由 Phase 3 接入。

```powershell
pwsh -File scripts/Test-Desktop.ps1 -Output Publish
pwsh -File scripts/Test-Desktop.ps1 -Output Demo -Capture -CaptureDirectory artifacts/screenshots/phase-2.1
pwsh -File scripts/Test-Desktop.ps1 -Output DemoEmpty -Capture -CaptureDirectory artifacts/screenshots/phase-2.1
```
