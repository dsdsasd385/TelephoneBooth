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
    }
}
