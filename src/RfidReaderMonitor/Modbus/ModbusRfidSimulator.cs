using System.IO;
using System.Net;
using System.Net.Sockets;

namespace RfidReaderMonitor.Modbus;

/// <summary>
/// 개발자 모드용 Modbus TCP RFID 리더 시뮬레이터. 산업용 리더가 없을 때 ModbusReaderProvider 를 시험한다.
/// FC 01/02 는 PresentBitAddress 한 비트, FC 03/04 는 UID 워드와 PresentRegisterAddress 를 서비스한다.
/// Holding 과 Input 은 같은 값을 돌려주고 Unit ID 는 무시한다. 쓰기는 지원하지 않는다(예외 코드 1).
/// </summary>
public sealed class ModbusRfidSimulator : IDisposable
{
    private readonly object _sync = new();
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _stop;
    private readonly List<TcpClient> _clients = new();
    private byte[] _uid = Array.Empty<byte>();
    private long _requests;

    // 레지스터 맵 (요청 시점 값을 읽음)
    public int PresentBitAddress { get; set; }
    public int PresentRegisterAddress { get; set; } = 10;
    public int PresentRegisterBit { get; set; }
    public int UidAddress { get; set; }
    public int UidBytes { get; set; } = 8;
    public bool SwapBytes { get; set; }
    public bool ReverseWords { get; set; }

    public volatile bool Present;

    public byte[] Uid
    {
        get { lock (_sync) return _uid; }
        set { lock (_sync) _uid = value ?? Array.Empty<byte>(); }
    }

    public bool Running => _listener is not null;
    public int Port { get; private set; }
    public int ClientCount { get { lock (_sync) return _clients.Count; } }
    public long RequestCount => Interlocked.Read(ref _requests);

    /// <summary>요청·접속 로그 한 줄.</summary>
    public event Action<string>? Log;
    public event Action? Changed;

    public void Start(int port)
    {
        lock (_sync)
        {
            if (_listener is not null) return;
            _stop = false;
            Port = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ModbusSim" };
            _acceptThread.Start();
        }
        Log?.Invoke($"시뮬레이터 시작 0.0.0.0:{port}");
        Changed?.Invoke();
    }

    public void Stop()
    {
        TcpListener? l;
        List<TcpClient> clients;
        lock (_sync)
        {
            l = _listener;
            _listener = null;
            _stop = true;
            clients = _clients.ToList();
            _clients.Clear();
        }
        if (l is null) return;
        try { l.Stop(); } catch { }
        foreach (var c in clients) { try { c.Close(); } catch { } }
        _acceptThread?.Join(2000);
        _acceptThread = null;
        Log?.Invoke("시뮬레이터 중지");
        Changed?.Invoke();
    }

    private void AcceptLoop()
    {
        var listener = _listener;
        while (!_stop && listener is not null)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { break; }
            lock (_sync) _clients.Add(client);
            var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
            Log?.Invoke($"접속 {ep}");
            Changed?.Invoke();
            var t = new Thread(() => ServeClient(client, ep)) { IsBackground = true, Name = "ModbusSim:" + ep };
            t.Start();
        }
    }

    private void ServeClient(TcpClient client, string ep)
    {
        try
        {
            using var stream = client.GetStream();
            var header = new byte[7];
            while (!_stop)
            {
                stream.ReadExactly(header, 0, 7);
                int length = (header[4] << 8) | header[5];
                if (length < 2 || length > 260) throw new InvalidOperationException("MBAP 길이 이상");
                var pdu = new byte[length - 1];
                stream.ReadExactly(pdu, 0, pdu.Length);
                Interlocked.Increment(ref _requests);

                var response = Handle(pdu, out var note);
                var frame = new byte[7 + response.Length];
                Array.Copy(header, frame, 7);
                frame[4] = (byte)((response.Length + 1) >> 8);
                frame[5] = (byte)(response.Length + 1);
                Array.Copy(response, 0, frame, 7, response.Length);
                stream.Write(frame, 0, frame.Length);
                if (note is not null) Log?.Invoke($"{ep} {note}");
            }
        }
        catch (Exception ex) when (_stop || ex is IOException or EndOfStreamException or ObjectDisposedException or SocketException)
        {
            // 상대가 끊었거나 우리가 멈춤
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "시뮬레이터 클라이언트 처리 오류 {Ep}", ep);
        }
        finally
        {
            lock (_sync) _clients.Remove(client);
            try { client.Close(); } catch { }
            Log?.Invoke($"끊김 {ep}");
            Changed?.Invoke();
        }
    }

    /// <summary>PDU 처리. 값이 바뀌지 않는 반복 폴링은 note 를 null 로 두어 로그를 줄인다.</summary>
    private byte[] Handle(byte[] pdu, out string? note)
    {
        note = null;
        byte fc = pdu[0];
        if (pdu.Length < 5) return new[] { (byte)(fc | 0x80), (byte)3 };
        int addr = (pdu[1] << 8) | pdu[2];
        int count = (pdu[3] << 8) | pdu[4];

        switch (fc)
        {
            case 1:
            case 2:
                {
                    if (count is < 1 or > 2000) return new[] { (byte)(fc | 0x80), (byte)3 };
                    int nbytes = (count + 7) / 8;
                    var body = new byte[2 + nbytes];
                    body[0] = fc;
                    body[1] = (byte)nbytes;
                    bool present = Present;
                    for (int i = 0; i < count; i++)
                        if (addr + i == PresentBitAddress && present) body[2 + i / 8] |= (byte)(1 << (i % 8));
                    return body;
                }
            case 3:
            case 4:
                {
                    if (count is < 1 or > 125) return new[] { (byte)(fc | 0x80), (byte)3 };
                    var words = EncodeUidWords();
                    bool present = Present;
                    var body = new byte[2 + count * 2];
                    body[0] = fc;
                    body[1] = (byte)(count * 2);
                    for (int i = 0; i < count; i++)
                    {
                        int a = addr + i;
                        ushort v = 0;
                        if (a >= UidAddress && a < UidAddress + words.Length) v = present ? words[a - UidAddress] : (ushort)0;
                        else if (a == PresentRegisterAddress && present) v = PresentRegisterBit < 0 ? (ushort)1 : (ushort)(1 << Math.Min(15, PresentRegisterBit));
                        body[2 + i * 2] = (byte)(v >> 8);
                        body[3 + i * 2] = (byte)v;
                    }
                    return body;
                }
            default:
                note = $"지원하지 않는 FC {fc:X2} → 예외 1";
                return new[] { (byte)(fc | 0x80), (byte)1 };
        }
    }

    /// <summary>ModbusReaderProvider 의 디코딩(워드 역순 → 바이트 스왑)의 역순으로 UID 를 워드에 싣는다.</summary>
    private ushort[] EncodeUidWords()
    {
        var uid = Uid;
        int bytes = Math.Clamp(UidBytes, 1, 32);
        var buf = new byte[(bytes + 1) / 2 * 2];
        Array.Copy(uid, buf, Math.Min(uid.Length, bytes));
        var words = new ushort[buf.Length / 2];
        for (int i = 0; i < words.Length; i++)
        {
            byte hi = buf[i * 2], lo = buf[i * 2 + 1];
            if (SwapBytes) (hi, lo) = (lo, hi);
            words[i] = (ushort)((hi << 8) | lo);
        }
        if (ReverseWords) Array.Reverse(words);
        return words;
    }

    public void Dispose() => Stop();
}
