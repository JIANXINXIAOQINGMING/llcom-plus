var text = bytesToString(uartData).trim();
if (text.length === 0) {
    return uartData;
}
if (text[0] === "$") {
    text = text.substring(1);
}
var star = text.indexOf("*");
if (star >= 0) {
    text = text.substring(0, star);
}
var checksum = 0;
for (var i = 0; i < text.length; i++) {
    checksum ^= text.charCodeAt(i);
}
var hex = checksum.toString(16).toUpperCase();
if (hex.length < 2) {
    hex = "0" + hex;
}
return stringToBytes("$" + text + "*" + hex + "\r\n");
