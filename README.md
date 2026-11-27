# BAM · Biang Automation Manager

**One Hub, All Scripts.**

BAM（Biang Automation Manager，Biang 自动化管理器）是面向 Windows 11 x64 的游戏自动化脚本管理器。在一个界面配置、启动和查看 MaaEnd、MAA、BBchannel 及其模拟器。各工具沿用自己的界面、配置、游戏任务和更新机制。

> **0.1.0 发行说明。** 本版加入可随进程观测更新的批量启动结果。[发行说明与升级回退](docs/releases/v0.1.0.md) · [稳定版路线](docs/ROADMAP.md)

## 下载

Windows 11 x64 用户可从 GitHub Releases 下载已发布的便携压缩包，核对 SHA-256 并完整解压后运行 `BAM.exe`。包内包含 .NET 运行时，脚本和模拟器由各项目单独提供。[GitHub Releases](https://github.com/BiangMax/BAM/releases)

## 支持的工具

| 工具 | 游戏 | 当前日用范围 |
| --- | --- | --- |
| [MaaEnd](https://github.com/MaaEnd/MaaEnd) | 明日方舟：终末地 | 通用进程与窗口管理，支持配置模拟器入口及启动等待时间。 |
| [MAA](https://github.com/MaaAssistantArknights/MaaAssistantArknights) | 明日方舟 | 通用进程与窗口管理，按 MAA 原设置启动模拟器。 |
| [BBchannel](https://github.com/xiaoruanmm/FGOautomation-BBchannel-) | Fate/Grand Order | 使用原入口和已确认的工作进程。支持配对启动，正常关闭可能收起到自身托盘。 |

## 技术栈

- C#、.NET 10 LTS、WPF、WPF UI、CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection 和 xUnit v3。

## 本地构建

需要 Windows 11 x64、Git 和 [`global.json`](global.json) 指定的稳定 .NET 10 x64 SDK。首次还原需要访问 NuGet 源。桌面烟测使用 PowerShell 7。

```powershell
dotnet restore BAM.slnx --locked-mode
dotnet build BAM.slnx -c Release --no-restore
dotnet test BAM.slnx -c Release --no-build --no-restore
```

运行桌面程序可执行 `src/BAM.Desktop/bin/Release/net10.0-windows/win-x64/BAM.exe`。框架依赖构建需要对应的 .NET 10 x64 桌面和 ASP.NET Core 共享运行时。BAM 启动后检测已知软件，也可在「脚本管理」中手动选择入口、调整参数或重新定位。原软件可能根据自身设置在启动时运行任务。BAM 设置、安装记录和日志存放于 `%LocalAppData%\BAM`。管理员或权限未知时可以读取现有配置，设置、安装变更和新增日志仅在会话内生效。[构建和夹具验证](docs/PHASE3_REPORT.md)

## 项目结构

| 路径 | 内容 |
| --- | --- |
| [`src/BAM.Desktop`](src/BAM.Desktop) | WPF 入口、四个页面、设计系统、托盘与单实例机制 |
| [`src/BAM.Core`](src/BAM.Core) | 与桌面 UI 解耦的实例身份、观测状态和能力契约 |
| [`src/BAM.Adapters`](src/BAM.Adapters) | 三种安装的启动链、版本来源、实例协调和能力判定 |
| [`src/BAM.Infrastructure`](src/BAM.Infrastructure) | Windows 进程、窗口、快捷方式和图标，以及本地 JSON 配置和日志 |
| [`tests/BAM.Tests`](tests/BAM.Tests) | 核心行为的自动化测试 |
| [`tests/BAM.Desktop.Tests`](tests/BAM.Desktop.Tests) | 桌面资源历史和设置存储的自动化测试 |
| [`poc/`](poc) | 独立技术验证与夹具 |
| [`scripts/`](scripts) | 桌面验证脚本 |
| [`docs/`](docs) | 调查、验收、架构与界面规范 |

## 开发路线

批量启动反馈已通过夹具验收。便携包、配置升级和 Windows 恢复场景按稳定版路线核验。原 Phase 5 已因产品需求调整取消，定时任务、无人值守自动调度和高级任务队列已移出计划。深度任务适配是未来可选项。

接手开发先读 [AGENTS.md](AGENTS.md)、[稳定版路线](docs/ROADMAP.md)、[统一待办](docs/BACKLOG.md) 和 [实施与验证记录](docs/plans/MVP.md)。

## 第三方项目与许可

BAM 采用 MIT 许可证，版权归 BiangMax 所有。上述工具及游戏的名称归各自权利人所有。用户通过各原项目获取和更新工具。[BAM 许可证](LICENSE) · [第三方声明](docs/releases/THIRD-PARTY-MIT.txt)
