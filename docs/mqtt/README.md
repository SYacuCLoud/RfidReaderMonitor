# MQTT 발행 형식 (v1)

RfidReaderMonitor 가 **MQTT 브로커로 발행** 출력에서 내는 메시지의 토픽과 페이로드입니다. 실시간 현황판([Grid Tile Editor `/live`](https://github.com/SYacuCLoud/Grid.Tile.Editor)) 같은 구독자가 이 문서와 옆의 JSON Schema 를 기준으로 삼습니다.

이 형식은 표준(GS1 EPCIS, Sparkplug B)이 아니라 **이 프로젝트가 정한 것**입니다. 리더 상태를 벽걸이 화면에 가볍게 전하는 용도라 표준의 무게가 맞지 않았습니다. 대신 판 번호(`v`)와 스키마로 고정합니다.

## 토픽

```
{prefix}/{site}/reader/{key}/state    retained · QoS 1   리더의 지금 상태. 바뀔 때만 다시 낸다
{prefix}/{site}/reader/{key}/event    QoS 1              등장 · 제거 이벤트. 로컬 큐를 거쳐 순서대로, 최소 1회
{prefix}/{site}/host/{host}/status    retained · QoS 1   감시 PC online/offline. offline 은 유언(LWT)으로도 나간다
```

| 마디 | 값 |
|---|---|
| `prefix` | 설정 `MqttSink.Prefix`. 기본 `rfid` |
| `site` | 설정 탭의 **사업장**. 기본 `default` |
| `key` | 리더 S/N → 없으면 별명 → 없으면 리더 이름. 토픽에 못 쓰는 문자(`/` `+` `#` 공백)는 `_` |
| `host` | 감시 PC 이름(`Environment.MachineName`) |

retained 토픽에 **빈 페이로드**가 오면 그 항목을 지웠다는 뜻입니다(MQTT 규약).

## 페이로드

셋 다 UTF-8 JSON 객체이고 `v` 와 `type` 이 먼저 옵니다.

| 토픽 | 스키마 | 필수 필드 |
|---|---|---|
| `…/state` | [state.schema.json](./state.schema.json) | `v` `type` `time` `host` `reader` `serial` `present` `online` `uid` `state` |
| `…/event` | [event.schema.json](./event.schema.json) | `v` `type` `time` `kind` `reader` `readerName` `serial` `uid` `host` (+ REMOVE 에 `dwellMs`) |
| `…/status` | [status.schema.json](./status.schema.json) | `v` `type` `online` `host` (+ online 이면 `time` `version` 건수들) |

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
