# 代码结构与数据流

当前程序是 .NET Framework 4.8 / WPF 桌面应用。界面通过事件和委托连接核心服务，`MainWindow` 的部分功能已拆到多个 partial 文件，仍负责较多界面状态与发送入口协调。

| 范围 | 主要入口 | 职责 |
| --- | --- | --- |
| 主窗口与快捷指令 | `UI/View/MainWindow*.cs` | 输入历史、快捷指令编辑、流程执行、工具导航和工作区操作 |
| 主串口 | `Core/Model/Uart.cs`、`SerialSendQueue.cs` | 连接代次、收发队列、配置快照和断开处理 |
| 分屏串口 | `UI/Pages/MultiPortPage.xaml.cs` | 独立端口连接、分包、会话日志和各窗格视图 |
| 串口资源协调 | `Core/Tools/SerialPortAccessCoordinator.cs`、`SerialConnectionMonitor.cs` | 同一 COM 的占用与释放、设备断开检测 |
| 串口进程监听 | `SerialMonitorPage`、`SerialMonitorBrokerClient`、`SerialMonitorBroker/`、`serial_monitor_rs/` | 按目标位数选择助手、版本化有界通信和原生捕获的启停 |
| 日志与分析 | `Logger.cs`、`SerialTraceHub.cs`、`LogFindBar`、`LogAnalysisPage` | 文件记录、原始事件、主日志查找和时间线 |
| 配置与恢复 | `Settings.cs`、`QuickSendBackupService.cs`、`WorkspaceService.cs` | 按 COM 配置、快捷指令快照、工作区导入恢复 |
| 自动发送 | `QuickSendWorkflow.cs`、`SerialTestRunner.cs`、`CircularSendPage`、`LogReplayPage` | 顺序发送、等待回复、停止与超时处理 |
| CH347 | `Ch347BusService.cs`、`Ch347Native.cs`、`Ch347BusPage` | 独立的 I²C / SPI / JTAG 请求队列、设备身份与原生 SDK 调用 |
| 自动更新 | `GitHubReleaseUpdater.cs`、`UpdateCheckController.cs` | 版本检测、下载、独立签名校验和退出后的安装 |

## 需要保持的边界

- **连接身份**：开始操作时捕获连接代次与端口配置。后台完成和界面队列执行时都要验证身份，旧连接的回调不能写进新连接的视图或文件。
- **接收路径**：驱动读取按块交给分包队列，不能等持续数据流完全空闲后才交付，也不能在回调里无限累积。
- **分屏接收落盘**：直接串口读取后在连接锁内交给分包缓冲，保留跨读取块的字符解码。完整分包先写原会话日志，再排入界面队列；关闭、换代或更换日志文件前保存尚未达到超时的缓冲。旧界面回调可被丢弃，文件记录不得丢失或重复。
- **日志视图**：隐藏 TX、隐藏回车换行标记、查找和暂停滚动都只改变视图，不改变实际收发数据。记录保留首次脚本处理后的文本/HEX 投影，显示模式切换时立即重绘历史，不重跑脚本；连接身份与捕获时的解码配置继续保留。已启用的文件日志与有界视图历史分别维护。
- **恢复与导入**：替换快捷指令前必须成功建立快照；页内容、名称和选中页一起提交，写盘失败要回滚内存，保留原行对象供界面继续编辑。
- **快捷发送页操作**：切页、增加页和删除页的保存失败须回滚列表、名称与选中页；界面先提交后刷新，并在所有退出路径恢复自动保存开关。内容与历史示例相同不构成删除依据。
- **删除单条指令**：删除前快照必须成功创建或确认已有相同快照，失败时保留行对象、选中状态和已有撤销记录。单步内存撤销不能替代持久备份。
- **文件任务界面**：取消计算的入口负责恢复可用按钮，已脱离的旧任务不能再替新任务恢复状态；缓存页重新进入时根据当前发送任务重建控件启用状态。
- **自动发送目标**：快捷流程与日志回放绑定整次运行的串口连接，发送入口再次核对身份，禁止误转发到 MQTT/TCP 主发送目标。等待实际发送结束后才执行后续步骤，停止会取消尚未完成的请求。
- **文件工具与快捷流程**：文件或原始数据发送与快捷流程相互检查运行状态。工具捕获的串口目标保留连接身份和打开状态，实际写入前仍要拒绝正在运行的快捷流程；旧目标不能绕过检查，流程内部的发送许可也不适用于文件工具。已进入驱动的字节不能撤回。
- **时间线筛选**：更改筛选会立即取消旧查询，取消后的结果与错误不得回写新视图。HEX 匹配使用线性扫描并在扫描中检查取消和时间预算。
- **网络接收回调**：原生 TCP / UDP 同时绑定连接对象与代次，SSH / OpenSSL 数据与诊断绑定进程代次；经过界面调度后再次校验，旧连接不得向新连接的日志或脚本通道发布数据。
- **MQTT 任务**：发布和订阅提交时捕获 client 与 attempt，后台不得重新解析为另一个 broker。接收和订阅完成在界面执行前再次核对连接身份。
- **脚本发送与队列**：发送同时携带取消令牌与运行身份；等待界面捕获串口目标后须再次验证，已退休的脚本不能向新连接发送。MQTT 事件按 topic 与实际 payload 字节计量，准入后复制嵌套 payload，沿用事件数量和总字节上限。
- **文件保存**：循环发送配置先写同目录临时文件，再替换主文件，保留一份 `.bak`。读取失败时尝试该备份；未编辑的回退视图不能覆盖原文件。再次编辑保存时，损坏原文件独立保留为 `.load-error.bak`，不会覆盖上一份正常备份。
- **更新下载**：取消必须能中断正在阻塞的响应流读取；仍要求独立签名、版本与架构匹配，安装器等待程序正常退出。
- **硬件工具**：CH347 与串口使用各自的设备连接和任务队列；取消不能撤回已经送到设备的字节，失败后不能自动重放写入操作。
- **进程监听**：主界面不直接跨位数调用原生模块，按明确选择的目标架构启动匹配助手。PID 与创建时间一起验证，旧助手的状态和数据不得改变新会话；离页、取消及父进程退出都要停止捕获。未知架构、受保护进程和权限错误继续拒绝。仍在原生清理中的助手保留到清理完成，不强制卸载目标内 hook。

## 回归验证入口

`build/Test-CriticalFixes.ps1` 覆盖核心数据保护、脚本、日志、循环发送与更新限制。专用测试进一步覆盖：

- 串口：`Test-SerialReceiveIsolation`、`Test-SplitReceivePersistence`、`Test-SerialDisconnection`、`Test-SerialPortAccess`。
- 日志：`Test-TxVisibility`、`Test-LineEndings`、`Test-LogPrefix`、`Test-LogDisplayMode`、`Test-MainLogIntegration`、`Test-InlineLogSearch`。
- 数据：`Test-QuickSendBackupRetry`、`Test-QuickCommandEditing`、`Test-Workspaces`。
- 流程：`Test-QuickWorkflow`、`Test-AtWorkflow`、`Test-SendInput`、`Test-WorkflowToolGate`（文件工具与快捷流程的双向保护，含旧目标及发送中状态）。
- 更新、网络与总线：`Test-UpdateLifecycle`、`Test-SocketCallbacks`、`Test-MqttAttempts`、`Test-Ch347Bus`。
- 脚本：`Test-ScriptSessionSend` 覆盖界面捕获阻塞后的停止/替换、写入取消令牌、MQTT 嵌套负载边界及 JavaScript 字段兼容。

隔离 Release 构建应同时指定独立的 `OutputPath`、相对项目目录的 `IntermediateOutputPath` 和 `ReleaseArtifactDirectory`，避免覆盖已发布 ZIP 或读取常用输出目录中的用户配置。打包排除配置、备份、凭据、日志、工作区及其恢复目录，以及三个可编辑的用户脚本目录。默认脚本仍由内嵌资源在首次运行时生成。正式发布在签名后对最终 ZIP 运行 `Test-Release`，保持独立签名验证。

这些脚本应在对应 x64 / x86 的 Windows PowerShell 5.1 中运行；涉及 WPF 的测试使用 STA。测试使用隔离配置、模拟连接与离屏控件，不代替真实硬件的连续收发、拔插及目标板验证。用户操作说明见 [WORKFLOWS.md](WORKFLOWS.md)，CH347 接线与能力范围见 [CH347.md](CH347.md)。
