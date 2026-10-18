using System;

namespace Data
{
    /// <summary>
    /// 이 PC가 몇 번 부스인지(1–3)와 부스별 운영 로그 URL. StreamingAssets/Settings.json에 템플릿 설정과 함께 적는다.
    /// <para>
    /// 템플릿 Settings 클래스는 이 항목들을 모르고 무시하므로, 프로젝트가 같은 파일을 따로 읽는다(작업계획서 11장).
    /// deviceNumber 하나로 운영 로그 URL(apiUrls[deviceNumber - 1])과 TCP 메시지의 deviceNumber가 함께 정해진다.
    /// 해당 번호의 URL이 비어 있으면 템플릿의 apiUrl을 그대로 쓴다.
    /// </para>
    /// </summary>
    [Serializable]
    public class DeviceProfile
    {
        public const int MinDeviceNumber = 1;
        public const int MaxDeviceNumber = 3;

        // 이 PC의 부스 번호(1–3).
        public int deviceNumber = 1;
        // 부스 1·2·3의 운영 로그 URL(서버가 발급한 message= 까지 포함한 전체 URL). 순서대로 1번, 2번, 3번.
        public string[] apiUrls = Array.Empty<string>();

        /// <summary>
        /// 범위를 벗어난 번호를 1–3으로 맞춘 값.
        /// </summary>
        public int ClampedDeviceNumber => Math.Max(MinDeviceNumber, Math.Min(MaxDeviceNumber, deviceNumber));

        /// <summary>
        /// 이 부스의 운영 로그 URL. 지정되지 않았거나 비어 있으면 null.
        /// </summary>
        public string GetApiUrl()
        {
            int index = ClampedDeviceNumber - 1;
            if (apiUrls == null || index >= apiUrls.Length) return null;

            string url = apiUrls[index];
            return string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        }
    }
}
