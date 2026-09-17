using System.IO;
using Json.Schema;

namespace RfidReaderMonitor.Tests;

/// <summary>
/// docs/mqtt/*.schema.json 을 프로세스에 한 번만 읽는다.
/// JsonSchema.Net 은 같은 $id 를 두 번 등록하면 "Overwriting registered schemas is not permitted" 로 던지므로,
/// 테스트 클래스마다 따로 읽으면 안 된다. event · status · replay* 는 state 의 $defs/time 을 $id 상대 경로로 참조한다.
/// </summary>
internal static class TestSchemas
{
    private static readonly string SchemaDir = Path.Combine(AppContext.BaseDirectory, "schemas");

    private static readonly Lazy<Dictionary<string, JsonSchema>> All = new(() =>
    {
        var map = new Dictionary<string, JsonSchema>();
        foreach (var name in new[] { "state", "event", "status", "replay", "replay-done" })
        {
            var schema = JsonSchema.FromFile(Path.Combine(SchemaDir, name + ".schema.json"));
            SchemaRegistry.Global.Register(schema);
            map[name] = schema;
        }
        return map;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static JsonSchema Get(string name) => All.Value[name];
}
