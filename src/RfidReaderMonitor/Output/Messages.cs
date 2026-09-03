using System.Text.Json;
using System.Text.Json.Serialization;
using RfidReaderMonitor.Core;

namespace RfidReaderMonitor.Output;

/// <summary>하트비트에 실리는 리더 요약.</summary>
public sealed record HeartbeatReader(string Name, string Alias, string Serial, string State, string Uid);

/// <summary>
/// 감시 PC가 주기적으로 보내는 상태 메시지. 수집 모드와 SQL 상태 테이블이 소비한다.
/// JSON: {"type":"heartbeat","time":...,"host":...,"version":...,"readers":[...],"appearToday":n,"removeToday":n}
/// </summary>
public sealed record HeartbeatMessage(
    DateTimeOffset Time,
    string Host,
    string Version,
    IReadOnlyList<HeartbeatReader> Readers,
    int AppearToday,
    int RemoveToday)
{
    public int OnlineReaders => Readers.Count(r => r.State is not ("뽑힘" or "사용불가" or "?"));
    public int PresentReaders => Readers.Count(r => r.State.StartsWith("PRESENT", StringComparison.Ordinal));

    public string ToJson() => JsonSerializer.Serialize(new
    {
        type = "heartbeat",
        time = Time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        host = Host,
        version = Version,
        readers = Readers.Select(r => new { name = r.Name, alias = r.Alias, serial = r.Serial, state = r.State, uid = r.Uid }),
        appearToday = AppearToday,
        removeToday = RemoveToday
    }, Envelope.JsonOpts);
}

/// <summary>JSON 한 줄을 이벤트 또는 하트비트로 해석.</summary>
public static class Envelope
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool TryParse(string json, out TagEvent? ev, out HeartbeatMessage? hb)
    {
        ev = null;
        hb = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = Str(root, "type") ?? "event";
            var time = DateTimeOffset.TryParse(Str(root, "time"), out var t) ? t : DateTimeOffset.Now;
            var host = Str(root, "host") ?? "";

            if (type == "heartbeat")
            {
                var readers = new List<HeartbeatReader>();
                if (root.TryGetProperty("readers", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in arr.EnumerateArray())
                        readers.Add(new HeartbeatReader(Str(r, "name") ?? "", Str(r, "alias") ?? "", Str(r, "serial") ?? "", Str(r, "state") ?? "?", Str(r, "uid") ?? ""));
                }
                hb = new HeartbeatMessage(time, host, Str(root, "version") ?? "", readers, Int(root, "appearToday"), Int(root, "removeToday"));
                return true;
            }

            if (type == "event")
            {
                var kind = Str(root, "kind") == "REMOVE" ? TagEventKind.Remove : TagEventKind.Appear;
                long? dwell = root.TryGetProperty("dwellMs", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : null;
                ev = new TagEvent(time, kind,
                    Str(root, "readerName") ?? "", Str(root, "alias") ?? "", Str(root, "serial") ?? "",
                    Str(root, "uid") ?? "?", Str(root, "tech") ?? "", Str(root, "atr") ?? "", dwell)
                { Host = host };
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
