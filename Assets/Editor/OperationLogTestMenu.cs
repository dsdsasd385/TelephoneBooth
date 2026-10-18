using System;
using System.IO;
using App;
using Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace EditorTools
{
    /// <summary>
    /// 운영 로그 API 테스트 전송 메뉴(Tools > 운영 로그 API). 템플릿 ApiRetryUtil은 에디터·Development Build에서
    /// 전송을 생략하므로, 서버 연결과 URL이 맞는지 에디터에서 확인할 때 쓴다.
    /// <para>
    /// Settings.json의 apiUrls에서 부스 URL을 골라 "test (editor)" 메시지를 GET으로 한 번 보낸다. 실제 운영 서버에
    /// 기록이 남으므로 보내기 전에 확인 창을 띄운다. 플레이 모드와 관계없이 동작한다.
    /// </para>
    /// </summary>
    public static class OperationLogTestMenu
    {
        private const string MenuRoot = "Tools/운영 로그 API/";
        private const string TestMessage = "test (editor)";
        private const int TimeoutSeconds = 10;
        // 응답 본문을 콘솔에 남길 때의 최대 길이. 서버가 HTML 오류 페이지를 돌려주면 길어지므로 자른다.
        private const int MaxResponseLogLength = 300;

        [MenuItem(MenuRoot + "현재 부스(deviceNumber)로 테스트 전송", false, 0)]
        private static void SendForCurrentDevice()
        {
            DeviceProfile profile = LoadProfile();
            if (profile == null) return;

            Send(profile, profile.ClampedDeviceNumber);
        }

        [MenuItem(MenuRoot + "부스 1로 테스트 전송", false, 20)]
        private static void SendForDevice1() => SendForDevice(1);

        [MenuItem(MenuRoot + "부스 2로 테스트 전송", false, 21)]
        private static void SendForDevice2() => SendForDevice(2);

        [MenuItem(MenuRoot + "부스 3으로 테스트 전송", false, 22)]
        private static void SendForDevice3() => SendForDevice(3);

        private static void SendForDevice(int deviceNumber)
        {
            DeviceProfile profile = LoadProfile();
            if (profile == null) return;

            Send(profile, deviceNumber);
        }

        /// <summary>
        /// StreamingAssets/Settings.json에서 부스 번호와 부스별 URL을 읽음. 실패하면 null.
        /// </summary>
        private static DeviceProfile LoadProfile()
        {
            string path = Path.Combine(Application.streamingAssetsPath, Constants.Files.AppSettings);
            if (!File.Exists(path))
            {
                Debug.LogError($"[OperationLogTest] Settings file not found: {path}");
                return null;
            }

            try
            {
                return JsonUtility.FromJson<DeviceProfile>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogError($"[OperationLogTest] Failed to parse {Constants.Files.AppSettings}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 지정한 부스의 URL에 테스트 메시지를 붙여 GET으로 보냄. 결과는 콘솔에 남긴다.
        /// URL의 쿼리 스트링(uid 등 발급 파라미터)은 로그에 남기지 않는다.
        /// </summary>
        private static void Send(DeviceProfile profile, int deviceNumber)
        {
            int index = deviceNumber - 1;
            string baseUrl = profile.apiUrls != null && index < profile.apiUrls.Length ? profile.apiUrls[index] : null;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                Debug.LogWarning($"[OperationLogTest] apiUrls[{index}] (device {deviceNumber}) is empty in {Constants.Files.AppSettings}.");
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "운영 로그 테스트 전송",
                $"부스 {deviceNumber}의 운영 로그 서버로 \"{TestMessage}\"를 보냅니다.\n실제 서버에 기록이 남습니다. 보낼까요?",
                "보내기", "취소");
            if (!confirmed) return;

            string url = baseUrl.Trim() + Uri.EscapeDataString(TestMessage);
            string safeUrl = StripQuery(url);

            UnityWebRequest request = UnityWebRequest.Get(url);
            request.timeout = TimeoutSeconds;
            Debug.Log($"[OperationLogTest] Device {deviceNumber}: sending \"{TestMessage}\" to {safeUrl}");

            // 에디터 모드에서도 완료 콜백은 호출되므로 UniTask 없이 처리한다.
            request.SendWebRequest().completed += _ =>
            {
                try
                {
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        Debug.Log($"[OperationLogTest] Device {deviceNumber}: success (HTTP {request.responseCode}). Response: {Truncate(request.downloadHandler.text)}");
                    }
                    else
                    {
                        Debug.LogError($"[OperationLogTest] Device {deviceNumber}: failed ({request.result}, HTTP {request.responseCode}): {request.error}. Response: {Truncate(request.downloadHandler?.text)}");
                    }
                }
                finally
                {
                    request.Dispose();
                }
            };
        }

        private static string StripQuery(string url)
        {
            int queryIndex = url.IndexOf('?');
            return queryIndex < 0 ? url : url.Substring(0, queryIndex) + "?[REDACTED]";
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            text = text.Trim();
            return text.Length <= MaxResponseLogLength ? text : text.Substring(0, MaxResponseLogLength) + "...";
        }
    }
}
