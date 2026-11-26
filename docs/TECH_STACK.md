# Windows 桌面技术选型

**决定**　V0.1.0 使用 C#、.NET 10 LTS 与 WPF。目标为 `net10.0-windows`、`win-x64`，Core 可用 `net10.0`。三个接入对象都在本地 Windows 桌面，真正影响可靠性的是映像身份、权限、窗口和现有配置。WPF 可直接使用 Win32，同时以 XAML 资源控制视觉细节。长期支持截至 2028-11-14，正式构建选择当期受支持的 10.0 服务补丁。[.NET 支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) · [WPF 概述](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)

## 依赖决策

| 引入时间 | 组件 | 使用边界与依据 |
|---|---|---|
| Phase 1 | .NET SDK 10 x64、Windows Desktop 引用包、MSBuild | SDK 随附构建能力。已使用 10.0.404 x64 验证。SDK 由 global.json 选择，rollForward 为 latestFeature，禁用预览。 |
| Phase 1 | Microsoft.Extensions.DependencyInjection 10.0.x | MIT。仅在组合根解析对象，核心构造函数显式列出依赖。暂用应用级取消源和后台任务，无需为三个程序引入通用宿主的全部功能。 |
| Phase 1 | xUnit v3、对应测试运行器 | Apache-2.0；测试包仅测试项目引用。选取相容的 VSTest 适配器与 Microsoft.NET.Test.Sdk，先证明 dotnet test 能发现和执行测试，再锁定补丁。 |
| Phase 2 | WPF-UI 4.3.0 | MIT，NuGet 包含 net10.0-windows7.0 目标和 WPF-UI.Abstractions；项目持续发布。仅 Desktop 引用，主要使用窗口、导航和主题。先验收 Snap、键盘和高 DPI；个别控件可换原生 WPF。 |
| Phase 2 | CommunityToolkit.Mvvm 8.4.2 | MIT。ObservableObject、生成式命令和属性通知，减少手写样板代码；版本源生成器须经实际 SDK 编译确认。 |
| Phase 2–3 | WPF Drawing、WinForms NotifyIcon、System.Drawing.Icon | Windows 桌面框架随附。轻量曲线和托盘无须另加图表或托盘包。图标读取后转换、冻结并缓存，释放原生句柄。 |
| Phase 3 | System.Diagnostics.Process、Win32、Windows 快捷方式 COM、System.Text.Json | 平台随附。使用 Windows 系统目录中的命令解释器处理已确认的批处理入口。Win32 包装负责实例复核、窗口、图标、工作集和句柄释放。 |
| Phase 3 | BCL 文件 I/O、JSON Lines | BAM 单写者输出结构化事件，4 个文件各约 1 MiB，保留期 14 天。UI 最多保留 2,000 条。沿用已锁定的依赖版本。 |
| Phase 4 | ASP.NET Core 10 Kestrel | 通过 FrameworkReference 使用 SDK 配套框架，启用 MAA 回环后才启动服务。框架依赖版同时需要对应 x64 ASP.NET Core 共享运行时，现有 Windows 开发机已用本地 HTTP 夹具验证。真实 MAA 连接待验。 |
| 有证据后 | FlaUI.UIA3、SQLite | 暂缓。FlaUI（MIT）仅在有可复现且稳定的 AutomationId 时评估；有历史查询需求后评估 SQLite 引擎及 .NET 提供程序、原生二进制的单独许可证和部署成本。 |

Phase 2 已引入 WPF-UI 4.3.0、其传递依赖 WPF-UI.Abstractions 4.3.0 和 CommunityToolkit.Mvvm 8.4.2。组合方式和本机验收记录见 [Phase 2 报告](PHASE2_REPORT.md)。后续依赖按表中阶段引入。Directory.Packages.props 集中固定版本，CI 使用锁定模式还原。正式发布时清点传递依赖、图标字形和字体的许可及通知要求。第三方应用保持独立安装。BAM 的许可证由项目所有者决定。

[WPF UI 包与许可证](https://www.nuget.org/packages/WPF-UI/4.3.0) · [MVVM Toolkit](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2) · [Microsoft DI](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/10.0.15) · [xUnit](https://github.com/xunit/xunit) · [Kestrel 绑定](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0)

## 候选比较

| 方案 | 对 BAM 的实际取舍 |
|---|---|
| WPF + WPF UI | Windows 窗口互操作和后台管理直接，设计系统可以渐进实现。代价是自定义标题栏、托盘生命周期和跨 DPI 仍要逐项测试。 |
| WinUI 3 | 原生 Fluent 控件和现代标题栏有优势。Windows App SDK 的部署、窗口互操作和托盘接入增加验证面，当前没有必须采用它才能完成的功能。 |
| Avalonia | 跨平台 UI 有价值；本项目的快捷方式、进程和窗口能力仍需 Windows 专用实现。 |
| Vue + Tauri | 前端设计灵活，可复用系统 WebView2。本项目会增加 Rust/JS、桥接层与 WebView 生命周期。 |
| Vue + Electron | 界面开发成熟，需分发 Chromium 和 Node，多进程开销不利于常驻托盘目标。 |

上述是工程取舍。未在本机为各框架做性能对比，实际 CPU、工作集和冷启动以路线图的同条件测量为准。

## 发布路径

Windows x64 便携包的根目录仅放 `BAM.exe`、`使用说明.txt` 和 `THIRD-PARTY`。从 Alpha 2 开始，发布脚本生成自包含单文件。托管程序集从包内加载，所需的原生库解包到 Windows 临时目录。发布保持不裁剪，并用打包后的可执行文件验证 WPF 启动、单实例和退出。常规开发构建仍使用框架依赖目录。签名、MSIX/安装器、自动更新至 Phase 7 决策。
