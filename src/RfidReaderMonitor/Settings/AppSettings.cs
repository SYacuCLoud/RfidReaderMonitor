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

    /// <summary>키: 리더 S/N (없으면 "name:PC/SC 이름").</summary>
    public Dictionary<string, ReaderProfile> Readers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public CsvSinkSettings Csv { get; set; } = new();
    public TcpSinkSettings Tcp { get; set; } = new();
    public TcpClientSinkSettings TcpClient { get; set; } = new();
    public PipeSinkSettings Pipe { get; set; } = new();
    public SqlSinkSettings Sql { get; set; } = new();

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
