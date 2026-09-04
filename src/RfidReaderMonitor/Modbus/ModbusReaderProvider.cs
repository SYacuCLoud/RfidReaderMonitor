using System.Diagnostics;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.Readers;
using RfidReaderMonitor.Settings;
using Serilog;

namespace RfidReaderMonitor.Modbus;

/// <summary>리더 도구 탭이 보여 주는 리더 한 대의 실시간 진단값.</summary>
public sealed record ModbusReaderDiagnostics(
    bool Connected,
    string LastError,
    DateTimeOffset? LastPoll,
    double LastResponseMs,
    long Requests,
    long Failures,
    bool Present,
    ushort PresentRaw,
    ushort[] UidWords,
    byte[] Uid);

/// <summary>
/// Modbus TCP 로 산업용 RFID 리더(Turck TBEN, Balluff BIS V, IO-Link 마스터 등)를 폴링하는 IRfidReaderProvider.
/// 리더 한 대마다 스레드 하나가 Tag Present 비트를 읽고, 있으면 UID 레지스터를 읽어 캐시한다.
/// PC/SC 와 달리 ATR 이 없으므로 규격은 설정(TagFamily)에서 온다.
/// </summary>
public sealed class ModbusReaderProvider : IRfidReaderProvider
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private ModbusSettings _settings = new();
    private bool _started;

    public event EventHandler<ReaderListChangedEventArgs>? ReadersChanged;
    public event EventHandler<ReaderPresenceEventArgs>? PresenceChanged;
    public event EventHandler<ProviderStatusEventArgs>? StatusChanged;

    public IReadOnlyList<string> Readers { get; private set; } = Array.Empty<string>();

    /// <summary>리더별 접속 상태 변화 (UI 표시용): 이름, 연결 여부, 오류 메시지.</summary>
    public event Action<string, bool, string>? ReaderConnectionChanged;

    /// <summary>설정을 바꾼다. 시작 전이면 저장만, 시작 후면 바뀐 리더만 재시작한다.</summary>
    public void Configure(ModbusSettings settings)
    {
        bool apply;
        lock (_sync)
        {
            _settings = settings;
            apply = _started;
        }
        if (apply) Apply();
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }
        Apply();
    }

    public void Stop()
    {
        List<Worker> stopping;
        lock (_sync)
        {
            _started = false;
            stopping = _workers.Values.ToList();
            _workers.Clear();
        }
        foreach (var w in stopping) w.Stop();
    }

    public void Refresh()
    {
        List<Worker> ws;
        lock (_sync) ws = _workers.Values.ToList();
        foreach (var w in ws) w.RequestReconnect();
    }

    /// <summary>리더 하나만 끊고 다시 붙인다.</summary>
    public void Reconnect(string readerName) => TryGet(readerName)?.RequestReconnect();

    /// <summary>실행 중인 리더의 설정 사본. 없으면 null.</summary>
    public ModbusReaderSettings? ConfigOf(string readerName) => TryGet(readerName)?.Config.Clone();

    /// <summary>실행 중인 리더의 진단값. 없으면 null.</summary>
    public ModbusReaderDiagnostics? Diagnostics(string readerName) => TryGet(readerName)?.Snapshot();

    /// <summary>
    /// 현재 설정과 실행 중 워커를 맞춘다. 설정이 같은 리더는 그대로 두고, 바뀐 것만 재시작한다.
    /// 워커 스레드가 상태 보고를 위해 _sync 를 잡으므로, 워커 정지(Join)는 잠금 밖에서 한다.
    /// </summary>
    private void Apply()
    {
        var toStop = new List<Worker>();
        var toStart = new List<Worker>();
        List<string> list;
        bool changed;

        lock (_sync)
        {
            var wanted = _settings.Enabled
                ? _settings.Readers.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Host)).ToList()
                : new List<ModbusReaderSettings>();

            foreach (var (name, w) in _workers.ToList())
            {
                var cfg = wanted.FirstOrDefault(r => string.Equals(r.EffectiveName, name, StringComparison.OrdinalIgnoreCase));
                if (cfg is null || !w.SameConfig(cfg, _settings.TimeoutMs))
                {
                    toStop.Add(w);
                    _workers.Remove(name);
                }
            }

            foreach (var cfg in wanted)
            {
                if (_workers.ContainsKey(cfg.EffectiveName)) continue;
                var w = new Worker(cfg.Clone(), _settings.TimeoutMs, this);
                _workers[cfg.EffectiveName] = w;
                toStart.Add(w);
            }

            list = wanted.Select(r => r.EffectiveName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            changed = !list.SequenceEqual(Readers);
            Readers = list;
        }

        foreach (var w in toStop) w.Stop();
        foreach (var w in toStart) w.Start();

        if (changed || toStop.Count > 0)
        {
            Log.Information("Modbus 리더 목록: {Readers}", string.Join(" | ", list));
            ReadersChanged?.Invoke(this, new ReaderListChangedEventArgs { Readers = list });
        }
        PublishStatus();
    }

    private void PublishStatus()
    {
        List<Worker> ws;
        lock (_sync) ws = _workers.Values.ToList();
        if (ws.Count == 0) return;
        int connected = ws.Count(w => w.Connected);
        StatusChanged?.Invoke(this, new ProviderStatusEventArgs
        {
            Healthy = connected == ws.Count,
            Message = connected == ws.Count ? $"Modbus 리더 {ws.Count}대 연결됨" : $"Modbus 리더 {connected}/{ws.Count} 연결"
        });
    }

    private Worker? TryGet(string readerName)
    {
        lock (_sync) return _workers.TryGetValue(readerName, out var w) ? w : null;
    }

    private Worker Get(string readerName) => TryGet(readerName) ?? throw new InvalidOperationException($"Modbus 리더 없음: {readerName}");

    public ReaderKind KindOf(string readerName) { Get(readerName); return ReaderKind.Modbus; }

    public ReaderIdentity Identify(string readerName)
    {
        var c = Get(readerName).Config;
        return new ReaderIdentity(
            Vendor: "Modbus TCP",
            IfdType: $"{c.Host}:{c.Port} unit {c.UnitId}",
            IfdVersion: null,
            Serial: $"{c.Host}:{c.Port}/{c.UnitId}",
            DeviceSystemName: c.Host);
    }

    public TagReadResult ReadTag(string readerName) => Get(readerName).ReadTagNow();

    public byte[] Escape(string readerName, byte[] command)
        => throw new NotSupportedException("Modbus 리더는 에스케이프 명령을 지원하지 않습니다.");

    public void Dispose() => Stop();

    // ------------------------------------------------------------ 리더 한 대

    private sealed class Worker
    {
        public ModbusReaderSettings Config { get; }
        private readonly int _timeoutMs;
        private readonly ModbusReaderProvider _owner;
        private readonly ModbusTcpClient _client;
        private readonly object _io = new();
        private readonly ManualResetEventSlim _wake = new(false);
        private Thread? _thread;
        private volatile bool _stop;
        private volatile bool _reconnect;

        private PresenceState _last = PresenceState.Unknown;
        private byte[] _lastUid = Array.Empty<byte>();

        // 진단 (읽기는 잠금 없이 스냅샷, 쓰기는 폴링 스레드만)
        public volatile bool Connected;
        public string LastError = "";
        private DateTimeOffset? _lastPoll;
        private double _lastResponseMs;
        private long _requests;
        private long _failures;
        private bool _present;
        private ushort _presentRaw;
        private ushort[] _uidWords = Array.Empty<ushort>();
        private byte[] _uid = Array.Empty<byte>();

        public Worker(ModbusReaderSettings cfg, int timeoutMs, ModbusReaderProvider owner)
        {
            Config = cfg;
            _timeoutMs = timeoutMs;
            _owner = owner;
            _client = new ModbusTcpClient(cfg.Host, cfg.Port, timeoutMs);
        }

        public ModbusReaderDiagnostics Snapshot() => new(
            Connected, LastError, _lastPoll, _lastResponseMs, Interlocked.Read(ref _requests), Interlocked.Read(ref _failures),
            _present, _presentRaw, _uidWords, _uid);

        public bool SameConfig(ModbusReaderSettings o, int timeoutMs)
        {
            var c = Config;
            return timeoutMs == _timeoutMs
                && c.Host == o.Host && c.Port == o.Port && c.UnitId == o.UnitId && c.PollMs == o.PollMs
                && c.PresentArea == o.PresentArea && c.PresentAddress == o.PresentAddress && c.PresentBit == o.PresentBit
                && c.UidArea == o.UidArea && c.UidAddress == o.UidAddress && c.UidBytes == o.UidBytes
                && c.UidSwapBytes == o.UidSwapBytes && c.UidReverseWords == o.UidReverseWords
                && c.TagFamily == o.TagFamily;
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Modbus:" + Config.EffectiveName };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _wake.Set();
            _thread?.Join(Math.Max(2000, _timeoutMs + 500));
            lock (_io) _client.Close();
            if (_last is not PresenceState.Unknown and not PresenceState.Removed)
                Raise(PresenceState.Removed, 0, null);
        }

        public void RequestReconnect()
        {
            _reconnect = true;
            _wake.Set();
        }

        private void Run()
        {
            var name = Config.EffectiveName;
            int backoffMs = 1000;
            var sw = new Stopwatch();
            while (!_stop)
            {
                try
                {
                    if (_reconnect)
                    {
                        _reconnect = false;
                        lock (_io) _client.Close();
                    }
                    if (!_client.IsConnected)
                    {
                        lock (_io) _client.Connect();
                        backoffMs = 1000;
                        SetConnected(true, "");
                        Log.Information("Modbus 접속 {Reader} {Host}:{Port}", name, Config.Host, Config.Port);
                    }

                    bool present;
                    ushort raw;
                    ushort[] words = Array.Empty<ushort>();
                    sw.Restart();
                    lock (_io)
                    {
                        (present, raw) = ModbusRfidMap.ReadPresent(_client, Config);
                        if (present) words = ModbusRfidMap.ReadUidWords(_client, Config);
                    }
                    sw.Stop();
                    var uid = present ? ModbusRfidMap.Decode(Config, words) : Array.Empty<byte>();

                    _lastResponseMs = sw.Elapsed.TotalMilliseconds;
                    _lastPoll = DateTimeOffset.Now;
                    Interlocked.Increment(ref _requests);
                    _present = present;
                    _presentRaw = raw;
                    _uidWords = words;
                    _uid = uid;

                    var state = present ? PresenceState.Present : PresenceState.Empty;
                    bool uidChanged = present && !uid.AsSpan().SequenceEqual(_lastUid);
                    if (state != _last || uidChanged)
                    {
                        _lastUid = present ? uid : Array.Empty<byte>();
                        Raise(state, raw, RawText(raw));
                    }

                    _wake.Wait(Math.Max(20, Config.PollMs));
                    _wake.Reset();
                }
                catch (Exception ex)
                {
                    if (_stop) break;
                    lock (_io) _client.Close();
                    Interlocked.Increment(ref _failures);
                    var wasConnected = Connected;
                    SetConnected(false, ex.Message);
                    if (wasConnected || _last != PresenceState.Unavailable)
                        Log.Warning("Modbus {Reader} 끊김: {Msg}", name, ex.Message);
                    if (_last != PresenceState.Unavailable) Raise(PresenceState.Unavailable, 0, ex.Message);
                    _lastUid = Array.Empty<byte>();
                    _present = false;
                    _wake.Wait(backoffMs);
                    _wake.Reset();
                    backoffMs = Math.Min(backoffMs * 2, 10_000);
                }
            }
        }

        private string RawText(ushort raw) => Config.PresentArea is ModbusArea.Coil or ModbusArea.DiscreteInput
            ? $"bit={raw}"
            : $"reg=0x{raw:X4}";

        private void SetConnected(bool connected, string error)
        {
            if (Connected == connected && LastError == error) return;
            Connected = connected;
            LastError = error;
            _owner.ReaderConnectionChanged?.Invoke(Config.EffectiveName, connected, error);
            _owner.PublishStatus();
        }

        private void Raise(PresenceState state, ushort raw, string? rawText)
        {
            _last = state;
            _owner.PresenceChanged?.Invoke(_owner, new ReaderPresenceEventArgs
            {
                ReaderName = Config.EffectiveName,
                State = state,
                Atr = Array.Empty<byte>(),
                RawFlags = raw,
                Time = DateTimeOffset.Now,
                RawText = rawText ?? ""
            });
        }

        /// <summary>즉시 읽기. 태그 없음·미접속·UID 0 은 예외 (PresenceTracker 가 재시도한다).</summary>
        public TagReadResult ReadTagNow()
        {
            byte[] uid;
            lock (_io)
            {
                if (!_client.IsConnected) throw new InvalidOperationException("Modbus 미접속: " + LastError);
                var (present, _) = ModbusRfidMap.ReadPresent(_client, Config);
                if (!present) throw new InvalidOperationException("태그 없음");
                uid = ModbusRfidMap.Decode(Config, ModbusRfidMap.ReadUidWords(_client, Config));
            }
            if (uid.Length == 0) throw new InvalidOperationException("UID 레지스터가 0");
            return new TagReadResult(uid, Array.Empty<byte>(), ModbusRfidMap.TechOf(Config.TagFamily));
        }
    }
}
