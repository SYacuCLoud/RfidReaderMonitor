using RfidReaderMonitor.Native;
using RfidReaderMonitor.Readers;
using Serilog;

namespace RfidReaderMonitor.PcSc;

/// <summary>
/// SCardGetStatusChange 를 전용 스레드에서 돌리며 리더 추가/제거(PnP 가상 리더)와
/// 카드 PRESENT/EMPTY 변화를 이벤트로 올린다. 서비스 중단·컨텍스트 무효 시 재생성 루프.
/// </summary>
public sealed class PcscMonitor : IDisposable
{
    private readonly object _sync = new();
    private Thread? _thread;
    private PcscContext? _ctx;
    private volatile bool _stop;

    public IReadOnlyList<string> Readers { get; private set; } = Array.Empty<string>();

    public event EventHandler<ReaderListChangedEventArgs>? ReadersChanged;
    public event EventHandler<ReaderPresenceEventArgs>? PresenceChanged;
    public event EventHandler<ProviderStatusEventArgs>? StatusChanged;

    /// <summary>이 비트들이 바뀔 때만 PresenceChanged 를 올린다.</summary>
    private const uint SignificantMask =
        WinScard.SCARD_STATE_IGNORE | WinScard.SCARD_STATE_UNKNOWN | WinScard.SCARD_STATE_UNAVAILABLE |
        WinScard.SCARD_STATE_EMPTY | WinScard.SCARD_STATE_PRESENT | WinScard.SCARD_STATE_MUTE;

    /// <summary>GetStatusChange 대기 상한. 안전장치이며 실제 이벤트는 즉시 온다.</summary>
    public int WaitTimeoutMs { get; set; } = 30_000;
    public int RetryDelayMs { get; set; } = 1500;

    public void Start()
    {
        lock (_sync)
        {
            if (_thread is not null) return;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "PcscMonitor" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (_sync)
        {
            _stop = true;
            t = _thread;
            _ctx?.Cancel();
        }
        t?.Join(3000);
        lock (_sync) { _thread = null; }
    }

    /// <summary>리더 목록 재조회를 강제한다.</summary>
    public void Refresh()
    {
        lock (_sync) { _ctx?.Cancel(); }
    }

    private void Run()
    {
        while (!_stop)
        {
            try
            {
                using var ctx = PcscContext.Establish();
                lock (_sync) { _ctx = ctx; }
                StatusChanged?.Invoke(this, new ProviderStatusEventArgs { Healthy = true, Message = "스마트카드 서비스 연결됨" });
                WatchLoop(ctx);
            }
            catch (PcscException ex)
            {
                Log.Warning("PC/SC 감시 중단: {Msg}", ex.Message);
                StatusChanged?.Invoke(this, new ProviderStatusEventArgs { Healthy = false, Message = ex.Message });
                MarkAllUnavailable(PresenceState.Unavailable);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PC/SC 감시 예외");
                StatusChanged?.Invoke(this, new ProviderStatusEventArgs { Healthy = false, Message = ex.Message });
                MarkAllUnavailable(PresenceState.Unavailable);
            }
            finally
            {
                lock (_sync) { _ctx = null; }
            }

            if (_stop) break;
            Thread.Sleep(RetryDelayMs);
        }
    }

    private void MarkAllUnavailable(PresenceState state)
    {
        foreach (var r in Readers)
        {
            PresenceChanged?.Invoke(this, new ReaderPresenceEventArgs
            {
                ReaderName = r, State = state, Atr = Array.Empty<byte>(), RawFlags = 0, Time = DateTimeOffset.Now
            });
        }
    }

    private void WatchLoop(PcscContext ctx)
    {
        while (!_stop)
        {
            var readers = ctx.ListReaders();
            PublishReaderList(readers);

            var states = new WinScard.SCARD_READERSTATE[readers.Count + 1];
            for (int i = 0; i < readers.Count; i++)
            {
                states[i] = new WinScard.SCARD_READERSTATE
                {
                    szReader = readers[i],
                    dwCurrentState = WinScard.SCARD_STATE_UNAWARE,
                    rgbAtr = new byte[36]
                };
            }
            states[^1] = new WinScard.SCARD_READERSTATE
            {
                szReader = WinScard.PnpNotificationReader,
                dwCurrentState = (uint)readers.Count << 16,
                rgbAtr = new byte[36]
            };

            bool relist = false;
            while (!_stop && !relist)
            {
                var rc = WinScard.SCardGetStatusChange(ctx.Handle, (uint)WaitTimeoutMs, states, (uint)states.Length);

                if (rc == PcscError.E_TIMEOUT) continue;
                if (rc == PcscError.E_CANCELLED)
                {
                    if (_stop) return;
                    relist = true; // Refresh()
                    continue;
                }
                if (rc == PcscError.E_NO_READERS_AVAILABLE)
                {
                    // PnP 가상 리더만 있어도 이 코드가 오는 경우가 있다 → 잠시 후 재조회
                    Thread.Sleep(1000);
                    relist = true;
                    continue;
                }
                if (rc != PcscError.S_SUCCESS) throw new PcscException("SCardGetStatusChange", rc);

                for (int i = 0; i < states.Length; i++)
                {
                    ref var st = ref states[i];
                    if ((st.dwEventState & WinScard.SCARD_STATE_CHANGED) == 0) continue;

                    if (i == states.Length - 1)
                    {
                        // 리더 추가/제거
                        relist = true;
                        st.dwCurrentState = st.dwEventState & ~WinScard.SCARD_STATE_CHANGED;
                        continue;
                    }

                    var ev = st.dwEventState;
                    var prev = st.dwCurrentState;
                    st.dwCurrentState = ev & ~WinScard.SCARD_STATE_CHANGED;

                    // INUSE/EXCLUSIVE 비트와 상위 16비트(삽입/제거 카운터)만 바뀐 경우는 무시한다.
                    // 이 프로그램 자신의 UID 읽기(공유 접속)와 속성 조회(DIRECT 접속)가 그 비트를 흔들어
                    // 이벤트 → 읽기 → 이벤트 무한 되먹임을 만들기 때문이다.
                    if (prev != WinScard.SCARD_STATE_UNAWARE &&
                        (prev & SignificantMask) == (ev & SignificantMask))
                        continue;

                    var atr = new byte[st.cbAtr];
                    Array.Copy(st.rgbAtr, atr, (int)Math.Min(st.cbAtr, 36));

                    PresenceState ps;
                    if ((ev & (WinScard.SCARD_STATE_UNKNOWN | WinScard.SCARD_STATE_IGNORE)) != 0)
                    {
                        ps = PresenceState.Removed;
                        relist = true;
                    }
                    else if ((ev & WinScard.SCARD_STATE_UNAVAILABLE) != 0) ps = PresenceState.Unavailable;
                    else if ((ev & WinScard.SCARD_STATE_PRESENT) != 0)
                    {
                        if ((ev & WinScard.SCARD_STATE_MUTE) != 0) ps = PresenceState.Mute;
                        else if ((ev & WinScard.SCARD_STATE_EXCLUSIVE) != 0) ps = PresenceState.InUse;
                        else ps = PresenceState.Present;
                    }
                    else if ((ev & WinScard.SCARD_STATE_EMPTY) != 0) ps = PresenceState.Empty;
                    else ps = PresenceState.Unknown;

                    PresenceChanged?.Invoke(this, new ReaderPresenceEventArgs
                    {
                        ReaderName = st.szReader,
                        State = ps,
                        Atr = atr,
                        RawFlags = ev,
                        Time = DateTimeOffset.Now
                    });
                }
            }
        }
    }

    private void PublishReaderList(IReadOnlyList<string> readers)
    {
        var old = Readers;
        Readers = readers;
        if (!old.SequenceEqual(readers) || old.Count == 0)
        {
            Log.Information("리더 목록: {Readers}", string.Join(" | ", readers));
            ReadersChanged?.Invoke(this, new ReaderListChangedEventArgs { Readers = readers });
        }
    }

    public void Dispose() => Stop();
}
