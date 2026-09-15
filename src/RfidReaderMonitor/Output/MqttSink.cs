using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using MQTTnet;
using MQTTnet.Protocol;
using RfidReaderMonitor.Core;
using RfidReaderMonitor.Settings;
using Serilog;

namespace RfidReaderMonitor.Output;

/// <summary>
/// MQTT 브로커 발행 싱크. 실시간 현황판(Grid Tile Editor /live 등)이 구독하는 쪽이다.
///
/// 토픽 세 갈래:
///  - {prefix}/{site}/reader/{리더S/N}/state  retained. 리더의 지금 상태(present · uid · online).
///    화면이 늦게 켜져도 접속 즉시 모든 리더의 마지막 값을 받는다. 바뀔 때만 다시 낸다.
///  - {prefix}/{site}/reader/{리더S/N}/event  QoS 1, retained 아님. 등장·제거 이벤트 그대로(TagEvent.ToJson).
///    로컬 재전송 큐를 거치므로 브로커가 꺼져 있어도 순서대로 뒤에 나간다.
///  - {prefix}/{site}/host/{PC}/status        retained + LWT. 하트비트마다 online:true, 끊기면 브로커가 online:false 를 대신 낸다.
///
/// 리더 키는 S/N 이고(Grid Tile Editor 장치 대장의 S/N 과 같은 값), S/N 이 없으면 별명 → 이름 순으로 대신 쓴다.
/// 이력·집계는 여기서 하지 않는다 — SQL Server 싱크가 맡는다. MQTT 는 "지금" 만 전한다.
/// </summary>
public sealed class MqttSink : SinkBase
{
    private readonly MqttSinkSettings _s;
    private readonly string _queueFolder;
    private readonly string _host = Environment.MachineName;
    private readonly ConcurrentDictionary<string, ReaderState> _states = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>리더 이름 → 마지막으로 읽은 S/N. 뽑힌 순간에는 S/N 을 못 읽으므로 이것으로 같은 키를 유지한다.</summary>
    private readonly ConcurrentDictionary<string, string> _serialByName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>S/N 을 알게 된 뒤 이름 · 별명 키 토픽을 한 번 지운 리더. 예전 판이 남긴 유령 retained 를 치우는 용도.</summary>
    private readonly ConcurrentDictionary<string, byte> _staleCleared = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _pubLock = new(1, 1);
    private IMqttClient? _client;
    private OutboxQueue? _queue;
    private CancellationTokenSource? _cts;
    private Task? _connectLoop;
    private string? _lastError;
    private HeartbeatMessage? _lastHeartbeat;

    public override string Name => "MQTT";

    /// <summary>브로커와 세션이 붙어 있는지. 끊겨 있어도 이벤트는 큐에 쌓이고 상태는 접속 뒤 다시 낸다.</summary>
    public bool IsConnected => _client?.IsConnected == true;

    public MqttSink(MqttSinkSettings settings, string queueFolder)
    {
        _s = settings;
        _queueFolder = queueFolder;
    }

    // ------------------------------------------------------------ 토픽

    private string Prefix => Segment(string.IsNullOrWhiteSpace(_s.Prefix) ? "rfid" : _s.Prefix);
    private string Site => Segment(string.IsNullOrWhiteSpace(_s.Site) ? "default" : _s.Site);
    private string HostStatusTopic => $"{Prefix}/{Site}/host/{Segment(_host)}/status";
    private string StateTopic(string key) => $"{Prefix}/{Site}/reader/{key}/state";
    private string EventTopic(string key) => $"{Prefix}/{Site}/reader/{key}/event";

    /// <summary>토픽 한 마디로 쓸 수 있게 다듬는다. 구분자 · 와일드카드 · 공백은 _ 로.</summary>
    internal static string Segment(string raw)
    {
        var s = Regex.Replace(raw.Trim(), @"[\s/+#]+", "_");
        return s.Length == 0 ? "unknown" : s;
    }

    /// <summary>리더를 가리키는 키. S/N → 별명 → 이름.</summary>
    internal static string ReaderKey(string serial, string alias, string readerName)
    {
        if (!string.IsNullOrWhiteSpace(serial)) return Segment(serial);
        if (!string.IsNullOrWhiteSpace(alias)) return Segment(alias);
        return Segment(readerName);
    }

    /// <summary>
    /// 키와 S/N 을 정한다. S/N 이 비어 있어도(뽑힘 · 재열거 순간) 같은 이름으로 전에 읽은 S/N 이 있으면 그것을 쓴다.
    /// 그래야 뽑힌 상태가 **같은 토픽**에 실리고, 이름 키의 유령 토픽이 생기지 않는다.
    /// </summary>
    internal (string Key, string Serial) ResolveKey(string serial, string alias, string readerName)
    {
        var name = readerName.Trim();
        if (!string.IsNullOrWhiteSpace(serial))
        {
            if (name.Length > 0) _serialByName[name] = serial.Trim();
            return (ReaderKey(serial, alias, readerName), serial.Trim());
        }
        if (name.Length > 0 && _serialByName.TryGetValue(name, out var known))
            return (ReaderKey(known, alias, readerName), known);
        return (ReaderKey(serial, alias, readerName), serial);
    }

    /// <summary>S/N 없이 냈을 때 쓰였을 키들(별명 · 이름). S/N 키와 같은 것은 뺀다.</summary>
    internal static IEnumerable<string> StaleKeysFor(string alias, string readerName, string serialKey)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { serialKey };
        foreach (var raw in new[] { alias, readerName })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var key = Segment(raw);
            if (seen.Add(key)) yield return key;
        }
    }

    /// <summary>
    /// S/N 을 알게 된 리더의 이름 · 별명 키 토픽을 빈 retained 로 지운다(프로세스마다 한 번).
    /// 이 판 이전에 뽑힌 순간 이름 키로 남긴 `뽑힘` 상태가 브로커에 영원히 남아 현황판에 "미배치 리더" 로 떠 있었다.
    /// </summary>
    private async Task ClearStaleKeysAsync(string alias, string readerName, string serialKey, CancellationToken ct)
    {
        if (!_staleCleared.TryAdd(serialKey, 0)) return;
        foreach (var stale in StaleKeysFor(alias, readerName, serialKey))
        {
            _states.TryRemove(stale, out _);
            await PublishIfConnectedAsync(StateTopic(stale), "", retain: true, ct);
        }
    }

    // ------------------------------------------------------------ 시작 · 접속

    public override Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s.Host)) throw new ArgumentException("MQTT 브로커 주소가 비어 있음");
        _cts = new CancellationTokenSource();

        var client = new MqttClientFactory().CreateMqttClient();
        client.DisconnectedAsync += e =>
        {
            if (e.ClientWasConnected) Log.Warning("MQTT 연결 끊김 {Host}:{Port}: {Reason}", _s.Host, _s.Port, e.ReasonString ?? e.Reason.ToString());
            _lastError = e.Exception?.Message ?? e.ReasonString ?? e.Reason.ToString();
            UpdateStatus();
            return Task.CompletedTask;
        };
        client.ConnectedAsync += async _ =>
        {
            Log.Information("MQTT 연결 {Host}:{Port} ({ClientId})", _s.Host, _s.Port, _s.EffectiveClientId);
            _lastError = null;
            UpdateStatus();
            // 끊겨 있는 동안 바뀐 상태를 한꺼번에 다시 낸다. retained 라 마지막 값만 남는다.
            await RepublishAllAsync(_cts.Token);
        };
        _client = client;

        _queue = new OutboxQueue(_queueFolder, "mqtt", SendQueuedAsync);
        _queue.Changed += _ => UpdateStatus();
        _connectLoop = Task.Run(() => ConnectLoopAsync(_cts.Token));
        UpdateStatus();
        return Task.CompletedTask;
    }

    private MqttClientOptions BuildOptions()
    {
        var will = OfflineStatusJson(_host);
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(_s.Host.Trim(), _s.Port)
            .WithClientId(_s.EffectiveClientId)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithWillTopic(HostStatusTopic)
            .WithWillPayload(will)
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrWhiteSpace(_s.Username)) b = b.WithCredentials(_s.Username, _s.Password);
        if (_s.UseTls)
        {
            b = b.WithTlsOptions(o =>
            {
                o.UseTls();
                if (_s.IgnoreCertErrors) o.WithCertificateValidationHandler(_ => true);
            });
        }
        return b.Build();
    }

    /// <summary>끊겨 있으면 다시 붙는다. 실패가 이어지면 5초에서 30초까지 간격을 늘린다.</summary>
    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            if (_client is { IsConnected: false } c)
            {
                try
                {
                    await c.ConnectAsync(BuildOptions(), ct);
                    delay = TimeSpan.FromSeconds(5);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    UpdateStatus();
                    Log.Debug("MQTT 접속 실패 {Host}:{Port}: {Msg}", _s.Host, _s.Port, ex.Message);
                    delay = TimeSpan.FromSeconds(Math.Min(30, Math.Max(5, delay.TotalSeconds * 2)));
                }
            }
            else
            {
                delay = TimeSpan.FromSeconds(5);
            }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }

    // ------------------------------------------------------------ 발행

    public override async Task PublishAsync(TagEvent e, CancellationToken ct)
    {
        var (key, serial) = ResolveKey(e.Serial, e.Alias, e.ReaderName);
        // 이벤트는 큐로(순서 · 최소 1회). 큐 줄에 키를 붙여 두어 보낼 때 토픽을 다시 셈하지 않는다.
        _queue?.Enqueue(JsonSerializer.Serialize(new { key, line = e.ToJson() }, Envelope.JsonOpts));

        // 상태는 바로(마지막 값만 의미 있다). 끊겨 있으면 접속 뒤 RepublishAll 이 낸다.
        var present = e.Kind == TagEventKind.Appear;
        var next = new ReaderState(key, e.ReaderName, e.Alias, serial, present, true,
            present ? e.Uid : "", present ? e.Tech : "", present ? "PRESENT" : "EMPTY", e.Time);
        if (serial.Length > 0) await ClearStaleKeysAsync(e.Alias, e.ReaderName, key, ct);
        await UpsertStateAsync(next, ct);
    }

    public override async Task PublishHeartbeatAsync(HeartbeatMessage hb, CancellationToken ct)
    {
        _lastHeartbeat = hb;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in hb.Readers)
        {
            // 뽑힌 리더는 S/N 이 비어 온다. 전에 읽어 둔 S/N 으로 같은 키를 유지해야 "그 자리가 비었다" 가 된다.
            var (key, serial) = ResolveKey(r.Serial, r.Alias, r.Name);
            seen.Add(key);
            var present = r.State.StartsWith("PRESENT", StringComparison.Ordinal);
            var online = r.State is not ("뽑힘" or "사용불가" or "?");
            _states.TryGetValue(key, out var prev);
            var next = new ReaderState(key, r.Name, r.Alias, serial, present, online,
                present ? (string.IsNullOrEmpty(r.Uid) ? prev?.Uid ?? "" : r.Uid) : "",
                present ? prev?.Tech ?? "" : "", r.State, hb.Time);
            if (serial.Length > 0) await ClearStaleKeysAsync(r.Alias, r.Name, key, ct);
            await UpsertStateAsync(next, ct);
        }
        // 목록에서 사라진 리더(뽑힘 뒤 제거)는 offline 으로 남긴다. 지우지는 않는다 — 화면이 "있던 자리가 비었다" 를 알아야 한다.
        foreach (var (key, st) in _states)
        {
            if (seen.Contains(key) || !st.Online) continue;
            await UpsertStateAsync(st with { Present = false, Online = false, Uid = "", Tech = "", StateText = "뽑힘", At = hb.Time }, ct);
        }
        await PublishHostStatusAsync(hb, ct);
    }

    private async Task UpsertStateAsync(ReaderState next, CancellationToken ct)
    {
        if (_states.TryGetValue(next.Key, out var prev) && prev.Signature == next.Signature) return;
        _states[next.Key] = next;
        await PublishIfConnectedAsync(StateTopic(next.Key), next.ToJson(_host), retain: true, ct);
    }

    private Task PublishHostStatusAsync(HeartbeatMessage hb, CancellationToken ct)
        => PublishIfConnectedAsync(HostStatusTopic, OnlineStatusJson(hb, _host), retain: true, ct);

    /// <summary>`…/host/{PC}/status` 의 online 페이로드. 스키마: docs/mqtt/status.schema.json</summary>
    internal static string OnlineStatusJson(HeartbeatMessage hb, string host) => JsonSerializer.Serialize(new
    {
        v = Envelope.Version,
        type = "status",
        online = true,
        host,
        time = hb.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        version = hb.Version,
        readerCount = hb.Readers.Count,
        onlineReaders = hb.OnlineReaders,
        presentReaders = hb.PresentReaders,
        appearToday = hb.AppearToday,
        removeToday = hb.RemoveToday
    }, Envelope.JsonOpts);

    /// <summary>offline 페이로드. 유언(LWT)과 정상 종료가 같은 것을 낸다. 시각은 없다 — 브로커가 대신 낼 때는 알 수 없다.</summary>
    internal static string OfflineStatusJson(string host)
        => JsonSerializer.Serialize(new { v = Envelope.Version, type = "status", online = false, host }, Envelope.JsonOpts);

    /// <summary>접속돼 있을 때만 낸다. 안 됐으면 조용히 넘어간다 — retained 값은 접속 뒤 한꺼번에 다시 내기 때문이다.</summary>
    private async Task PublishIfConnectedAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        var c = _client;
        if (c is null || !c.IsConnected) return;
        await _pubLock.WaitAsync(ct);
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(retain)
                .Build();
            await c.PublishAsync(msg, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
            UpdateStatus();
            Log.Debug("MQTT 발행 실패 {Topic}: {Msg}", topic, ex.Message);
        }
        finally
        {
            _pubLock.Release();
        }
    }

    private async Task RepublishAllAsync(CancellationToken ct)
    {
        foreach (var st in _states.Values.ToList())
            await PublishIfConnectedAsync(StateTopic(st.Key), st.ToJson(_host), retain: true, ct);
        if (_lastHeartbeat is not null) await PublishHostStatusAsync(_lastHeartbeat, ct);
    }

    /// <summary>재전송 큐가 부른다. 접속이 없으면 false 를 돌려 큐가 뒤에 다시 시도하게 한다.</summary>
    private async Task<bool> SendQueuedAsync(string line, CancellationToken ct)
    {
        var c = _client;
        if (c is null || !c.IsConnected) return false;
        string key, payload;
        try
        {
            using var doc = JsonDocument.Parse(line);
            key = doc.RootElement.GetProperty("key").GetString() ?? "unknown";
            payload = doc.RootElement.GetProperty("line").GetString() ?? "";
        }
        catch (Exception)
        {
            return true; // 해석 불가 줄은 버린다.
        }
        if (payload.Length == 0) return true;

        await _pubLock.WaitAsync(ct);
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(EventTopic(key))
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var result = await c.PublishAsync(msg, ct);
            if (!result.IsSuccess) { _lastError = result.ReasonString ?? result.ReasonCode.ToString(); return false; }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            UpdateStatus();
            return false;
        }
        finally
        {
            _pubLock.Release();
        }
    }

    private void UpdateStatus()
    {
        var pending = _queue?.Pending ?? 0;
        var where = $"{_s.Host.Trim()}:{_s.Port} {Prefix}/{Site}";
        Status = IsConnected
            ? $"{where} 연결됨, 리더 {_states.Count}, 대기 {pending}"
            : $"{where} 연결 안 됨, 재시도 중 (대기 {pending}){(_lastError is null ? "" : " - " + _lastError)}";
    }

    public override async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_queue is not null) await _queue.DisposeAsync();
        var c = _client;
        if (c is not null)
        {
            try
            {
                if (c.IsConnected)
                {
                    // 정상 종료는 유언이 안 나가므로 offline 을 직접 남긴다.
                    var bye = OfflineStatusJson(_host);
                    using var t = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await c.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(HostStatusTopic).WithPayload(bye)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(true).Build(), t.Token);
                    await c.DisconnectAsync(cancellationToken: t.Token);
                }
            }
            catch { }
            c.Dispose();
        }
        if (_connectLoop is not null) { try { await _connectLoop; } catch { } }
        Status = "중지";
    }

    /// <summary>리더 하나의 "지금". retained 로 나가는 것은 이 값이다.</summary>
    internal sealed record ReaderState(
        string Key, string ReaderName, string Alias, string Serial,
        bool Present, bool Online, string Uid, string Tech, string StateText, DateTimeOffset At)
    {
        /// <summary>바뀌었는지 볼 때 쓰는 값. 시각은 빼야 하트비트마다 다시 내지 않는다.</summary>
        public string Signature => $"{ReaderName}|{Alias}|{Serial}|{Present}|{Online}|{Uid}|{Tech}|{StateText}";

        /// <summary>`…/reader/{S/N}/state` 페이로드. 스키마: docs/mqtt/state.schema.json</summary>
        public string ToJson(string host) => JsonSerializer.Serialize(new
        {
            v = Envelope.Version,
            type = "state",
            // 이벤트 · 하트비트와 같은 이름(time). 상태가 바뀐 시각이다.
            time = At.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            host,
            reader = string.IsNullOrWhiteSpace(Alias) ? ReaderName : Alias,
            alias = Alias,
            readerName = ReaderName,
            serial = Serial,
            present = Present,
            online = Online,
            uid = Uid,
            tech = Tech,
            state = StateText
        }, Envelope.JsonOpts);
    }
}
