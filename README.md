# llcom plus

[English README](README_EN.md)

<p align="center">
    <br>
    <img src="./llcom plus/Assets/AppIcon.ico" width="150"/>
    <br>
</p>

<p align="center">
    <img alt="license" src="https://img.shields.io/github/license/JIANXINXIAOQINGMING/llcom-plus?color=ff69b4&labelColor=333">
    <img alt="release" src="https://img.shields.io/github/v/release/JIANXINXIAOQINGMING/llcom-plus?label=release&color=ff69b4&labelColor=333">
    <img alt="top language" src="https://img.shields.io/github/languages/top/JIANXINXIAOQINGMING/llcom-plus?color=ff69b4&labelColor=333">
    <img alt="inspired by Codex" src="https://img.shields.io/badge/inspired%20by-Codex-ff69b4?logo=openai&logoColor=white&labelColor=333">
    <img alt="inspired by llcom" src="https://img.shields.io/badge/inspired%20by-LLCOM-ff69b4?labelColor=333">
</p>

## 介绍

llcom plus 是在原 [llcom](https://github.com/chenxuuu/llcom) 基础上继续扩展的串口调试工具，保留了原项目“能跑 JavaScript 脚本”的高自由度，同时加入了多串口分屏、快捷发送补全、文件发送、TLS/OpenSSL、日志回放、HTTP/MQTT/socket 等开发调试中常用的工具页。

## 下载

- 正式版本：[GitHub Releases](https://github.com/JIANXINXIAOQINGMING/llcom-plus/releases/latest)

## 主要功能

### 串口调试

- 支持常见串口打开、关闭、发送、接收、自动重连和终端模式。
- 支持文本/HEX 发送与显示，支持自定义字符编码。
- TX/RX 日志颜色区分，支持原始数据、实际发送数据、串口日志和脚本日志。
- 串口配置按端口保存，波特率、RTS、DTR、HEX 等常用选项可以跟随不同 COM 口自动切换。
- 发送前可通过 JavaScript 脚本处理数据，快捷发送区域同样适用。

### 主界面分屏

- 在“更多设置”中可配置主界面分屏数量，支持 1 到 4 个串口窗口。
- 分屏模式复用原有串口刷新、串口选择、打开/关闭、波特率和发送框。
- 点击某个分屏窗口会自动切换当前目标，底部发送框、RTS、DTR、HEX 等选项跟随目标窗口。
- 分屏日志按端口/窗口保存，TX/RX 颜色保持与普通串口日志一致。
- 切换分屏数量时尽量保留未变化窗口的串口连接，避免不必要的关闭重开。

### 快捷发送

- 快捷发送支持多页、重命名、导入当前页、导入全部、导出当前页和导出全部。
- 发送框支持从快捷发送内容中快速补全，并显示快捷发送按钮备注。
- HEX 快捷发送项不会参与补全。
- 每条快捷发送可单独勾选“排除”，让该指令不进入发送框补全候选。
- 快捷发送内容可绑定接收处理脚本和脚本参数，方便做一键交互流程。

### 文件发送与循环发送

- 数据计算/文件发送工具支持手动输入或选择文件作为数据源。
- 文件发送支持进度显示、暂停、继续发送和重新发送。
- 循环发送工具可以从快捷发送导入命令，按指定次数和间隔循环发送。

### 脚本能力

- 内置 Jint JavaScript 运行环境，可独立运行测试脚本。
- 脚本支持串口收发回调、定时器和通用消息通道。
- 发送处理脚本、接收处理脚本、独立运行脚本可覆盖大部分自动化串口调试场景。
- API 文档见 [JavaScriptApi.md](JavaScriptApi.md)。

### 网络与协议工具

- socket 客户端支持 TCP、UDP、TLS、DTLS、DNS、Ping、NTP 等调试场景。
- DNS 查询可选择全部地址、IPv4(A) 或 IPv6(AAAA)。
- TLS/DTLS 支持 OpenSSL 参数、证书、目标主机、吊销检查和加密套件配置。
- TLS 日志默认展示 ClientHello、ServerHello 等握手摘要，避免原始 TLS 字节刷屏。
- MQTT 工具支持常用 MQTT 客户端测试。
- HTTP 工具支持独立窗口调试请求、Header、Body 和 TLS/OpenSSL 相关配置。

### 辅助工具

- 日志回放：可导入 llcom plus 日志，按发送/接收顺序做自动化回放和响应匹配。
- 串口监听：可监听其他软件的串口通信数据。
- 编码转换和乱码修复：用于常见字符集转换与文本恢复。
- 曲线工具：脚本可将数据推送到曲线页显示。
- WinUSB 工具：用于基础 USB/WinUSB 调试。

## JavaScript 示例

### 发送前处理数据

1. 结尾追加换行回车：

```javascript
return concatBytes(uartData, "\r\n");
```

2. 将输入内容按 HEX 发送：

```javascript
return hexToBytes(bytesToString(uartData));
```

此脚本可将形如 `30313233` 的输入处理为实际字节 `0123`。

3. 将逗号分隔文本转成 JSON：

```javascript
var items = bytesToString(uartData).split(",");
return stringToBytes(JSON.stringify({
    key1: items[0],
    key2: items[1],
    key3: items[2],
}));
```

此脚本可将 `a,b,c` 处理为 `{"key1":"a","key2":"b","key3":"c"}`。

### 独立脚本处理串口收发

```javascript
apiSetCb("uart", function(data) {
    apiPrintLog("recv: " + bytesToHex(data));
    apiSend("uart", stringToBytes("ok!\r\n"), null);
});

apiStartTimer(1, 1000);

function onTrigger(id, type, data) {
    if (type === "timer") {
        apiPrintLog("timer " + id);
        apiStartTimer(id, 1000);
    }
}
```



本项目采用 Apache 2.0 协议。如有借用、分发或二次开发，请保留指向本项目和原始项目的链接。

## 致谢与引用项目

- [chenxuuu/llcom](https://github.com/chenxuuu/llcom)：本项目基于原 llcom 开源项目继续开发，保留并扩展串口调试、脚本接口和工具页能力。
- [OpenSSL](https://www.openssl.org/)：用于 TLS/DTLS 连接、HTTPS 辅助请求和握手信息解析。
- [Jint](https://github.com/sebastienros/jint)：提供 JavaScript 脚本运行能力。
