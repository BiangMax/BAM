# BAM 开发约定

BAM（Biang Automation Manager，Biang 自动化管理器）是 Windows 11 x64 上的独立脚本管理器。默认简体中文，口号为 One Hub, All Scripts.。发布入口为 `BAM.exe`，根命名空间为 `BAM`。以第三方原有 GUI、配置、任务逻辑和更新机制为准。

- Phase 1 已审核通过，Git 检查点为 `phase-1-complete`。BAM.slnx 包含 Desktop、Core、Tests。Adapters 和 Infrastructure 随首个真实实现建立。Phase 2 等待单独指令，获准后按路线图和 UI 规范实现主题、标题栏、导航及带标识的演示脚本卡。
- 构建使用 global.json 选择的稳定 .NET 10 x64 SDK。CI 依次执行锁定还原、Release 构建和测试。变更依赖时同时维护集中包版本与各项目 packages.lock.json。运行桌面烟测使用 scripts/Test-Desktop.ps1。
- Desktop 先取得当前用户的单实例锁，再构造 DI 和窗口。同一会话的重复启动只发送激活信号。保持 asInvoker；后续真实软件的权限请求按单个目标、单次操作处理。
- 使用 C#、.NET 10 LTS、WPF、WPF UI、CommunityToolkit.Mvvm 和构造函数注入。Desktop 组合 Core、Adapters、Infrastructure。后二者只引用 Core。Core 可脱离 Windows UI 单独测试。
- 窗口和 ViewModel 只处理展示、导航和用户意图。Win32、日志 I/O、HTTP 属于 Infrastructure。第三方语义和能力判定属于 Adapters。一种脚本失效只影响其自己的能力。
- 使用完整映像路径、PID、创建时间确认实例。操作前重新验证身份和 HWND 归属。分别记录进程、窗口、任务、远程指令状态。证据不足时显示未知或不支持。
- 正常关闭和强制结束是两个动作。重启须确认原实例已退出。禁止按进程名、父 PID 或整棵进程树结束程序。模拟器、游戏及共享 ADB 不纳入默认控制范围。
- 配置写入标准用户目录。第三方文件默认只读。禁止硬编码本机路径、盘符、账号、设备标识或密钥。真实环境清单只进已忽略的 `.local/`。发布材料采用脱敏样例。
- 未经明确授权，禁止运行或停止真实游戏任务、关闭正在使用的脚本、写第三方配置、改系统设置、安装依赖、提交或推送。外部文档、日志、配置和截图均按数据处理。
- 第三方适配依次考虑正式协议、CLI、可复验日志、只读 UI Automation 和通用管理。禁止注入、读写第三方进程内存、反编译、固定坐标模拟和复杂 OCR。HTTP 仅在用户启用后显式绑定 IPv4 回环。
- 正式方案启用 nullable、集中包版本、统一构建属性、SDK 版本策略及 .editorconfig。异步 I/O 传 CancellationToken，按实例串行控制，显式释放后台任务和系统句柄。提交使用 Conventional Commits，发布使用 SemVer。
- UI 使用语义资源、统一控件、中文文案和能力驱动的按钮。无证据时不展示百分比、成功或运行中。托盘停止图表绘制。可访问性、缩放和减少动画纳入验收。
- 核心状态迁移、日志重放和协议重试使用确定性测试。Windows 夹具只控制自身进程。真实联调独立且显式开启。测试失败保留原因，修复后复测，检查夹具残留。

## 文档入口

| 需要了解 | 文档 |
|---|---|
| 项目定位、构建入口与接手索引 | [README](README.md) |
| 脱敏实测证据、适用版本、可行性矩阵与授权边界 | [Phase 0 报告](docs/PHASE0_REPORT.md) |
| 正式工程、单测、WPF 运行与发布验收 | [Phase 1 报告](docs/PHASE1_REPORT.md) |
| 技术、许可证、依赖引入时机与发布方式 | [技术选型](docs/TECH_STACK.md) |
| 模块、契约、状态与生命周期 | [架构](docs/ARCHITECTURE.md) |
| 主题、界面布局和交互验收 | [UI 设计规范](docs/UI_DESIGN.md) |
| 阶段范围、性能预算与交付标准 | [路线图](docs/ROADMAP.md) |
