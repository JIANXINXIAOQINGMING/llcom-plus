using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace llcom_plus.Tools
{
    public static class OpenSslMessageExplainer
    {
        private static readonly Regex HeaderRegex = new Regex(
            @"^(?<dir><<<|>>>)\s+(?<label>.*?)\s+\[length\s+(?<length>[0-9a-fA-F]+)\](?:,\s*(?<name>.*))?\s*$",
            RegexOptions.Compiled);

        public static string Explain(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text ?? string.Empty;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var output = new StringBuilder();
            for (int i = 0; i < lines.Length;)
            {
                var match = HeaderRegex.Match(lines[i].TrimEnd());
                if (!match.Success)
                {
                    i++;
                    continue;
                }

                var block = new OpenSslMessageBlock
                {
                    Direction = match.Groups["dir"].Value,
                    Label = match.Groups["label"].Value,
                    Name = match.Groups["name"].Value,
                    DeclaredLength = ParseHexInt(match.Groups["length"].Value),
                    OriginalHeader = lines[i].TrimEnd()
                };

                i++;
                while (i < lines.Length && TryParseHexLine(lines[i], block.Bytes))
                    i++;

                if (block.Bytes.Count == 0 || !IsHelloHandshakeBlock(block))
                    continue;

                output.Append(FormatBlock(block));
            }

            return output.ToString().TrimEnd();
        }

        private static string FormatBlock(OpenSslMessageBlock block)
        {
            var builder = new StringBuilder();
            var direction = block.Direction == ">>>" ? "发送" : "接收";
            var title = string.IsNullOrWhiteSpace(block.Name) ? block.Label : block.Name.Trim();

            builder.AppendLine($"{block.Direction} {direction}: {title}");
            builder.AppendLine($"  OpenSSL摘要: {block.Label}{(string.IsNullOrWhiteSpace(block.Name) ? "" : ", " + block.Name)}");
            builder.AppendLine($"  数据长度: {block.DeclaredLength} bytes");

            if (LooksLikeRecordHeader(block.Bytes))
            {
                FormatRecordHeader(builder, block.Bytes);
                return builder.ToString();
            }

            if (IsHandshakeBlock(block))
            {
                FormatHandshake(builder, block.Bytes);
                return builder.ToString();
            }

            if (IsChangeCipherSpecBlock(block))
            {
                FormatChangeCipherSpec(builder, block.Bytes);
                return builder.ToString();
            }

            if (IsAlertBlock(block))
            {
                FormatAlert(builder, block.Bytes);
                return builder.ToString();
            }

            builder.AppendLine("  说明: 该 OpenSSL 明细暂未内置字段级解释。");
            return builder.ToString();
        }

        private static bool IsHandshakeBlock(OpenSslMessageBlock block)
        {
            return block.Label.IndexOf("Handshake", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (block.Bytes.Count >= 4 && GetHandshakeTypeName(block.Bytes[0]) != null);
        }

        private static bool IsHelloHandshakeBlock(OpenSslMessageBlock block)
        {
            return IsHandshakeBlock(block) &&
                   block.Bytes.Count >= 4 &&
                   (block.Bytes[0] == 0x01 || block.Bytes[0] == 0x02);
        }

        private static bool IsChangeCipherSpecBlock(OpenSslMessageBlock block)
        {
            return block.Label.IndexOf("ChangeCipherSpec", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (block.Bytes.Count == 1 && block.Bytes[0] == 0x01);
        }

        private static bool IsAlertBlock(OpenSslMessageBlock block)
        {
            return block.Label.IndexOf("Alert", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (block.Bytes.Count == 2 && GetAlertLevelName(block.Bytes[0]) != null);
        }

        private static bool LooksLikeRecordHeader(IReadOnlyList<byte> bytes)
        {
            if (bytes.Count != 5)
                return false;

            var type = bytes[0];
            return type == 0x14 || type == 0x15 || type == 0x16 || type == 0x17 || type == 0x18;
        }

        private static void FormatRecordHeader(StringBuilder builder, IReadOnlyList<byte> bytes)
        {
            var contentType = bytes[0];
            var version = ReadUInt16(bytes, 1);
            var length = ReadUInt16(bytes, 3);

            builder.AppendLine("  TLS记录头:");
            builder.AppendLine($"    内容类型: {GetContentTypeName(contentType)}");
            builder.AppendLine($"    兼容版本字段: {GetVersionName(version)}");
            builder.AppendLine($"    后续负载长度: {length} bytes");
        }

        private static void FormatHandshake(StringBuilder builder, IReadOnlyList<byte> bytes)
        {
            if (bytes.Count < 4)
            {
                builder.AppendLine("  Handshake: 长度不足，无法解析握手头。");
                return;
            }

            var handshakeType = bytes[0];
            var bodyLength = ReadUInt24(bytes, 1);
            var bodyEnd = Math.Min(bytes.Count, 4 + bodyLength);
            builder.AppendLine("  TLS握手消息:");
            builder.AppendLine($"    类型: {GetHandshakeTypeName(handshakeType) ?? "unknown"}");
            builder.AppendLine($"    消息体长度: {bodyLength} bytes");

            switch (handshakeType)
            {
                case 0x01:
                    FormatClientHello(builder, bytes, 4, bodyEnd);
                    break;
                case 0x02:
                    FormatServerHello(builder, bytes, 4, bodyEnd);
                    break;
                case 0x08:
                    FormatEncryptedExtensions(builder, bytes, 4, bodyEnd);
                    break;
                case 0x0b:
                    builder.AppendLine("    Certificate: 证书链消息，包含服务端或客户端证书。证书 ASN.1 内容未在这里展开。");
                    break;
                case 0x0d:
                    builder.AppendLine("    CertificateRequest: 服务端请求客户端证书，用于双向 TLS。");
                    break;
                case 0x0f:
                    builder.AppendLine("    CertificateVerify: 证明证书私钥持有者参与了本次握手。");
                    break;
                case 0x14:
                    builder.AppendLine("    Finished: 握手完整性校验值，证明双方派生出的密钥一致。");
                    break;
                case 0x18:
                    builder.AppendLine("    KeyUpdate: TLS 1.3 更新后续应用数据加密密钥。");
                    break;
            }
        }

        private static void FormatClientHello(StringBuilder builder, IReadOnlyList<byte> bytes, int offset, int end)
        {
            if (!HasBytes(end, offset, 35))
            {
                builder.AppendLine("    ClientHello: 长度不足。");
                return;
            }

            var legacyVersion = ReadUInt16(bytes, offset);
            offset += 2;
            builder.AppendLine($"    legacy_version: {GetVersionName(legacyVersion)}（兼容字段，TLS 1.3 也通常填 TLS 1.2）");
            builder.AppendLine("    random: 32 bytes（客户端随机数，参与密钥派生）");
            offset += 32;

            if (!TryReadVector8(bytes, ref offset, end, out var sessionIdLength))
                return;
            builder.AppendLine($"    session_id: {sessionIdLength} bytes（会话恢复/兼容字段）");

            if (!TryReadUInt16(bytes, ref offset, end, out var cipherBytes) || !HasBytes(end, offset, cipherBytes))
                return;
            var cipherEnd = offset + cipherBytes;
            builder.AppendLine($"    cipher_suites: {cipherBytes / 2} 个（客户端支持的加密套件，按偏好排序）");
            while (offset + 1 < cipherEnd)
            {
                var suite = ReadUInt16(bytes, offset);
                offset += 2;
                builder.AppendLine($"      - {GetCipherSuiteName(suite)}");
            }

            if (!TryReadVector8(bytes, ref offset, end, out var compressionLength))
                return;
            builder.AppendLine($"    compression_methods: {DescribeCompressionMethods(bytes, offset - compressionLength, compressionLength)}");

            if (offset < end)
                FormatExtensions(builder, bytes, ref offset, end, "ClientHello");
        }

        private static void FormatServerHello(StringBuilder builder, IReadOnlyList<byte> bytes, int offset, int end)
        {
            if (!HasBytes(end, offset, 38))
            {
                builder.AppendLine("    ServerHello: 长度不足。");
                return;
            }

            var legacyVersion = ReadUInt16(bytes, offset);
            offset += 2;
            builder.AppendLine($"    legacy_version: {GetVersionName(legacyVersion)}（兼容字段）");
            builder.AppendLine("    random: 32 bytes（服务端随机数，参与密钥派生）");
            offset += 32;

            if (!TryReadVector8(bytes, ref offset, end, out var sessionIdLength))
                return;
            builder.AppendLine($"    session_id_echo: {sessionIdLength} bytes（回显客户端会话 ID）");

            if (!TryReadUInt16(bytes, ref offset, end, out var cipherSuite))
                return;
            builder.AppendLine($"    selected_cipher_suite: {GetCipherSuiteName(cipherSuite)}");

            if (!TryReadByte(bytes, ref offset, end, out var compression))
                return;
            builder.AppendLine($"    compression_method: {GetCompressionMethodName(compression)}");

            if (offset < end)
                FormatExtensions(builder, bytes, ref offset, end, "ServerHello");
        }

        private static void FormatEncryptedExtensions(StringBuilder builder, IReadOnlyList<byte> bytes, int offset, int end)
        {
            builder.AppendLine("    EncryptedExtensions: 服务端加密发送的扩展参数。");
            if (offset < end)
                FormatExtensions(builder, bytes, ref offset, end, "EncryptedExtensions");
        }

        private static void FormatExtensions(StringBuilder builder, IReadOnlyList<byte> bytes, ref int offset, int end, string context)
        {
            if (!TryReadUInt16(bytes, ref offset, end, out var totalLength) || !HasBytes(end, offset, totalLength))
                return;

            var extEnd = offset + totalLength;
            builder.AppendLine($"    extensions: {totalLength} bytes（扩展列表）");
            while (offset + 4 <= extEnd)
            {
                var type = ReadUInt16(bytes, offset);
                offset += 2;
                var length = ReadUInt16(bytes, offset);
                offset += 2;
                if (!HasBytes(extEnd, offset, length))
                    break;

                builder.AppendLine($"      - {DescribeExtension(type, bytes, offset, length, context)}");
                offset += length;
            }
        }

        private static string DescribeExtension(int type, IReadOnlyList<byte> bytes, int offset, int length, string context)
        {
            switch (type)
            {
                case 0x0000:
                    return $"server_name: {DescribeServerName(bytes, offset, length)}";
                case 0x0005:
                    return "status_request: 客户端请求 OCSP 证书状态。";
                case 0x000a:
                    return $"supported_groups: {DescribeNamedGroupList(bytes, offset, length)}";
                case 0x000b:
                    return $"ec_point_formats: {DescribeEcPointFormats(bytes, offset, length)}";
                case 0x000d:
                    return $"signature_algorithms: {DescribeSignatureSchemes(bytes, offset, length)}";
                case 0x0010:
                    return $"alpn: {DescribeAlpn(bytes, offset, length)}";
                case 0x0012:
                    return "signed_certificate_timestamp: 证书透明度 SCT 扩展。";
                case 0x0015:
                    return $"padding: 填充扩展，用于调整 ClientHello 长度，长度 {length} bytes。";
                case 0x0016:
                    return "encrypt_then_mac: TLS 1.2 CBC 套件的先加密后 MAC 扩展。";
                case 0x0017:
                    return "extended_master_secret: TLS 1.2 主密钥绑定增强。";
                case 0x0018:
                    return "token_binding: Token Binding 协议扩展。";
                case 0x001c:
                    return "record_size_limit: 限制单个 TLS 记录的最大明文长度。";
                case 0x0023:
                    return length == 0 ? "session_ticket: 客户端支持会话票据恢复。" : "session_ticket: 会话票据数据。";
                case 0x002b:
                    return $"supported_versions: {DescribeSupportedVersions(bytes, offset, length, context)}";
                case 0x002d:
                    return $"psk_key_exchange_modes: {DescribePskModes(bytes, offset, length)}";
                case 0x0033:
                    return $"key_share: {DescribeKeyShare(bytes, offset, length, context)}";
                case 0xff01:
                    return "renegotiation_info: 安全重协商标记。";
                default:
                    return $"{GetExtensionName(type)}: {length} bytes（未内置字段级解释）";
            }
        }

        private static string DescribeServerName(IReadOnlyList<byte> bytes, int offset, int length)
        {
            var end = offset + length;
            if (!TryReadUInt16(bytes, ref offset, end, out var listLength))
                return "SNI 主机名列表";
            var listEnd = Math.Min(end, offset + listLength);
            var names = new List<string>();
            while (offset + 3 <= listEnd)
            {
                var nameType = bytes[offset++];
                var nameLength = ReadUInt16(bytes, offset);
                offset += 2;
                if (!HasBytes(listEnd, offset, nameLength))
                    break;
                var value = Encoding.ASCII.GetString(bytes.Skip(offset).Take(nameLength).ToArray());
                offset += nameLength;
                names.Add(nameType == 0 ? value + " (host_name)" : value);
            }
            return names.Count == 0 ? "SNI 主机名列表为空" : "SNI=" + string.Join(", ", names);
        }

        private static string DescribeNamedGroupList(IReadOnlyList<byte> bytes, int offset, int length)
        {
            var end = offset + length;
            if (!TryReadUInt16(bytes, ref offset, end, out var listLength))
                return "命名组列表";
            var listEnd = Math.Min(end, offset + listLength);
            var groups = new List<string>();
            while (offset + 1 < listEnd)
            {
                groups.Add(GetNamedGroupName(ReadUInt16(bytes, offset)));
                offset += 2;
            }
            return string.Join(", ", groups);
        }

        private static string DescribeEcPointFormats(IReadOnlyList<byte> bytes, int offset, int length)
        {
            var end = offset + length;
            if (!TryReadByte(bytes, ref offset, end, out var listLength))
                return "椭圆曲线点格式列表";
            var listEnd = Math.Min(end, offset + listLength);
            var formats = new List<string>();
            while (offset < listEnd)
                formats.Add(GetEcPointFormatName(bytes[offset++]));
            return string.Join(", ", formats);
        }

        private static string DescribeSignatureSchemes(IReadOnlyList<byte> bytes, int offset, int length)
        {
            var end = offset + length;
            if (!TryReadUInt16(bytes, ref offset, end, out var listLength))
                return "签名算法列表";
            var listEnd = Math.Min(end, offset + listLength);
            var schemes = new List<string>();
            while (offset + 1 < listEnd)
            {
                schemes.Add(GetSignatureSchemeName(ReadUInt16(bytes, offset)));
                offset += 2;
            }
            return string.Join(", ", schemes);
        }

        private static string DescribeAlpn(IReadOnlyList<byte> bytes, int offset, int length)
        {
            var end = offset + length;
            if (!TryReadUInt16(bytes, ref offset, end, out var listLength))
                return "应用层协议协商列表";
            var listEnd = Math.Min(end, offset + listLength);
            var protocols = new List<string>();
            while (offset < listEnd)
            {
                var itemLength = bytes[offset++];
                if (!HasBytes(listEnd, offset, itemLength))
                    break;
                protocols.Add(Encoding.ASCII.GetString(bytes.Skip(offset).Take(itemLength).ToArray()));
                offset += itemLength;
            }
            return protocols.Count == 0 ? "无协议" : string.Join(", ", protocols);
        }

        private static string DescribeSupportedVersions(IReadOnlyList<byte> bytes, int offset, int length, string context)
        {
            var versions = new List<string>();
            if (context == "ServerHello" && length == 2)
                return "选择 " + GetVersionName(ReadUInt16(bytes, offset));

            var end = offset + length;
            if (!TryReadByte(bytes, ref offset, end, out var listLength))
                return "支持的 TLS 版本列表";
            var listEnd = Math.Min(end, offset + listLength);
            while (offset + 1 < listEnd)
            {
                versions.Add(GetVersionName(ReadUInt16(bytes, offset)));
                offset += 2;
            }
            return string.Join(", ", versions);
        }

        private static string DescribePskModes(IReadOnlyList<byte> bytes, int offset, int length)
        {
            var end = offset + length;
            if (!TryReadByte(bytes, ref offset, end, out var listLength))
                return "PSK 密钥交换模式列表";
            var listEnd = Math.Min(end, offset + listLength);
            var modes = new List<string>();
            while (offset < listEnd)
                modes.Add(bytes[offset++] == 0 ? "psk_ke" : "psk_dhe_ke");
            return string.Join(", ", modes);
        }

        private static string DescribeKeyShare(IReadOnlyList<byte> bytes, int offset, int length, string context)
        {
            var end = offset + length;
            var shares = new List<string>();
            if (context == "ServerHello")
            {
                if (offset + 4 > end)
                    return "服务端选择的密钥交换参数";
                var group = ReadUInt16(bytes, offset);
                offset += 2;
                var keyLength = ReadUInt16(bytes, offset);
                shares.Add($"{GetNamedGroupName(group)}, key_exchange {keyLength} bytes");
                return string.Join(", ", shares);
            }

            if (!TryReadUInt16(bytes, ref offset, end, out var listLength))
                return "客户端密钥交换参数列表";
            var listEnd = Math.Min(end, offset + listLength);
            while (offset + 4 <= listEnd)
            {
                var group = ReadUInt16(bytes, offset);
                offset += 2;
                var keyLength = ReadUInt16(bytes, offset);
                offset += 2;
                if (!HasBytes(listEnd, offset, keyLength))
                    break;
                shares.Add($"{GetNamedGroupName(group)}, key_exchange {keyLength} bytes");
                offset += keyLength;
            }
            return string.Join(", ", shares);
        }

        private static void FormatChangeCipherSpec(StringBuilder builder, IReadOnlyList<byte> bytes)
        {
            builder.AppendLine("  ChangeCipherSpec:");
            builder.AppendLine(bytes.Count > 0 && bytes[0] == 0x01
                ? "    含义: 切换到新协商出的加密参数。"
                : "    含义: ChangeCipherSpec 消息，内容异常或未识别。");
        }

        private static void FormatAlert(StringBuilder builder, IReadOnlyList<byte> bytes)
        {
            if (bytes.Count < 2)
                return;
            builder.AppendLine("  TLS告警:");
            builder.AppendLine($"    级别: {GetAlertLevelName(bytes[0])}");
            builder.AppendLine($"    描述: {GetAlertDescriptionName(bytes[1])}");
        }

        private static string DescribeCompressionMethods(IReadOnlyList<byte> bytes, int offset, int length)
        {
            var methods = new List<string>();
            var end = Math.Min(bytes.Count, offset + length);
            while (offset < end)
                methods.Add(GetCompressionMethodName(bytes[offset++]));
            return string.Join(", ", methods);
        }

        private static bool TryParseHexLine(string line, IList<byte> bytes)
        {
            var trimmed = (line ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return false;

            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            var parsed = new List<byte>();
            foreach (var part in parts)
            {
                if (part.Length != 2 || !byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    return false;
                parsed.Add(value);
            }

            foreach (var value in parsed)
                bytes.Add(value);
            return true;
        }

        private static int ParseHexInt(string value)
        {
            return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result)
                ? result
                : 0;
        }

        private static bool TryReadByte(IReadOnlyList<byte> bytes, ref int offset, int end, out byte value)
        {
            value = 0;
            if (!HasBytes(end, offset, 1))
                return false;
            value = bytes[offset++];
            return true;
        }

        private static bool TryReadUInt16(IReadOnlyList<byte> bytes, ref int offset, int end, out int value)
        {
            value = 0;
            if (!HasBytes(end, offset, 2))
                return false;
            value = ReadUInt16(bytes, offset);
            offset += 2;
            return true;
        }

        private static bool TryReadVector8(IReadOnlyList<byte> bytes, ref int offset, int end, out int length)
        {
            length = 0;
            if (!TryReadByte(bytes, ref offset, end, out var byteLength) || !HasBytes(end, offset, byteLength))
                return false;
            length = byteLength;
            offset += length;
            return true;
        }

        private static bool HasBytes(int end, int offset, int length)
        {
            return offset >= 0 && length >= 0 && offset + length <= end;
        }

        private static int ReadUInt16(IReadOnlyList<byte> bytes, int offset)
        {
            return (bytes[offset] << 8) | bytes[offset + 1];
        }

        private static int ReadUInt24(IReadOnlyList<byte> bytes, int offset)
        {
            return (bytes[offset] << 16) | (bytes[offset + 1] << 8) | bytes[offset + 2];
        }

        private static string GetContentTypeName(int value)
        {
            switch (value)
            {
                case 0x14: return "ChangeCipherSpec（切换加密参数）";
                case 0x15: return "Alert（告警）";
                case 0x16: return "Handshake（握手消息）";
                case 0x17: return "ApplicationData（加密应用数据）";
                case 0x18: return "Heartbeat（心跳）";
                default: return "unknown";
            }
        }

        private static string GetVersionName(int value)
        {
            switch (value)
            {
                case 0x0300: return "SSL 3.0";
                case 0x0301: return "TLS 1.0";
                case 0x0302: return "TLS 1.1";
                case 0x0303: return "TLS 1.2";
                case 0x0304: return "TLS 1.3";
                case 0xfefd: return "DTLS 1.2";
                case 0xfeff: return "DTLS 1.0";
                default: return $"unknown version 0x{value:X4}";
            }
        }

        private static string GetHandshakeTypeName(int value)
        {
            switch (value)
            {
                case 0x01: return "ClientHello（客户端发起握手，声明能力）";
                case 0x02: return "ServerHello（服务端选择协议参数）";
                case 0x04: return "NewSessionTicket（会话恢复票据）";
                case 0x08: return "EncryptedExtensions（加密扩展参数）";
                case 0x0b: return "Certificate（证书链）";
                case 0x0d: return "CertificateRequest（请求客户端证书）";
                case 0x0f: return "CertificateVerify（证书私钥签名证明）";
                case 0x10: return "ClientKeyExchange（TLS 1.2 密钥交换）";
                case 0x14: return "Finished（握手校验完成）";
                case 0x18: return "KeyUpdate（更新应用数据密钥）";
                case 0xfe: return "message_hash（HelloRetryRequest transcript）";
                default: return null;
            }
        }

        private static string GetCipherSuiteName(int value)
        {
            switch (value)
            {
                case 0x1301: return "TLS_AES_128_GCM_SHA256（TLS 1.3 AEAD 套件）";
                case 0x1302: return "TLS_AES_256_GCM_SHA384（TLS 1.3 AEAD 套件）";
                case 0x1303: return "TLS_CHACHA20_POLY1305_SHA256（TLS 1.3 AEAD 套件）";
                case 0x1304: return "TLS_AES_128_CCM_SHA256（TLS 1.3 AEAD 套件）";
                case 0x1305: return "TLS_AES_128_CCM_8_SHA256（TLS 1.3 AEAD 套件）";
                case 0x00ff: return "TLS_EMPTY_RENEGOTIATION_INFO_SCSV（安全重协商标记）";
                case 0x009c: return "TLS_RSA_WITH_AES_128_GCM_SHA256";
                case 0x009d: return "TLS_RSA_WITH_AES_256_GCM_SHA384";
                case 0xc02b: return "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256";
                case 0xc02c: return "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384";
                case 0xc02f: return "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256";
                case 0xc030: return "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384";
                case 0xcca8: return "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256";
                case 0xcca9: return "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256";
                default: return $"unknown cipher suite 0x{value:X4}";
            }
        }

        private static string GetCompressionMethodName(int value)
        {
            return value == 0 ? "null（不压缩）" : $"unknown compression 0x{value:X2}";
        }

        private static string GetExtensionName(int value)
        {
            switch (value)
            {
                case 0x0000: return "server_name";
                case 0x0005: return "status_request";
                case 0x000a: return "supported_groups";
                case 0x000b: return "ec_point_formats";
                case 0x000d: return "signature_algorithms";
                case 0x0010: return "alpn";
                case 0x0012: return "signed_certificate_timestamp";
                case 0x0015: return "padding";
                case 0x0016: return "encrypt_then_mac";
                case 0x0017: return "extended_master_secret";
                case 0x0018: return "token_binding";
                case 0x001c: return "record_size_limit";
                case 0x0023: return "session_ticket";
                case 0x002b: return "supported_versions";
                case 0x002d: return "psk_key_exchange_modes";
                case 0x0033: return "key_share";
                case 0xff01: return "renegotiation_info";
                default: return $"unknown_extension_0x{value:X4}";
            }
        }

        private static string GetNamedGroupName(int value)
        {
            switch (value)
            {
                case 0x0017: return "secp256r1";
                case 0x0018: return "secp384r1";
                case 0x0019: return "secp521r1";
                case 0x001d: return "x25519";
                case 0x001e: return "x448";
                case 0x0100: return "ffdhe2048";
                case 0x0101: return "ffdhe3072";
                case 0x0102: return "ffdhe4096";
                case 0x0103: return "ffdhe6144";
                case 0x0104: return "ffdhe8192";
                default: return $"unknown group 0x{value:X4}";
            }
        }

        private static string GetSignatureSchemeName(int value)
        {
            switch (value)
            {
                case 0x0401: return "rsa_pkcs1_sha256";
                case 0x0501: return "rsa_pkcs1_sha384";
                case 0x0601: return "rsa_pkcs1_sha512";
                case 0x0403: return "ecdsa_secp256r1_sha256";
                case 0x0503: return "ecdsa_secp384r1_sha384";
                case 0x0603: return "ecdsa_secp521r1_sha512";
                case 0x0804: return "rsa_pss_rsae_sha256";
                case 0x0805: return "rsa_pss_rsae_sha384";
                case 0x0806: return "rsa_pss_rsae_sha512";
                case 0x0807: return "ed25519";
                case 0x0808: return "ed448";
                case 0x0809: return "rsa_pss_pss_sha256";
                case 0x080a: return "rsa_pss_pss_sha384";
                case 0x080b: return "rsa_pss_pss_sha512";
                default: return $"unknown signature scheme 0x{value:X4}";
            }
        }

        private static string GetEcPointFormatName(int value)
        {
            switch (value)
            {
                case 0: return "uncompressed";
                case 1: return "ansiX962_compressed_prime";
                case 2: return "ansiX962_compressed_char2";
                default: return $"unknown format 0x{value:X2}";
            }
        }

        private static string GetAlertLevelName(int value)
        {
            switch (value)
            {
                case 1: return "warning";
                case 2: return "fatal";
                default: return null;
            }
        }

        private static string GetAlertDescriptionName(int value)
        {
            switch (value)
            {
                case 0: return "close_notify";
                case 10: return "unexpected_message";
                case 20: return "bad_record_mac";
                case 22: return "record_overflow";
                case 40: return "handshake_failure";
                case 42: return "bad_certificate";
                case 43: return "unsupported_certificate";
                case 44: return "certificate_revoked";
                case 45: return "certificate_expired";
                case 46: return "certificate_unknown";
                case 47: return "illegal_parameter";
                case 48: return "unknown_ca";
                case 49: return "access_denied";
                case 50: return "decode_error";
                case 51: return "decrypt_error";
                case 70: return "protocol_version";
                case 71: return "insufficient_security";
                case 80: return "internal_error";
                case 90: return "user_canceled";
                case 109: return "missing_extension";
                case 110: return "unsupported_extension";
                case 112: return "unrecognized_name";
                case 116: return "certificate_required";
                default: return $"unknown alert 0x{value:X2}";
            }
        }

        private sealed class OpenSslMessageBlock
        {
            public string Direction { get; set; }
            public string Label { get; set; }
            public string Name { get; set; }
            public int DeclaredLength { get; set; }
            public string OriginalHeader { get; set; }
            public List<byte> Bytes { get; } = new List<byte>();
        }
    }
}
