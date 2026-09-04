using System.Collections.Concurrent;
using RfidReaderMonitor.Readers;
using Serilog;

namespace RfidReaderMonitor.Core;

public enum TagEventKind { Appear, Remove }

/// <summary>디바운스를 거친 확정 이벤트.</summary>
public sealed record TrackerEvent(
    DateTimeOffset Time,
    TagEventKind Kind,
    string ReaderName,
    string Uid,
    byte[] UidBytes,
    CardTech Tech,
    string Atr,
    long? DwellMs);

/// <summary>원신호 로그 한 줄.</summary>
public sealed record RawSignal(DateTimeOffset Time, string ReaderName, string State, string Atr, string Note);

/// <summary>
/// 원신호(PRESENT/EMPTY)를 확정 이벤트(등장/제거)로 바꾼다.
/// 모든 판정은 전용 워커 스레드 하나에서 순서대로 처리한다.
///
/// 규칙
///  - 등장: PRESENT 후 UID 읽기에 성공해야 확정. 실패하면 IdentifyTimeoutMs 동안 IdentifyRetryMs 간격으로 재시도.
///          그래도 못 읽으면 UID "?" 로 확정(태그가 실제로 있지만 읽을 수 없는 경우).
///  - UID 를 모르는 신호("?", 사용중, 무응답)는 이미 확정된 태그를 절대 대체하지 않는다.
///  - 제거: EMPTY(또는 사용불가)가 RemovalDebounceMs 이상 유지되어야 확정. 그 안에 다시 PRESENT 가 오면 무시.
///  - 서로 다른 UID 가 확인되면 이전 태그 제거 → 새 태그 등장.
/// </summary>
public sealed class PresenceTracker : IDisposable
{
    public const string UnknownUid = "?";

    private sealed class ReaderState
    {
        public string? CurrentUid;
        public byte[] CurrentUidBytes = Array.Empty<byte>();
        public CardTech CurrentTech = CardTech.Unknown;
        public string CurrentAtr = string.Empty;
        public DateTimeOffset AppearTime;

        public bool RawPresent;

        // 제거 대기
        public Timer? RemovalTimer;
        public int RemovalTicket;

        // UID 확인 대기 (첫 등장)
        public bool Identifying;
        public DateTimeOffset IdentifyStart;
        public CardTech IdentifyTech = CardTech.Unknown;
        public string IdentifyAtr = string.Empty;
        public Timer? IdentifyTimer;
        public int IdentifyTicket;
        public int IdentifyAttempts;
    }

    private abstract record WorkItem;
    private sealed record PresenceItem(ReaderPresenceEventArgs Args) : WorkItem;
    private sealed record RemovalTimerItem(string Reader, int Ticket, DateTimeOffset Fired) : WorkItem;
    private sealed record IdentifyRetryItem(string Reader, int Ticket, DateTimeOffset Fired) : WorkItem;
    private sealed record ResetItem(string Reader, DateTimeOffset Time, string Reason) : WorkItem;

    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Dictionary<string, ReaderState> _states = new();
    private readonly Func<string, TagReadResult?> _readTag;
    private readonly Thread _worker;
    private volatile bool _disposed;

    /// <summary>EMPTY 가 이 시간 이상 유지되면 제거 확정.</summary>
    public int RemovalDebounceMs { get; set; } = 1000;
    /// <summary>첫 등장 시 UID 읽기를 포기하고 "?" 로 확정하기까지의 시간.</summary>
    public int IdentifyTimeoutMs { get; set; } = 1500;
    /// <summary>UID 재시도 간격.</summary>
    public int IdentifyRetryMs { get; set; } = 80;
    public bool ReverseIso15693 { get; set; }

    public event Action<TrackerEvent>? Confirmed;
    public event Action<RawSignal>? Raw;

    public PresenceTracker(Func<string, TagReadResult?> readTag)
    {
        _readTag = readTag;
        _worker = new Thread(Loop) { IsBackground = true, Name = "PresenceTracker" };
        _worker.Start();
    }

    public void OnPresence(ReaderPresenceEventArgs e)
    {
        if (!_disposed) _queue.Add(new PresenceItem(e));
    }

    /// <summary>리더가 뽑혔거나 감시가 끊겼을 때: 들고 있던 태그를 제거로 마감.</summary>
    public void ResetReader(string reader, string reason)
    {
        if (!_disposed) _queue.Add(new ResetItem(reader, DateTimeOffset.Now, reason));
    }

    private void Loop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                switch (item)
                {
                    case PresenceItem p: HandlePresence(p.Args); break;
                    case RemovalTimerItem t: HandleRemovalTimer(t); break;
                    case IdentifyRetryItem i: HandleIdentifyRetry(i); break;
                    case ResetItem r: HandleReset(r); break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PresenceTracker 처리 오류");
            }
        }
    }

    private ReaderState Get(string reader)
    {
        if (!_states.TryGetValue(reader, out var s))
        {
            s = new ReaderState();
            _states[reader] = s;
        }
        return s;
    }

    // ------------------------------------------------------------ 원신호 분기

    private void HandlePresence(ReaderPresenceEventArgs e)
    {
        var s = Get(e.ReaderName);
        var atrHex = Hex.Of(e.Atr);
        // 원신호 표의 "원값" 칸. PC/SC 는 ATR, Modbus 는 프로바이더가 준 레지스터 값.
        var rawText = e.RawText ?? atrHex;
        var emptyText = e.RawText ?? string.Empty;

        switch (e.State)
        {
            case PresenceState.Present:
            case PresenceState.InUse:
            case PresenceState.Mute:
                s.RawPresent = true;
                OnRawPresent(e, s, atrHex, rawText);
                break;

            case PresenceState.Empty:
            case PresenceState.Unavailable:
                // 사용불가는 다른 프로그램(또는 이 프로그램의 DIRECT 접속)이 리더를 잠깐 점유할 때 수 ms 스친다.
                // EMPTY 와 같이 T_off 디바운스를 탄다.
                s.RawPresent = false;
                if (s.Identifying)
                {
                    CancelIdentify(s);
                    Raw?.Invoke(new RawSignal(e.Time, e.ReaderName, e.State.Ko(), emptyText,"UID 확인 전에 사라짐, 등장 미확정"));
                }
                else
                {
                    Raw?.Invoke(new RawSignal(e.Time, e.ReaderName, e.State.Ko(), emptyText,s.CurrentUid is null ? "" : $"T_off {RemovalDebounceMs}ms 대기"));
                }
                StartRemovalTimer(e.ReaderName, s);
                break;

            case PresenceState.Removed:
                Raw?.Invoke(new RawSignal(e.Time, e.ReaderName, e.State.Ko(), emptyText,""));
                HandleReset(new ResetItem(e.ReaderName, e.Time, e.State.Ko()));
                break;

            default:
                Raw?.Invoke(new RawSignal(e.Time, e.ReaderName, e.State.Ko(), rawText, $"flags=0x{e.RawFlags:X}"));
                break;
        }
    }

    private void OnRawPresent(ReaderPresenceEventArgs e, ReaderState s, string atrHex, string rawText)
    {
        var tech = AtrParser.Parse(e.Atr);
        bool wasPendingRemoval = s.RemovalTimer is not null;
        if (wasPendingRemoval) CancelRemovalTimer(s);

        if (s.Identifying)
        {
            // 재시도 타이머가 이미 UID 를 읽고 있다. 여기서 또 읽으면 접속 → 상태변경 → 재진입 되먹임이 생긴다.
            Raw?.Invoke(new RawSignal(e.Time, e.ReaderName, e.State.Ko(), rawText, "UID 확인 대기 중"));
            return;
        }

        TagReadResult? read = null;
        string note;
        if (e.State == PresenceState.Present)
        {
            read = _readTag(e.ReaderName);
            note = read is null ? "UID 읽기 실패" : "";
        }
        else
        {
            note = e.State == PresenceState.InUse ? "다른 접속이 점유, UID 미확인" : "카드 무응답";
        }

        if (read is not null && read.Tech.Family != CardFamily.Unknown) tech = read.Tech;
        var uid = read is null ? null : UidFormatter.Format(read.Uid, tech.Family, ReverseIso15693);

        Raw?.Invoke(new RawSignal(e.Time, e.ReaderName, e.State.Ko(), rawText, uid ?? note));

        if (uid is null)
        {
            if (s.CurrentUid is not null)
            {
                // 이미 확정된 태그가 있다 → UID 를 모르는 신호로 대체하지 않는다.
                Raw?.Invoke(new RawSignal(e.Time, e.ReaderName, "유지", "", wasPendingRemoval
                    ? $"재등장 (UID 미확인, T_off 이내) {s.CurrentUid} 유지"
                    : $"UID 미확인 신호, {s.CurrentUid} 유지"));
                return;
            }
            // 새 태그인데 아직 UID 를 모른다 → 확인 대기
            BeginOrContinueIdentify(e.ReaderName, s, e.Time, tech, atrHex);
            return;
        }

        CancelIdentify(s);
        ConfirmAppear(e.ReaderName, s, e.Time, uid, read!.Uid, tech, atrHex, wasPendingRemoval);
    }

    // ------------------------------------------------------------ 등장 확정 / UID 확인 대기

    private void ConfirmAppear(string reader, ReaderState s, DateTimeOffset when, string uid, byte[] uidBytes, CardTech tech, string atrHex, bool wasPendingRemoval)
    {
        if (s.CurrentUid is not null)
        {
            if (s.CurrentUid == uid)
            {
                if (wasPendingRemoval)
                    Raw?.Invoke(new RawSignal(when, reader, "무시", "", $"재등장 (T_off 이내) {uid}"));
                return; // 같은 태그, 중복 PRESENT
            }
            // 다른 UID → 이전 태그 제거 확정
            EmitRemove(reader, s, when);
        }

        s.CurrentUid = uid;
        s.CurrentUidBytes = uidBytes;
        s.CurrentTech = tech;
        s.CurrentAtr = atrHex;
        s.AppearTime = when;
        Confirmed?.Invoke(new TrackerEvent(when, TagEventKind.Appear, reader, uid, uidBytes, tech, atrHex, null));
    }

    private void BeginOrContinueIdentify(string reader, ReaderState s, DateTimeOffset now, CardTech tech, string atrHex)
    {
        if (!s.Identifying)
        {
            s.Identifying = true;
            s.IdentifyStart = now;
            s.IdentifyAttempts = 1;
        }
        s.IdentifyTech = tech;
        s.IdentifyAtr = atrHex;
        ScheduleIdentifyRetry(reader, s);
    }

    private void ScheduleIdentifyRetry(string reader, ReaderState s)
    {
        s.IdentifyTimer?.Dispose();
        var ticket = ++s.IdentifyTicket;
        s.IdentifyTimer = new Timer(_ =>
        {
            if (!_disposed) _queue.Add(new IdentifyRetryItem(reader, ticket, DateTimeOffset.Now));
        }, null, Math.Max(20, IdentifyRetryMs), Timeout.Infinite);
    }

    private void CancelIdentify(ReaderState s)
    {
        s.IdentifyTimer?.Dispose();
        s.IdentifyTimer = null;
        s.IdentifyTicket++;
        s.Identifying = false;
    }

    private void HandleIdentifyRetry(IdentifyRetryItem item)
    {
        if (!_states.TryGetValue(item.Reader, out var s)) return;
        if (item.Ticket != s.IdentifyTicket || !s.Identifying) return;
        if (!s.RawPresent) { CancelIdentify(s); return; }

        s.IdentifyAttempts++;
        var read = _readTag(item.Reader);
        if (read is not null)
        {
            var tech = read.Tech.Family != CardFamily.Unknown ? read.Tech : s.IdentifyTech;
            var uid = UidFormatter.Format(read.Uid, tech.Family, ReverseIso15693);
            Raw?.Invoke(new RawSignal(item.Fired, item.Reader, "UID 확인", s.IdentifyAtr, $"{uid} ({s.IdentifyAttempts}회 시도)"));
            CancelIdentify(s);
            ConfirmAppear(item.Reader, s, item.Fired, uid, read.Uid, tech, s.IdentifyAtr, false);
            return;
        }

        if ((item.Fired - s.IdentifyStart).TotalMilliseconds >= IdentifyTimeoutMs)
        {
            Raw?.Invoke(new RawSignal(item.Fired, item.Reader, "UID 포기", s.IdentifyAtr, $"{s.IdentifyAttempts}회 실패, 미확인 태그로 등장 확정"));
            var tech = s.IdentifyTech;
            var atr = s.IdentifyAtr;
            CancelIdentify(s);
            ConfirmAppear(item.Reader, s, item.Fired, UnknownUid, Array.Empty<byte>(), tech, atr, false);
            return;
        }

        ScheduleIdentifyRetry(item.Reader, s);
    }

    // ------------------------------------------------------------ 제거 대기

    private void StartRemovalTimer(string reader, ReaderState s)
    {
        if (s.CurrentUid is null || s.RemovalTimer is not null) return;
        var ticket = ++s.RemovalTicket;
        var delay = Math.Max(0, RemovalDebounceMs);
        s.RemovalTimer = new Timer(_ =>
        {
            if (!_disposed) _queue.Add(new RemovalTimerItem(reader, ticket, DateTimeOffset.Now));
        }, null, delay, Timeout.Infinite);
    }

    private void CancelRemovalTimer(ReaderState s)
    {
        s.RemovalTimer?.Dispose();
        s.RemovalTimer = null;
        s.RemovalTicket++;
    }

    private void HandleRemovalTimer(RemovalTimerItem t)
    {
        if (!_states.TryGetValue(t.Reader, out var s)) return;
        if (t.Ticket != s.RemovalTicket) return; // 취소된 타이머
        s.RemovalTimer?.Dispose();
        s.RemovalTimer = null;
        if (s.RawPresent) return; // 그 사이 다시 올라옴
        EmitRemove(t.Reader, s, t.Fired);
    }

    private void HandleReset(ResetItem r)
    {
        if (!_states.TryGetValue(r.Reader, out var s)) return;
        CancelRemovalTimer(s);
        CancelIdentify(s);
        s.RawPresent = false;
        if (s.CurrentUid is not null)
        {
            Raw?.Invoke(new RawSignal(r.Time, r.Reader, "강제 마감", "", r.Reason));
            EmitRemove(r.Reader, s, r.Time);
        }
    }

    private void EmitRemove(string reader, ReaderState s, DateTimeOffset when)
    {
        if (s.CurrentUid is null) return;
        var dwell = (long)(when - s.AppearTime).TotalMilliseconds;
        Confirmed?.Invoke(new TrackerEvent(when, TagEventKind.Remove, reader, s.CurrentUid, s.CurrentUidBytes, s.CurrentTech, s.CurrentAtr, dwell));
        s.CurrentUid = null;
        s.CurrentUidBytes = Array.Empty<byte>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        foreach (var s in _states.Values)
        {
            s.RemovalTimer?.Dispose();
            s.IdentifyTimer?.Dispose();
        }
    }
}
