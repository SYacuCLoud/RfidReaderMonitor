using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace RfidReaderMonitor.Output;

/// <summary>
/// 수집 모드용 TCP 서버. 감시 PC들이 접속해 JSON 한 줄씩 보내면 이벤트/하트비트로 해석해 올린다.
/// 접속 하나는 감시 PC 하나이며, 첫 메시지의 host 로 연결↔PC 를 묶는다.
/// </summary>
public sealed class CollectorServer : IAsyncDisposable
{
    private readonly int _port;
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private int _clients;

    public int ClientCount => Volatile.Read(ref _clients);

    public event Action<TagEvent, string>? EventReceived;          // (이벤트, 원격 주소)
    public event Action<HeartbeatMessage, string>? HeartbeatReceived;
    public event Action<string>? ClientConnected;                   // 원격 주소
    public event Action<string, string?>? ClientDisconnected;       // 원격 주소, host(알면)
    public event Action<string>? Error;

    public CollectorServer(int port) => _port = port;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Log.Information("수집 서버 대기 포트 {Port}", _port);
        _ = AcceptLoop(_cts.Token);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var c = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClient(c, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Error?.Invoke(ex.Message);
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        string? host = null;
        Interlocked.Increment(ref _clients);
        ClientConnected?.Invoke(remote);
        try
        {
            client.NoDelay = true;
            using var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length == 0) continue;
                if (!Envelope.TryParse(line, out var ev, out var hb))
                {
                    Log.Debug("수집: 해석 불가 {Remote}: {Line}", remote, line.Length > 200 ? line[..200] : line);
                    continue;
                }
                if (hb is not null)
                {
                    host ??= hb.Host;
                    HeartbeatReceived?.Invoke(hb, remote);
                }
                else if (ev is not null)
                {
                    host ??= ev.Host;
                    EventReceived?.Invoke(ev, remote);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug("수집: 연결 종료 {Remote}: {Msg}", remote, ex.Message);
        }
        finally
        {
            client.Dispose();
            Interlocked.Decrement(ref _clients);
            ClientDisconnected?.Invoke(remote, host);
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        return ValueTask.CompletedTask;
    }
}
