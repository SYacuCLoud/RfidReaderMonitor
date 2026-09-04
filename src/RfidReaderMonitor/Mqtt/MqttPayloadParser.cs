using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RfidReaderMonitor.Settings;

namespace RfidReaderMonitor.Mqtt;

/// <summary>페이로드 해석 결과. Note 는 진단 화면에 보이는 설명.</summary>
public sealed record MqttParseResult(bool Present, byte[] Uid, string Note)
{
    public static MqttParseResult Error(string note) => new(false, Array.Empty<byte>(), note);
}

/// <summary>
/// MQTT 페이로드 → (태그 있음, UID). 프로바이더, 등록 대화상자의 연결 테스트, 리더 도구 탭이 모두 이것을 쓴다.
/// JSON 경로는 점과 대괄호 인덱스만 지원한다: data.tags[0].uid
/// </summary>
public static class MqttPayloadParser
{
    public static MqttParseResult Parse(MqttReaderSettings s, ReadOnlySpan<byte> payload)
    {
        try
        {
            if (s.PayloadFormat == MqttPayloadFormat.Text)
            {
                var text = Encoding.UTF8.GetString(payload).Trim();
                var uidT = DecodeUidText(text, s.UidEncoding, s.UidReverseBytes);
                return new MqttParseResult(uidT.Length > 0, uidT, uidT.Length > 0 ? "" : "페이로드 비어 있음 → 태그 없음");
            }

            if (payload.Length == 0) return MqttParseResult.Error("빈 페이로드");
            using var doc = JsonDocument.Parse(payload.ToArray());
            var root = doc.RootElement;

            byte[] uid = Array.Empty<byte>();
            string uidNote = "";
            var uidEl = Select(root, s.UidPath);
            if (uidEl is null) uidNote = $"UID 경로 '{s.UidPath}' 없음";
            else uid = DecodeUid(uidEl.Value, s.UidEncoding, s.UidReverseBytes);

            bool present;
            string presentNote;
            if (s.PresentMode == MqttPresentMode.UidNonEmpty)
            {
                present = uid.Length > 0;
                presentNote = present ? "UID 있음 → 태그 있음" : "UID 비어 있음 → 태그 없음";
            }
            else
            {
                var pEl = Select(root, s.PresentPath);
                if (pEl is null) return new MqttParseResult(false, uid, $"Present 경로 '{s.PresentPath}' 없음" + (uidNote == "" ? "" : ", " + uidNote));
                present = Truthy(pEl.Value);
                presentNote = $"{s.PresentPath} = {pEl.Value.ToString()}";
            }
            var note = presentNote + (uidNote == "" ? "" : ", " + uidNote);
            return new MqttParseResult(present, uid, note);
        }
        catch (JsonException ex)
        {
            return MqttParseResult.Error("JSON 아님: " + ex.Message);
        }
        catch (Exception ex)
        {
            return MqttParseResult.Error(ex.Message);
        }
    }

    /// <summary>점·대괄호 경로로 요소를 찾는다. 없으면 null. 빈 경로는 루트.</summary>
    public static JsonElement? Select(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return root;
        var cur = root;
        foreach (var seg in Tokenize(path))
        {
            if (seg.Index is int idx)
            {
                if (cur.ValueKind != JsonValueKind.Array || idx < 0 || idx >= cur.GetArrayLength()) return null;
                cur = cur[idx];
            }
            else
            {
                if (cur.ValueKind != JsonValueKind.Object) return null;
                if (!cur.TryGetProperty(seg.Name!, out var next))
                {
                    // 대소문자 무시 재시도
                    var hit = cur.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, seg.Name, StringComparison.OrdinalIgnoreCase));
                    if (hit.Value.ValueKind == JsonValueKind.Undefined) return null;
                    next = hit.Value;
                }
                cur = next;
            }
        }
        return cur;
    }

    private readonly record struct Seg(string? Name, int? Index);

    private static IEnumerable<Seg> Tokenize(string path)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < path.Length)
        {
            char c = path[i];
            if (c == '.')
            {
                if (sb.Length > 0) { yield return new Seg(sb.ToString(), null); sb.Clear(); }
                i++;
            }
            else if (c == '[')
            {
                if (sb.Length > 0) { yield return new Seg(sb.ToString(), null); sb.Clear(); }
                int close = path.IndexOf(']', i);
                if (close < 0) throw new FormatException("']' 없음");
                var inner = path.Substring(i + 1, close - i - 1).Trim().Trim('"', '\'');
                if (int.TryParse(inner, out var idx)) yield return new Seg(null, idx);
                else yield return new Seg(inner, null);
                i = close + 1;
            }
            else { sb.Append(c); i++; }
        }
        if (sb.Length > 0) yield return new Seg(sb.ToString(), null);
    }

    public static bool Truthy(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.TryGetDouble(out var d) && d != 0,
        JsonValueKind.String => el.GetString() is { } s && s.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on" or "present",
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.Array => el.GetArrayLength() > 0,
        JsonValueKind.Object => true,
        _ => false
    };

    public static byte[] DecodeUid(JsonElement el, MqttUidEncoding enc, bool reverse)
    {
        byte[] bytes;
        switch (el.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return Array.Empty<byte>();
            case JsonValueKind.Array:
                {
                    var list = new List<byte>();
                    foreach (var item in el.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var v)) list.Add((byte)(v & 0xFF));
                        else if (item.ValueKind == JsonValueKind.String && byte.TryParse(item.GetString(), NumberStyles.HexNumber, null, out var hb)) list.Add(hb);
                    }
                    bytes = list.ToArray();
                    break;
                }
            case JsonValueKind.Number:
                {
                    if (enc == MqttUidEncoding.Hex && el.TryGetUInt64(out var hexNum))
                    {
                        // 숫자로 온 16진(따옴표 없는 e004… 는 JSON 이 아님) → 10진으로 본다
                        bytes = ToBigEndian(hexNum);
                    }
                    else if (el.TryGetUInt64(out var n)) bytes = ToBigEndian(n);
                    else bytes = DecodeUidText(el.GetRawText(), enc, false);
                    break;
                }
            default:
                bytes = DecodeUidText(el.GetString() ?? "", enc, false);
                break;
        }
        return Finish(bytes, reverse);
    }

    public static byte[] DecodeUidText(string text, MqttUidEncoding enc, bool reverse)
    {
        text = text.Trim();
        if (text.Length == 0) return Array.Empty<byte>();
        byte[] bytes;
        try
        {
            switch (enc)
            {
                case MqttUidEncoding.Hex:
                    {
                        var clean = new string(text.Where(Uri.IsHexDigit).ToArray());
                        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
                        if (clean.Length % 2 == 1) clean = "0" + clean;
                        bytes = clean.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(clean);
                        break;
                    }
                case MqttUidEncoding.Base64:
                    bytes = Convert.FromBase64String(text);
                    break;
                case MqttUidEncoding.Decimal:
                    bytes = ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? ToBigEndian(n) : Array.Empty<byte>();
                    break;
                case MqttUidEncoding.ByteArray:
                    {
                        var parts = text.Trim('[', ']').Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        bytes = parts.Select(p => byte.TryParse(p, out var b) ? b : (byte)0).ToArray();
                        break;
                    }
                default:
                    bytes = Encoding.UTF8.GetBytes(text);
                    break;
            }
        }
        catch
        {
            bytes = Array.Empty<byte>();
        }
        return Finish(bytes, reverse);
    }

    private static byte[] ToBigEndian(ulong n)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, n);
        return buf;
    }

    private static byte[] Finish(byte[] bytes, bool reverse)
    {
        if (bytes.All(b => b == 0)) return Array.Empty<byte>();
        if (reverse) Array.Reverse(bytes);
        return bytes;
    }

    /// <summary>토픽 탐색용: JSON 을 (경로, 값) 목록으로 펼친다. 배열은 인덱스 3개까지만.</summary>
    public static List<(string Path, string Value)> Flatten(ReadOnlySpan<byte> payload)
    {
        var list = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(payload.ToArray());
            Walk(doc.RootElement, "", list, 0);
        }
        catch { }
        return list;
    }

    private static void Walk(JsonElement el, string path, List<(string, string)> list, int depth)
    {
        if (depth > 6) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name, list, depth + 1);
                break;
            case JsonValueKind.Array:
                {
                    int i = 0;
                    foreach (var item in el.EnumerateArray())
                    {
                        if (i >= 3) break;
                        Walk(item, $"{path}[{i}]", list, depth + 1);
                        i++;
                    }
                    break;
                }
            default:
                list.Add((path, el.ToString()));
                break;
        }
    }
}
