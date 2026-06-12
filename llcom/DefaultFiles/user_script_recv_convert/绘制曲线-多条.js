var text = bytesToString(uartData).trim();
var values = text.split(/[\s,;]+/);
for (var i = 0; i < values.length && i < 10; i++) {
    var value = parseFloat(values[i]);
    if (!isNaN(value)) {
        apiAddPoint(value, i);
    }
}
return uartData;
