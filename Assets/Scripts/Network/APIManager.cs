using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using App;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using Data;
using HuliacDev.Data;
using HuliacDev.Network;
using HuliacDev.Utils;
using UnityEngine;
using VContainer;
using ZLogger;

namespace Network
{
    /// <summary>
    /// 프로젝트 운영 로그 매니저. 템플릿 ApiManagerBase의 전송(시작·종료·대기 복귀·외부 호출 결과)을 그대로 쓰고,
    /// 보낼 URL만 이 PC의 부스 번호에 맞춘다(작업계획서 11장).
    /// <para>
    /// Settings.json의 deviceNumber(1–3)에 해당하는 apiUrls 항목이 있으면, 시작 로그를 보내기 전에 템플릿이 함께 쓰는
    /// Settings 객체의 apiUrl을 그 URL로 바꾼다. 이후 모든 운영 로그가 그 URL로 간다. 항목이 비어 있으면 기존 apiUrl을 쓴다.
    /// </para>
    /// <para>
    /// 부스 URL은 Settings.json 파일의 apiUrl에도 적어 둔다. 작업 스케줄러 가드 스크립트가 end_kill을 보낼 때 이 값만 읽기 때문이다.
    /// 그래서 설치할 때 PC마다 deviceNumber만 바꾸고 한 번 실행하면 된다.
    /// </para>
    /// <para>
    /// 템플릿은 에디터·Development Build에서 운영 로그를 보내지 않는다. 전송 확인이 필요할 때 _sendLogsForTest를 켜면
    /// 두 환경에서도 start·end·move_idle·move_idle_timeout을 실제 서버로 보낸다. 릴리스 빌드에서는 이 값과 관계없이 템플릿 경로를 쓴다.
    /// </para>
    /// </summary>
    public class APIManager : ApiManagerBase
    {
        // 템플릿 ApiManagerBase가 같은 날 재실행을 구분할 때 쓰는 PlayerPrefs 키. 테스트 전송도 같은 규칙을 따른다.
        private const string LastStartupLogDateKey = "ApiManagerBase_LastStartupLogDate";
        private const string MoveIdleMessage = "move_idle";
        private const string MoveIdleTimeoutMessage = "move_idle_timeout";
        // 에디터에서 플레이를 멈출 때 종료 로그를 기다리는 최대 시간(초). 이 시간 동안 에디터가 멈춘다.
        private const int EditorExitLogTimeoutSeconds = 5;

        // Settings.json의 "apiUrl": "..." 항목. 그룹 2가 따옴표 안 값이다. "apiUrls"는 키 뒤에 s가 붙어 걸리지 않는다.
        private static readonly Regex ApiUrlEntryPattern = new Regex("(\"apiUrl\"\\s*:\\s*\")((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        [Header("Test (editor / development build only)")]
        [Tooltip("켜면 에디터·Development Build에서도 운영 로그를 실제 서버로 보냄. 운영 서버에 기록이 남으므로 확인 후 끈다. 릴리스 빌드에는 영향 없음.")]
        [SerializeField] private bool _sendLogsForTest;

        private DeviceProfile _deviceProfile;
        // 부스 URL을 적용한 뒤의 apiUrl. 에디터 종료 시 동기 전송에 쓴다.
        private string _resolvedApiUrl;
        private bool _isTestExitLogSent;

        /// <summary>
        /// 테스트 전송이 켜져 있는지. 릴리스 빌드에서는 항상 false라 템플릿 정책을 그대로 따른다.
        /// </summary>
        private bool IsTestSendEnabled
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return _sendLogsForTest;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// 부스 번호·부스별 URL을 주입받음. 템플릿의 Construct와 함께 메서드 주입된다.
        /// </summary>
        [Inject]
        public void ConstructDeviceProfile(DeviceProfile deviceProfile)
        {
            _deviceProfile = deviceProfile;
        }

        /// <summary>
        /// 부스 URL을 먼저 적용한 뒤 템플릿의 시작 로그를 보냄. 테스트 전송이 켜져 있으면 직접 보낸다.
        /// </summary>
        protected override async UniTaskVoid SendStartupLogAsync(CancellationToken cancellationToken)
        {
            await ApplyDeviceApiUrlAsync(cancellationToken);

            if (IsTestSendEnabled)
            {
                SendStartupLogForTestAsync(cancellationToken).Forget();
                return;
            }

            base.SendStartupLogAsync(cancellationToken).Forget();
        }

        /// <summary>
        /// 종료 로그. 테스트 전송이 켜져 있으면 템플릿과 같은 메시지·재시도 정책으로 직접 보낸다.
        /// Development Build의 Alt+F4·GameCloser 종료가 이 경로를 탄다.
        /// </summary>
        protected override UniTask SendExitLogAsync()
        {
            if (!IsTestSendEnabled) return base.SendExitLogAsync();

            return SendExitLogForTestAsync();
        }

        protected override void OnMoveIdle()
        {
            if (!IsTestSendEnabled)
            {
                base.OnMoveIdle();
                return;
            }

            SendTestLogAsync(MoveIdleMessage, ApiRetryUtil.DefaultMaxAttemptCount, ApiRetryUtil.DefaultRetryDelaySeconds,
                this.GetCancellationTokenOnDestroy()).Forget();
        }

        protected override void OnInactivityTimeout()
        {
            if (!IsTestSendEnabled)
            {
                base.OnInactivityTimeout();
                return;
            }

            SendTestLogAsync(MoveIdleTimeoutMessage, ApiRetryUtil.DefaultMaxAttemptCount, ApiRetryUtil.DefaultRetryDelaySeconds,
                this.GetCancellationTokenOnDestroy()).Forget();
        }

#if UNITY_EDITOR
        /// <summary>
        /// 에디터에서 플레이를 멈추면 wantsToQuit이 오지 않아 종료 로그가 나가지 않는다. 테스트 전송이 켜져 있으면
        /// 여기서 동기로 한 번 보낸다(최대 EditorExitLogTimeoutSeconds 동안 에디터가 멈춤).
        /// </summary>
        private void OnApplicationQuit()
        {
            if (!IsTestSendEnabled || _isTestExitLogSent || string.IsNullOrEmpty(_resolvedApiUrl)) return;

            _isTestExitLogSent = true;
            string message = ZString.Concat("end (by ", QuitReason.Current, ")");
            string url = ZString.Concat(_resolvedApiUrl, Uri.EscapeDataString(message));

            try
            {
                using System.Net.Http.HttpClient client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(EditorExitLogTimeoutSeconds) };
                // Unity 동기화 컨텍스트에 이어 붙지 않도록 스레드 풀에서 보내고 기다린다.
                System.Net.Http.HttpResponseMessage response = System.Threading.Tasks.Task.Run(() => client.GetAsync(url)).GetAwaiter().GetResult();
                Debug.Log($"[APIManager] Test send ({message}): HTTP {(int)response.StatusCode}");
                response.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogError($"[APIManager] Test send ({message}) failed: {e.Message}");
            }
        }
#endif

        /// <summary>
        /// 템플릿이 공유하는 Settings 객체의 apiUrl을 이 부스의 URL로 바꿈. URL 본문(발급 파라미터 포함)은 로그에 남기지 않는다.
        /// </summary>
        private async UniTask ApplyDeviceApiUrlAsync(CancellationToken cancellationToken)
        {
            if (SettingsProvider == null || _deviceProfile == null) return;

            try
            {
                Settings settings = await SettingsProvider.GetAsync(cancellationToken);
                if (settings == null) return;

                int deviceNumber = _deviceProfile.ClampedDeviceNumber;
                string url = _deviceProfile.GetApiUrl();
                if (url != null)
                {
                    settings.apiUrl = url;
                    _resolvedApiUrl = url;
                    if (Logger != null) Logger.ZLogInformation($"[APIManager] Device {deviceNumber}: using apiUrls[{deviceNumber - 1}] for operation logs.");
                    SyncApiUrlToSettingsFile(url);
                    return;
                }

                _resolvedApiUrl = settings.apiUrl;
                if (Logger != null)
                {
                    string fallback = string.IsNullOrEmpty(settings.apiUrl) ? "apiUrl is empty, logs are skipped" : "using apiUrl";
                    Logger.ZLogInformation($"[APIManager] Device {deviceNumber}: apiUrls[{deviceNumber - 1}] is not set; {fallback}.");
                }
            }
            catch (OperationCanceledException)
            {
                // 오브젝트 파괴로 인한 정상 취소.
            }
        }

        /// <summary>
        /// Settings.json 파일의 apiUrl 값을 이 부스의 URL로 맞춤. 작업 스케줄러 가드 스크립트가 end_kill을 보낼 때
        /// apiUrls를 모르고 이 파일의 apiUrl만 읽기 때문이다(템플릿 Tools~/ShutdownScheduleEditor).
        /// <para>
        /// apiUrl 값 한 줄만 바꾸고 나머지 내용·서식은 그대로 둔다. 값이 이미 같으면 쓰지 않는다. 임시 파일에 먼저 쓴 뒤 덮어써서
        /// 쓰는 도중 꺼져도 원본이 깨지지 않게 한다. 실패해도 운영 로그 전송에는 영향이 없도록 경고만 남긴다.
        /// 에디터에서는 저장소의 Settings.json이 바뀌지 않도록 하지 않는다.
        /// </para>
        /// </summary>
        private void SyncApiUrlToSettingsFile(string url)
        {
#if UNITY_EDITOR
            if (Logger != null) Logger.ZLogInformation($"[APIManager] Editor: skipping apiUrl sync to {Constants.Files.AppSettings}.");
#else
            string path = Path.Combine(Application.streamingAssetsPath, Constants.Files.AppSettings);
            string tempPath = path + ".tmp";

            try
            {
                string json = File.ReadAllText(path);
                Match match = ApiUrlEntryPattern.Match(json);
                if (!match.Success)
                {
                    if (Logger != null) Logger.ZLogWarning($"[APIManager] \"apiUrl\" entry not found in {Constants.Files.AppSettings}; end_kill log will not be sent.");
                    return;
                }

                string escapedUrl = url.Replace("\\", "\\\\").Replace("\"", "\\\"");
                if (match.Groups[2].Value == escapedUrl) return;

                string updated = ZString.Concat(json.Substring(0, match.Groups[2].Index), escapedUrl, json.Substring(match.Groups[2].Index + match.Groups[2].Length));
                File.WriteAllText(tempPath, updated, new UTF8Encoding(false));
                File.Copy(tempPath, path, true);
                File.Delete(tempPath);

                if (Logger != null) Logger.ZLogInformation($"[APIManager] Synced apiUrl in {Constants.Files.AppSettings} to device {_deviceProfile.ClampedDeviceNumber} URL (for end_kill log).");
            }
            catch (Exception e)
            {
                if (Logger != null) Logger.ZLogWarning($"[APIManager] Failed to sync apiUrl in {Constants.Files.AppSettings}: {e.Message}");
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch (Exception)
                {
                    // 임시 파일 정리 실패는 무시한다. 다음 실행 때 덮어쓴다.
                }
            }
#endif
        }

        /// <summary>
        /// 테스트 전송용 시작 로그. 템플릿과 같이 오늘 이미 보냈으면 "start (restart)"를 보낸다.
        /// </summary>
        private async UniTaskVoid SendStartupLogForTestAsync(CancellationToken cancellationToken)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            bool alreadyLoggedToday = PlayerPrefs.GetString(LastStartupLogDateKey, string.Empty) == today;
            string message = alreadyLoggedToday ? "start (restart)" : "start";

            bool success = await SendTestLogAsync(message, ApiRetryUtil.DefaultMaxAttemptCount, ApiRetryUtil.DefaultRetryDelaySeconds, cancellationToken);
            if (!success) return;

            PlayerPrefs.SetString(LastStartupLogDateKey, today);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// 테스트 전송용 종료 로그. 템플릿과 같은 3회·1초 간격·전체 상한으로 보낸다.
        /// </summary>
        private async UniTask SendExitLogForTestAsync()
        {
            _isTestExitLogSent = true;
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(ExitLogTimeoutSeconds));
            string message = ZString.Concat("end (by ", QuitReason.Current, ")");
            await SendTestLogAsync(message, ExitLogMaxAttemptCount, ExitLogRetryDelaySeconds, cts.Token);
        }

        /// <summary>
        /// apiUrl에 메시지를 붙여 실제로 GET 전송함. GetTextWithRetryAsync는 에디터·Development Build에서도 보낸다.
        /// </summary>
        private async UniTask<bool> SendTestLogAsync(string message, int maxAttemptCount, float retryDelaySeconds, CancellationToken cancellationToken)
        {
            if (SettingsProvider == null) return false;

            try
            {
                Settings settings = await SettingsProvider.GetAsync(cancellationToken);
                if (settings == null || string.IsNullOrEmpty(settings.apiUrl))
                {
                    if (Logger != null) Logger.ZLogInformation($"[APIManager] apiUrl is not set; skipping test send: {message}");
                    return false;
                }

                string url = ZString.Concat(settings.apiUrl, Uri.EscapeDataString(message));
                (bool isSuccess, string _) = await ApiRetryUtil.GetTextWithRetryAsync(
                    url, ZString.Concat("[test] ", message), Logger, cancellationToken, maxAttemptCount, retryDelaySeconds);
                return isSuccess;
            }
            catch (OperationCanceledException)
            {
                if (Logger != null) Logger.ZLogWarning($"[APIManager] Test send canceled or timed out: {message}");
                return false;
            }
            catch (Exception e)
            {
                if (Logger != null) Logger.ZLogError($"[APIManager] Exception during test send ({message}): {e.Message}");
                return false;
            }
        }
    }
}
