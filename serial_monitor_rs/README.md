# 串口进程监听 / Serial process monitor

用于观察指定程序对指定 COM 的 Win32 串口读写。它与 llcom plus 自己打开串口进行收发的功能独立；进程列表也不代表这些进程正在使用当前选择的 COM。

## 位数与组件

主界面按目标程序的位数选择监听助手，x64 主程序可以监听 x86 目标，x86 主程序在 64 位 Windows 上也可以选择 x64 目标。助手和注入到目标的模块始终与目标保持相同位数，不直接跨位数加载 DLL。

```text
llcom plus.exe (x86 or x64)
  └─ inherited stdin/stdout, bounded versioned frames
       └─ Monitor/{target-architecture}/SerialMonitorBroker.exe
            └─ serial_monitor.dll (same architecture)
                 └─ serial_monitor_hook DLL embedded in host
                      └─ selected process, selected COM only
```

`serial_monitor_hook` 的 x64 实现沿用固定版本 retour 0.3.1；x86 使用固定版本 MinHook 1.3.4 的指令解码、跳板和线程上下文处理。第三方源码、许可证及本地严格错误处理补丁的来源见 `vendor/minhook/UPSTREAM.md`。不使用固定截取五字节的手工补丁。

不支持 ARM64 目标或无法可靠判断位数的目标。权限不足、受系统保护或不允许注入的进程会拒绝启动，不自动提权或绕过保护。目标须由用户明确选择，PID 与创建时间一起绑定，进程重启后需重新选择。

## 数据与生命周期

- ABI 版本为 **3**。固定头部 64 字节，加最多 8192 字节负载，总包长 **8256 字节**，采用小端、`Pack=1`。
- 线上的句柄值使用 `u64`，不传递进程内指针或托管回调地址。包包含连接代次、序号、COM、方向、分片信息和丢包计数。
- 原生 hook 与助手回调均使用有界队列；慢界面或慢管道不能无限积压，也不能阻塞目标程序的串口回调。丢包计数随下一包传回界面。
- `DllMain` 不连接管道、不安装 hook；通过显式初始化握手启动。
- 停止先阻止新回调并清空托管队列，再请求原生捕获停止。旧助手和旧连接的回调不能进入新会话。
- 已安装的 hook 模块保留到目标退出，不强制卸载正在使用的代码。原生停用仍待清理时，助手保留到清理完成或目标退出；主界面可以继续操作。
- 离开监听页、关闭软件或父进程退出都会关闭本次监听。只管理本工具启动的助手，不结束被监听程序。

这是一种运行时调试机制，不能保证每一种串口库、内核驱动、受保护软件或已经打开的句柄都能被观察到；应在目标测试程序中核对实际数据。它不替代硬件串口分析仪。

## 构建

使用固定 Rust **1.82.0**、MSVC 工具链，以及 `x86_64-pc-windows-msvc` 和 `i686-pc-windows-msvc` 两个目标：

```powershell
.\serial_monitor_rs\build.ps1 -Config Release -Arch All -RustToolchain 1.82.0
```

脚本先构建 hook，再将匹配架构的 hook 嵌入 host DLL，最后更新 `llcom plus/Runtime/Native/{x86|x64}/serial_monitor.dll` 与来源校验记录。脚本不自动安装工具链。

主项目构建时会生成两种位数的监听助手，分别与对应的原生 DLL 一起放到输出目录的 `Monitor/x86` 和 `Monitor/x64`。Release 构建使用 `RequireNativeSourceBuild=true` 校验两份原生组件的来源记录，版本包同时包含两套助手及 MinHook 许可证。

## 验证范围

默认回归验证协议大小与边界、位数选择、过期回调、启动取消、停止和助手退出，以及原生本地合成函数的调用约定、跳板与并发切换。测试不向用户正在运行的软件注入，也不打开真实串口。

实际程序的读取方式、同步或异步 I/O、权限、启停及串口数据仍需在专门测试环境验证。不要用系统服务作为首次联调目标。

参考：[MinHook 官方项目](https://github.com/TsudaKageyu/minhook)、[Microsoft Detours 的位数说明](https://github.com/microsoft/Detours/wiki/Detouring-32-bit-and-64-bit-Processes)。
