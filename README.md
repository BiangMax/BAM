# BAM · Biang Automation Manager

**One Hub, All Scripts.**

BAM（Biang 自动化管理器）是面向 Windows 11 x64 的独立桌面项目。它计划在同一界面发现和管理用户已安装的游戏自动化工具，并分别呈现软件进程、窗口和游戏任务状态。各工具继续使用自己的界面、配置、任务逻辑和更新机制。

> **早期开发 · Phase 1 已审核通过。** 当前可运行的是工程验证窗口，已实现单实例启动与唤起，以及进程身份、状态和能力判定的核心契约。正式界面与第三方工具接入均在路线图中。

## 计划接入

| 工具 | 游戏 | 计划范围 |
| --- | --- | --- |
| [MaaEnd](https://github.com/MaaEnd/MaaEnd) | 明日方舟：终末地 | 通用软件管理。后续验证 MXU 的实例选择、启动参数和只读任务观测。 |
| [MAA](https://github.com/MaaAssistantArknights/MaaAssistantArknights) | 明日方舟 | 通用软件管理。后续验证由用户启用的回环远控及任务事件。 |
| [BBchannel](https://github.com/xiaoruanmm/FGOautomation-BBchannel-) | Fate/Grand Order | 通过启动入口和工作进程提供通用管理。 |

## 技术栈

- **当前** · C#、.NET 10 LTS、WPF、Microsoft.Extensions.DependencyInjection、xUnit v3。
- **Phase 2** · 计划引入 WPF UI 和 CommunityToolkit.Mvvm，用于桌面壳、主题、导航及 ViewModel。

## 本地构建

需要 Windows 11 x64、Git 和 [`global.json`](global.json) 指定的稳定 .NET 10 x64 SDK。首次还原需要访问 NuGet 源。桌面烟测使用 PowerShell 7。

```powershell
dotnet restore BAM.slnx --locked-mode
dotnet build BAM.slnx -c Release --no-restore
dotnet test BAM.slnx -c Release --no-build --no-restore
```

运行工程验证窗口可执行 `src/BAM.Desktop/bin/Release/net10.0-windows/win-x64/BAM.exe`。单实例与窗口烟测可从项目根运行 `pwsh -File scripts/Test-Desktop.ps1 -Output Build`。

## 项目结构

| 路径 | 内容 |
| --- | --- |
| [`src/BAM.Desktop`](src/BAM.Desktop) | WPF 入口、依赖组合、单实例机制与验证窗口 |
| [`src/BAM.Core`](src/BAM.Core) | 与桌面 UI 解耦的实例身份、观测状态和能力契约 |
| [`tests/BAM.Tests`](tests/BAM.Tests) | 核心行为的自动化测试 |
| [`poc/`](poc) | 独立技术验证与夹具 |
| [`scripts/`](scripts) | 桌面验证脚本 |
| [`docs/`](docs) | 调查、验收、架构与界面规范 |

## 开发路线

1. **Phase 2 · 桌面壳。** 建立主题、主窗口、导航和带明确标识的演示脚本卡，验证键盘、高 DPI 和窗口贴靠。
2. **Phase 3 · 通用管理。** 开发安装发现、只读采样、经过身份复核的实例控制，以及本地配置和日志。
3. **后续 · 专属适配与调度。** 按工具版本验证专属能力，再评估队列、排程和稳定性。

接手开发可从 [AGENTS.md](AGENTS.md)、[Phase 1 验收报告](docs/PHASE1_REPORT.md)、[架构规范](docs/ARCHITECTURE.md)、[UI 设计规范](docs/UI_DESIGN.md)和[完整路线图](docs/ROADMAP.md)开始。

## 第三方项目与许可

BAM 是独立项目。上述工具及游戏的名称归各自权利人所有。用户通过各原项目获取和更新工具。本仓库仅保留 BAM 工程及其验证资料。BAM 的许可证由项目所有者决定。
