# llcom plus
<!-- ALL-CONTRIBUTORS-BADGE:START - Do not remove or modify this section -->
[![All Contributors](https://img.shields.io/badge/all_contributors-6-orange.svg?style=flat-square)](#contributors-)
<!-- ALL-CONTRIBUTORS-BADGE:END -->

[English readme click here](/README_EN.md)

<p align="center">
    <br>
    <img src="./llcom plus/Assets/AppIcon.ico" width="150"/>
    <br>
</p>

<p align="center">
    <img alt="GitHub" src="https://img.shields.io/github/license/chenxuuu/llcom">
    <img alt="GitHub release (latest by date)" src="https://img.shields.io/github/v/release/chenxuuu/llcom">
    <img alt="GitHub top language" src="https://img.shields.io/github/languages/top/chenxuuu/llcom">
    <img alt="code-size" src="https://img.shields.io/github/languages/code-size/chenxuuu/llcom.svg">
</p>

可运行JavaScript脚本的高自由度串口调试工具。使用交流群：`931546484`

## 下载

从微软商店安装：

<a href='//www.microsoft.com/store/apps/9PMPB0233S0S?cid=storebadge&ocid=badge'><img src='https://developer.microsoft.com/store/badges/images/Chinese_Simplified_get-it-from-MS.png' alt='Chinese badge' width='160'/></a>

exe便携版：[国内用户点我下载](https://llcom.papapoi.com/llcom.zip)

CI快照版：[Github Action](https://nightly.link/chenxuuu/llcom/workflows/build/master/llcom_x64)

所有正式版本：[GitHub Releases](https://github.com/chenxuuu/llcom/releases/latest)

## 功能列表

- 其他串口调试功能具有的功能
- 收发日志清晰明了，可同时显示HEX值与实际字符串
- 自动保存串口与JavaScript脚本日志，并附带时间
- 串口断开后，如果再次连接，会自动重连
- 发送的数据可被用户自定义的JavaScript脚本提前处理
- 右侧快捷发送栏，快捷发送条目数量不限制
- 右侧快捷发送栏，支持10页数据，互相独立
- 可独立运行JavaScript脚本，并拥有定时器和通用消息通道能力
- 可选文字编码格式
- 终端功能，直接敲键盘发送数据（包含ctrl+字母键）
- 可单独隐藏发送数据
- 集成TCP、UDP、SSL socket客户端功能，并且支持IPV6
- 集成各种编码互转功能
- 集成乱码恢复功能
- 集成mqtt测试功能
- 集成串口监听功能，可监听其他软件的串口通信数据

![screen](/image/screen.png)
![screen3](/image/screen3.png)
![screen2](/image/screen2.jpg)

## 特色功能示范

### 使用JavaScript脚本提前处理待发送的数据

1. 结尾加上换行回车

```javascript
return concatBytes(uartData, "\r\n");
```

2. 发送16进制数据

```javascript
return hexToBytes(bytesToString(uartData));
```

此脚本可将形如`30313233`发送数据，处理为`0123`的结果

3. 更多玩法等你发现

```javascript
var items = bytesToString(uartData).split(",");
return stringToBytes(JSON.stringify({
    key1: items[0],
    key2: items[1],
    key3: items[2],
}));
```

此脚本可将形如`a,b,c`发送数据，处理为`{"key1":"a","key2":"b","key3":"c"}`的结果

**此处理脚本，同样对右侧快捷发送区域有效。**

### 独立的JavaScript脚本自动处理串口收发

右侧的脚本调试区域，可直接运行你写的串口测试脚本，如：

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

使用此功能，你可以完成大部分的自动化串口调试操作。

## 接口文档

接口文档可以在[JavaScriptApi.md](JavaScriptApi.md)查看

## 已知问题与待添加的功能（请大家反馈，谢谢！）

- [x] ~~bug：某些条件下（比如Air720重启），COM口消失后不会被释放，导致无法再次开启该COM口，只能重启软件（[.net 框架的bug，微软的人在看了](https://github.com/dotnet/corefx/issues/39464)）~~（已解决 #2f26e68）

## 开源

如果各位大佬不觉得麻烦的话，欢迎对本项目进行pr或直接重构。

本项目在前期只是为了实现功能，代码相当零散，所以不太适合阅读我的源码进行学习，等我有空的时候会重构代码。

本项目采用Apache 2.0协议，如有借用，请保留指向该项目的链接。

## Contributors ✨

Thanks goes to these wonderful people ([emoji key](https://allcontributors.org/docs/en/emoji-key)):

<!-- ALL-CONTRIBUTORS-LIST:START - Do not remove or modify this section -->
<!-- prettier-ignore-start -->
<!-- markdownlint-disable -->

<table>
  <tr>
    <td align="center"><a href="https://github.com/whc2001"><img src="https://avatars2.githubusercontent.com/u/16266909?v=4?s=100" width="100px;" alt=""/><br /><sub><b>whc2001</b></sub></a><br /><a href="https://github.com/chenxuuu/llcom/commits?author=whc2001" title="Code">💻</a> <a href="https://github.com/chenxuuu/llcom/issues?q=author%3Awhc2001" title="Bug reports">🐛</a></td>
    <td align="center"><a href="https://www.chenxublog.com/"><img src="https://avatars3.githubusercontent.com/u/10357394?v=4?s=100" width="100px;" alt=""/><br /><sub><b>chenxuuu</b></sub></a><br /><a href="#projectManagement-chenxuuu" title="Project Management">📆</a></td>
    <td align="center"><a href="https://github.com/neomissing"><img src="https://avatars0.githubusercontent.com/u/22003930?v=4?s=100" width="100px;" alt=""/><br /><sub><b>neomissing</b></sub></a><br /><a href="#ideas-neomissing" title="Ideas, Planning, & Feedback">🤔</a></td>
    <td align="center"><a href="https://github.com/RYLF"><img src="https://avatars3.githubusercontent.com/u/28991981?v=4?s=100" width="100px;" alt=""/><br /><sub><b>RuoYun</b></sub></a><br /><a href="https://github.com/chenxuuu/llcom/issues?q=author%3ARYLF" title="Bug reports">🐛</a></td>
    <td align="center"><a href="http://www.diycms.com"><img src="https://avatars.githubusercontent.com/u/13432299?v=4?s=100" width="100px;" alt=""/><br /><sub><b>王龙</b></sub></a><br /><a href="#ideas-wanglong126" title="Ideas, Planning, & Feedback">🤔</a> <a href="https://github.com/chenxuuu/llcom/issues?q=author%3Awanglong126" title="Bug reports">🐛</a> <a href="https://github.com/chenxuuu/llcom/commits?author=wanglong126" title="Code">💻</a></td>
    <td align="center"><a href="https://github.com/linhongz"><img src="https://avatars.githubusercontent.com/u/49241612?v=4?s=100" width="100px;" alt=""/><br /><sub><b>linhongz</b></sub></a><br /><a href="#ideas-linhongz" title="Ideas, Planning, & Feedback">🤔</a> <a href="https://github.com/chenxuuu/llcom/issues?q=author%3Alinhongz" title="Bug reports">🐛</a></td>
  </tr>
</table>

<!-- markdownlint-restore -->
<!-- prettier-ignore-end -->

<!-- ALL-CONTRIBUTORS-LIST:END -->

This project follows the [all-contributors](htts://github.com/all-contributors/all-contributors) specification. Contributions of any kind welcome!


## 特别感谢

[![icon-resharper](/image/icon-resharper.svg)](https://www.jetbrains.com/?from=llcom plus)
