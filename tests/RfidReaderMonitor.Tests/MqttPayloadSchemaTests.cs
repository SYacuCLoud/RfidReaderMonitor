using System.IO;
using System.Text.Json;
using Json.Schema;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.Output;
using Xunit;

namespace RfidReaderMonitor.Tests;

/// <summary>
/// MQTT 로 나가는 페이로드가 docs/mqtt/*.schema.json 과 맞는지.
/// 발행 코드(MqttSink · TagEvent)가 만든 실제 JSON 을 스키마로 검증한다 — 문서와 코드가 따로 놀지 않게.
/// </summary>
public sealed class MqttPayloadSchemaTests
{
    private static readonly string SchemaDir = Path.Combine(AppContext.BaseDirectory, "schemas");

    private static readonly Lazy<(JsonSchema State, JsonSchema Event, JsonSchema Status)> Schemas = new(() =>
    {
        var state = JsonSchema.FromFile(Path.Combine(SchemaDir, "state.schema.json"));
        var ev = JsonSchema.FromFile(Path.Combine(SchemaDir, "event.schema.json"));
        var status = JsonSchema.FromFile(Path.Combine(SchemaDir, "status.schema.json"));
        // event · status 는 state 의 $defs/time 을 $id 기준 상대 경로로 참조한다. 등록해 두어야 풀린다.
        SchemaRegistry.Global.Register(state);
        SchemaRegistry.Global.Register(ev);
        SchemaRegistry.Global.Register(status);
        return (state, ev, status);
    });

    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    private static void AssertValid(JsonSchema schema, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = schema.Evaluate(doc.RootElement, Options);
        var errors = string.Join("\n", result.Details
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} {e.Value}")));
        Assert.True(result.IsValid, $"스키마 위반:\n{errors}\n페이로드: {json}");
    }

    private static void AssertInvalid(JsonSchema schema, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = schema.Evaluate(doc.RootElement, Options);
        Assert.False(result.IsValid, $"통과하면 안 되는 페이로드가 통과함: {json}");
    }

    private static readonly DateTimeOffset At = new(2026, 9, 14, 15, 32, 32, 931, TimeSpan.FromHours(9));

    [Fact]
    public void State_present_and_empty_match_schema()
    {
        var present = new MqttSink.ReaderState("RR657-005592", "ACS ACR1552 1", "1번 저울", "RR657-005592",
            Present: true, Online: true, "E0040150ABCDEF01", "ISO 15693", "PRESENT", At);
        AssertValid(Schemas.Value.State, present.ToJson("PC-LINE1"));

        var empty = present with { Present = false, Uid = "", Tech = "", StateText = "EMPTY" };
        AssertValid(Schemas.Value.State, empty.ToJson("PC-LINE1"));

        var offline = present with { Present = false, Online = false, Uid = "", Tech = "", StateText = "뽑힘" };
        AssertValid(Schemas.Value.State, offline.ToJson("PC-LINE1"));
    }

    [Fact]
    public void State_uses_time_not_at_and_carries_version()
    {
        var st = new MqttSink.ReaderState("K", "R", "", "", true, true, "E0", "", "PRESENT", At);
        using var doc = JsonDocument.Parse(st.ToJson("H"));
        var root = doc.RootElement;
        Assert.Equal(Envelope.Version, root.GetProperty("v").GetInt32());
        Assert.Equal("state", root.GetProperty("type").GetString());
        Assert.Equal("2026-09-14T15:32:32.931+09:00", root.GetProperty("time").GetString());
        Assert.False(root.TryGetProperty("at", out _), "시각 필드는 이벤트와 같은 time 이어야 한다");
        Assert.True(root.GetProperty("reader").GetString() == "R", "별명이 없으면 리더 이름");
    }

    [Fact]
    public void Event_appear_and_remove_match_schema()
    {
        var appear = new TagEvent(At, TagEventKind.Appear, "ACS ACR1552 1", "1번 저울", "RR657-005592",
            "E0040150ABCDEF01", "ISO 15693", "3B8F8001804F0CA0000003060300010000000068", null) { Host = "PC-LINE1" };
        AssertValid(Schemas.Value.Event, appear.ToJson());
        Assert.DoesNotContain("dwellMs", appear.ToJson());

        var remove = appear with { Kind = TagEventKind.Remove, Time = At.AddSeconds(7), DwellMs = 7069 };
        AssertValid(Schemas.Value.Event, remove.ToJson());

        // 별명 · S/N 이 없는 리더(빈 문자열)도 형식은 지킨다.
        var bare = new TagEvent(At, TagEventKind.Appear, "Modbus 192.168.0.50:502/1", "", "", "?", "", "", null);
        AssertValid(Schemas.Value.Event, bare.ToJson());
    }

    [Fact]
    public void Status_online_and_offline_match_schema()
    {
        var hb = new HeartbeatMessage(At, "PC-LINE1", "0.3.2",
            new List<HeartbeatReader>
            {
                new("ACS ACR1552 1", "1번 저울", "RR657-005592", "PRESENT", "E0040150ABCDEF01"),
                new("Modbus 192.168.0.50:502/1", "", "", "EMPTY", ""),
                new("ACS ACR1552 2", "", "RR657-005593", "뽑힘", ""),
            }, 5, 4);

        var online = MqttSink.OnlineStatusJson(hb, "PC-LINE1");
        AssertValid(Schemas.Value.Status, online);
        using var doc = JsonDocument.Parse(online);
        Assert.Equal(3, doc.RootElement.GetProperty("readerCount").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("onlineReaders").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("presentReaders").GetInt32());

        var offline = MqttSink.OfflineStatusJson("PC-LINE1");
        AssertValid(Schemas.Value.Status, offline);
        Assert.DoesNotContain("time", offline);
    }

    [Fact]
    public void Schema_examples_validate_against_their_own_schema()
    {
        foreach (var (name, schema) in new[] { ("state", Schemas.Value.State), ("event", Schemas.Value.Event), ("status", Schemas.Value.Status) })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(SchemaDir, $"{name}.schema.json")));
            var examples = doc.RootElement.GetProperty("examples");
            Assert.True(examples.GetArrayLength() > 0, $"{name}: examples 가 비어 있다");
            foreach (var example in examples.EnumerateArray())
                AssertValid(schema, example.GetRawText());
        }
    }

    [Fact]
    public void Schemas_reject_missing_version_and_wrong_types()
    {
        // 스키마가 실제로 무엇인가를 막는지 — 전부 통과시키는 스키마면 이 테스트가 잡는다.
        AssertInvalid(Schemas.Value.State, """{"type":"state","time":"2026-09-14T15:32:32.931+09:00","host":"H","reader":"R","serial":"","present":true,"online":true,"uid":"E0","state":"PRESENT"}""");
        AssertInvalid(Schemas.Value.State, """{"v":1,"type":"state","time":"2026-09-14 15:32:32","host":"H","reader":"R","serial":"","present":true,"online":true,"uid":"E0","state":"PRESENT"}""");
        AssertInvalid(Schemas.Value.Event, """{"v":1,"type":"event","time":"2026-09-14T15:32:32.931+09:00","kind":"GONE","reader":"R","readerName":"","serial":"","uid":"E0","host":"H"}""");
        AssertInvalid(Schemas.Value.Status, """{"v":1,"type":"status","online":true,"host":"H"}""");
        AssertValid(Schemas.Value.Status, """{"v":1,"type":"status","online":false,"host":"H"}""");
    }

    [Theory]
    [InlineData("RR657-005592", "별명", "이름", "RR657-005592")]
    [InlineData("", "1번 저울", "ACS ACR1552 1", "1번_저울")]
    [InlineData("", "", "Modbus 192.168.0.50:502/1", "Modbus_192.168.0.50:502_1")]
    [InlineData("a/b+c#d e", "", "", "a_b_c_d_e")]
    [InlineData("", "", "   ", "unknown")]
    public void Reader_key_prefers_serial_then_alias_then_name_and_is_topic_safe(string serial, string alias, string name, string expected)
    {
        Assert.Equal(expected, MqttSink.ReaderKey(serial, alias, name));
    }
}
