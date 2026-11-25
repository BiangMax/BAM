# Phase 1 正式工程验收

**2026-11-25 · 已审核通过。** 已在 Windows 11 x64 实际完成锁定还原、Release 构建、核心单测、WPF 启停及 win-x64 目录发布。当前程序为明确标记的 Phase 1 验证窗口。基础版总览和第三方接入留在后续阶段。

## 工程基线

| 项目 | 实际值 |
|---|---|
| SDK 与运行环境 | dotnet --info 确认为 .NET SDK 10.0.404 x64、MSBuild 18.9.15、Windows build 26100。已安装 Microsoft.WindowsDesktop.App 10.0.15。 |
| SDK 选择 | global.json 指定 10.0.404、latestFeature、禁用 preview。CI 用同一文件安装 SDK。 |
| 构建配置 | BAM.slnx、Directory.Build.props、Directory.Packages.props、.editorconfig。启用 nullable、确定性编译、编译警告作为错误、路径映射和每项目锁文件。当前程序集包版本为 0.1.0-alpha.1。 |
| NuGet | 已确认启用 nuget.org。实际下载依赖并生成三个 packages.lock.json；再次强制求解锁定还原，锁文件哈希保持一致。 |
| Git 起点 | Phase 1 实施开始时，`main` 尚无提交或远程。公开检查点使用 `phase-1-complete`。 |

| 项目 | 框架和引用 | 本阶段职责 |
|---|---|---|
| src/BAM.Desktop | net10.0-windows、win-x64 → Core；DI 10.0.15 | WPF 组合根、当前用户单实例、激活信号、验证窗口和资源释放。入口 BAM.exe。 |
| src/BAM.Core | net10.0；无项目或第三方包引用 | 身份、会话状态流、适配能力、控制前置判定与探测故障隔离。 |
| tests/BAM.Tests | net10.0 → Core | xunit.v3 3.2.2、xunit.runner.visualstudio 3.1.5、Microsoft.NET.Test.Sdk 18.10.2，全部限测试项目。 |

根命名空间为 BAM。Desktop 唯一直接 NuGet 依赖为 Microsoft.Extensions.DependencyInjection，发布还包含它的 Abstractions 10.0.15。锁文件列出完整传递依赖。Adapters 和 Infrastructure 待首个具体实现再建立；当前适配器仅有 Core 契约和测试替身。WPF UI、MVVM Toolkit、Serilog、HTTP、数据库均留在原定阶段。

## 核心行为与测试

- **安装与身份。** ScriptDefinition 区分启动入口及允许的工作程序路径集合，ProcessIdentity 使用规范化绝对路径、PID 和 UTC 创建时间。ProcessInspection 提供“确认不存在”和“身份无法读取”的独立结果。
- **观测与状态。** Observation 保存会话、采样时间、来源及状态值。SessionState 将进程、窗口、游戏任务状态分流，命令流额外匹配命令 ID。旧会话、会话开始前及乱序观测被丢弃；重复事件不更新；同时间冲突降级未知。尚未实现真实观测生产者和跨源时钟校准。
- **能力与命令。** Capability 区分可用、不可用、不支持及未知。CommandPolicy 检查具体能力、安装归属、所选及当前身份，仅产生 CommandResult。Accepted 表示通过前置检查。CapabilityProbe 隔离单个适配器错误，并传播调用方取消。
- **桌面机制。** 主实例持有按本地 Windows 用户隔离的具名互斥量；当前会话内以自动重置事件通知已存在的 Dispatcher 还原并尝试激活原窗口。主实例才初始化 DI、解析窗口并显示。正常退出释放服务容器、信号订阅与互斥量。验证窗口只展示阶段用途及关闭按钮。

| 单测组 | 发现及通过 | 实际覆盖 |
|---|---:|---|
| IdentityAndControlTests | 12 | 同名异路径、PID 复用、路径规范化、拒绝相对路径、不可读与停止、归属不明、目标替换、能力状态及错误操作类型。 |
| SessionTests | 6 | 使用固定时钟。旧会话、过期、重复、乱序、同时间冲突及恢复、进程/窗口与游戏任务独立、远程回报对命令 ID 隔离。 |
| CapabilityProbeTests | 3 | 一个适配器抛错时另一个可用，取消传播，探测对象不匹配时返回未知。 |
| **合计** | **21** | VSTest 的 TRX 记录发现 21、执行 21、通过 21、失败 0、跳过 0。 |

## 构建、运行与发布实证

| 验收项 | 操作与结果 |
|---|---|
| 干净构建 | 在项目根执行 restore、restore --locked-mode --force、clean 后执行 Release build。三个项目都成功，0 警告、0 错误。 |
| 本地 CI 路径 | 在仅复制工程输入和锁文件的独立工作副本中设置 CI=true，按 Windows workflow 的顺序执行锁定还原、Release 构建、测试。构建成功且再次通过 21 项测试。无远程仓库，未触发 GitHub 托管作业。 |
| WPF 构建输出 | 用 [窗口烟测脚本](../scripts/Test-Desktop.ps1) 启动 Release 的 BAM.exe，取得真实标题及 HWND；最小化后重复启动，次实例退出码 0，原 HWND 恢复；正常关闭退出码 0；重新启动并再次正常关闭。窗口由已启用容器构建校验的 DI 解析。 |
| 发布 | Desktop 以 Release、win-x64、framework-dependent 发布到 artifacts/publish/win-x64/。PE machine 为 0x8664；清单为 asInvoker、uiAccess=false。runtimeconfig 引用 .NET 10 共享 Core 与 Desktop。输出为目录式，未使用裁剪或单文件。 |
| WPF 发布输出 | 从发布目录独立重复完整窗口烟测，普通权限进程下通过。终检该路径匹配的 BAM 残留进程数为 0。测试证明同会话唤起/还原；未将 Windows 全局前台焦点作为成功条件。 |
| 出包核对 | 8 个文件逐个与构建输出 SHA-256 比对一致：BAM.exe、BAM.dll、BAM.Core.dll、BAM.Core.pdb、deps 和 runtimeconfig 两个 JSON、DI 两个 DLL。对 UTF-8/UTF-16 内容筛查当前工作区、用户路径/用户名、安装清单中的真实安装目录和 .local 清单标记，命中 0。输入和文件清单中没有第三方配置、设备数据、测试、PoC 或本地报告。 |

### 复验命令

```powershell
dotnet --info
dotnet restore BAM.slnx --locked-mode
dotnet build BAM.slnx -c Release --no-restore
dotnet test BAM.slnx -c Release --no-build --no-restore
dotnet publish src/BAM.Desktop/BAM.Desktop.csproj -c Release -r win-x64 --self-contained false --no-build --no-restore -o artifacts/publish/win-x64
pwsh -File scripts/Test-Desktop.ps1 -Output Build
pwsh -File scripts/Test-Desktop.ps1 -Output Publish
```

## 问题、差异与验收边界

1. 沙箱无法向用户级 .dotnet 目录执行首次写入，也无法读取用户 NuGet 配置。实际验收使用项目内已忽略的 CLI/包缓存，使用允许网络还原的执行环境读取源并下载包。未安装 SDK 或更改系统配置。
2. VSTest 最初提示测试产物目录未指定，回退到用户临时目录；更新 Test SDK 后仍可见该提示。最终将本次测试进程的 VSTEST_ARTIFACTS_DIRECTORY 指向项目内缓存，正常生成 TRX。锁定版本及测试数量以上表为准。
3. 单实例的同用户同会话路径已验证。跨会话、不同完整性级别、并发冷启动压力、遗弃锁恢复及操作系统拒绝前台仍需专门的 Windows 回归。Phase 1 未运行真实游戏脚本，也未申请第三方程序提权。
4. 本期实现的是状态与能力基础。真实进程控制前仍需持有或重新核验目标句柄；观测聚合器将绑定安装/实例生命周期。休眠恢复、配置原子写入与迁移、损坏恢复、采样取消、长跑和性能目标均留在既定阶段。

## Git 变更与审核点

检查点纳入正式解决方案、三个项目及其锁文件、集中构建配置、Windows CI、桌面烟测脚本、PoC 和项目文档。公开前再次按 CI 顺序执行锁定还原、Release 构建和测试，构建零警告、零错误，21 项全部通过。生成文件与私人调查资料分别存放在忽略目录中。

Phase 2 等待单独开发指令。获准后按 UI 规范建立主题资源、标题栏和导航，逐项验证 Snap、键盘与高 DPI，再使用明确标识的演示数据开发脚本卡。保持本轮单实例、Release、测试与发布验证可重复执行。
