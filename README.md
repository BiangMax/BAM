# BAM · Biang Automation Manager

**One Hub, All Scripts.**

BAM（Biang 自动化管理器）是面向 Windows 11 x64 的独立桌面项目。它计划在同一界面发现和管理用户已安装的游戏自动化工具，并分别呈现软件进程、窗口和游戏任务状态。各工具继续使用自己的界面、配置、任务逻辑和更新机制。

> **早期开发 · Phase 2 与 2.1 已通过工程及视觉验收。** 当前桌面界面包括总览、脚本管理、运行日志、设置和系统托盘。正式版使用空态提供器。安装发现、真实图标、进程监控与控制将在 Phase 3 接入。

## 计划接入

| 工具 | 游戏 | 计划范围 |
| --- | --- | --- |
| [MaaEnd](https://github.com/MaaEnd/MaaEnd) | 明日方舟：终末地 | 通用软件管理。后续验证 MXU 的实例选择、启动参数和只读任务观测。 |
| [MAA](https://github.com/MaaAssistantArknights/MaaAssistantArknights) | 明日方舟 | 通用软件管理。后续验证由用户启用的回环远控及任务事件。 |
| [BBchannel](https://github.com/xiaoruanmm/FGOautomation-BBchannel-) | Fate/Grand Order | 通过启动入口和工作进程提供通用管理。 |

## 技术栈

- C#、.NET 10 LTS、WPF、WPF UI、CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection 和 xUnit v3。

## 本地构建

需要 Windows 11 x64、Git 和 [`global.json`](global.json) 指定的稳定 .NET 10 x64 SDK。首次还原需要访问 NuGet 源。桌面烟测使用 PowerShell 7。

```powershell
dotnet restore BAM.slnx --locked-mode
dotnet build BAM.slnx -c Release --no-restore
dotnet test BAM.slnx -c Release --no-build --no-restore
```

运行桌面程序可执行 `src/BAM.Desktop/bin/Release/net10.0-windows/win-x64/BAM.exe`。正常入口显示当前可用的数据。要查看三个软件的合成示例，请使用独立演示构建。开发构建、演示启动、截图和发布命令见 [Phase 2 验收报告](docs/PHASE2_REPORT.md)。

## 项目结构

| 路径 | 内容 |
| --- | --- |
| [`src/BAM.Desktop`](src/BAM.Desktop) | WPF 入口、四个页面、设计系统、托盘与单实例机制 |
| [`src/BAM.Core`](src/BAM.Core) | 与桌面 UI 解耦的实例身份、观测状态和能力契约 |
| [`tests/BAM.Tests`](tests/BAM.Tests) | 核心行为的自动化测试 |
| [`tests/BAM.Desktop.Tests`](tests/BAM.Desktop.Tests) | 桌面资源历史和设置存储的自动化测试 |
| [`poc/`](poc) | 独立技术验证与夹具 |
| [`scripts/`](scripts) | 桌面验证脚本 |
| [`docs/`](docs) | 调查、验收、架构与界面规范 |

## 开发路线

1. **已完成 · Phase 0 至 2.1。** 完成可行性调查、工程基础、桌面壳和视觉精修。当前界面作为后续设计基线。
2. **下一阶段 · Phase 3 通用进程管理。** 计划接入安装发现、本地图标、只读采样和经身份复核的实例控制。BBchannel 保持轻量适配。
3. **Phase 4 及以后 · 专属适配与调度。** 按工具版本验证游戏任务深度控制，再评估队列、排程和稳定性。

接手开发可从 [AGENTS.md](AGENTS.md)、[Phase 2 验收报告](docs/PHASE2_REPORT.md)、[架构规范](docs/ARCHITECTURE.md)、[UI 设计规范](docs/UI_DESIGN.md)和[完整路线图](docs/ROADMAP.md)开始。

## 第三方项目与许可

BAM 是独立项目。上述工具及游戏的名称归各自权利人所有。用户通过各原项目获取和更新工具。本仓库仅保留 BAM 工程及其验证资料。BAM 的许可证由项目所有者决定。
