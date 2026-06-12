apiPrintLog("JavaScript script started");
apiStartTimer(1, 1000);

function onTrigger(id, type, data) {
    if (type === "timer") {
        apiPrintLog("timer " + id);
        apiStartTimer(id, 1000);
    } else if (type === "uart") {
        apiPrintLog("uart recv: " + bytesToHex(data));
    }
}
