# BAM Phase 0 技术可行性报告

**调查日期　2026-11-25**　实机调查验证了三个工具的入口发现和图标读取。推荐先实现通用管理，再分别验证 MaaEnd 的 MXU 启动参数和 MAA 的回环远控协议。BBchannel 计划采用通用管理。本文保留脱敏的调查证据。正式工程见 [Phase 1 报告](PHASE1_REPORT.md)。

**证据口径**　“实测”指本机实际文件、现场快照或明确指名的夹具结果；“官方”指版本源码或协议；“推测”指机制可行、仍缺真实应用验证；“未知”表示缺证据；“建议”属于产品决策。以下每个本地路径均以安装根或 Known Folder 为基准。精确路径清单仅存项目内被 Git 忽略的 `.local/installations.json`。没有复制用户配置、设备标识、截图或原始日志。

## 1　开发机与快捷方式

| 实测项 | 结果及意义 |
|---|---|
| 操作系统 | 通过 CIM 确认为 Windows 11 x64。 |
| 工具 | PowerShell 7.6.9、Git 2.56.0.windows.4；主机附带 Python 3.12.14 可运行标准库测试。初始工作区为空；已建立 main 空仓库，零提交、无远程。 |
| .NET | x64 dotnet host 10.0.1；Microsoft.NETCore.App 和 Microsoft.WindowsDesktop.App 各有 8.0.12、10.0.1。dotnet --info 未列 SDK 或 Microsoft.AspNetCore.App。MAA 附带的运行时只供其自身使用。 |
| 构建工具 | 标准 VS Installer 位置无 vswhere，常见 VS、SDK 目录与 PATH 未发现可用现代 MSBuild、devenv、cl、MakeAppx 或 SignTool；未发现标准 Windows Kits 10/bin。旧 Framework64 v4 编译器和 MSBuild 存在。未全盘查找自定义工具。 |
| 能实际完成 | Framework csc 编译并运行 C# 测试夹具，Python 运行 HTTP 往返。正式 WPF 的 restore/build/test/publish、.slnx 及安装包未实测。安装建议见技术选型；未更改依赖。 |

**L1 · 发现方法**　解析当前用户和公共 DesktopDirectory，另检查用户 Desktop 及 OneDrive 环境路径。命中三条当前用户桌面链接，公共桌面无其他匹配，候选 OneDrive Desktop 路径不存在。用 WScript.Shell 读 TargetPath、Arguments、WorkingDirectory、IconLocation，再核实目标文件、版本和脚本内容。

| 快捷方式 | 解析目标、启动参数、工作目录 | 工作程序与图标 |
|---|---|---|
| `<用户桌面>/MaaEnd.lnk` | `<MaaEnd>/MaaEnd.exe`，参数为空，工作目录为安装根 | 直接指向产品名 mxu 的 EXE；PE 关联图标 32×32。运行期间可能发生提权重启，见 §2。 |
| `<用户桌面>/MAA.lnk` | `<MAA>/MAA.exe`，参数为空，工作目录为安装根 | 直接 GUI；PE 关联图标 32×32。独立更新器不作为主实例。 |
| `<用户桌面>/BBchannel.lnk` | `<BB>/启动.cmd`，参数为空，工作目录为安装根 | 批处理先切至自身目录，按系统架构启动 dist 下的 BBchannel.exe；显式指定 BB.ico 索引 0，成功解码 32×32。 |

## 2　三种安装的证据与适配边界

### MaaEnd　资源 v3.3.0-beta.2 / MXU 2.11.2

**E1 · 已安装结构。** `interface.json` 标识项目、资源版本及接口版本 2；`MaaEnd.exe` 的 ProductName=mxu、ProductVersion=2.11.2。安装包 README 指向 MaaFramework 和 MXU；本地存在 `maafw/MaaFramework.dll`、Agent 客户端/服务端 DLL、`agent/go-service.exe`、`agent/cpp-algo.exe` 和独立 cloud-shell 程序。`maafw.log` 的 Logger 版本行显示 v6.0.0-beta.3。MXU 的同版实现为 Tauri/Rust + Web 前端，Windows 使用 WebView2。进程快照命中 MaaEnd 名称，其直接子进程含 WebView2 和 adb；此时 CIM 未返回主进程映像路径，之后采样时候选已消失，退出原因未知。文件存在不足以证明 Agent 当时运行。更新由原项目负责，MXU 配置有更新检查选项。[MaaEnd 对应发布](https://github.com/MaaEnd/MaaEnd/releases/tag/v3.3.0-beta.2) · [MXU 对应标签](https://github.com/MaaXYZ/MXU/tree/v2.11.2)

**E2 · 启动语义。** 配置结构包含任务实例，单个安装可保存多个实例。实例数量不能用来推断 Windows 进程数量。安装版帮助元数据与对应版本源码中的参数一致。

| 候选参数 | 对应版本源码结论 | 本机验证结果 |
|---|---|---|
| `--help` | main 首先调用 init_cli，帮助分支打印并退出，再往后才是单实例锁和 WebView 初始化。 | 仅以普通权限、重定向输出尝试此参数，系统在进程创建时要求提升，未获得帮助输出。本机 HKLM 应用兼容层对该 EXE 设置 RUNASADMIN。 |
| `--autostart` | 进入自动执行路径；仍需有匹配的自动执行实例和配置。 | 本地帮助文本命中，真实执行未调用。 |
| `--instance <名称>` | 在 autostart 模式按名称选现有实例。找不到时实现可回退至默认自动实例。 | 配置结构可读。建议启动前检查唯一精确名称，消失或重名时中止。未执行实例。 |
| `--quit-after-run` | 实际自动运行后观察该实例 isRunning 从真变假并退出，不附带游戏结果。 | 本地帮助文本命中；未组合调用。 |

**建议。** 优先选择已保存实例，启动前检查权限、同目录进程、实例配置是否变更，以及启动前后动作可能产生的副作用。版本源码中已有实例会被唤起，新进程随后返回；自行提权的 ShellExecuteEx 分支未显式传递原始参数。这两条路径都需要独立验证。暂不向已运行 GUI 提供“远程开始/停止”承诺。未发现可据以控制现有 MXU 会话的稳定公开 HTTP/IPC；内置 Agent 通讯和 MaaFramework API 作用于其管理的核心会话。另建核心运行器需要重建控制器、实例和任务上下文，配置复用和长期维护成本高。选择单个现有任务与选择一个配置实例分别验收。[同版 CLI 实现](https://github.com/MaaXYZ/MXU/blob/v2.11.2/src-tauri/src/commands/system.rs) · [Windows 启动入口](https://github.com/MaaXYZ/MXU/blob/v2.11.2/src-tauri/src/main.rs) · [实例选择与退出逻辑](https://github.com/MaaXYZ/MXU/blob/v2.11.2/src/App.tsx)

**E3 · 日志实测。** 现有 `debug/maafw.log`、其 gzip 归档和 `debug/mxu-tauri.log` 可离线读取。样本含 UTF-8 编码、带时间的任务事件及 entry、task_id 字段。部分事件也出现在转发记录中，需要跨日志去重，按会话与任务 ID 对齐，并区分取消、重试和孤立终态。总步数、队列成功率和卡死阈值缺少可靠证据。

### MAA　6.23.1

**A1 · 本机结构与运行。** `MAA.exe` 为 6.23.1，MAA.dll 的版本含提交 `40472436115fc9906269f72bfc737bc227737b22`。目录内有 WPF 程序集、MaaCore.dll、MAA.Updater.exe、更新日志；runtimeconfig 声明 net10.0 和随包 Desktop/Core 10.0.10。GUI 为 C#/WPF，核心由独立原生库承载。配置有 `config/gui.json`、`gui.new.json` 和备份。原 GUI 的 RunDirectly、StartEmulator 设置可使启动软件时触发原有任务及模拟器，本轮只观察现有进程。对应 EXE 也有 HKLM RUNASADMIN 标记。CIM 显示 MAA 的直接子进程包含模拟器和 adb，它们均被排除。官方提供 GUI 配置切换参数 `--config <配置名>`，多开文档采用独立目录与独立设备地址；同目录并行启动未测。[GUI 配置](https://docs.maa.plus/zh-cn/manual/introduction/others.html) · [官方连接指引](https://docs.maa.plus/zh-cn/manual/connection.html)

**A2 · 官方远控。** 已核对对应源码和 6.23.1 协议。MAA 作为 HTTP 客户端主动轮询，BAM 计划作为可选的本地服务端。协议使用由用户设置的 user 和客户端生成的 device 配对。这些字段属于本地资料。实现边界和启用条件见[架构](ARCHITECTURE.md)。

| 通道或命令 | 字段及准确语义 |
|---|---|
| 获取任务 | 路径可配。请求含 user、device。响应为含 tasks 数组的 JSON；空闲也要返回空数组。每项有唯一字符串 id、type，可选 params。轮询可重复投递相同 id，服务端需能重入。 |
| 汇报任务 | 请求含 user、device、task（命令 id）、status（SUCCESS/FAILED）、payload（字符串）。协议允许任意响应内容，不能依赖客户端一定重试；失联或超时记录为结果未知。 |
| LinkStart | 按现有配置启动一键长草，属于顺序队列。实现等待空闲、触发 GUI 动作、再等空闲。整个指令回报成功不足以断言每个游戏任务都成功。 |
| LinkStart-* | Base、WakeUp、Combat、Recruiting、Mall、Mission、AutoRoguelike 可单独执行相应子功能，忽略主列表勾选；Reclamation 为兼容别名。 |
| HeartBeat | 即时返回当前远程顺序任务 id 或空字符串。不能覆盖用户手工启动的所有任务，也不能按此推导游戏进度。 |
| StopTask | 即时请求停止当前任务，不等待停止确认；后续排队任务仍可开始。需结合心跳和任务事件确认。 |
| CaptureImage / CaptureImageNow | 分别排队/即时截图；可能连接设备并产生大型 Base64 数据。BAM 首版不启用。 |
| 其他同版分支 | Toolbox-GachaOnce、GachaTenTimes（同前缀）、Toolbox-Reclamation，及 Settings-ConnectAddress、Settings-Stage1。存在于协议不等于产品要暴露这些动作。 |

用“已排队 → 已提供给轮询 → 有执行证据 → 收到指令回报”追踪传输；发送 HTTP 响应本身不能确认客户端接收。游戏任务另以可信起止事件归因，缺少关联时保留未知。对于回环 HTTP，MAA 官方提示明文连接可能记录警告。端口冲突、重启、请求限额和停服规则见架构。[同提交远控实现](https://github.com/MaaAssistantArknights/MaaAssistantArknights/blob/40472436115fc9906269f72bfc737bc227737b22/src/MaaWpfGui/Services/RemoteControl/RemoteControlService.cs) · [版本化协议](https://github.com/MaaAssistantArknights/MaaAssistantArknights/blob/v6.23.1/docs/zh-cn/protocol/remote-control-schema.md)

**A3 · 日志和替代方案。** 已读取 `debug/asst.log`、`debug/gui.log`，发现对应 .bak.log 及 pending-update-applier.log。前两者 32 KiB 前缀通过严格 UTF-8，抽样为带时间和级别的文本，核心附回调和结构化字段。样本包含 TaskChainStart、TaskChainCompleted、AllTasksCompleted、SubTaskError 和 SubTaskExtraInfo；事件带 taskchain、taskid，队列终态有 finished_tasks。子任务失败可能触发重试，不能直接宣布整次游戏任务失败。GUI 有异常类型，归因还需同一会话的任务链和上下文。官方回调描述错误与完成的层级，未在本次样本确认顶层失败的完整样例。maa-cli 是调用 MaaCore 的单独客户端，使用自己的任务/配置，未在 PATH 或安装内找到独立 maa-cli。MaaCore C API 用于自己创建的句柄，不能用来直接订阅现有 GUI 的会话。现有 GUI 的公开 HTTP 协议最适合复用。UIA 依赖控件树、语言和权限，仅作为最后的只读补充。[核心回调](https://docs.maa.plus/zh-cn/protocol/callback-schema.html) · [maa-cli 配置](https://github.com/MaaAssistantArknights/maa-cli/blob/main/crates/maa-cli/docs/zh-CN/config.md)

### BBchannel　安装清单 3.87（基础包 3.62）

**B1 · 实测。** `updateInfo.txt` 给出 base_version=3.62、this_version=3.87，EXE 无有效产品版本；未用标题栏作第二次确认。`启动.cmd` 优先尝试 WMIC，再按环境分支选择 `dist/BBchannel64/BBchannel.exe` 或 `dist/BBchannel/BBchannel.exe`。调试脚本对应两目录的 `BBchannel_debug.exe`；未在安装树发现 UI.exe 或 UI_debug.exe。普通 x64 PE 的 Subsystem=2（GUI），调试版=3（Console）。Python 3.6 运行库、Tk、Qt 和 Airtest 文件存在；仅凭打包依赖无法确定具体窗口组件。发现 `update.exe`、`cleanProcess.exe`、多个 adb、UIsettings.json、scripts_settings.json 和策略文件。没有运行上述程序。未发现 .log 文件或可据以确认任务状态的公开状态文件；错误截图及配置不能代替任务状态。

**B2 · 接口及降级。** 快照没有运行中的 BBchannel 或调试版，因此无对应窗口可枚举 UIA；未启动调试版抓 stdout/stderr。Console 子系统仅说明具备候选输出通道，作者批处理使用 start，不能保证捕获派生进程输出。未证实公开 API、CLI、IPC、多实例、剩余次数或稳定调试记录。作者指南介绍从界面设置、启动及补丁更新；公开 GitHub 仓库只有 README 和单次提交，未提供可对照 3.87 的代码或发布物。作者空间直接抓取返回 412，通过作者署名的指南、补丁专栏完成交叉核对。维持原版启动及更新；MVP 禁用任务动作，显示任务检测“不支持”。[作者空间](https://space.bilibili.com/36142005) · [作者安装指南](https://www.bilibili.com/opus/604724682244937153) · [作者界面指南](https://www.bilibili.com/read/cv14510194/) · [3.62 基础包更新说明](https://www.bilibili.com/opus/1094612246963683337) · [公开仓库](https://github.com/xiaoruanmm/FGOautomation-BBchannel-)

## 3　实机测试记录

| 编号、环境和方法 | 实际结果 | 证据边界 |
|---|---|---|
| P1 · Windows 11，同一 C# 夹具以两个目录下的同名 EXE 运行，枚举后校验路径、PID、创建时间 | 两实例可区分；找到已有实例，独立关闭其中之一，另一实例存活。测试窗口句柄、标题、响应检查通过。 | [夹具源码](../poc/ProcessFixture.cs)；从枚举发现模拟外部启动，没有让用户手工启动三个游戏软件。 |
| P2 · 隐藏的专用窗口、自己创建并持有的 Process 对象 | WM_CLOSE 返回后观察退出码 0；重启后创建时间改变；对夹具显式 Kill 并等待退出；另一个夹具自行返回 23，检测到非预期退出。 | [运行脚本](../poc/Test-Process.ps1)。遇到托盘、自定义退出确认、UIPI 或任务自退出，真实软件行为可能不同。非零码仅是诊断证据。 |
| P3 · 资源采样，Process.Refresh、Stopwatch、两次 TotalProcessorTime | 夹具的 CPU 范围、内存和时长断言通过。真实 MAA 按 QueryFullProcessImageName、PID、创建时间匹配，并取得单次 GUI 主进程样本。 | [只读探针](../poc/Inspect-Installed.ps1)。整机归一化 CPU 使用进程 CPU 时间增量、采样间隔和逻辑处理器数。内存指标为驻留工作集及进程私有提交。单次样本仅验证采样可行。 |
| P4 · 原生 EnumWindows、GetWindowThreadProcessId、GetWindowText、IsHungAppWindow | 受限会话看不到目标窗口，交互会话能够读取窗口可见性和响应状态。两次的精确映像匹配一致。 | 零可见窗口可能是托盘或桌面隔离。响应是时点观测。本项只读。 |
| P5 · 仅在测试进程的私有桌面测试窗口最小化、恢复、前台调用 | 首次附加夹具抛出未处理异常，出现 BamPhase0Fixture.exe 错误框；随后一次切换线程桌面返回 Win32 170，一次通过窗口断言但退出超时。按路径、启动参数、创建时间和 PID 清理了该测试实例。修正为 MTA、原生专用窗口、顶层异常处理和有界等待后，复测最小化及恢复通过；SetForegroundWindow 返回 False，实际也未成为前台。终检无夹具进程。 | 未切换用户输入桌面。隔离桌面的失败不能预测用户点击 BAM 后的前台结果。用户主动唤起时才请求前台，失败则闪烁任务栏或说明需手动切换。[前台限制](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow) · [UIPI](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/user-account-control-only-elevate-uiaccess-applications-that-are-installed-in-secure-locations) |
| P6 · Python 标准库 HTTPServer，仅 127.0.0.1 临时端口，HTTPConnection 模拟 MAA | 完成取 HeartBeat、重复轮询同 id、成功回报、重复回报、完成后空队列。未配对请求 403、未知命令回报 400、冲突回报 409。测试通过并关闭监听。 | [模拟协议测试](../poc/test_loopback.py)。只证明本机回环和协议形状；未连接 MAA，未运行 Kestrel。状态码为 BAM 端策略。 |
| P7 · 两套软件已有日志，白名单事件名、字段名和 UTF-8 样本 | 见 E3、A3。样本包含同一时间戳的事件和不规则的时间戳间隔。 | 记录间隔与写入延迟是两个量。日志静默不足以判定卡死，轮换阈值和保留策略仍待验证。 |

**P8 · 结构化离线重放。** [日志解析 PoC](../poc/read_events.py) 按进程标识、设备上下文、任务 ID 和入口或链名配对，输出聚合结果。实测覆盖开始、完成、失败以及缺少开始记录的终态。历史文件缺少 Windows 创建时间，结果只适用于该文件内的离线配对。

复现入口（从项目根运行，Python 可换成本机已有解释器）：

```powershell
pwsh -File ./poc/Inspect-Installed.ps1
pwsh -File ./poc/Test-Process.ps1
python -I ./poc/test_loopback.py -v
# 将参数换为只读日志文件，可处理 maafw.log、其 .gz 归档或 asst.log。
python -I ./poc/read_events.py "<日志文件>"
```

检查器只读取当前环境。夹具只编译到新建的 .local/fixture-* 目录。权限、会话和运行中的软件会影响输出；历史日志统计会随原软件滚动而改变。

## 4　功能可行性矩阵

每列仅针对表头所列安装。每行最后一列给出方法、结果与限制，编号可回查上文的文件、源码或 PoC。“不支持”用于 BAM 此版明确不提供的能力；对第三方未公开的能力保留“未知”。日志的“实测通过”限于已有文件中的证据提取。

| 能力 | MaaEnd 3.3.0-beta.2 / MXU 2.11.2 | MAA 6.23.1 | BBchannel 3.87 清单 | 方法、结果及限制 |
|---|---|---|---|---|
| 软件路径识别 | 实测通过 | 实测通过 | 实测通过 | L1 查目标实际存在；BB 双架构启动链读自 cmd，未动态跟踪。 |
| 软件版本读取 | 实测通过 | 实测通过 | 实测通过 | E1/A1/B1 分别读资源清单与 PE、PE 提交、文本清单；BB 无 PE 版本。 |
| 软件图标 | 实测通过 | 实测通过 | 实测通过 | L1/P3 两个 EXE 资源及一个独立 ICO 可解码。 |
| 软件启动 | 理论可行，待验证 | 理论可行，待验证 | 理论可行，待验证 | L1/P2 有原始入口；真实启动可能提权、更新或自动拉起游戏。 |
| 普通权限直接启动并捕获 --help | 实测失败 | 未知 | 未知 | E2 系统拒绝普通 CreateProcess；其他两款未尝试该参数。 |
| 正常关闭 | 理论可行，待验证 | 理论可行，待验证 | 理论可行，待验证 | P2 仅夹具通过。真实窗口可能转入托盘、拒绝消息或要求确认。 |
| 强制结束 | 理论可行，待验证 | 理论可行，待验证 | 理论可行，待验证 | P2 仅夹具通过；真实对象须有终止权限，不结束后代。 |
| 重启 | 理论可行，待验证 | 理论可行，待验证 | 理论可行，待验证 | P2 等确认退出再启动；原实例仍在或操作可能触发游戏时需中止或明确提示。 |
| 精确进程监控、发现外部实例 | 理论可行，待验证 | 实测通过 | 理论可行，待验证 | E1 出现过候选但未匹配完整路径；A1/P3 两次匹配运行中的同一真实 GUI；B2 无样本。 |
| CPU 监控 | 理论可行，待验证 | 实测通过 | 理论可行，待验证 | P3 同一身份差分采样，缺一次采样时未知。 |
| 内存监控 | 理论可行，待验证 | 实测通过 | 理论可行，待验证 | P3 工作集、私有字节均读取；默认仅主程序。 |
| 运行时长 | 理论可行，待验证 | 实测通过 | 理论可行，待验证 | P3 以创建时间计算，重启重新计时。 |
| 识别异常退出 | 理论可行，待验证 | 理论可行，待验证 | 理论可行，待验证 | P2 已测可观察退出；E1 候选消失原因未知，用户退出、更新与崩溃需不同证据。 |
| 多实例区分及防重启 | 理论可行，待验证 | 理论可行，待验证 | 理论可行，待验证 | P1 同名不同路径夹具通过；E2 同目录锁、A1 多目录、BB 多开未知；默认拦截重复启动。 |
| 窗口枚举、标题、响应 | 理论可行，待验证 | 实测通过 | 理论可行，待验证 | P4 真实 MAA 交互桌面正样本。无 HWND 或无法归属时不可操作。 |
| 恢复、打开窗口、前台激活 | 理论可行，待验证 | 理论可行，待验证 | 理论可行，待验证 | P5 仅夹具恢复通过。系统焦点规则与 UIPI 限制须在真实权限组合下验收。 |
| 日志读取 | 实测通过 | 实测通过 | 未知 | E3/A3/P7 已读当前和备份样本；B1 未发现运行日志。 |
| 游戏任务启动 | 官方文档支持，尚未实测 | 官方文档支持，尚未实测 | 未知 | E2 仅启动时选实例；A2 LinkStart 需先配置和配对；B2 无公开契约。 |
| 游戏任务停止 | 未知 | 官方文档支持，尚未实测 | 未知 | E2 无稳定现有 GUI 控制入口；A2 StopTask 为异步请求；B2 无样本。 |
| 游戏任务实时状态 | 理论可行，待验证 | 理论可行，待验证 | 不支持 | E3/A3 离线起止事件已见，当前进程/会话/取消关联仍待实测；B2 通用模式。 |
| 当前任务名称 | 理论可行，待验证 | 理论可行，待验证 | 不支持 | E3 entry、A3 taskchain 可读；名称映射及运行归属待校准。 |
| 游戏任务进度 | 未知 | 未知 | 不支持 | 节点、子任务与心跳未提供通用可靠分母；BB 无可复验计数。 |
| 任务完成检测（当前整次运行） | 理论可行，待验证 | 理论可行，待验证 | 不支持 | E3/A3 找到成功/完成标记，需区分节点、任务、队列、取消及旧会话。 |
| 任务失败检测（当前整次运行） | 理论可行，待验证 | 理论可行，待验证 | 不支持 | E3 框架任务失败，A3 子任务错误；需有标注终态对照，不能直接聚合。 |
| 疑似卡死检测 | 未知 | 未知 | 不支持 | P4/P7 响应、静默或空心跳均非充分条件。V0.1 不输出此诊断。 |

## 5　决策、阻塞与后续授权

- **MVP 可以独立推进。** 三个入口、版本来源和图标已确认；通用控制使用夹具验证。基础版保留“软件已启动”和“任务未知”的独立呈现。详细功能与性能门槛见[路线图](ROADMAP.md)。
- **正式构建阻塞。** 在获准进入 Phase 1 后，需要可用的 .NET 10 x64 SDK 或一台有 SDK 的构建机，并确认 NuGet 可还原。本次未安装、未构建正式 BAM、未发布软件。
- **MaaEnd 的最小真实补测。** 单独授权提升权限后，首先只运行帮助并捕获输出；另择空闲时段、指定安全测试实例和可接受的游戏动作，再分别验证启动参数传递、错误名称阻断、已开 GUI 和结束后退出。不得使用生产实例做故障注入。
- **MAA 的最小真实补测。** 需授权在原 GUI 填写仅回环的取任务及汇报地址、配对身份。先以空队列和单条 HeartBeat 验证通道；实际 LinkStart、StopTask、截图及启停软件需分别明确范围。首选先收集脱敏的正常、取消和失败会话，再放开任务结果展示。
- **BBchannel 的边界。** 待用户自然启动后可只读补测路径、窗口、UIA 控件可见性；主动启动或运行调试版需先确认启动副作用。无需为它延迟 V0.1.0。
- **权限与数据风险。** 不默认提升 BAM。不清楚归属时维持只读；针对真实程序的正常关闭、强制结束、重启、跨权限前台操作和 UIA 控件均未实测。第三方软件资源仅作本地只读调查；系统与第三方配置未写入，未向任何服务发送本地日志或配置。
