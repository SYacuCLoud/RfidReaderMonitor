using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using RfidReaderMonitor.Core;
using Serilog;

namespace RfidReaderMonitor.Output;

/// <summary>외부로 나가는 확정 이벤트 (별명·시리얼이 채워진 형태).</summary>
public sealed record TagEvent(
    DateTimeOffset Time,
    TagEventKind Kind,
    string ReaderName,
    string Alias,
    string Serial,
    string Uid,
    string Tech,
    string Atr,
    long? DwellMs)
{
    public string KindText => Kind == TagEventKind.Appear ? "등장" : "제거";
    public string KindCode => Kind == TagEventKind.Appear ? "APPEAR" : "REMOVE";

    /// <summary>화면 표시용: 별명이 있으면 별명, 없으면 PC/SC 이름.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? ReaderName : Alias;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string ToJson() => JsonSerializer.Serialize(new
    {
        time = Time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        kind = KindCode,
        reader = DisplayName,
        alias = Alias,
        readerName = ReaderName,
        serial = Serial,
        uid = Uid,
        tech = Tech,
        atr = Atr,
        dwellMs = DwellMs,
        host = Environment.MachineName
    }, JsonOpts);

    public static string CsvHeader => "time,kind,alias,readerName,serial,uid,tech,atr,dwellMs";

    /// <summary>화면 복사용 (바인딩 대상).</summary>
    public string CsvLine => ToCsv();

    public string ToCsv() => string.Join(",",
        Time.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        KindText, Q(Alias), Q(ReaderName), Q(Serial), Q(Uid), Q(Tech), Q(Atr), DwellMs?.ToString() ?? "");

    private static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

public interface IEventSink : IAsyncDisposable
{
    string Name { get; }
    string Status { get; }
    event Action<IEventSink>? StatusChanged;
    Task StartAsync(CancellationToken ct);
    Task PublishAsync(TagEvent e, CancellationToken ct);
}

public abstract class SinkBase : IEventSink
{
    private string _status = "대기";
    public abstract string Name { get; }
    public string Status
    {
        get => _status;
        protected set { _status = value; StatusChanged?.Invoke(this); }
    }
    public event Action<IEventSink>? StatusChanged;
    public abstract Task StartAsync(CancellationToken ct);
    public abstract Task PublishAsync(TagEvent e, CancellationToken ct);
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ---------------------------------------------------------------- CSV

public sealed class CsvEventSink : SinkBase
{
    private readonly string _folder;
    private readonly object _lock = new();
    public override string Name => "CSV";

    public CsvEventSink(string folder) => _folder = folder;

    public override Task StartAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_folder);
        Status = $"폴더 {_folder}";
        return Task.CompletedTask;
    }

    public override Task PublishAsync(TagEvent e, CancellationToken ct)
    {
        var path = Path.Combine(_folder, $"events-{e.Time:yyyyMMdd}.csv");
        lock (_lock)
        {
            var isNew = !File.Exists(path);
            using var w = new StreamWriter(path, true, new UTF8Encoding(true));
            if (isNew) w.WriteLine(TagEvent.CsvHeader);
            w.WriteLine(e.ToCsv());
        }
        return Task.CompletedTask;
    }
}

/// <summary>원신호 로그 (확정 이벤트와 별도 파일).</summary>
public sealed class RawSignalLogger
{
    private readonly string _folder;
    private readonly object _lock = new();
    public bool Enabled { get; set; } = true;

    public RawSignalLogger(string folder) => _folder = folder;

    public void Write(RawSignal s)
    {
        if (!Enabled) return;
        try
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, $"raw-{s.Time:yyyyMMdd}.csv");
            lock (_lock)
            {
                var isNew = !File.Exists(path);
                using var w = new StreamWriter(path, true, new UTF8Encoding(true));
                if (isNew) w.WriteLine("time,reader,state,atr,note");
                w.WriteLine($"{s.Time:yyyy-MM-dd HH:mm:ss.fff},\"{s.ReaderName}\",{s.State},{s.Atr},\"{s.Note.Replace("\"", "\"\"")}\"");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "원신호 로그 기록 실패");
        }
    }
}

// ---------------------------------------------------------------- TCP

/// <summary>TCP 서버. 접속한 클라이언트 전부에 JSON 한 줄씩 방송.</summary>
public sealed class TcpBroadcastSink : SinkBase
{
    private readonly int _port;
    private TcpListener? _listener;
    private readonly List<TcpClient> _clients = new();
    private CancellationTokenSource? _cts;
    public override string Name => "TCP";

    public TcpBroadcastSink(int port) => _port = port;

    public override Task StartAsync(CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Status = $"포트 {_port} 대기, 클라이언트 0";
        _ = AcceptLoop(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var c = await _listener.AcceptTcpClientAsync(ct);
                c.NoDelay = true;
                lock (_clients) _clients.Add(c);
                UpdateStatus();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Debug(ex, "TCP accept 오류");
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    private void UpdateStatus()
    {
        int n;
        lock (_clients) n = _clients.Count;
        Status = $"포트 {_port} 대기, 클라이언트 {n}";
    }

    public override async Task PublishAsync(TagEvent e, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(e.ToJson() + "\n");
        List<TcpClient> snapshot;
        lock (_clients) snapshot = _clients.ToList();
        var dead = new List<TcpClient>();
        foreach (var c in snapshot)
        {
            try { await c.GetStream().WriteAsync(bytes, ct); }
            catch { dead.Add(c); }
        }
        if (dead.Count > 0)
        {
            lock (_clients) foreach (var d in dead) { _clients.Remove(d); d.Dispose(); }
            UpdateStatus();
        }
    }

    public override ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        lock (_clients) { foreach (var c in _clients) c.Dispose(); _clients.Clear(); }
        Status = "중지";
        return ValueTask.CompletedTask;
    }
}

// ---------------------------------------------------------------- Named pipe

public sealed class NamedPipeSink : SinkBase
{
    private readonly string _pipeName;
    private readonly List<NamedPipeServerStream> _clients = new();
    private CancellationTokenSource? _cts;
    public override string Name => "명명된 파이프";

    public NamedPipeSink(string pipeName) => _pipeName = pipeName;

    public override Task StartAsync(CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_cts.Token);
        Status = $@"\\.\pipe\{_pipeName} 대기, 클라이언트 0";
        return Task.CompletedTask;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? s = null;
            try
            {
                s = new NamedPipeServerStream(_pipeName, PipeDirection.Out, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await s.WaitForConnectionAsync(ct);
                lock (_clients) _clients.Add(s);
                UpdateStatus();
            }
            catch (OperationCanceledException) { s?.Dispose(); break; }
            catch (Exception ex)
            {
                s?.Dispose();
                Log.Debug(ex, "파이프 accept 오류");
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    private void UpdateStatus()
    {
        int n;
        lock (_clients) n = _clients.Count;
        Status = $@"\\.\pipe\{_pipeName} 대기, 클라이언트 {n}";
    }

    public override async Task PublishAsync(TagEvent e, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(e.ToJson() + "\n");
        List<NamedPipeServerStream> snapshot;
        lock (_clients) snapshot = _clients.ToList();
        var dead = new List<NamedPipeServerStream>();
        foreach (var c in snapshot)
        {
            try { await c.WriteAsync(bytes, ct); await c.FlushAsync(ct); }
            catch { dead.Add(c); }
        }
        if (dead.Count > 0)
        {
            lock (_clients) foreach (var d in dead) { _clients.Remove(d); d.Dispose(); }
            UpdateStatus();
        }
    }

    public override ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        lock (_clients) { foreach (var c in _clients) c.Dispose(); _clients.Clear(); }
        Status = "중지";
        return ValueTask.CompletedTask;
    }
}

// ---------------------------------------------------------------- SQL Server

public sealed class SqlServerSink : SinkBase
{
    private readonly string _connectionString;
    private readonly string _table;
    private readonly bool _autoCreate;
    public override string Name => "SQL Server";

    public SqlServerSink(string connectionString, string table, bool autoCreate)
    {
        _connectionString = connectionString;
        _table = SanitizeTable(table);
        _autoCreate = autoCreate;
    }

    private static string SanitizeTable(string t)
    {
        var clean = new string(t.Where(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '[' or ']').ToArray());
        if (string.IsNullOrWhiteSpace(clean)) throw new ArgumentException("테이블 이름이 비어 있음");
        return clean;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        if (_autoCreate)
        {
            var sql = $@"
IF OBJECT_ID(N'{_table}', N'U') IS NULL
CREATE TABLE {_table} (
    Id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    EventTime    DATETIMEOFFSET(3) NOT NULL,
    Kind         NVARCHAR(10)  NOT NULL,
    ReaderAlias  NVARCHAR(100) NOT NULL,
    ReaderName   NVARCHAR(200) NOT NULL,
    ReaderSerial NVARCHAR(100) NULL,
    Uid          NVARCHAR(64)  NOT NULL,
    Tech         NVARCHAR(100) NULL,
    Atr          NVARCHAR(128) NULL,
    DwellMs      BIGINT NULL,
    Host         NVARCHAR(64)  NULL
);";
            await using var cmd = new SqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Status = $"연결 확인, 테이블 {_table}";
    }

    public override async Task PublishAsync(TagEvent e, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"INSERT INTO {_table} (EventTime, Kind, ReaderAlias, ReaderName, ReaderSerial, Uid, Tech, Atr, DwellMs, Host) " +
            "VALUES (@t, @k, @a, @n, @s, @u, @tech, @atr, @d, @h)", conn);
        cmd.Parameters.AddWithValue("@t", e.Time);
        cmd.Parameters.AddWithValue("@k", e.KindCode);
        cmd.Parameters.AddWithValue("@a", e.Alias);
        cmd.Parameters.AddWithValue("@n", e.ReaderName);
        cmd.Parameters.AddWithValue("@s", (object?)e.Serial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@u", e.Uid);
        cmd.Parameters.AddWithValue("@tech", e.Tech);
        cmd.Parameters.AddWithValue("@atr", e.Atr);
        cmd.Parameters.AddWithValue("@d", (object?)e.DwellMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@h", Environment.MachineName);
        await cmd.ExecuteNonQueryAsync(ct);
        Status = $"마지막 기록 {DateTime.Now:HH:mm:ss}";
    }
}

// ---------------------------------------------------------------- Dispatcher

/// <summary>이벤트를 큐에 넣고 백그라운드에서 모든 싱크에 전달. 싱크 오류가 감시를 막지 않게 한다.</summary>
public sealed class EventDispatcher : IAsyncDisposable
{
    private readonly Channel<TagEvent> _channel = Channel.CreateUnbounded<TagEvent>();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _reconfigure = new(1, 1);
    private List<IEventSink> _sinks = new();
    private readonly Task _pump;

    public IReadOnlyList<IEventSink> Sinks => _sinks;
    public event Action<IEventSink, string>? SinkError;
    public event Action? SinksChanged;

    public EventDispatcher()
    {
        _pump = Task.Run(PumpAsync);
    }

    public void Publish(TagEvent e) => _channel.Writer.TryWrite(e);

    /// <summary>싱크 교체. 기존 싱크는 정리한다. 시작 실패한 싱크는 오류로 보고하고 제외.</summary>
    public async Task ReconfigureAsync(IEnumerable<IEventSink> sinks)
    {
        await _reconfigure.WaitAsync();
        try
        {
            foreach (var old in _sinks)
            {
                try { await old.DisposeAsync(); } catch { }
            }
            var next = new List<IEventSink>();
            foreach (var s in sinks)
            {
                try
                {
                    await s.StartAsync(_cts.Token);
                    next.Add(s);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "싱크 시작 실패 {Name}", s.Name);
                    SinkError?.Invoke(s, ex.Message);
                }
            }
            _sinks = next;
            SinksChanged?.Invoke();
        }
        finally
        {
            _reconfigure.Release();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var e in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                var sinks = _sinks;
                foreach (var s in sinks)
                {
                    try { await s.PublishAsync(e, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "싱크 전송 실패 {Name}", s.Name);
                        SinkError?.Invoke(s, ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _pump; } catch { }
        foreach (var s in _sinks)
        {
            try { await s.DisposeAsync(); } catch { }
        }
    }
}
