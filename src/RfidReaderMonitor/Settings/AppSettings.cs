using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace RfidReaderMonitor.Settings;

public sealed class ReaderProfile
{
    public string Alias { get; set; } = "";
    public string Note { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class CsvSinkSettings
{
    public bool Enabled { get; set; } = true;
    public string Folder { get; set; } = "";
}

public sealed class TcpSinkSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 9750;
}

public sealed class PipeSinkSettings
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = "RfidReaderMonitor";
}

/// <summary>수집 서버(수집 모드로 띄운 이 프로그램)로 이벤트·하트비트를 보내는 클라이언트.</summary>
public sealed class TcpClientSinkSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9760;
}

public sealed class SqlSinkSettings
{
    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = "";
    public string Table { get; set; } = "dbo.RfidEvents";
    public bool AutoCreateTable { get; set; } = true;
}

/// <summary>Modbus 데이터 영역.</summary>
public enum ModbusArea
{
    /// <summary>FC 01, 1비트 읽기/쓰기.</summary>
    Coil,
    /// <summary>FC 02, 1비트 읽기 전용.</summary>
    DiscreteInput,
    /// <summary>FC 03, 16비트 읽기/쓰기.</summary>
    HoldingRegister,
    /// <summary>FC 04, 16비트 읽기 전용.</summary>
    InputRegister
}

/// <summary>
/// Modbus TCP 로 폴링하는 산업용 리더 한 대(헤드 한 개)의 레지스터 맵.
/// 주소는 모두 0 기준(PDU 주소). 문서가 40001 식 1 기준이면 1을 빼서 넣는다.
/// </summary>
public sealed class ModbusReaderSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>리더 이름. 이벤트·CSV·SQL 에 그대로 나간다. 비우면 호스트/유닛으로 만든다.</summary>
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 502;
    public int UnitId { get; set; } = 1;
    public int PollMs { get; set; } = 100;

    /// <summary>태그 있음(Tag Present) 비트 위치.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ModbusArea PresentArea { get; set; } = ModbusArea.DiscreteInput;
    public int PresentAddress { get; set; }
    /// <summary>레지스터 영역일 때 비트 번호(0~15). -1 이면 레지스터 값이 0 이 아니면 있음.</summary>
    public int PresentBit { get; set; }

    /// <summary>UID 가 놓인 레지스터.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ModbusArea UidArea { get; set; } = ModbusArea.InputRegister;
    public int UidAddress { get; set; }
    public int UidBytes { get; set; } = 8;
    /// <summary>각 16비트 워드 안의 두 바이트를 뒤집는다 (리틀엔디언 장치).</summary>
    public bool UidSwapBytes { get; set; }
    /// <summary>워드 순서를 뒤집는다.</summary>
    public bool UidReverseWords { get; set; }

    /// <summary>태그 규격. ATR 이 없으므로 여기서 지정한다. 산업용 HF 는 대개 ISO 15693.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Core.CardFamily TagFamily { get; set; } = Core.CardFamily.Iso15693;

    [JsonIgnore]
    public string EffectiveName => string.IsNullOrWhiteSpace(Name) ? $"Modbus {Host}:{Port}/{UnitId}" : Name.Trim();

    public ModbusReaderSettings Clone() => (ModbusReaderSettings)MemberwiseClone();
}

public sealed class ModbusSettings
{
    public bool Enabled { get; set; }
    /// <summary>접속·응답 대기(ms).</summary>
    public int TimeoutMs { get; set; } = 1000;
    public List<ModbusReaderSettings> Readers { get; set; } = new();
}

public sealed class AppSettings
{
    /// <summary>설정 파일 구조 버전. 구조가 바뀌면 올리고 SettingsStore.Migrate 에 변환을 추가한다.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public int RemovalDebounceMs { get; set; } = 1000;
    public bool ReverseIso15693Uid { get; set; }
    public bool BeepOnAppear { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool LogRawEvents { get; set; } = true;
    public string LogFolder { get; set; } = "";
    public int TestDurationSec { get; set; } = 30;
    public int TestIntervalMs { get; set; } = 100;

    /// <summary>개발자 탭(시뮬레이터) 표시. Ctrl+Shift+D 또는 --dev 인자로 켠다.</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>키: 리더 S/N (없으면 "name:PC/SC 이름").</summary>
    public Dictionary<string, ReaderProfile> Readers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public CsvSinkSettings Csv { get; set; } = new();
    public TcpSinkSettings Tcp { get; set; } = new();
    public TcpClientSinkSettings TcpClient { get; set; } = new();
    public PipeSinkSettings Pipe { get; set; } = new();
    public SqlSinkSettings Sql { get; set; } = new();

    /// <summary>산업용 리더(Modbus TCP) 입력.</summary>
    public ModbusSettings Modbus { get; set; } = new();

    /// <summary>하트비트(상태 메시지) 주기. 0이면 끔.</summary>
    public int HeartbeatSec { get; set; } = 60;

    /// <summary>--collector 로 띄울 때 기본 대기 포트.</summary>
    public int CollectorPort { get; set; } = 9760;

    [JsonIgnore]
    public string EffectiveQueueFolder => Path.Combine(SettingsStore.AppDataFolder, "queue");

    [JsonIgnore]
    public string EffectiveLogFolder => string.IsNullOrWhiteSpace(LogFolder) ? SettingsStore.DefaultLogFolder : LogFolder;

    [JsonIgnore]
    public string EffectiveCsvFolder => string.IsNullOrWhiteSpace(Csv.Folder) ? Path.Combine(EffectiveLogFolder, "events") : Csv.Folder;

    public ReaderProfile GetOrCreate(string key)
    {
        if (!Readers.TryGetValue(key, out var p))
        {
            p = new ReaderProfile();
            Readers[key] = p;
        }
        return p;
    }
}

public static class SettingsStore
{
    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RfidReaderMonitor");

    public static string DefaultLogFolder => Path.Combine(AppDataFolder, "logs");
    public static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, Opts);
                if (s is not null)
                {
                    s.Readers = new Dictionary<string, ReaderProfile>(s.Readers, StringComparer.OrdinalIgnoreCase);
                    Migrate(s);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "설정 읽기 실패, 기본값 사용");
        }
        return new AppSettings();
    }

    /// <summary>옛 버전 설정 파일을 현재 구조로 올린다. 버전별 변환은 아래 switch 에 추가.</summary>
    private static void Migrate(AppSettings s)
    {
        if (s.SchemaVersion >= AppSettings.CurrentSchemaVersion) return;
        var from = s.SchemaVersion;
        // 0: SchemaVersion 필드가 없던 초기 파일. 구조 동일 → 번호만 올림.
        s.SchemaVersion = AppSettings.CurrentSchemaVersion;
        Log.Information("설정 스키마 {From} → {To} 마이그레이션", from, s.SchemaVersion);
        Save(s);
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
            File.Move(tmp, SettingsPath, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "설정 저장 실패");
        }
    }
}
