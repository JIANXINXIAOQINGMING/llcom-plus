var text = bytesToString(uartData).trim();
var value = parseFloat(text);
if (!isNaN(value)) {
    apiAddPoint(value, 0);
}
return uartData;
