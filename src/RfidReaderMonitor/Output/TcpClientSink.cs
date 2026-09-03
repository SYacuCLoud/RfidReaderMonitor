using System.Net.Sockets;
using System.Text;
using Serilog;

namespace RfidReaderMonitor.Output;

/// <summary>
/// 수집 서버(수집 모드로 띄운 이 프로그램 또는 임의의 라인 기반 TCP 서버)로 접속해 이벤트·하트비트를 보낸다.
/// 로컬 재전송 큐를 거치므로 서버가 꺼져 있어도 이벤트가 사라지지 않는다.
/// </summary>
public sealed class TcpClientSink : SinkBase
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _queueFolder;
    private OutboxQueue? _queue;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _connected;

    public override string Name => "TCP 클라이언트";

    public TcpClientSink(string host, int port, string queueFolder)
    {
        _host = host;
        _port = port;
        _queueFolder = queueFolder;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        _queue = new OutboxQueue(_queueFolder, "tcp-client", SendAsync);
        _queue.Changed += _ => UpdateStatus();
        UpdateStatus();
        return Task.CompletedTask;
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
        try
        {
            if (_stream is null || _client is null || !_client.Connected)
            {
                CloseSocket();
                var c = new TcpClient { NoDelay = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(5000);
                await c.ConnectAsync(_host, _port, timeout.Token);
                _client = c;
                _stream = c.GetStream();
                _connected = true;
                Log.Information("TCP 클라이언트 연결 {Host}:{Port}", _host, _port);
                UpdateStatus();
            }
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (_connected) Log.Warning("TCP 클라이언트 연결 끊김 {Host}:{Port}: {Msg}", _host, _port, ex.Message);
            CloseSocket();
            UpdateStatus(ex.Message);
            return false;
        }
    }

    private void CloseSocket()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
        _connected = false;
    }

    private void UpdateStatus(string? error = null)
    {
        var pending = _queue?.Pending ?? 0;
        Status = _connected
            ? $"{_host}:{_port} 연결됨, 대기 {pending}"
            : $"{_host}:{_port} 연결 안 됨, 재시도 중 (대기 {pending}){(error is null ? "" : " - " + error)}";
    }

    public override async ValueTask DisposeAsync()
    {
        if (_queue is not null) await _queue.DisposeAsync();
        CloseSocket();
        Status = "중지";
    }
}
