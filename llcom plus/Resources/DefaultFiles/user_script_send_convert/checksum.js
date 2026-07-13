var source = apiToBytes(uartData);
var data = [];
var length = source.length === undefined ? source.Length : source.length;
for (var i = 0; i < length; i++) {
    data.push(source[i] & 0xff);
}
var checksum = 0;
for (var j = 0; j < data.length; j++) {
    checksum ^= data[j] & 0xff;
}
data.push(checksum);
return data;
