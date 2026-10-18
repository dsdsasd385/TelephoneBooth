using System;

namespace Data
{
    /// <summary>
    /// PC마다 바꾸는 장치 설정. StreamingAssets/Data/DeviceSettings.json에서 앱 시작 시 한 번 읽는다.
    /// </summary>
    [Serializable]
    public class DeviceSettings
    {
        // true면 아두이노로 수화기를 감지하고, false면 Space 토글 키보드 구현을 쓴다(디버그용).
        public bool useArduino;
        // 아두이노에 상태를 요청하는 주기(ms).
        public int receiverRequestIntervalMs = 100;
        // 이 시간(ms) 동안 응답이 없으면 연결 이상 로그를 남긴다. 수화기 상태는 마지막 값을 유지한다.
        public int receiverResponseTimeoutMs = 1000;

        // 메시지를 받는 프로그램의 주소(작업계획서 10장). 부스 번호는 Settings.json의 deviceNumber(DeviceProfile)를 쓴다.
        public string messageServerHost = "127.0.0.1";
        public int messageServerPort = 7000;
        // 한 번 보낼 때 연결부터 ACK까지의 제한 시간(ms).
        public int messageSendTimeoutMs = 1500;
        // 처음 보낼 때 시도 횟수. 모두 실패하면 로컬에 저장하고 나중에 다시 보낸다.
        public int messageSendAttempts = 3;
        // 저장해 둔 메시지를 다시 보내는 주기(초).
        public int messageResendIntervalSeconds = 60;
    }
}
