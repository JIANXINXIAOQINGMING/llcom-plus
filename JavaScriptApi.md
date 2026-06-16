# llcom plus JavaScript API

llcom plus now uses JavaScript scripts through Jint.

## Data Conversion Scripts

Send/receive conversion scripts receive:

- `uartData`: byte array. In send conversion it is the data before sending; in receive conversion it is the received packet before display.
- `uartPara`: receive parameter, only in receive conversion. Quick-send rows pass their "parameter" text here.
- `uartSendRaw`: raw bytes of the quick-send row that selected the receive script, only in receive conversion.

Return a byte array or string. Returning `null`/`undefined` hides the received data.

Send conversion scripts affect normal serial sends, including the main input box, quick-send rows, and file/data-calc sends. Data sent directly by runtime script APIs is not processed again.

Receive conversion scripts affect only the data display/log display. Runtime script callbacks still receive the original raw bytes.

Helpers:

- `bytesToString(data)`
- `stringToBytes(text)`
- `hexToBytes(hexText)`
- `bytesToHex(data)`
- `concatBytes(a, b, ...)`
- `apiUnescapeText(text)`

Examples:

```javascript
// send conversion: append CRLF
return concatBytes(uartData, "\r\n");
```

```javascript
// receive conversion: hide packets that do not contain OK
var text = bytesToString(uartData);
if (text.indexOf("OK") < 0) {
    return null;
}
return uartData;
```

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
