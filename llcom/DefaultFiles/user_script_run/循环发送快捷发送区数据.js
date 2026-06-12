var index = 1;
apiStartTimer(1, 1000);

function onTrigger(id, type, data) {
    if (type !== "timer") {
        return;
    }

    var item = apiQuickSendList(index);
    if (!item) {
        index = 1;
        apiStartTimer(1, 1000);
        return;
    }

    var payload = item.substring(1);
    if (item[0] === "H") {
        apiSend("uart", hexToBytes(payload), null);
    } else {
        apiSend("uart", stringToBytes(payload), null);
    }
    index++;
    apiStartTimer(1, 1000);
}
