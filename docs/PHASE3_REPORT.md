# Phase 3 通用进程管理验证报告

**2026-11-25 · 开发会话记录。** 以下表格、测试计数和截图保留首次实现时的证据。随后所有者验证了三个空闲 GUI 的运行识别、资源趋势、窗口打开、权限差异和 BAM 重启后的接管。独立审查已修复控制及配置问题，当前停在实机验收前。最终代码对应的结果与剩余操作见[独立审查](PHASE3_CODE_REVIEW.md)。

所有者后续已验收 BAM 托盘、BBchannel 原 CMD 启动及窗口恢复，MaaEnd 和 MAA 的启动、开窗、正常关闭及重启也已实测。管理员 BAM 启动 MaaEnd 未再弹出 UAC。BBchannel 的关闭请求会收起至软件自己的托盘。A-01 已完成，稳定检查点为 `phase-3-complete`。[所有者验收结果](PHASE3_CODE_REVIEW.md#所有者验收与出口)

## 交付与工程

| 模块 | 主要实现 |
| --- | --- |
| `BAM.Core` | 安装、入口、窗口、采样和事件模型，以及系统访问端口。沿用 `ProcessIdentity`、`ScriptDefinition` 和 `CommandPolicy`。 |
| `BAM.Adapters` | 按 Known Folder 快捷方式发现三种软件，支持通用 EXE 与间接入口。按安装协调操作、采样、覆盖范围和事件。 |
| `BAM.Infrastructure` | Windows 快捷方式解析、文件版本、图标、Tool Help 进程枚举、受限 Win32 访问、配置及日志 I/O。 |
| `BAM.Desktop` | 通过 DI 将实际数据接入 `IDesktopData`。沿用总览、脚本管理、日志、设置及托盘，新增入口选择、编辑、重新定位、移除和操作确认。 |
| 测试及烟测 | 独立开启自有 Windows 夹具与真机只读观测。正常、演示和截图验证使用独立目录。 |

开发与运行验证采用 Windows 11 26100 x64、SDK 10.0.404、.NET 与 Windows Desktop Runtime 10.0.15。Phase 3 使用系统的 Win32、Windows Script Host、System.Drawing 和 .NET 标准库，NuGet 包版本沿用 WPF-UI 4.3.0（含 Abstractions 4.3.0）、CommunityToolkit.Mvvm 8.4.2、Microsoft.Extensions.DependencyInjection 及 Abstractions 10.0.15。测试使用 Microsoft.NET.Test.Sdk 18.10.2、xUnit v3 3.2.2 和 VS 适配器 3.1.5。新增项目引用已记录在解决方案和锁文件中。

### 发现与配置

从用户和公用桌面的 Windows Known Folder、OneDrive 候选桌面与有限深度的开始菜单读取 `.lnk`。保存原始入口、实际启动目标、工作目录、原始参数、图标来源及索引、安装目录和可匹配的工作 EXE。按工作程序路径去重。同名的不同安装可以分别保存，手动配置优先。移除记录会保留发现排除项，明确手动添加可以恢复管理。BBchannel 的已知批处理保留 `启动.cmd`，只观测安装下两个架构目录中的 BBchannel.exe 候选。

配置在 `%LocalAppData%\BAM`。安装与设置各存一个版本化 JSON。安装文件通过同目录临时文件替换，保留上一文件备份。从备份读取的情况会通知用户，后续保存时保留损坏原件。高版本 schema 会停止写入。现有未显式声明版本的安装文档可读取，并在下次保存时写入当前 schema。第三方版本来源在限定大小内只读。图标在后台提取为 64×64 并按文件时间、索引缓存，在 WPF 中冻结。失败时显示 Fluent 占位图标。

### 身份、采样和操作

运行观测以完整 Win32 映像路径、PID、UTC 创建时间三者共同识别。同名候选用于缩小枚举范围。权限拒绝、多个匹配或枚举不完整会使状态降为未知。每轮观察一次 Tool Help 快照，已匹配的工作进程保留查询句柄以确认退出和获取退出码。窗口使用属于该进程的可见无主顶层 HWND，分别显示最小化、标题和响应状态。短命启动器退出与工作进程存在是独立证据。 [Tool Help 快照](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/nf-tlhelp32-createtoolhelp32snapshot)

CPU 将相邻累计处理器时间之差按单调时钟及逻辑处理器数归一化。首次、缺失、换实例或大于 15 秒间隔留空。内存为工作集 MiB，可能包括共享页。运行时长从实际创建时间计算。聚合按身份去重，缺失或身份未确认时显示覆盖范围，合计留空。仅统计已确认的所选主进程。前台、最小化和托盘采样分别约一、五、十秒，前台保留 120 秒有界历史，托盘暂停绘制。 [GetProcessTimes](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)

操作开始时按安装 ID 互斥，重新枚举进程并验证当前配置。控制时再次确认原实例，结束和窗口操作使用已复核的句柄。正常关闭提交 `WM_CLOSE`，等待最多八秒确认退出。重启在原实例退出且当前无其他工作进程时继续。强制结束需要目标名称和 PID 的独立确认，只结束目标句柄对应进程。启动请求后最多观察 25 秒，仍找不到工作进程时在本次 BAM 会话暂停重复启动。

BAM 使用 `asInvoker`。用户确认启动后 Windows 可按目标要求 UAC，拒绝、取消或跨完整性限制有对应反馈。快捷方式中已设置的提权标记和启动字段传给原启动目标。EXE 由 Windows 启动，CMD/BAT 交给系统命令解释器，保留批处理路径、空格引号、工作目录和受限形式的参数。对命令连接、变量展开、转义及失配引号拒绝执行。MAA 和通用启动确认均提示现有配置可能触发自动任务，MAA 另提示可能启动模拟器。 [Windows CMD 参数](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cmd) · [Shell Link 标记](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-shllink/ae350202-3ba9-4790-9e9e-98935f4ee5af)

### 日志与生命周期

BAM 写入结构化 JSON Lines，最多四份、每份约 1 MiB，启动时清理超过 14 天的文件。只持久化必要的事件字段。用户可筛选、搜索、查看详情及复制记录，UI 最多 2,000 条。发现、启动确认、退出、路径、权限、工作集读取失败和恢复，以及操作结果按状态变化记录。只有取得未经请求的非零退出码时才标为疑似异常。退出原因未知时记录普通退出。BAM 退出只取消监控和操作收尾并释放自己持有的资源。

| Phase 2 审查项 | 处理与回归 |
| --- | --- |
| P2 设置退出同步等待 | Dispatcher 保持可用，异步等待保存和监控收尾，正常退出有三秒上限。回归测试覆盖停滞的写入任务。 |
| P2 日志沿用旧筛选 | 脚本入口清空搜索和级别，通过安装 ID 选择来源。回归从无结果的错误筛选切回目标。 |
| P3 采样时钟回拨 | CPU 和真实曲线使用单调时间，用户可看真实采样时间。覆盖回拨、间隔过长、丢样和身份变化。 |

## 真机只读观测

自动发现三个有效安装。三种图标均在本机提取为 64×64，截图中显示对应原始图标。两轮观测都未发现匹配的工作进程，因此运行时 CPU、内存、时长及窗口字段为空。记录仅保存在 Git 忽略的本地目录。

| 软件 | 快捷方式与工作程序 | 版本口径 | 本轮状态 |
| --- | --- | --- | --- |
| MaaEnd | 快捷方式指向 MaaEnd.exe，直接工作进程 | MXU GUI 2.11.2，资源清单 v3.3.0-beta.2 | 已停止 |
| MAA | 快捷方式指向 MAA.exe，更新器、模拟器及 ADB 在统计范围外 | 程序 6.23.1 | 已停止 |
| BBchannel | 快捷方式指向 `启动.cmd`，限定安装下 `dist/BBchannel64` 与 `dist/BBchannel` 的 BBchannel.exe | `updateInfo.txt` 当前包版本 3.87 | 已停止 |

### 能力矩阵

| 能力 | MaaEnd | MAA | BBchannel |
| --- | --- | --- | --- |
| 自动发现、快捷方式、图标、版本 | 已实现，真实只读通过 | 已实现，真实只读通过 | 已实现，真实只读通过 |
| 进程身份、CPU、工作集、时长 | 自有夹具通过。真实只读确认本轮无工作进程，运行态仍待观察 | 自有夹具通过。真实只读确认本轮无工作进程，运行态仍待观察 | 自有夹具通过。真实只读确认本轮无工作进程，运行态仍待观察 |
| 用户发起启动与参数 | 已实现，真实启动及提权后交接待授权 | 已实现，真实启动与原配置行为待授权 | 批处理夹具验证路径、参数和目录。真实启动链待授权 |
| 打开/还原窗口、正常关闭、重启、强制结束 | 自有夹具通过。跨完整性控制可能受限，真实操作待授权 | 自有夹具通过。跨完整性控制可能受限，真实操作待授权 | 自有夹具通过。真实窗口与操作待授权 |
| 权限与异常 | 已实现降级。Phase 0 记录过提权情形，本轮无运行中实例可观测 | 已实现降级。Phase 0 记录过管理员运行标记，本轮无运行中实例可观测 | 无额外权限实证。未确认的能力按条件不可用 |
| 游戏任务状态及专属控制 | 本阶段未接入。MXU 与任务日志在后续阶段按版本验证 | 本阶段未接入。回环配对及回报在后续阶段按版本验证 | 通用管理之外的任务和进度检测不支持 |

## 自动化与发布

- 原有 27 项测试均保留。显式启用两项现场测试后共通过 58 项，核心与桌面分别为 43 项和 15 项，零失败。常规 CI 执行其中 56 项。Release 构建为零警告、零错误。
- 自有夹具复用 `poc/ProcessFixture.cs`。覆盖带空格和中文的 CMD 路径、参数跨启动器、工作目录、同名不同目录隔离、多实例歧义、旧 PID 创建时间、正常关闭后重启、窗口恢复、无响应窗口、关闭超时、单目标强制结束与已知非零退出码。权限拒绝由独立的确定性用例模拟。
- 发行目录为 `artifacts/publish/win-x64/`，入口 `BAM.exe`。框架依赖目录式发布包含 12 个文件，PE 为 AMD64，清单核对了 `asInvoker`、`uiAccess=false` 与 `PerMonitorV2`。UTF-8、UTF-16LE 和转义路径检查均无真实路径或用户名命中，正式目录中各文件均未命中演示及截图入口标记。独立演示构建需显式 `--demo`。正式版传入同一参数仍使用实际数据。
- 正式、演示、未指定演示参数的演示构建及截图验证构建均通过 WPF 烟测。正式版在标准本地配置目录保存了三个真实安装。正常启动、重复启动恢复、最小化恢复、关闭退出及重新运行有现场验证。托盘隐藏与恢复通过演示及真实数据验证。既有 Phase 0 进程夹具也已复跑，验证结束后未发现 BAM 或夹具进程残留。
- 首次沙箱还原因 NuGet 审计源不可达返回 NU1900。先以本地缓存完成锁定还原，随后在允许访问 NuGet 的环境以默认审计设置和 `--locked-mode --force` 复验通过。

```powershell
dotnet restore BAM.slnx --locked-mode
dotnet build BAM.slnx -c Release --no-restore
$env:BAM_PHASE3_FIXTURE = '1' # 仅在交互式 Windows 会话启用自有夹具
$env:BAM_PHASE3_OBSERVE = '1' # 仅在获准读取具备这三种安装的主机启用
dotnet test BAM.slnx -c Release --no-build --no-restore
dotnet publish src/BAM.Desktop/BAM.Desktop.csproj -c Release -r win-x64 --self-contained false --no-restore -o artifacts/publish/win-x64
dotnet publish src/BAM.Desktop/BAM.Desktop.csproj -c Release -r win-x64 --self-contained false --no-restore -p:EnableBamDemo=true -o artifacts/publish/demo-win-x64
dotnet publish src/BAM.Desktop/BAM.Desktop.csproj -c Release -r win-x64 --self-contained false --no-restore -p:EnableBamVerification=true -o artifacts/publish/verification-win-x64
pwsh -File scripts/Test-Desktop.ps1 -Output Publish
pwsh -File scripts/Test-Desktop.ps1 -Output Demo
pwsh -File scripts/Test-Desktop.ps1 -Output DemoEmpty
pwsh -File scripts/Test-Desktop.ps1 -Output Verification -Capture -CaptureDirectory artifacts/screenshots/phase-3
```

## 截图与局部性能

截图位于 Git 忽略的 `artifacts/screenshots/phase-3/`。使用同一正式数据源的 WPF 截图构建及隔离的 BAM 配置。路径、参数、窗口标题在截图模式中脱敏。显示比例为 150%。缺失路径截图来自单独的自有配置夹具。

| 文件 | 场景 |
| --- | --- |
| `01-dashboard-dark.png`、`02-dashboard-light.png` | 深浅总览、三个真实安装的卡片、原图标和版本 |
| `03-dashboard-narrow.png` | 800 DIP 宽布局 |
| `04-scripts.png` | 已发现安装、BBchannel 启动及能力详情 |
| `05-logs.png`、`06-settings.png` | BAM 真实运行事件与实际生效的资源采样开关 |
| `10-unavailable.png` | 缺失路径提示、重新定位和控制降级 |

性能数据以 16 个逻辑处理器归一化，只测 BAM 自身。采样条件为管理三个已停止的真实安装。启动测量从创建进程到可观测 WPF 主窗口为 970 ms，缓存状态未控制。

| 场景 | 观测窗口 | CPU | 工作集 | 私有内存 | 句柄 |
| --- | --- | --- | --- | --- | --- |
| 前台空闲，正常启用监控 | 12.0 秒 | 0.049% | 211.4 MiB | 149.8 MiB | 870 → 870 |
| 托盘后台，完成主题切换和截图后 | 11.0 秒 | 0.027% | 249.9 MiB | 206.5 MiB | 883 → 878 |

前台工作集短测超过 180 MiB 目标。连续运行时的内存、真实软件活跃负载、十分钟空闲采样与 24 小时稳定性仍待按路线图验证。

## 差异自审与审核点

对照 `phase-2-complete` 审阅变更、新增项目及发布文件。

| 级别 | 结论 |
| --- | --- |
| P0 | 未发现会在发现、采样、设置恢复或 BAM 退出时主动触发第三方命令的代码路径。 |
| P1 | 重启先确认旧实例退出。停止和强制控制均复核实际映像、PID 与创建时间。权限不明、同路径多实例和候选无法读取时禁用控制。外部同时启动与 Windows 前台限制仍有系统竞态。 |
| P2 | 修复了测试环境短路径长路径同一安装的识别，以及设置退出阻塞、日志跳转保留旧筛选。真实管理员权限窗口和运行中软件观察仍待现场实测。 |
| P3 | 修复采样墙钟回拨问题以及截图工具把 Windows 缺失文件的默认时间当成已写入的判定。混合 DPI、高对比度、读屏和长跑仍待验证。 |

用户可在各工具处于获准的空闲窗口后，逐项授权真实启动、UAC 确认和取消、窗口激活、正常关闭与重启。强制结束应单独授权测试。MAA 验证前应由所有者确认现有自动运行和启动模拟器设置及影响。通用跨完整性控制能力以实测为准。

Phase 4 开始前需审核 Phase 3 差异，明确各安装版本、测试用账户和空闲环境，并分别授权 MaaEnd 实例参数与任务日志、MAA 用户启用的回环协议及任务回报验证。BBchannel 在没有稳定可复验接口时维持通用管理。当前工作区停在 Phase 3 审核点，Git 保持原分支与标签。
