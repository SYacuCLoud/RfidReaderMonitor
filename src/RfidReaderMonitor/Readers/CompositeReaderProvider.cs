namespace RfidReaderMonitor.Readers;

/// <summary>
/// 여러 프로바이더(PC/SC, Modbus TCP 등)를 하나로 합친다.
/// 리더 이름은 프로바이더 간에 겹치지 않는다고 가정하며, 이름으로 소유 프로바이더를 찾아 위임한다.
/// 상태 메시지는 프로바이더별 마지막 상태를 모아 하나로 보인다.
/// </summary>
public sealed class CompositeReaderProvider : IRfidReaderProvider
{
    private readonly IReadOnlyList<(string Label, IRfidReaderProvider Provider)> _children;
    private readonly Dictionary<IRfidReaderProvider, ProviderStatusEventArgs> _status = new();
    private readonly object _sync = new();

    public event EventHandler<ReaderListChangedEventArgs>? ReadersChanged;
    public event EventHandler<ReaderPresenceEventArgs>? PresenceChanged;
    public event EventHandler<ProviderStatusEventArgs>? StatusChanged;

    public IReadOnlyList<string> Readers { get; private set; } = Array.Empty<string>();

    public CompositeReaderProvider(params (string Label, IRfidReaderProvider Provider)[] children)
    {
        _children = children;
        foreach (var (label, p) in children)
        {
            p.ReadersChanged += (_, _) => PublishReaders();
            p.PresenceChanged += (s, e) => PresenceChanged?.Invoke(s, e);
            p.StatusChanged += (_, e) => OnChildStatus(label, p, e);
        }
    }

    private void PublishReaders()
    {
        IReadOnlyList<string> list;
        lock (_sync)
        {
            list = _children.SelectMany(c => c.Provider.Readers).Distinct().ToList();
            Readers = list;
        }
        ReadersChanged?.Invoke(this, new ReaderListChangedEventArgs { Readers = list });
    }

    private void OnChildStatus(string label, IRfidReaderProvider p, ProviderStatusEventArgs e)
    {
        bool healthy;
        string message;
        lock (_sync)
        {
            _status[p] = e;
            var all = _children
                .Where(c => _status.ContainsKey(c.Provider))
                .Select(c => (c.Label, Status: _status[c.Provider]))
                .ToList();
            healthy = all.All(x => x.Status.Healthy);
            message = all.Count == 1
                ? all[0].Status.Message
                : string.Join(" · ", all.Select(x => $"{x.Label}: {x.Status.Message}"));
        }
        StatusChanged?.Invoke(this, new ProviderStatusEventArgs { Healthy = healthy, Message = message });
    }

    private IRfidReaderProvider Owner(string readerName)
    {
        foreach (var (_, p) in _children)
            if (p.Readers.Contains(readerName)) return p;
        throw new InvalidOperationException($"리더를 찾을 수 없음: {readerName}");
    }

    public void Start() { foreach (var (_, p) in _children) p.Start(); }
    public void Stop() { foreach (var (_, p) in _children) p.Stop(); }
    public void Refresh() { foreach (var (_, p) in _children) p.Refresh(); }

    public ReaderKind KindOf(string readerName) => Owner(readerName).KindOf(readerName);
    public ReaderIdentity Identify(string readerName) => Owner(readerName).Identify(readerName);
    public TagReadResult ReadTag(string readerName) => Owner(readerName).ReadTag(readerName);
    public byte[] Escape(string readerName, byte[] command) => Owner(readerName).Escape(readerName, command);

    public void Dispose() { foreach (var (_, p) in _children) p.Dispose(); }
}
