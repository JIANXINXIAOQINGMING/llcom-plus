# llcom plus

![icon](/llcom plus/Assets/AppIcon.ico)

[![Build status](https://ci.appveyor.com/api/projects/status/telji5j8r0v5001c?svg=true)](https://ci.appveyor.com/project/chenxuuu/llcom)
[![MIT](https://img.shields.io/static/v1.svg?label=license&message=Apache+2&color=blue)](https://github.com/chenxuuu/llcom/blob/master/LICENSE)
[![code-size](https://img.shields.io/github/languages/code-size/chenxuuu/llcom.svg)](https://github.com/chenxuuu/llcom/archive/master.zip)

A serial port debugger tool with JavaScript scripting.

> this tool is only Chinese and English now, you can help me to translate, thanks!

## Download

Get it from Microsoft store:

<a href='//www.microsoft.com/store/apps/9PMPB0233S0S?cid=storebadge&ocid=badge'><img src='https://developer.microsoft.com/store/badges/images/English_get-it-from-MS.png' alt='English badge' width='160'/></a>

Portable exe version: [GitHub](https://github.com/chenxuuu/llcom/releases/latest)

Appveyor snapshot version: [Appveyor Artifacts](https://ci.appveyor.com/project/chenxuuu/llcom/build/artifacts)

## Functions

- Basic functions of serial port debugger tools.
- The log is clear with two colors, display both HEX values and strings at same time.
- Auto save serial and JavaScript script logs, with time stamp.
- Auto reconnect serial port after disconnected.
- Data you want to send can be processed with your own JavaScript scripts.
- Quick send bar on the right.
- JavaScript scripts can be run independently with timers and common message channels.
- TCP, UDP, SSL socket client. Also support IPV6.
- mqtt client test
- Encoding converter
- Garbled code fix
- monitor serial data which send or received by other software

![screenEN](/image/screenEN.png)
![screen3](/image/screen3EN.png)
![screen2](/image/screen2.jpg)

## features' exemples

### Use JavaScript script process data you want to send

1. end with "\r\n"

```javascript
return concatBytes(uartData, "\r\n");
```

2. send HEX values

```javascript
return hexToBytes(bytesToString(uartData));
```

this script can change `30313233` to `0123`.

3. another script example

```javascript
var items = bytesToString(uartData).split(",");
return stringToBytes(JSON.stringify({
    key1: items[0],
    key2: items[1],
    key3: items[2],
}));
```

this script can change `a,b,c` to `{"key1":"a","key2":"b","key3":"c"}`.

**these scripts also work with Quick send bar**

### independent script auto process uart sand and receive

you can run your own JavaScript script on the right, such as:

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

you can make your debug automatic

## api document (in Chinese)

you can read [JavaScriptApi.md](JavaScriptApi.md)

## Known bugs and functions to be added

- [x] ~~bug: SerialPort The Requested Resource is in Use([.net's bug](https://github.com/dotnet/corefx/issues/39464))~~(fixed #2f26e68)

## Special Thanks

[![icon-resharper](/image/icon-resharper.svg)](https://www.jetbrains.com/?from=llcom plus)
