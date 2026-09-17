using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RfidReaderMonitor.Core;

namespace RfidReaderMonitor.Output;

/// <summary>
/// 재발행 요청 — 현황판(구독자)이 어떤 시간 구간의 이벤트를 놓쳤을 때 "그 구간을 다시 내 달라" 고 감시 PC 에 보내는 메시지.
///
/// 토픽 `{prefix}/{site}/replay`(사업장의 모든 PC) 또는 `{prefix}/{site}/host/{PC}/replay`(한 PC).
/// 페이로드 `{ "v":1, "type":"replay", "from":"2026-09-17T16:31:00+09:00", "to":"2026-09-18T07:05:00+09:00", "requestId":"…", "host":"C1101"(선택) }`.
/// 감시 PC 는 자기 CSV 출력(events-yyyyMMdd.csv)에서 그 구간을 읽어 `{prefix}/{site}/reader/{key}/replay` 로 다시 내고,
/// 끝나면 `{prefix}/{site}/host/{PC}/replay-done` 으로 건수를 알린다. 스키마: docs/mqtt/replay.schema.json · replay-done.schema.json
/// </summary>
public sealed record ReplayRequest(DateTimeOffset From, DateTimeOffset To, string RequestId, string? Host)
{
    /// <summary>한 요청이 다룰 수 있는 최대 폭. CSV 는 하루 한 파일이라 폭이 넓어도 읽기는 가볍지만, 브로커로 쏟는 양을 막는다.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);

    /// <summary>한 요청이 다시 낼 최대 건수. 넘으면 앞부분만 내고 done 에 truncated 를 표시한다.</summary>
    public const int MaxEvents = 20_000;

    public static bool TryParse(string json, out ReplayRequest? request, out string? error)
    {
        request = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "객체가 아님"; return false; }
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != "replay")
            { error = "type 이 replay 가 아님"; return false; }
            if (!TryTime(root, "from", out var from)) { error = "from 을 읽을 수 없음"; return false; }
            if (!TryTime(root, "to", out var to)) { error = "to 를 읽을 수 없음"; return false; }
            if (to <= from) { error = "to 가 from 보다 앞"; return false; }
            if (to - from > MaxRange) { error = $"폭이 {MaxRange.TotalDays:0}일을 넘음"; return false; }
            var id = root.TryGetProperty("requestId", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString() ?? "" : "";
            if (id.Length == 0) id = Guid.NewGuid().ToString("N")[..12];
            string? host = root.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(host)) host = null;
            request = new ReplayRequest(from, to, id, host);
            return true;
        }
        catch (JsonException ex)
        {
            error = "JSON 오류: " + ex.Message;
            return false;
        }
    }

    private static bool TryTime(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.String)
            return DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var ms))
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return true;
        }
        return false;
    }

    /// <summary>이 PC 가 답해야 하는 요청인가. host 가 비어 있으면 모두, 있으면 이름이 같을 때만(대소문자 무시).</summary>
    public bool IsFor(string machineName) => Host is null || string.Equals(Host, machineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>구간이 걸치는 로컬 날짜들(CSV 파일 하루 하나). 시각은 이 PC 시간대로 본다 — CSV 가 그 시각으로 적혀 있다.</summary>
    public IEnumerable<DateTime> LocalDays()
    {
        var first = From.ToLocalTime().Date;
        var last = To.ToLocalTime().Date;
        for (var d = first; d <= last; d = d.AddDays(1)) yield return d;
    }

    public bool Contains(DateTimeOffset t) => t >= From && t <= To;
}

/// <summary>재발행이 끝났다는 알림. `{prefix}/{site}/host/{PC}/replay-done`.</summary>
public sealed record ReplayDone(string Host, string RequestId, DateTimeOffset From, DateTimeOffset To, int Count, bool Truncated, int Files, string? Error)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        v = Envelope.Version,
        type = "replay-done",
        host = Host,
        requestId = RequestId,
        from = From.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        to = To.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        count = Count,
        truncated = Truncated,
        files = Files,
        error = Error
    }, Envelope.JsonOpts);
}

/// <summary>
/// CSV 출력(`CsvEventSink`, events-yyyyMMdd.csv)을 다시 <see cref="TagEvent"/> 로 읽는다. 재발행의 재료다.
/// 열은 머리글로 찾는다 — 옛 판 파일에 `host` 열이 없어도 읽힌다(이 PC 이름으로 채움).
/// 시각은 파일에 시간대 없이 이 PC 로컬 시각으로 적혀 있으므로 로컬 오프셋을 붙인다.
/// </summary>
public static class ReplayCsv
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>RFC 4180 식 한 줄 나누기. <see cref="Csv.Q"/> 가 만든 인용을 되돌린다.</summary>
    public static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>머리글 → 열 번호. BOM 이 붙어 있어도 첫 열 이름을 읽는다.</summary>
    public static Dictionary<string, int> HeaderMap(string headerLine)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cols = SplitLine(headerLine.TrimStart('﻿'));
        for (int i = 0; i < cols.Count; i++) map[cols[i].Trim()] = i;
        return map;
    }

    /// <summary>한 줄을 이벤트로. 시각 · 종류를 못 읽으면 null.</summary>
    public static TagEvent? ParseEvent(Dictionary<string, int> header, string line, string defaultHost)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var f = SplitLine(line);
        string Col(string name) => header.TryGetValue(name, out var i) && i < f.Count ? f[i] : "";

        if (!DateTime.TryParseExact(Col("time"), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        // Kind Unspecified → 이 PC 로컬 오프셋(파일이 로컬 시각이다).
        var time = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified));

        var kindText = Col("kind");
        TagEventKind kind;
        if (kindText is "등장" or "APPEAR" or "Appear") kind = TagEventKind.Appear;
        else if (kindText is "제거" or "REMOVE" or "Remove") kind = TagEventKind.Remove;
        else return null;

        long? dwell = long.TryParse(Col("dwellMs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
        var host = Col("host");
        return new TagEvent(time, kind, Col("readerName"), Col("alias"), Col("serial"), Col("uid"), Col("tech"), Col("atr"), dwell)
        {
            Host = string.IsNullOrWhiteSpace(host) ? defaultHost : host
        };
    }

    public static string FileFor(string folder, DateTime day) => Path.Combine(folder, $"events-{day:yyyyMMdd}.csv");

    /// <summary>
    /// 구간의 이벤트를 파일 순 · 줄 순으로 읽는다. 쓰기 중인 파일도 읽을 수 있게 ReadWrite 공유로 연다.
    /// 최대 건수를 넘으면 멈추고 truncated 를 돌려준다.
    /// </summary>
    public static (List<TagEvent> Events, int Files, bool Truncated) Read(string folder, ReplayRequest request, string defaultHost, int maxEvents = ReplayRequest.MaxEvents)
    {
        var events = new List<TagEvent>();
        int files = 0;
        foreach (var day in request.LocalDays())
        {
            var path = FileFor(folder, day);
            if (!File.Exists(path)) continue;
            files++;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var headerLine = reader.ReadLine();
            if (headerLine is null) continue;
            var header = HeaderMap(headerLine);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var e = ParseEvent(header, line, defaultHost);
                if (e is null || !request.Contains(e.Time)) continue;
                if (events.Count >= maxEvents) return (events, files, true);
                events.Add(e);
            }
        }
        return (events, files, false);
    }
}
