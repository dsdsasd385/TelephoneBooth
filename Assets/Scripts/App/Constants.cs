namespace App
{
    /// <summary>
    /// 여러 파일에서 함께 참조하는 식별자 상수 모음.
    /// </summary>
    public static class Constants
    {
        public static class Scenes
        {
            public const string Idle = "0_Idle";
            // 미션 씬. 미션1 = 문화수호, 미션2 = 진실수호, 미션3 = 민족수호. 씬 이름을 바꾸면 여기만 고친다.
            public const string Mission1 = "Mission1";
            public const string Mission2 = "Mission2";
            public const string Mission3 = "Mission3";
            // 공용 종료 흐름(메시지 입력). 아직 씬이 없어 미션 마무리의 "메시지 보내기"는 0_Idle로 대신 간다.
            public const string Message = "9_Message";
        }

        public static class Files
        {
            // StreamingAssets 기준 상대 경로. 0_Idle 페이지별 영상·TTS 파일 목록.
            public const string IdlePages = "Data/IdlePages.json";
            // 영상 재생 방식(더블 버퍼링 on/off) 설정.
            public const string MediaSettings = "Data/MediaSettings.json";
            // 미션1(문화수호) 페이지별 영상·TTS와 선택지 피드백 TTS.
            public const string Mission1Content = "Data/Mission1Content.json";
            // 미션2(진실수호) 대사 화면별 대사 TTS.
            public const string Mission2Content = "Data/Mission2Content.json";
            // 미션3(민족수호) 화면별 TTS·오답 TTS·정답 효과음.
            public const string Mission3Content = "Data/Mission3Content.json";
            // 템플릿 앱 설정. 프로젝트는 여기서 부스 번호와 부스별 운영 로그 URL(DeviceProfile)도 읽는다.
            public const string AppSettings = "Settings.json";
            // PC마다 바꾸는 장치 설정(수화기 입력 방식·아두이노 요청 주기·응답 제한 시간).
            public const string DeviceSettings = "Data/DeviceSettings.json";
            // 9_Message 메시지 입력 설정(최대 글자 수).
            public const string MessageSettings = "Data/MessageSettings.json";
        }

        // 수화기 아두이노 시리얼 프로토콜(작업계획서 4.2). 줄 단위 텍스트이며 펌웨어와 글자 하나까지 맞춰야 한다.
        public static class Arduino
        {
            // 통신 속도. 펌웨어의 Serial.begin 값과 같아야 한다.
            public const int BaudRate = 9600;
            // 아두이노가 시작할 때 보내는 식별 문자열(대소문자 구분).
            public const string Handshake = "Telephone_Booth";
            // PC → 아두이노 상태 요청.
            public const string RequestState = "State";
            // 아두이노 → PC 응답. On = 수화기가 걸려 있음(내려놓음), Off = 들려 있음.
            public const string ResponseHungUp = "On";
            public const string ResponseLifted = "Off";
        }
    }
}
