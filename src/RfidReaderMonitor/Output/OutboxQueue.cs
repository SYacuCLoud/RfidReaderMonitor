using System.IO;
using System.Text;
using Serilog;

namespace RfidReaderMonitor.Output;

/// <summary>
/// 파일 기반 재전송 큐. 한 줄(JSON)이 한 메시지.
///  - Enqueue 는 파일 끝에 덧붙이기만 하므로 실패하지 않는다.
///  - 펌프 스레드가 ack 오프셋부터 순서대로 읽어 send 위임자에 넘기고, 성공하면 오프셋을 전진·저장한다.
///  - 전송 실패 시 지수 백오프 후 같은 메시지를 다시 시도한다 (순서 보존, 최소 1회 전달).
///  - 모두 전송된 뒤 파일이 커져 있으면 비운다.
/// 파일: {folder}/{name}.jsonl, {folder}/{name}.ack
/// </summary>
public sealed class OutboxQueue : IAsyncDisposable
{
    private readonly string _dataPath;
    private readonly string _ackPath;
    private readonly Func<string, CancellationToken, Task<bool>> _send;
    private readonly object _fileLock = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private long _offset;
    private int _pending;
    private const long CompactThreshold = 1_000_000;

    public string Name { get; }
    public int Pending => Volatile.Read(ref _pending);
    public DateTimeOffset? LastSentAt { get; private set; }
    public string? LastError { get; private set; }
    public event Action<OutboxQueue>? Changed;

    public OutboxQueue(string folder, string name, Func<string, CancellationToken, Task<bool>> send)
    {
        Directory.CreateDirectory(folder);
        Name = name;
        _dataPath = Path.Combine(folder, name + ".jsonl");
        _ackPath = Path.Combine(folder, name + ".ack");
        _send = send;

        _offset = ReadAck();
        _pending = CountPendingLines();
        if (_pending > 0) Log.Information("큐 {Name}: 미전송 {Pending}건 이어서 전송", name, _pending);

        _pump = Task.Run(PumpAsync);
    }

    public void Enqueue(string jsonLine)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonLine.Replace("\r", "").Replace("\n", " ") + "\n");
        lock (_fileLock)
        {
            using var fs = new FileStream(_dataPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            fs.Write(bytes, 0, bytes.Length);
        }
        Interlocked.Increment(ref _pending);
        _signal.Release();
        Changed?.Invoke(this);
    }

    private async Task PumpAsync()
    {
        int failures = 0;
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var line = ReadLineAt(_offset, out var next);
                if (line is null)
                {
                    TryCompact();
                    await _signal.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                bool ok;
                try { ok = await _send(line, ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    ok = false;
                    LastError = ex.Message;
                }

                if (ok)
                {
                    _offset = next;
                    WriteAck(_offset);
                    Interlocked.Decrement(ref _pending);
                    LastSentAt = DateTimeOffset.Now;
                    LastError = null;
                    failures = 0;
                    Changed?.Invoke(this);
                }
                else
                {
                    failures++;
                    Changed?.Invoke(this);
                    var delay = Math.Min(30_000, 1000 * (1 << Math.Min(failures, 5)));
                    await Task.Delay(delay, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warning(ex, "큐 {Name} 펌프 오류", Name);
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private string? ReadLineAt(long offset, out long nextOffset)
    {
        nextOffset = offset;
        lock (_fileLock)
        {
            if (!File.Exists(_dataPath)) return null;
            using var fs = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset >= fs.Length) return null;
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new MemoryStream();
            int b;
            while ((b = fs.ReadByte()) != -1)
            {
                if (b == '\n') break;
                buf.WriteByte((byte)b);
            }
            if (b == -1) return null; // 아직 줄이 끝나지 않음 (쓰는 중)
            nextOffset = fs.Position;
            var text = Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\r');
            if (text.Length == 0)
            {
                // 빈 줄은 건너뜀
                return ReadLineAt(nextOffset, out nextOffset);
            }
            return text;
        }
    }

    private void TryCompact()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_dataPath)) return;
            var len = new FileInfo(_dataPath).Length;
            if (_offset < len || len < CompactThreshold) return;
            using (new FileStream(_dataPath, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite)) { }
            _offset = 0;
            WriteAck(0);
            Log.Debug("큐 {Name} 비움", Name);
        }
    }

    private long ReadAck()
    {
        try
        {
            if (File.Exists(_ackPath) && long.TryParse(File.ReadAllText(_ackPath).Trim(), out var v))
            {
                var len = File.Exists(_dataPath) ? new FileInfo(_dataPath).Length : 0;
                return Math.Min(v, len);
            }
        }
        catch { }
        return 0;
    }

    private void WriteAck(long offset)
    {
        try { File.WriteAllText(_ackPath, offset.ToString()); }
        catch (Exception ex) { Log.Debug(ex, "ack 저장 실패 {Name}", Name); }
    }

    private int CountPendingLines()
    {
        try
        {
            int n = 0;
            long off = _offset;
            while (ReadLineAt(off, out var next) is not null) { n++; off = next; }
            return n;
        }
        catch { return 0; }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _signal.Release();
        try { await _pump; } catch { }
        _cts.Dispose();
    }
}
