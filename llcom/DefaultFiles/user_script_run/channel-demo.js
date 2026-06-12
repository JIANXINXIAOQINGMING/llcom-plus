apiPrintLog("channel demo started");

apiSetCb("uart", function(data) {
    apiPrintLog("uart recv: " + bytesToHex(data));
});

apiSetCb("socket-client", function(data) {
    apiPrintLog("socket recv: " + bytesToHex(data));
});

apiSetCb("mqtt", function(msg) {
    apiPrintLog("mqtt recv topic=" + msg.topic + " payload=" + bytesToHex(msg.payload));
});

apiSend("uart", stringToBytes("send message by JavaScript!\r\n"), null);
