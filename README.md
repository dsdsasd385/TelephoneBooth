# 전화부스

천안 독립기념관 전시 체험 앱입니다. 관람객이 수화기를 들면 "과거의 독립운동가에게서 걸려 온 전화"가 시작됩니다.
미션(미니게임)을 고르고, 마친 뒤에는 다른 미션을 더 하거나 독립운동가에게 보내는 메시지를 남깁니다.

- 같은 앱을 PC 3대(부스 1–3번)에서 실행합니다.
- 화면: FHD 1920×1080 가로, Windows 터치 모니터
- 소리: 수화기 스피커로만 출력(기본 오디오 장치 1개)
- 수화기 감지: 아두이노 + USB 시리얼
- 메시지: TCP로 별도 수신 프로그램(`TelephoneBooth_Server`)에 보냅니다.

기획과 결정 사항은 [작업계획서.md](작업계획서.md), 진행 상황과 주의할 점은 [인수인계.md](인수인계.md)(맨 위 "지금 상태"부터), 할 일 목록은 [작업현황.md](작업현황.md)를 보세요.

## 개발 환경

- **Unity 2022.3.62f3 LTS**, Universal RP 14
- 저장소를 클론해 Unity Hub에서 엽니다. 처음 열 때 패키지를 내려받느라 몇 분 걸립니다.
- [0_Startup 템플릿](#기반-템플릿과-스택)으로 시작한 프로젝트입니다.

## 체험 흐름

```
[0_Idle]
 ① 대기 ──수화기 듦──▶ ② 안내 ─▶ ③ 안내 ─▶ ④ 안내 ─▶ ⑤ 미션 선택 (미션1 / 2 / 3, 홈 → ①)
                                                         │
[Mission1 · Mission2 · Mission3] ◀───────────────────────┘
 미션 진행 ─▶ 마무리 선택 화면 ─┬─ 메시지 보내기 ─▶ [9_Message]
                              └─ 다른 미션 ─▶ 현재 미션을 뺀 2개 중 선택 ─▶ [고른 미션 씬]
[9_Message]
 "수화기를 내려놓아 주세요" ──내려놓음──▶ 메시지 입력 ─▶ 보내기/건너뛰기 ─▶ 로딩 영상 ─▶ [0_Idle] ①
```

- 수화기를 내려놓으면 0_Idle ②–⑤와 미션 씬 어디서든 바로 ①로 돌아갑니다.
- 화면마다 영상(무음 반복)과 TTS(1회)가 나오고, 버튼은 TTS가 끝난 뒤 나타납니다.
- 버튼을 기다리는 화면에서 아무 입력이 없으면 `resetTime`(초) 뒤 0_Idle ①로 돌아갑니다. TTS가 나오는 동안에는 세지 않습니다.

## 씬

빌드 순서대로입니다. 씬마다 씬 매니저 `{씬}SceneController`와 DI 스코프 `{씬}LifetimeScope`가 있습니다.

| 씬 | 내용 | 주요 스크립트 |
|---|---|---|
| `0_Idle` | 대기·안내·미션 선택 화면 ①–⑤ | `IdleSceneController` |
| `Mission1` | 문화수호: 포장 → 보관 → 은닉 3단계 퀴즈 | `Mission1SceneController`, `QuizStageView` |
| `Mission2` | 진실수호: 보고서 사진 고르기 | `Mission2SceneController`, `ReportQuizView` |
| `Mission3` | 민족수호: 지도에서 장소를 찾아 암호 풀기 | `Mission3SceneController`, `CipherMapView` |
| `9_Message` | 화면 키보드로 이름·다짐 입력 후 전송 | `MessageSceneController`, `VirtualKeyboard` |

## 에디터에서 테스트하기

Play한 뒤 Game 뷰를 한 번 클릭해 포커스를 줍니다(키 입력은 Game 뷰에 포커스가 있어야 들어갑니다).

| 키 | 동작 |
|---|---|
| `Space` | 수화기 들기/내려놓기 전환(아두이노를 쓰지 않을 때) |
| `Ctrl + →` / `Ctrl + ←` | 다음·이전 화면(수화기·버튼 규칙 무시) |
| `F8` | 영상 더블 버퍼링 켜기/끄기 |

디버그 키는 에디터와 개발 빌드에서만 동작합니다. 아두이노 없이 키보드로 테스트하려면 `DeviceSettings.json`의 `useArduino`를 `false`로 바꿉니다.

## 설정 파일

모두 `Assets/StreamingAssets/`(빌드에서는 `<앱>_Data/StreamingAssets/`)에 있어서, 빌드한 뒤에도 파일만 고쳐 바꿀 수 있습니다.
**이미지는 씬에서 관리**하므로 바꾸려면 다시 빌드해야 하고, **영상·TTS는 JSON에 경로를 적고 파일을 StreamingAssets에 둡니다.**

| 파일 | 주요 항목 |
|---|---|
| `Settings.json` | `deviceNumber`(부스 번호 1–3), `apiUrls`(부스별 운영 로그 URL), `useInactivityTimer`·`resetTime`(무입력 복귀, 초), `targetFrameRate`, `closeSetting`(숨은 종료 버튼) |
| `ShutdownSettings.json` | 요일별 자동 종료 |
| `Data/DeviceSettings.json` | `useArduino`, 수화기 요청 주기·응답 제한 시간·핸드셰이크 대기(ms), `logReceiverResponses`, 메시지 서버 `messageServerHost`·`messageServerPort`, 전송 제한 시간·시도 횟수·재전송 간격 |
| `Data/MessageSettings.json` | 다짐 최대 글자 수 `maxLength`, 이름 최대 글자 수 `nameMaxLength`, 로딩 영상 경로·재생 횟수 |
| `Data/MediaSettings.json` | `doubleBuffering`(영상 반복 끊김 없애기, 기본 `false`) |
| `Data/IdlePages.json` | 0_Idle 화면별 영상·TTS 경로 |
| `Data/Mission1Content.json` – `Mission3Content.json` | 미션 화면별 영상·TTS 경로 |

## 설치(부스 PC마다)

1. 빌드 폴더를 복사합니다.
2. `Settings.json`의 `deviceNumber`를 그 부스 번호(1–3)로 바꿉니다. 운영 로그 URL과 메시지의 `deviceNumber`가 이 값 하나로 정해집니다.
3. 한 번 실행합니다. 시작할 때 `apiUrls[deviceNumber-1]`이 `apiUrl`에 써지고, 작업 스케줄러의 종료 로그가 이 값을 씁니다.
4. `DeviceSettings.json`의 `messageServerHost`·`messageServerPort`가 메시지 수신 PC 주소와 맞는지 확인합니다. 수신 PC의 방화벽에서 해당 포트를 열어야 합니다.
5. Arduino IDE의 시리얼 모니터처럼 COM 포트를 쓰는 프로그램은 닫아 둡니다.

## 하드웨어: 수화기 아두이노

- 펌웨어: [Tools/Arduino/TelephoneBooth/TelephoneBooth.ino](Tools/Arduino/TelephoneBooth/TelephoneBooth.ino)
- 9600bps, 줄 단위 텍스트입니다. 아두이노가 시작할 때 `Telephone_Booth`를 보내고, PC가 100ms마다 `State`를 보내면 `On`(걸려 있음) 또는 `Off`(들려 있음)로 답합니다.
- 기본 배선은 D2 ↔ GND(내부 풀업, 걸었을 때 LOW)입니다. 반대면 펌웨어의 `HUNG_UP_WHEN_LOW`를 `false`로 바꿉니다.
- 앱은 모든 COM 포트를 동시에 열어 핸드셰이크로 아두이노를 찾고, 끊기면 다시 찾습니다(`Hardware/ArduinoSerialPort`).
- 연결을 확인할 때는 `logReceiverResponses`를 `true`로 켜면 응답이 로그에 남습니다. 100ms마다 한 줄씩 쌓이므로 확인 뒤 다시 끕니다.

## 메시지 전송(TCP)

- 메시지마다 연결 → 4바이트 빅엔디언 길이 + UTF-8 JSON → ACK `{"type":"ack","id":"…"}` 수신 → 종료.
- 본문: `{"type":"message","id","deviceNumber","content","name","text","sentAt"}`. `content`는 마지막으로 한 미션(`culture`/`truth`/`nation`/`none`)입니다.
- 실패하면 몇 번 다시 시도하고, 그래도 안 되면 `persistentDataPath/MessageOutbox/`에 저장한 뒤 주기적으로 다시 보냅니다. 관람객 화면은 성공·실패와 관계없이 같습니다.
- 테스트용 수신기: `python Tools/MessageReceiver/test_receiver.py [--port 7000] [--no-ack] [--delay 초] [--show-text]`

## 운영 로그

템플릿 운영 로그(`start`, `end`, `move_idle`, `move_idle_timeout`, API 호출 결과)를 부스 번호에 맞는 URL로 보냅니다. 메시지 전송 결과도 `tcp://IP:포트` 호출로 남고, 메시지 본문은 남기지 않습니다.
릴리스 빌드에서만 실제로 보냅니다. 에디터에서 확인하려면 `APIManager`의 `Send Logs For Test`를 켜거나 메뉴 `Tools > 운영 로그 API`를 씁니다. 이때도 **운영 서버에 기록이 남습니다.**

## 테스트

EditMode 테스트([Assets/Tests/EditMode](Assets/Tests/EditMode))는 Unity `Window > General > Test Runner`에서 실행합니다.
한글 조합(`HangulComposer`), 암호 변환(`CipherCodec`), TCP 전송(`TcpMessageClient`)을 다룹니다.

## 폴더 구조

```
Assets/
├── Scenes/                 # 0_Idle, Mission1–3, 9_Message
├── Scripts/
│   ├── App/                # 루트·씬별 LifetimeScope, Constants(씬 이름·파일 경로·프로토콜 문자열)
│   ├── Core/               # GameManager, ExperienceSession(선택한 미션), InactivityTimerGate
│   ├── Data/               # 설정·콘텐츠 JSON 모델과 로더
│   ├── Hardware/           # 수화기 입력(IReceiverInput, 아두이노·키보드 구현)
│   ├── Idle/               # 0_Idle 씬 매니저
│   ├── Media/              # 화면 영상·TTS 재생
│   ├── Mission/            # 미션 씬 매니저와 화면 뷰
│   ├── Message/            # 9_Message 씬 매니저, 화면 키보드
│   ├── Network/            # 운영 로그 APIManager, MessageSender, Transport(TCP)
│   └── TextInput/          # 한글 조합, 암호 변환
├── StreamingAssets/        # 설정 JSON, 영상(Video)·TTS(Audio)
├── Prefabs/                # GameLifetimeScope(루트 DI) 등
└── Tests/EditMode/
Tools/
├── Arduino/TelephoneBooth/ # 수화기 펌웨어
└── MessageReceiver/        # 테스트용 TCP 수신기
```

## 기반 템플릿과 스택

0_Startup 템플릿과 공통 패키지 `com.huliacdev.template`(GameManagerBase, InactivityTimer, ShutdownScheduler, 매니저 베이스 등)을 씁니다.

| 패키지 | 용도 |
|---|---|
| [VContainer](https://github.com/hadashiA/VContainer) | DI |
| [UniTask](https://github.com/Cysharp/UniTask) | 비동기 |
| [MessagePipe](https://github.com/Cysharp/MessagePipe) | 앱 안 이벤트(Pub/Sub) |
| [R3](https://github.com/Cysharp/R3) | 리액티브 상태 |
| [ZLogger.Unity](https://github.com/Cysharp/ZLogger) · [ZString](https://github.com/Cysharp/ZString) | 로깅 · 문자열 |
| [DOTween](https://dotween.demigiant.com/) | 트윈(`Assets/Plugins/Demigiant`) |
| [MCP for Unity](https://github.com/CoplayDev/unity-mcp) | Claude Code와 Unity Editor 연동 |

- 루트 DI는 `Assets/Prefabs/GameLifetimeScope.prefab`이며 `VContainerSettings`에 지정돼 있어 씬에 두지 않아도 생성됩니다.
- `Tools > Update Stack Packages`는 위 스택 패키지만 업데이트합니다. URP·Input System·Addressables처럼 버전을 고정한 패키지는 건드리지 않습니다.
- `Assets/Plugins/Roslyn`은 MCP 전용이라 Editor 플랫폼에만 체크돼 있습니다. 폴더를 지우고 다시 설치하면 모든 플랫폼이 체크되므로 Editor만 남겨 플레이어 빌드에 들어가지 않게 합니다.
- 코드 규칙과 프로젝트별 예외는 [CLAUDE.md](CLAUDE.md)를 보세요.
