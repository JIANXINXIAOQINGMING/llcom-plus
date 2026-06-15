# llcom plus JavaScript API

llcom plus now uses JavaScript scripts through Jint.

## Data Conversion Scripts

Send/receive conversion scripts receive:

- `uartData`: byte array
- `uartPara`: receive parameter object, only in receive conversion
- `uartSendRaw`: raw send bytes, only in receive conversion

Return a byte array or string. Returning `null`/`undefined` hides the received data.

Helpers:

- `bytesToString(data)`
- `stringToBytes(text)`
- `hexToBytes(hexText)`
- `bytesToHex(data)`
- `concatBytes(a, b, ...)`
- `apiUnescapeText(text)`

## Runtime Scripts

Common APIs:

- `apiPrintLog(text)`
- `apiQuickSendList(index)`
- `apiInputBox(prompt, defaultInput, title)`
- `apiStartTimer(id, ms)`
- `apiStopTimer(id)`
- `apiSend(channel, data, options)`
- `apiSetCb(channel, callback)`

Runtime scripts can either define a global trigger:

```javascript
function onTrigger(id, type, data) {
    if (type === "uart") {
        apiPrintLog(bytesToHex(data));
    }
}
```

Or subscribe to one channel:

```javascript
apiSetCb("uart", function(data) {
    apiPrintLog(bytesToString(data));
});
```

Channels include `uart`, `mqtt`, `socket-client`, and `winusb`.
