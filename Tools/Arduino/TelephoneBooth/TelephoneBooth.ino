// 전화부스 수화기 감지 펌웨어(작업계획서 4.2).
// Unity의 ArduinoReceiverInput과 짝을 이룬다. 문자열과 통신 속도는 Unity Constants.Arduino와 글자 하나까지 같아야 한다.
//
// 배선(기본값): 훅 스위치 한쪽을 HOOK_PIN, 다른 쪽을 GND에 연결한다. 내부 풀업을 쓰므로 저항은 필요 없다.
//   수화기를 걸었을 때 스위치가 닫히면(LOW) HUNG_UP_WHEN_LOW = true, 반대면 false로 바꾼다.
// 동작:
//   - PC가 "State"를 보내면 수화기가 걸려 있을 때 "On", 들려 있을 때 "Off"로 답한다(요청이 있을 때만 보낸다).
//   - "State"를 한동안 못 받으면 PC가 포트를 찾는 중일 수 있으므로 "Telephone_Booth"를 주기적으로 보낸다.
//     Unity 템플릿 ArduinoManager는 포트를 연 뒤 500ms 안에 온 첫 줄로 장치를 식별하기 때문이다.
//   - 내장 LED는 수화기가 들려 있을 때 켜진다(현장 배선 확인용).
// 확인: 시리얼 모니터 9600, 줄 끝 "Newline"으로 두고 State를 보내면 On/Off가 돌아와야 한다.

const long BAUD_RATE = 9600;
const char HANDSHAKE[] = "Telephone_Booth";
const char REQUEST_STATE[] = "State";
const char RESPONSE_HUNG_UP[] = "On";
const char RESPONSE_LIFTED[] = "Off";

// 훅 스위치 입력 핀과 논리.
const uint8_t HOOK_PIN = 8;
const bool HUNG_UP_WHEN_LOW = true;

// 스위치 떨림을 무시하는 시간. 이 시간 동안 값이 그대로여야 상태를 바꾼다.
const unsigned long DEBOUNCE_MS = 30;
// 핸드셰이크 반복 주기와, State가 이만큼 끊기면 핸드셰이크를 다시 시작하는 시간.
// Unity는 100ms마다 State를 보내므로 정상 동작 중에는 핸드셰이크가 나가지 않는다.
const unsigned long HANDSHAKE_INTERVAL_MS = 200;
const unsigned long REQUEST_SILENCE_MS = 2000;

// 받은 한 줄을 모으는 버퍼. 이보다 긴 줄은 버린다.
const uint8_t LINE_BUFFER_SIZE = 32;

char lineBuffer[LINE_BUFFER_SIZE];
uint8_t lineLength = 0;
bool isLineOverflow = false;

bool isHungUp = true;
bool lastRawHungUp = true;
unsigned long rawChangedAt = 0;

bool hasRecentRequest = false;
unsigned long lastRequestAt = 0;
unsigned long lastHandshakeAt = 0;

void setup()
{
  pinMode(HOOK_PIN, INPUT_PULLUP);
  pinMode(LED_BUILTIN, OUTPUT);
  Serial.begin(BAUD_RATE);

  isHungUp = readRawHungUp();
  lastRawHungUp = isHungUp;
  rawChangedAt = millis();
  updateLed();

  sendHandshake(millis());
}

void loop()
{
  unsigned long now = millis();
  updateHookState(now);
  readSerial(now);
  sendHandshakeIfIdle(now);
}

// 핀 값을 "수화기가 걸려 있음" 여부로 바꿔 읽음.
bool readRawHungUp()
{
  bool isLow = digitalRead(HOOK_PIN) == LOW;
  return HUNG_UP_WHEN_LOW ? isLow : !isLow;
}

// 핀 값이 DEBOUNCE_MS 동안 유지됐을 때만 수화기 상태를 바꿈.
void updateHookState(unsigned long now)
{
  bool raw = readRawHungUp();

  if (raw != lastRawHungUp)
  {
    lastRawHungUp = raw;
    rawChangedAt = now;
    return;
  }

  if (raw != isHungUp && now - rawChangedAt >= DEBOUNCE_MS)
  {
    isHungUp = raw;
    updateLed();
  }
}

void updateLed()
{
  digitalWrite(LED_BUILTIN, isHungUp ? LOW : HIGH);
}

// 들어온 글자를 줄 단위로 모아 한 줄이 끝나면 처리함. '\r'은 버린다.
void readSerial(unsigned long now)
{
  while (Serial.available() > 0)
  {
    char c = (char)Serial.read();

    if (c == '\n')
    {
      if (!isLineOverflow)
      {
        lineBuffer[lineLength] = '\0';
        handleLine(trim(lineBuffer), now);
      }
      lineLength = 0;
      isLineOverflow = false;
    }
    else if (c == '\r')
    {
    }
    else if (lineLength < LINE_BUFFER_SIZE - 1)
    {
      lineBuffer[lineLength++] = c;
    }
    else
    {
      isLineOverflow = true;
    }
  }
}

// 앞뒤 공백을 지운 문자열의 시작 위치를 돌려줌(버퍼를 그 자리에서 자른다).
char* trim(char* text)
{
  while (*text == ' ' || *text == '\t') text++;

  char* end = text + strlen(text);
  while (end > text && (end[-1] == ' ' || end[-1] == '\t')) end--;
  *end = '\0';

  return text;
}

// State면 현재 수화기 상태로 답함. 그 밖의 줄은 무시한다.
void handleLine(const char* line, unsigned long now)
{
  if (strcmp(line, REQUEST_STATE) != 0) return;

  Serial.println(isHungUp ? RESPONSE_HUNG_UP : RESPONSE_LIFTED);
  hasRecentRequest = true;
  lastRequestAt = now;
}

// 최근에 State를 받지 못했으면 핸드셰이크를 주기적으로 보냄.
void sendHandshakeIfIdle(unsigned long now)
{
  if (hasRecentRequest)
  {
    if (now - lastRequestAt < REQUEST_SILENCE_MS) return;
    hasRecentRequest = false;
  }

  if (now - lastHandshakeAt >= HANDSHAKE_INTERVAL_MS) sendHandshake(now);
}

void sendHandshake(unsigned long now)
{
  Serial.println(HANDSHAKE);
  lastHandshakeAt = now;
}
