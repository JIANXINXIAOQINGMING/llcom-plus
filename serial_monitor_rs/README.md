# serial_monitor_rs

Rust rewrite of the closed-source `serial_monitor.dll` used by [llcom](https://github.com/chenxuuu/llcom).

Maintains full API compatibility with the original DLL so it drops in as a replacement in `llcom plus/Native/x64/` or `llcom plus/Native/x86/`.

---

## Architecture

```
llcom plus.exe
  │
  │  MonitorComm(pid, port, callback)
  ▼
serial_monitor.dll  (this crate)
  │  creates named pipe \\.\pipe\llcom_plus_smv2_<our_pid>
  │  writes pipe name to shared memory Local\llcom_plus_smv2_session
  │  extracts serial_monitor_hook.dll to %TEMP%\llcom_plus_smv2\
  │  injects hook DLL into target process via CreateRemoteThread+LoadLibraryW
  │  worker thread polls pipe and calls C# callback for each Udata packet
  ▼
serial_monitor_hook.dll  (embedded inside serial_monitor.dll)
  │  DllMain: reads pipe name from shared memory, connects as pipe client
  │  installs inline hooks on kernel32: CreateFileW/A, ReadFile, WriteFile, CloseHandle
  │  COM port detection: \\.\COMx or \Device\Serialx
  │  for each read/write on a COM handle → writes Udata to the pipe
```

### Udata wire format (Pack=1, matches C# `Udata` struct)

| Field | Type | Description |
|-------|------|-------------|
| `com_port` | `u8` | COM port number |
| `comm_state` | `u8` | 2=Disconnect, 3=Receive, 4=Send |
| `file_handle` | `i32` | Windows HANDLE (cast to 32-bit) |
| `data_size` | `i32` | Valid bytes in `data` |
| `data` | `[u8; 8192]` | Payload |

Total: **8202 bytes**.

---

## Building

### Prerequisites

- Rust toolchain (nightly or stable ≥ 1.75)
- MSVC target: `rustup target add x86_64-pc-windows-msvc`
- Visual Studio Build Tools (MSVC linker)

### Build and copy DLL

```powershell
cd serial_monitor_rs
.\build.ps1
```

This builds `serial_monitor.dll` (x64 release) and copies it to `llcom plus/Native/x64/serial_monitor.dll`.

### Manual build

```powershell
cd serial_monitor_rs
cargo build --release -p serial_monitor --target x86_64-pc-windows-msvc
# Output: target/x86_64-pc-windows-msvc/release/serial_monitor.dll
```

---

## C# Interface (unchanged from original DLL)

```csharp
public delegate int CallbackDelegate(IntPtr param);

[DllImport("serial_monitor.dll")]
static extern bool UnMonitorComm();

[DllImport("serial_monitor.dll")]
static extern bool MonitorComm(uint Pid, uint ComIndex, CallbackDelegate lpCallFunc);

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Udata {
    public byte ComPort;
    public byte CommState;   // 2=Disconnect, 3=Receive, 4=Send
    public int FileHandle;
    public int DataSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8192)]
    public byte[] Data;
}
```

---

## Crate structure

| Crate | Purpose |
|-------|---------|
| `serial_monitor` | Public DLL; manages injection and pipe server |
| `serial_monitor_hook` | Injected hook DLL; intercepts COM I/O in target process |

---

---

# serial_monitor_rs（中文说明）

用 Rust 重写的 `serial_monitor.dll`，替换 [llcom](https://github.com/chenxuuu/llcom) 原先使用的同名闭源 DLL。

保持与原 DLL 完全相同的导出接口，可直接覆盖 `llcom plus/Native/x64/` 或 `llcom plus/Native/x86/` 目录下的旧文件。

---

## 整体架构

```
llcom plus.exe
  │
  │  MonitorComm(pid, port, callback)
  ▼
serial_monitor.dll  （本项目）
  │  创建命名管道 \\.\pipe\llcom_plus_smv2_<自身pid>
  │  将管道名写入共享内存 Local\llcom_plus_smv2_session
  │  将 serial_monitor_hook.dll 解压到 %TEMP%\llcom_plus_smv2\
  │  通过 CreateRemoteThread+LoadLibraryW 将 Hook DLL 注入目标进程
  │  工作线程轮询管道，每收到一个 Udata 数据包就调用 C# 回调
  ▼
serial_monitor_hook.dll  （内嵌于 serial_monitor.dll 中）
  │  DllMain：从共享内存读取管道名，以客户端身份连接管道
  │  对 kernel32 进行内联 Hook：CreateFileW/A、ReadFile、WriteFile、CloseHandle
  │  串口检测：路径含 \\.\COMx 或 \Device\Serialx
  │  每次对串口句柄执行读/写 → 向管道写入一个 Udata 数据包
```

### Udata 数据格式（Pack=1，与 C# 端 `Udata` 结构体一致）

| 字段 | 类型 | 说明 |
|------|------|------|
| `com_port` | `u8` | COM 口编号 |
| `comm_state` | `u8` | 2=断开连接，3=接收数据，4=发送数据 |
| `file_handle` | `i32` | Windows HANDLE（截断为 32 位） |
| `data_size` | `i32` | `data` 中的有效字节数 |
| `data` | `[u8; 8192]` | 有效载荷 |

合计：**8202 字节**。

---

## 编译方法

### 前置条件

- Rust 工具链（stable ≥ 1.75 或 nightly）
- MSVC 目标：`rustup target add x86_64-pc-windows-msvc`
- Visual Studio Build Tools（提供 MSVC 链接器）

### 一键编译并复制 DLL

```powershell
cd serial_monitor_rs
.\build.ps1
```

脚本会依次编译 Hook DLL 和主 DLL（x64 release），并将 `serial_monitor.dll` 复制到 `llcom plus/Native/x64/`。

### 手动编译

```powershell
cd serial_monitor_rs
# 必须先编译 Hook DLL
cargo build --release -p serial_monitor_hook --target x86_64-pc-windows-msvc
# 再编译主 DLL（build.rs 会自动嵌入上一步产出的 Hook DLL）
cargo build --release -p serial_monitor --target x86_64-pc-windows-msvc
# 输出：target/x86_64-pc-windows-msvc/release/serial_monitor.dll
```

> **注意**：不能在同一个 `cargo build` 命令中同时编译两个 crate，因为 `serial_monitor` 的 `build.rs` 需要先找到已编译好的 `serial_monitor_hook.dll` 才能将其嵌入。

---

## C# 接口（与原 DLL 保持一致）

```csharp
public delegate int CallbackDelegate(IntPtr param);

[DllImport("serial_monitor.dll")]
static extern bool UnMonitorComm();

[DllImport("serial_monitor.dll")]
static extern bool MonitorComm(uint Pid, uint ComIndex, CallbackDelegate lpCallFunc);

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Udata {
    public byte ComPort;
    public byte CommState;   // 2=断开连接  3=接收  4=发送
    public int FileHandle;
    public int DataSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8192)]
    public byte[] Data;
}
```

---

## 项目结构

| Crate | 说明 |
|-------|------|
| `serial_monitor` | 主 DLL；负责进程注入与命名管道服务端 |
| `serial_monitor_hook` | Hook DLL（注入目标进程）；拦截串口 I/O |

`serial_monitor_hook.dll` is **embedded** inside `serial_monitor.dll` via `include_bytes!` (see `serial_monitor/build.rs`). No separate deployment needed.

---

## Limitations

- **x64 only**: The hook DLL must match the bitness of the target process. If the target serial application is 32-bit, an x86 build is needed (not currently provided).
- **Synchronous I/O only**: Overlapped (async) `ReadFile`/`WriteFile` calls in the target process are not captured.
- **Windows only** (`#[cfg(windows)]` implied by the windows crate).
- **No FTDI/USB-serial drivers that bypass kernel32**: If an application uses a custom I/O path that doesn't call kernel32 `ReadFile`/`WriteFile`, those calls won't be captured.

---

## How llcom plus integrates it

`llcom plus/llcom plus.csproj` copies the platform-specific DLL from `llcom plus/Native/x64/` or `llcom plus/Native/x86/` into the application output directory during build.

After building this Rust project, rebuild the C# project so the selected native DLL is copied next to `llcom plus.exe`.
