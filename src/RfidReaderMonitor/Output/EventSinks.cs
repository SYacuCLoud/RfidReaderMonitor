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
    /// <summary>이벤트를 만든 PC 이름. 수집 모드에서 원격 PC 이벤트를 받을 때 채워진다.</summary>
    public string Host { get; init; } = Environment.MachineName;

    public string KindText => Kind == TagEventKind.Appear ? "등장" : "제거";
    public string KindCode => Kind == TagEventKind.Appear ? "APPEAR" : "REMOVE";

    /// <summary>화면 표시용: 별명이 있으면 별명, 없으면 PC/SC 이름.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? ReaderName : Alias;

    public string ToJson() => JsonSerializer.Serialize(new
    {
        type = "event",
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
        host = Host
    }, Envelope.JsonOpts);

    public static string CsvHeader => "time,kind,alias,readerName,serial,uid,tech,atr,dwellMs,host";

    /// <summary>화면 복사용 (바인딩 대상).</summary>
    public string CsvLine => ToCsv();

    public string ToCsv() => string.Join(",",
        Time.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        KindText, Q(Alias), Q(ReaderName), Q(Serial), Q(Uid), Q(Tech), Q(Atr), DwellMs?.ToString() ?? "", Q(Host));

    private static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

public interface IEventSink : IAsyncDisposable
{
    string Name { get; }
    string Status { get; }
    event Action<IEventSink>? StatusChanged;
    Task StartAsync(CancellationToken ct);
    Task PublishAsync(TagEvent e, CancellationToken ct);
    /// <summary>주기 상태 메시지. 관심 없는 싱크는 무시한다.</summary>
    Task PublishHeartbeatAsync(HeartbeatMessage hb, CancellationToken ct) => Task.CompletedTask;
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
    public virtual Task PublishHeartbeatAsync(HeartbeatMessage hb, CancellationToken ct) => Task.CompletedTask;
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

    public override Task PublishAsync(TagEvent e, CancellationToken ct) => BroadcastAsync(e.ToJson(), ct);
    public override Task PublishHeartbeatAsync(HeartbeatMessage hb, CancellationToken ct) => BroadcastAsync(hb.ToJson(), ct);

    private async Task BroadcastAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
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

    public override Task PublishAsync(TagEvent e, CancellationToken ct) => BroadcastAsync(e.ToJson(), ct);
    public override Task PublishHeartbeatAsync(HeartbeatMessage hb, CancellationToken ct) => BroadcastAsync(hb.ToJson(), ct);

    private async Task BroadcastAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
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

/// <summary>
/// SQL Server 싱크. 로컬 재전송 큐를 거쳐 이벤트 테이블에 INSERT, 하트비트는 호스트 상태 테이블에 MERGE.
/// 호스트 테이블 이름은 이벤트 테이블 이름의 "Events" 를 "Hosts" 로 바꾼 것 (없으면 뒤에 Hosts 를 붙임).
/// </summary>
public sealed class SqlServerSink : SinkBase
{
    private readonly string _connectionString;
    private readonly string _table;
    private readonly string _hostTable;
    private readonly bool _autoCreate;
    private readonly string _queueFolder;
    private OutboxQueue? _queue;
    private bool _schemaReady;
    public override string Name => "SQL Server";

    /// <summary>마지막 연결·기록 시도가 성공했는지. 실패 중이어도 큐에 쌓아 두고 재시도를 계속한다.</summary>
    public bool IsConnected { get; private set; }

    public SqlServerSink(string connectionString, string table, bool autoCreate, string queueFolder)
    {
        _connectionString = connectionString;
        _table = SanitizeTable(table);
        _hostTable = _table.EndsWith("Events", StringComparison.OrdinalIgnoreCase)
            ? _table[..^"Events".Length] + "Hosts"
            : _table + "Hosts";
        _autoCreate = autoCreate;
        _queueFolder = queueFolder;
    }

    private static string SanitizeTable(string t)
    {
        var clean = new string(t.Where(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '[' or ']').ToArray());
        if (string.IsNullOrWhiteSpace(clean)) throw new ArgumentException("테이블 이름이 비어 있음");
        return clean;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        // 연결이 안 되어도 시작은 성공시킨다. 큐가 쌓아 두고 연결되면 흘려보낸다.
        try
        {
            await EnsureSchemaAsync(ct);
            IsConnected = true;
            Status = $"연결 확인, 테이블 {_table} / {_hostTable}";
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = "연결 안 됨, 큐에 보관 중 - " + ex.Message;
            Log.Warning("SQL 싱크 초기 연결 실패: {Msg}", ex.Message);
        }
        _queue = new OutboxQueue(_queueFolder, "sql", SendAsync);
        _queue.Changed += q =>
        {
            // IsConnected 를 Status 보다 먼저 바꿔야 StatusChanged 를 받는 쪽이 새 값을 본다.
            if (q.LastError is not null) { IsConnected = false; Status = $"연결 안 됨, 대기 {q.Pending} - {q.LastError}"; }
            else if (q.Pending > 0) { IsConnected = true; Status = $"전송 중, 대기 {q.Pending}"; }
            else if (q.LastSentAt is DateTimeOffset t) { IsConnected = true; Status = $"마지막 기록 {t:HH:mm:ss}, 테이블 {_table}"; }
        };
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady) return;
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
);
IF OBJECT_ID(N'{_hostTable}', N'U') IS NULL
CREATE TABLE {_hostTable} (
    Host           NVARCHAR(64)  NOT NULL PRIMARY KEY,
    LastSeen       DATETIMEOFFSET(3) NOT NULL,
    AppVersion     NVARCHAR(32)  NULL,
    ReaderCount    INT NOT NULL,
    OnlineReaders  INT NOT NULL,
    PresentReaders INT NOT NULL,
    AppearToday    INT NOT NULL,
    RemoveToday    INT NOT NULL,
    ReadersJson    NVARCHAR(MAX) NULL
);";
            await using var cmd = new SqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        _schemaReady = true;
    }

    public override Task PublishAsync(TagEvent e, CancellationToken ct)
    {
        _queue?.Enqueue(e.ToJson());
        return Task.CompletedTask;
    }

    public override Task PublishHeartbeatAsync(HeartbeatMessage hb, CancellationToken ct)
    {
        _queue?.Enqueue(hb.ToJson());
        return Task.CompletedTask;
    }

    private async Task<bool> SendAsync(string line, CancellationToken ct)
    {
        if (!Envelope.TryParse(line, out var ev, out var hb)) return true; // 해석 불가 메시지는 버림
        await EnsureSchemaAsync(ct);
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        if (ev is not null)
        {
            await using var cmd = new SqlCommand(
                $"INSERT INTO {_table} (EventTime, Kind, ReaderAlias, ReaderName, ReaderSerial, Uid, Tech, Atr, DwellMs, Host) " +
                "VALUES (@t, @k, @a, @n, @s, @u, @tech, @atr, @d, @h)", conn);
            cmd.Parameters.AddWithValue("@t", ev.Time);
            cmd.Parameters.AddWithValue("@k", ev.KindCode);
            cmd.Parameters.AddWithValue("@a", ev.Alias);
            cmd.Parameters.AddWithValue("@n", ev.ReaderName);
            cmd.Parameters.AddWithValue("@s", (object?)ev.Serial ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@u", ev.Uid);
            cmd.Parameters.AddWithValue("@tech", ev.Tech);
            cmd.Parameters.AddWithValue("@atr", ev.Atr);
            cmd.Parameters.AddWithValue("@d", (object?)ev.DwellMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@h", ev.Host);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }

        if (hb is not null)
        {
            var readersJson = JsonSerializer.Serialize(hb.Readers.Select(r => new { r.Name, r.Alias, r.Serial, r.State, r.Uid }), Envelope.JsonOpts);
            await using var cmd = new SqlCommand($@"
MERGE {_hostTable} AS t
USING (SELECT @h AS Host) AS s ON t.Host = s.Host
WHEN MATCHED THEN UPDATE SET LastSeen=@t, AppVersion=@v, ReaderCount=@rc, OnlineReaders=@on, PresentReaders=@pr, AppearToday=@ap, RemoveToday=@rm, ReadersJson=@rj
WHEN NOT MATCHED THEN INSERT (Host, LastSeen, AppVersion, ReaderCount, OnlineReaders, PresentReaders, AppearToday, RemoveToday, ReadersJson)
VALUES (@h, @t, @v, @rc, @on, @pr, @ap, @rm, @rj);", conn);
            cmd.Parameters.AddWithValue("@h", hb.Host);
            cmd.Parameters.AddWithValue("@t", hb.Time);
            cmd.Parameters.AddWithValue("@v", hb.Version);
            cmd.Parameters.AddWithValue("@rc", hb.Readers.Count);
            cmd.Parameters.AddWithValue("@on", hb.OnlineReaders);
            cmd.Parameters.AddWithValue("@pr", hb.PresentReaders);
            cmd.Parameters.AddWithValue("@ap", hb.AppearToday);
            cmd.Parameters.AddWithValue("@rm", hb.RemoveToday);
            cmd.Parameters.AddWithValue("@rj", readersJson);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        return true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_queue is not null) await _queue.DisposeAsync();
        Status = "중지";
    }
}

// ---------------------------------------------------------------- Dispatcher

/// <summary>이벤트를 큐에 넣고 백그라운드에서 모든 싱크에 전달. 싱크 오류가 감시를 막지 않게 한다.</summary>
public sealed class EventDispatcher : IAsyncDisposable
{
    private readonly Channel<object> _channel = Channel.CreateUnbounded<object>();
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
    public void PublishHeartbeat(HeartbeatMessage hb) => _channel.Writer.TryWrite(hb);

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
            await foreach (var msg in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                var sinks = _sinks;
                foreach (var s in sinks)
                {
                    try
                    {
                        if (msg is TagEvent e) await s.PublishAsync(e, _cts.Token);
                        else if (msg is HeartbeatMessage hb) await s.PublishHeartbeatAsync(hb, _cts.Token);
                    }
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
