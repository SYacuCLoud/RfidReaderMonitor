using System.Net.Sockets;

namespace RfidReaderMonitor.Modbus;

public sealed class ModbusException : Exception
{
    public byte FunctionCode { get; }
    public byte ExceptionCode { get; }

    public ModbusException(byte fc, byte code) : base($"Modbus 예외 FC={fc:X2} code={code} ({Describe(code)})")
    {
        FunctionCode = fc;
        ExceptionCode = code;
    }

    private static string Describe(byte code) => code switch
    {
        1 => "지원하지 않는 기능",
        2 => "잘못된 주소",
        3 => "잘못된 값",
        4 => "장치 오류",
        6 => "장치 사용 중",
        10 => "게이트웨이 경로 없음",
        11 => "게이트웨이 대상 응답 없음",
        _ => "?"
    };
}

/// <summary>
/// 최소 Modbus TCP 클라이언트. 읽기 함수(FC 01/02/03/04)만 구현한다.
/// 스레드 안전하지 않다. 호출자가 직렬화한다.
/// </summary>
public sealed class ModbusTcpClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _tx;

    public ModbusTcpClient(string host, int port, int timeoutMs)
    {
        _host = host;
        _port = port;
        _timeoutMs = Math.Max(200, timeoutMs);
    }

    public bool IsConnected => _tcp?.Connected == true && _stream is not null;

    public void Connect()
    {
        Close();
        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = _timeoutMs, SendTimeout = _timeoutMs };
        try
        {
            var task = tcp.ConnectAsync(_host, _port);
            if (!task.Wait(_timeoutMs))
            {
                // 소켓을 닫으면 이 작업이 뒤늦게 실패한다. 관찰해 두지 않으면 UnobservedTaskException 으로 로그에 남는다.
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                throw new TimeoutException($"{_host}:{_port} 접속 시간 초과");
            }
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            tcp.Dispose();
            throw ex.InnerException;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
        _stream.ReadTimeout = _timeoutMs;
        _stream.WriteTimeout = _timeoutMs;
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    public bool[] ReadCoils(byte unit, ushort address, ushort count) => ReadBits(unit, 0x01, address, count);
    public bool[] ReadDiscreteInputs(byte unit, ushort address, ushort count) => ReadBits(unit, 0x02, address, count);
    public ushort[] ReadHoldingRegisters(byte unit, ushort address, ushort count) => ReadRegisters(unit, 0x03, address, count);
    public ushort[] ReadInputRegisters(byte unit, ushort address, ushort count) => ReadRegisters(unit, 0x04, address, count);

    private bool[] ReadBits(byte unit, byte fc, ushort address, ushort count)
    {
        var pdu = Request(unit, fc, address, count);
        int byteCount = pdu[1];
        if (pdu.Length < 2 + byteCount || byteCount * 8 < count) throw new InvalidOperationException("Modbus 응답 길이 오류");
        var result = new bool[count];
        for (int i = 0; i < count; i++)
            result[i] = (pdu[2 + i / 8] & (1 << (i % 8))) != 0;
        return result;
    }

    private ushort[] ReadRegisters(byte unit, byte fc, ushort address, ushort count)
    {
        var pdu = Request(unit, fc, address, count);
        int byteCount = pdu[1];
        if (pdu.Length < 2 + byteCount || byteCount < count * 2) throw new InvalidOperationException("Modbus 응답 길이 오류");
        var result = new ushort[count];
        for (int i = 0; i < count; i++)
            result[i] = (ushort)((pdu[2 + i * 2] << 8) | pdu[3 + i * 2]);
        return result;
    }

    /// <summary>MBAP + PDU 를 보내고 응답 PDU(기능코드부터)를 돌려준다.</summary>
    private byte[] Request(byte unit, byte fc, ushort address, ushort count)
    {
        var stream = _stream ?? throw new InvalidOperationException("접속되지 않음");
        var tx = ++_tx;
        var frame = new byte[12];
        frame[0] = (byte)(tx >> 8); frame[1] = (byte)tx;
        frame[2] = 0; frame[3] = 0;               // protocol id
        frame[4] = 0; frame[5] = 6;               // length: unit + pdu(5)
        frame[6] = unit;
        frame[7] = fc;
        frame[8] = (byte)(address >> 8); frame[9] = (byte)address;
        frame[10] = (byte)(count >> 8); frame[11] = (byte)count;
        stream.Write(frame, 0, frame.Length);

        var header = new byte[7];
        stream.ReadExactly(header, 0, 7);
        var rxTx = (ushort)((header[0] << 8) | header[1]);
        int length = (header[4] << 8) | header[5];
        if (length < 2 || length > 260) throw new InvalidOperationException($"Modbus 헤더 길이 이상 {length}");
        var pdu = new byte[length - 1];
        stream.ReadExactly(pdu, 0, pdu.Length);
        if (rxTx != tx) throw new InvalidOperationException($"Modbus 트랜잭션 불일치 (보냄 {tx}, 받음 {rxTx})");
        if ((pdu[0] & 0x80) != 0) throw new ModbusException(fc, pdu.Length > 1 ? pdu[1] : (byte)0);
        if (pdu[0] != fc) throw new InvalidOperationException($"Modbus 기능코드 불일치 {pdu[0]:X2}");
        return pdu;
    }

    public void Dispose() => Close();
}
