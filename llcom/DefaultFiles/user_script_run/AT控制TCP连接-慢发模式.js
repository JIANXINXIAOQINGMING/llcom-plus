apiPrintLog("AT tcp slow mode example");
var commands = [
    "AT\r\n",
    "ATE0\r\n",
    "AT+PING=\"www.baidu.com\"\r\n"
];
var index = 0;
apiStartTimer(1, 500);

function onTrigger(id, type, data) {
    if (type !== "timer" || index >= commands.length) {
        return;
    }
    apiSend("uart", stringToBytes(commands[index]), null);
    index++;
    apiStartTimer(1, 1000);
}
