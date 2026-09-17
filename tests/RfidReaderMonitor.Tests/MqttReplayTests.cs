using System.IO;
using System.Text;
using System.Text.Json;
using Json.Schema;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.Output;
using Xunit;

namespace RfidReaderMonitor.Tests;

/// <summary>
/// 재발행 — 요청 해석, CSV 되읽기(ToCsv 와 왕복), 구간 · 날짜 계산, done 페이로드 스키마.
/// </summary>
public sealed class MqttReplayTests
{
    private static JsonSchema Load(string name) => TestSchemas.Get(name.Replace(".schema.json", ""));

    private static void AssertValid(JsonSchema schema, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var errors = string.Join("\n", result.Details.Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} {e.Value}")));
        Assert.True(result.IsValid, $"스키마 위반:\n{errors}\n페이로드: {json}");
    }

    [Fact]
    public void Request_parses_iso_and_epoch_and_rejects_bad_ranges()
    {
        Assert.True(ReplayRequest.TryParse("""{"v":1,"type":"replay","from":"2026-09-17T16:31:00+09:00","to":"2026-09-18T07:05:00+09:00","requestId":"abc","host":"C1101"}""", out var r, out _));
        Assert.NotNull(r);
        Assert.Equal("abc", r!.RequestId);
        Assert.Equal("C1101", r.Host);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 16, 31, 0, TimeSpan.FromHours(9)), r.From);
        Assert.True(r.IsFor("c1101"));
        Assert.False(r.IsFor("C1102"));

        Assert.True(ReplayRequest.TryParse("""{"from":1789630260000,"to":1789682700000}""", out var epoch, out _));
        Assert.True(epoch!.IsFor("ANY"), "host 가 없으면 모두 답한다");
        Assert.NotEmpty(epoch.RequestId);

        Assert.False(ReplayRequest.TryParse("""{"from":"2026-09-18T00:00:00Z","to":"2026-09-17T00:00:00Z"}""", out _, out var err1));
        Assert.Contains("앞", err1);
        Assert.False(ReplayRequest.TryParse("""{"from":"2026-01-01T00:00:00Z","to":"2026-03-01T00:00:00Z"}""", out _, out var err2));
        Assert.Contains("일", err2);
        Assert.False(ReplayRequest.TryParse("""{"type":"event","from":"2026-09-17T00:00:00Z","to":"2026-09-18T00:00:00Z"}""", out _, out _));
        Assert.False(ReplayRequest.TryParse("깨진", out _, out var err3));
        Assert.Contains("JSON", err3);
    }

    [Fact]
    public void Local_days_cover_every_calendar_day_the_range_touches()
    {
        var from = new DateTimeOffset(new DateTime(2026, 9, 17, 16, 31, 0, DateTimeKind.Local));
        var to = new DateTimeOffset(new DateTime(2026, 9, 19, 0, 5, 0, DateTimeKind.Local));
        var r = new ReplayRequest(from, to, "x", null);
        var days = r.LocalDays().ToList();
        Assert.Equal(new[] { new DateTime(2026, 9, 17), new DateTime(2026, 9, 18), new DateTime(2026, 9, 19) }, days);
        Assert.True(r.Contains(from));
        Assert.True(r.Contains(to));
        Assert.False(r.Contains(to.AddMilliseconds(1)));
    }

    [Fact]
    public void Csv_roundtrip_restores_the_event_including_quoted_fields_and_missing_host_column()
    {
        var time = new DateTimeOffset(new DateTime(2026, 9, 17, 16, 31, 45, 409, DateTimeKind.Local));
        var original = new TagEvent(time, TagEventKind.Remove, "ACS ACR1552 1S CL Reader PICC 0", "시메, \"입고\"", "RR657-005504", "58 9D 7A 17 53 01 04 E0", "ISO 15693", "", 831464) { Host = "C1101" };
        var header = ReplayCsv.HeaderMap("﻿" + TagEvent.CsvHeader);
        var back = ReplayCsv.ParseEvent(header, original.ToCsv(), "OTHER");
        Assert.NotNull(back);
        Assert.Equal(original.Time, back!.Time);
        Assert.Equal(TagEventKind.Remove, back.Kind);
        Assert.Equal(original.Alias, back.Alias);
        Assert.Equal(original.ReaderName, back.ReaderName);
        Assert.Equal(original.Serial, back.Serial);
        Assert.Equal(original.Uid, back.Uid);
        Assert.Equal(original.Tech, back.Tech);
        Assert.Equal(831464, back.DwellMs);
        Assert.Equal("C1101", back.Host);
        Assert.Equal(original.ToJson(), back.ToJson());

        // 옛 판 파일: host 열 없음, 등장은 dwellMs 빈 칸.
        var oldHeader = ReplayCsv.HeaderMap("time,kind,alias,readerName,serial,uid,tech,atr,dwellMs");
        var appear = ReplayCsv.ParseEvent(oldHeader, "2026-09-17 09:00:00.000,등장,01-01,Reader,RR1,AA BB,ISO 15693,,", "THIS-PC");
        Assert.NotNull(appear);
        Assert.Equal(TagEventKind.Appear, appear!.Kind);
        Assert.Null(appear.DwellMs);
        Assert.Equal("THIS-PC", appear.Host);

        Assert.Null(ReplayCsv.ParseEvent(header, "", "x"));
        Assert.Null(ReplayCsv.ParseEvent(header, "not a time,등장,,,,,,,,", "x"));
        Assert.Null(ReplayCsv.ParseEvent(header, "2026-09-17 09:00:00.000,모름,,,,,,,,", "x"));
    }

    [Fact]
    public void Read_filters_by_range_across_day_files_and_caps_the_count()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rfid-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            TagEvent Ev(DateTime local, TagEventKind kind, string uid) =>
                new(new DateTimeOffset(local), kind, "R", "01-01", "RR1", uid, "", "", kind == TagEventKind.Remove ? 1000 : null);

            void Write(DateTime day, params TagEvent[] events)
            {
                var path = ReplayCsv.FileFor(folder, day);
                using var w = new StreamWriter(path, false, new UTF8Encoding(true));
                w.WriteLine(TagEvent.CsvHeader);
                foreach (var e in events) w.WriteLine(e.ToCsv());
            }

            Write(new DateTime(2026, 9, 17),
                Ev(new DateTime(2026, 9, 17, 16, 0, 0), TagEventKind.Appear, "BEFORE"),
                Ev(new DateTime(2026, 9, 17, 16, 31, 45, 409), TagEventKind.Appear, "A1"),
                Ev(new DateTime(2026, 9, 17, 23, 59, 59, 999), TagEventKind.Remove, "A1"));
            Write(new DateTime(2026, 9, 18),
                Ev(new DateTime(2026, 9, 18, 7, 4, 35, 824), TagEventKind.Appear, "B1"),
                Ev(new DateTime(2026, 9, 18, 7, 30, 0), TagEventKind.Appear, "AFTER"));

            var req = new ReplayRequest(new DateTimeOffset(new DateTime(2026, 9, 17, 16, 31, 0)), new DateTimeOffset(new DateTime(2026, 9, 18, 7, 5, 0)), "x", null);
            var (events, files, truncated) = ReplayCsv.Read(folder, req, "PC");
            Assert.Equal(2, files);
            Assert.False(truncated);
            Assert.Equal(new[] { "A1", "A1", "B1" }, events.Select(e => e.Uid).ToArray());
            // ToCsv 는 host 열을 쓰므로(이 PC 이름) 되읽은 host 도 그것이다. 기본값 "PC" 는 host 열이 없는 옛 파일에만 쓰인다.
            Assert.All(events, e => Assert.Equal(Environment.MachineName, e.Host));

            var (capped, _, cut) = ReplayCsv.Read(folder, req, "PC", maxEvents: 2);
            Assert.True(cut);
            Assert.Equal(2, capped.Count);

            // 파일이 없는 날은 조용히 건너뛴다.
            var none = new ReplayRequest(new DateTimeOffset(new DateTime(2026, 8, 1)), new DateTimeOffset(new DateTime(2026, 8, 2)), "x", null);
            Assert.Equal(0, ReplayCsv.Read(folder, none, "PC").Files);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [Fact]
    public void Request_example_and_done_payload_match_their_schemas()
    {
        var request = Load("replay.schema.json");
        AssertValid(request, """{"v":1,"type":"replay","from":"2026-09-17T16:31:00.000+09:00","to":"2026-09-18T07:05:00.000+09:00","requestId":"k3j9x2"}""");

        var done = Load("replay-done.schema.json");
        var from = new DateTimeOffset(2026, 9, 17, 16, 31, 0, TimeSpan.FromHours(9));
        var ok = new ReplayDone("PC-LINE1", "k3j9x2", from, from.AddHours(14), 412, false, 2, null).ToJson();
        AssertValid(done, ok);
        Assert.Contains("\"type\":\"replay-done\"", ok);
        Assert.DoesNotContain("error", ok);
        var failed = new ReplayDone("PC-LINE1", "k3j9x2", from, from.AddHours(14), 0, false, 0, "CSV 폴더 없음").ToJson();
        AssertValid(done, failed);
        Assert.Contains("CSV 폴더 없음", failed);
    }
}
