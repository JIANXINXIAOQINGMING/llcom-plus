function u16le(data, offset) {
    var length = data.length === undefined ? data.Length : data.length;
    if (length <= offset + 1) {
        return 0;
    }
    return (data[offset] & 0xff) | ((data[offset + 1] & 0xff) << 8);
}

apiAddPoint(u16le(uartData, 0), 0);
apiAddPoint(u16le(uartData, 2), 1);
return uartData;
