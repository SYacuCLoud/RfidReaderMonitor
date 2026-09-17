# MQTT 발행 형식 (v1)

RfidReaderMonitor 가 **MQTT 브로커로 발행** 출력에서 내는 메시지의 토픽과 페이로드입니다. 실시간 현황판([Grid Tile Editor `/live`](https://github.com/SYacuCLoud/Grid.Tile.Editor)) 같은 구독자가 이 문서와 옆의 JSON Schema 를 기준으로 삼습니다.

이 형식은 표준(GS1 EPCIS, Sparkplug B)이 아니라 **이 프로젝트가 정한 것**입니다. 리더 상태를 벽걸이 화면에 가볍게 전하는 용도라 표준의 무게가 맞지 않았습니다. 대신 판 번호(`v`)와 스키마로 고정합니다.

## 토픽

```
{prefix}/{site}/reader/{key}/state    retained · QoS 1   리더의 지금 상태. 바뀔 때만 다시 낸다
{prefix}/{site}/reader/{key}/event    QoS 1              등장 · 제거 이벤트. 로컬 큐를 거쳐 순서대로, 최소 1회
{prefix}/{site}/host/{host}/status    retained · QoS 1   감시 PC online/offline. offline 은 유언(LWT)으로도 나간다

{prefix}/{site}/replay                ← 구독(요청)      놓친 구간을 다시 내 달라는 요청. 사업장의 모든 감시 PC 가 받는다 (v0.5.0)
{prefix}/{site}/host/{host}/replay    ← 구독(요청)      같은 요청을 한 PC 에게만
{prefix}/{site}/reader/{key}/replay   QoS 1              재발행된 이벤트. 페이로드는 …/event 와 같다. 현황판은 구독하지 않고 이력 기록기만 받는다
{prefix}/{site}/host/{host}/replay-done  QoS 1           재발행이 끝났다는 알림(건수)
```

| 마디 | 값 |
|---|---|
| `prefix` | 설정 `MqttSink.Prefix`. 기본 `rfid` |
| `site` | 설정 탭의 **사업장**. 기본 `default` |
| `key` | 리더 S/N → 없으면 별명 → 없으면 리더 이름. 토픽에 못 쓰는 문자(`/` `+` `#` 공백)는 `_`. 뽑힌 순간처럼 S/N 을 못 읽을 때는 같은 이름으로 전에 읽은 S/N 을 그대로 써서 키가 바뀌지 않는다(v0.4.1). S/N 을 알게 되면 이름 · 별명 키 토픽은 빈 retained 로 지운다 |
| `host` | 감시 PC 이름(`Environment.MachineName`) |

retained 토픽에 **빈 페이로드**가 오면 그 항목을 지웠다는 뜻입니다(MQTT 규약).

## 페이로드

셋 다 UTF-8 JSON 객체이고 `v` 와 `type` 이 먼저 옵니다.

| 토픽 | 스키마 | 필수 필드 |
|---|---|---|
| `…/state` | [state.schema.json](./state.schema.json) | `v` `type` `time` `host` `reader` `serial` `present` `online` `uid` `state` |
| `…/event` | [event.schema.json](./event.schema.json) | `v` `type` `time` `kind` `reader` `readerName` `serial` `uid` `host` (+ REMOVE 에 `dwellMs`) |
| `…/status` | [status.schema.json](./status.schema.json) | `v` `type` `online` `host` (+ online 이면 `time` `version` 건수들) |
| `…/replay` (요청) | [replay.schema.json](./replay.schema.json) | `v` `type` `from` `to` (+ `requestId` `host` 선택) |
| `…/replay-done` | [replay-done.schema.json](./replay-done.schema.json) | `v` `type` `host` `requestId` `from` `to` `count` `truncated` `files` (+ `error`) |

### 재발행 (v0.5.0)

이력을 쌓는 구독자(예: Grid Tile Editor 의 이벤트 기록기)가 한동안 죽어 있어 이벤트를 놓쳤을 때, 그 구간을 감시 PC 에게 다시 내 달라고 할 수 있습니다.

1. 요청자가 `{prefix}/{site}/replay`(모든 PC) 또는 `{prefix}/{site}/host/{host}/replay`(한 PC) 에 `{"v":1,"type":"replay","from":"2026-09-17T16:31:00+09:00","to":"2026-09-18T07:05:00+09:00","requestId":"k3j9x2"}` 를 냅니다. `from` · `to` 는 ISO 8601(오프셋 포함) 또는 epoch ms, 폭은 31일까지. `host` 를 넣으면 그 이름의 PC 만 답합니다.
2. 감시 PC 는 **CSV 출력**(`events-yyyyMMdd.csv`, 기본 `%LOCALAPPDATA%\RfidReaderMonitor\logs\events\`)에서 구간에 드는 줄을 읽어 한 건씩 `{prefix}/{site}/reader/{key}/replay` 로 냅니다. 페이로드는 `…/event` 와 **같은 형식**(`type: event`, 원래의 `time` · `host`)이라 소비자는 이벤트 처리 코드를 그대로 씁니다. 토픽만 다른 이유는 현황판이 지난 이벤트로 "지금 상태" 를 바꾸지 않게 하려는 것입니다.
3. 끝나면 `{prefix}/{site}/host/{host}/replay-done` 에 `count`(낸 건수) · `files`(읽은 파일 수) · `truncated`(PC 당 최대 20,000건에 걸림) · `error` 를 냅니다.

같은 `requestId` 는 PC 마다 한 번만 처리하고, 요청은 한 번에 하나씩 돕니다. CSV 출력이 꺼져 있던 기간은 다시 낼 것이 없습니다. 재발행 이벤트의 시각 · id 는 원래와 같으므로 소비자는 이미 있는 이벤트와 겹치는 것을 id 로 걸러 내면 됩니다.

공통 규칙:

- `time` 은 ISO 8601, 밀리초와 시간대 오프셋 포함(`2026-09-14T15:32:32.931+09:00`). 감시 PC 시계입니다.
- `uid` 는 16진 대문자, 공백 없음. ISO 15693 은 설정에 따라 역순일 수 있습니다.
- 문자열 필드는 값이 없어도 빈 문자열로 옵니다(`null` 은 쓰지 않습니다). 없을 수 있는 필드는 `dwellMs`(APPEAR 에 없음)와 offline `status` 의 통계뿐입니다.
- 소비자는 **모르는 필드를 무시**해야 합니다. 필드를 더할 때는 `v` 를 올리지 않습니다. 필드를 지우거나 뜻을 바꿀 때만 올립니다.

`…/event` 의 JSON 은 TCP 서버 · 명명된 파이프 · 수집 PC 전송이 내는 줄과 같은 형식입니다. 그 경로의 하트비트(`type: heartbeat`)는 MQTT 로는 나가지 않고 `…/status` 로 요약됩니다.

## 예

```json
{"v":1,"type":"state","time":"2026-09-14T15:32:32.931+09:00","host":"PC-LINE1","reader":"1번 저울","alias":"1번 저울","readerName":"ACS ACR1552 1S CL Reader PICC 0","serial":"RR657-005592","present":true,"online":true,"uid":"E0040150ABCDEF01","tech":"ISO 15693","state":"PRESENT"}
{"v":1,"type":"event","time":"2026-09-14T15:32:40.000+09:00","kind":"REMOVE","reader":"1번 저울","alias":"1번 저울","readerName":"ACS ACR1552 1S CL Reader PICC 0","serial":"RR657-005592","uid":"E0040150ABCDEF01","tech":"ISO 15693","atr":"","dwellMs":7069,"host":"PC-LINE1"}
{"v":1,"type":"status","online":true,"host":"PC-LINE1","time":"2026-09-14T15:32:33.931+09:00","version":"0.3.2","readerCount":2,"onlineReaders":2,"presentReaders":1,"appearToday":5,"removeToday":4}
{"v":1,"type":"status","online":false,"host":"PC-LINE1"}
```

## 검증

`tests/RfidReaderMonitor.Tests` 가 실제 발행 코드가 만든 페이로드를 이 스키마로 검증합니다(`dotnet test`). 스키마를 고치면 그 테스트가 지켜 줍니다.

## 바뀐 기록

- **v1** (2026-09-14) 첫 판. `state` 의 시각 필드 이름은 이벤트와 같은 `time` 입니다.
- **v1 + 재발행** (2026-09-18, 앱 v0.5.0) `…/replay` 요청 · `…/reader/{key}/replay` · `…/host/{host}/replay-done` 추가. 기존 세 토픽의 형식은 그대로라 `v` 는 올리지 않습니다.
