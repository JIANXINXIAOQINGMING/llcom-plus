apiPrintLog("AT tcp fast mode example");
apiSend("uart", stringToBytes("AT\r\n"), null);
apiStartTimer(1, 500);

function onTrigger(id, type, data) {
    if (type === "timer") {
        apiSend("uart", stringToBytes("AT+PING=\"www.baidu.com\"\r\n"), null);
    }
}
