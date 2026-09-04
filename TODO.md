# TODO

파일럿 운용과 확장 과제. 우선순위 순.

## 파일럿 현장에서 곧 필요한 것

- [x] **이벤트 재전송 큐** 파일 기반 로컬 큐(`OutboxQueue`). TCP 클라이언트·SQL 출력이 사용. (v0.2)
- [x] **하트비트 + 수집 모드** 주기 상태 메시지, `--collector` 화면, SQL 호스트 상태 테이블. (v0.2)
- [ ] **수집 모드 배너·애니메이션** 감시 모드의 라이브 배너를 수집 화면에도 적용.
- [ ] **수집 모드 이벤트 지속** 수집 화면의 이벤트 목록은 메모리. 재시작 시 CSV에서 오늘 분을 다시 읽어 오기.
- [ ] **시간 동기화 표시** 여러 PC의 이벤트를 합칠 때 시계 편차가 그대로 오차가 된다. 하트비트에 PC 시각을 넣어 수집 PC 시각과 비교, 편차가 크면 타일에 경고.
- [ ] **원격 상태 페이지** 간단한 HTTP `/health` (리더 목록, 상태, 마지막 이벤트, 싱크 상태). TCP 싱크와 같은 계층에 추가.
- [ ] **로그 보관 일수** raw/events CSV 자동 삭제(예: 30일). 앱 로그는 Serilog 설정으로 이미 30일.
- [ ] **리더 뽑힘 알림** 리더가 사라지면 배너를 붉게 유지하고 Windows 토스트 알림.
- [ ] **운용 모드(설정 잠금)** 작업자 PC에서 설정 탭을 잠그는 옵션. 잠금 해제는 비밀번호 또는 명령행 인자.

## 확장

- [ ] **다중 리더 배너** 리더 3대 이상일 때 리더별 타일 격자. 등장 시 해당 타일만 번쩍임.
- [ ] **UID → 대상물 이름 매핑** UID 화이트리스트와 표시 이름. 배너·이벤트·CSV에 함께 출력. 매핑 파일(CSV/JSON) 핫리로드.
- [ ] **순차 매핑 이벤트** 문서의 "제거 후 제한시간 안 등장 → 연결" 규칙. 상위 시스템에서 처리하는 것을 기본으로 두고, 옵션으로만 제공할지 결정.
- [ ] **시뮬레이터 프로바이더** `IRfidReaderProvider` 구현으로 가짜 리더·태그 이벤트 생성. 리더 없이 상위 시스템 개발·UI 시연용. 스크린샷 촬영에도 사용. (Modbus 경로는 개발자 탭의 Modbus TCP 리더 시뮬레이터로 이미 가능. PC/SC 경로용은 미구현)
- [x] **Modbus TCP 리더 프로바이더** 산업용 헤드(Turck TBEN, Balluff BIS V, IO-Link 마스터)의 Tag Present 비트·UID 레지스터를 폴링하는 `ModbusReaderProvider`. 설정 탭에서 리더별 레지스터 맵 편집. PC/SC 와 `CompositeReaderProvider` 로 합쳐 동시 감시. 실물 장치의 레지스터 맵 확인은 아직.
- [ ] **입력 경로 결정 대기 (장치 확정 후 진행)** 2026-09-04 결론: 어떤 산업용 리더가 들어올지 정해지지 않아 아래 둘은 보류.
  - **OPC UA AutoID 프로바이더** — 장치가 OPC UA for AutoID(OPC 30010)를 지원하면 이쪽이 표준 경로. `RfidScanResult`(CodeType, ScanData 유니온 ByteString/String, Sighting 의 안테나·RSSI, 타임스탬프) 디코딩, `DeviceStatus`, `ScanStart/ScanStop` 호출, `RfidScanEventType` 이벤트 구독. 이벤트 기반이라 PresenceTracker 에 "이벤트 후 무소식 = 제거" 모드 필요. 인증서(Basic256Sha256, 서버 신뢰 목록)와 사용자 인증(DPAPI 저장) 필수. 스택은 MIT 인 Workstation.UaClient(공식 OPC Foundation 스택은 비회원 GPL 2.0 이라 MIT 저장소에 못 씀). AutoID 로 대상을 고정하면 노드 브라우저는 생략 가능. 견적: Modbus 프로바이더의 1.5배.
  - **MQTT 구독 프로바이더(슬림)** — 장치가 MQTT 를 직접 내거나 게이트웨이(Kepware, Node-RED, Ignition 등)를 거칠 때. 설정 모델(`MqttReaderSettings`), 비밀번호 DPAPI 보호(`Mqtt/Secret.cs`), JSON 경로 해석기(`Mqtt/MqttPayloadParser.cs`)는 이미 들어가 있음. 남은 것: MQTTnet(MIT, 4.3.x) 참조 추가, `MqttReaderProvider`(브로커별 접속 공유, 재접속, 토픽 매칭, StaleMs), 등록 폼, 리더 도구 탭 표시. 토픽 탐색기·내장 브로커는 만들지 않음(MQTT Explorer, MQTTX 로 대체). 견적: Modbus 의 절반.
  - 일반 OPC UA(규격 없는 벤더 노드)는 만들지 않음. 미들웨어로 MQTT 변환 후 위 MQTT 경로 사용.
- [ ] **Modbus TCP 서버 싱크 (방향 2: PLC 가 이 PC 를 읽어감)** 502 포트를 열고 리더별 Tag Present 비트, UID 레지스터, 마지막 이벤트 시각을 레지스터 맵으로 유지. `IEventSink` 로 추가.
- [ ] **OPC UA 서버 싱크 (방향 2)** 리더마다 노드를 만들고 값 변경 시 갱신. AutoID Companion Spec 노드셋 탑재 여부와 스택 라이선스(GPL/상용) 결정 필요.
- [ ] **다국어 UI** XAML 문자열을 리소스로 분리한 뒤 영어 추가.
- [ ] **코드 서명** SmartScreen 경고 제거. 인증서 확보 후 CI 서명 단계 추가. 업데이터가 내려받은 exe 실행에도 도움.
- [x] **수동 업데이트** "업데이트 확인" 버튼 → GitHub Releases 비교 → sha256 검증 → 교체·재시작. (v0.2)
- [ ] **업데이트 자동 확인 옵션** 시작 시 또는 하루 1회 확인 후 배지만 표시. 수집 PC용 자동 적용 옵션(태그 없고 조작 없을 때만).
- [ ] **수집 모드에서 구버전 PC 표시** 하트비트의 version 과 최신 릴리스를 비교해 타일에 "업데이트 필요" 표시.

## 확인 필요 (실물)

- [ ] ACR1552U PICC 파라미터의 ISO 15693 비트 위치 (현재 0x20 추정). 제조사 도구 탭 "직접 명령"으로 `E0 00 00 20 00` 응답 확인.
- [ ] ACR1552U LED 비트 배치 (현재 0x0F 전체 ON으로만 시험). 색상별 비트 확인 후 UI에 색 선택 추가.
- [ ] ISO 15693 UID 바이트 순서. 리더가 E0 로 시작하는 순서로 주는지, 역순인지 실물로 확인 후 기본값 결정.
- [ ] Modbus TCP 리더 실물 확인. **Turck TBEN-S2-2RFID-4DXP** 는 매뉴얼 7.2.8 맵을 프리셋으로 넣었음(TP = Holding 0x0002 비트 0, UID = Holding 0x000C~, 바이트 스왑). 실물에서 UID 가 E0 로 시작하는지(워드 역순 여부)만 연결 테스트로 확인. **Balluff BIS V** 는 Modbus TCP 변종이 없음(구형 BIS M-626/U-626 만, 명령·응답 핸드셰이크 방식 → 별도 드라이버 필요). Balluff/IFM 헤드는 Modbus TCP 지원 IO-Link 마스터 경로로 갈 것. 문서 없는 장치는 등록 대화상자의 레지스터 스캐너로 위치를 찾는다.
