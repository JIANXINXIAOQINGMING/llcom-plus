# llcom plus

[中文 README](README.md)

<p align="center">
    <br>
    <img src="./llcom plus/Resources/Assets/AppIcon.ico" width="150"/>
    <br>
</p>

<p align="center">
    <img alt="license" src="https://img.shields.io/github/license/JIANXINXIAOQINGMING/llcom-plus?color=ff69b4&labelColor=333">
    <img alt="release" src="https://img.shields.io/github/v/release/JIANXINXIAOQINGMING/llcom-plus?label=release&color=ff69b4&labelColor=333">
    <img alt="top language" src="https://img.shields.io/github/languages/top/JIANXINXIAOQINGMING/llcom-plus?color=ff69b4&labelColor=333">
    <img alt="inspired by Codex" src="https://img.shields.io/badge/inspired%20by-Codex-ff69b4?logo=openai&logoColor=white&labelColor=333">
    <img alt="inspired by llcom" src="https://img.shields.io/badge/inspired%20by-LLCOM-ff69b4?labelColor=333">
</p>

## Introduction

llcom plus is a serial debugging tool extended from the original [llcom](https://github.com/chenxuuu/llcom) project. It keeps the original project's flexible JavaScript scripting capability and adds common development/debugging tools such as multi-port split view, quick-send suggestions, file sending, TLS/OpenSSL, log replay, HTTP/MQTT/socket utilities, and more.

## Download

- Stable builds: [GitHub Releases](https://github.com/JIANXINXIAOQINGMING/llcom-plus/releases/latest)

## Main Features

### Serial Debugging

- Supports common serial-port operations such as open, close, send, receive, auto-reconnect, and terminal mode.
- Supports text/HEX sending and display, with configurable text encoding.
- TX/RX logs use different colors and can show raw data, actual sent data, serial logs, and script logs.
- Serial configuration is saved per port, so baud rate, RTS, DTR, HEX, and other common options can follow different COM ports automatically.
- Data can be processed by JavaScript before sending, and the same flow also works for the quick-send area.

### Main Window Split View

- The split-view count can be configured in More Settings, supporting 1 to 4 serial panes.
- Split mode reuses the existing serial refresh, serial selection, open/close, baud-rate, and send-box controls.
- Clicking a pane automatically switches the current target; the bottom send box, RTS, DTR, HEX, and related options follow the target pane.
- Split logs are saved per port/pane, while TX/RX colors remain consistent with normal serial logs.
- When changing the split count, unchanged panes try to keep their serial connections open to avoid unnecessary close/reopen operations.

### Quick Send

- Quick send supports multiple pages, page renaming, current-page import, all-page import, current-page export, and all-page export.
- The send box can show quick suggestions from quick-send content and display each row's button remark.
- HEX quick-send rows do not participate in suggestions.
- Each quick-send row can be individually marked as excluded, so that command will not appear in send-box suggestions.
- Quick-send content can bind receive-processing scripts and script parameters for one-click interaction flows.

### File Sending and Loop Sending

- The data calculation/file sending tool supports manual input or a selected file as the data source.
- File sending supports progress display, pause, resume, and resend.
- Loop sending can import commands from quick send and send them repeatedly by configured count and interval.

### Scripting

- Built-in Jint JavaScript runtime for independent test scripts.
- Scripts support serial send/receive callbacks, timers, and common message channels.
- Send-processing scripts, receive-processing scripts, and independent scripts can cover most automated serial debugging scenarios.
- API documentation is available in [JavaScriptApi.md](JavaScriptApi.md).

### Network and Protocol Tools

- The socket client supports TCP, UDP, TLS, DTLS, DNS, Ping, NTP, and other debugging scenarios.
- DNS lookup can query all addresses, IPv4(A), or IPv6(AAAA).
- TLS/DTLS supports OpenSSL options, certificates, target host, revocation checking, and cipher-suite configuration.
- TLS logs show handshake summaries such as ClientHello and ServerHello by default, avoiding raw TLS byte flooding.
- MQTT tools support common MQTT client testing.
- The HTTP tool provides an independent request window with Header, Body, and TLS/OpenSSL related settings.

### Utility Tools

- Log replay: import llcom plus logs and run automated replay and response matching by send/receive order.
- Serial monitor: monitor serial communication data from other software.
- Encoding conversion and garbled-text recovery for common character-set conversion and text recovery.
- Plot tool: scripts can push data to the plot page for display.
- WinUSB tool: basic USB/WinUSB debugging.

## JavaScript Examples

### Process Data Before Sending

1. Append CRLF:

```javascript
return concatBytes(uartData, "\r\n");
```

2. Send input as HEX:

```javascript
return hexToBytes(bytesToString(uartData));
```

This script can convert input such as `30313233` into the actual bytes for `0123`.

3. Convert comma-separated text to JSON:

```javascript
var items = bytesToString(uartData).split(",");
return stringToBytes(JSON.stringify({
    key1: items[0],
    key2: items[1],
    key3: items[2],
}));
```

This script can convert `a,b,c` into `{"key1":"a","key2":"b","key3":"c"}`.

### Independent Script for Serial Send/Receive

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

This project is licensed under Apache 2.0. If you reuse, distribute, or modify it, please keep links to this project and the original upstream project.

## Acknowledgements and Referenced Projects

- [chenxuuu/llcom](https://github.com/chenxuuu/llcom): this project continues development from the original llcom open-source project and keeps/extends its serial debugging, scripting API, and tool-page capabilities.
- [OpenSSL](https://www.openssl.org/): used for TLS/DTLS connections, HTTPS helper requests, and handshake message parsing.
- [Jint](https://github.com/sebastienros/jint): provides JavaScript scripting capability.
