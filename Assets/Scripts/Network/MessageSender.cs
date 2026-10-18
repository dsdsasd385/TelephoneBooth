using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Core;
using Cysharp.Threading.Tasks;
using Data;
using HuliacDev.App;
using MessagePipe;
using Microsoft.Extensions.Logging;
using UnityEngine;
using VContainer.Unity;
using ZLogger;

namespace Network
{
    /// <summary>
    /// 관람객 메시지를 받는 프로그램으로 TCP 전송함(작업계획서 10장). 루트 스코프 싱글톤이라 씬이 바뀌어도 전송이 이어진다.
    /// <para>
    /// Send: 처음 보낼 때 messageSendAttempts번(기본 3, 한 번에 messageSendTimeoutMs, 사이 0.3초 → 약 5초 안) 시도하고,
    /// 모두 실패하면 persistentDataPath/MessageOutbox/{id}.json에 저장한다. 앱이 켜져 있는 동안 시작 5초 뒤와 그 뒤
    /// messageResendIntervalSeconds마다 저장된 메시지를 하나씩 다시 보내고 ACK를 받으면 파일을 지운다. 받는 쪽은 id로 중복을 거른다.
    /// </para>
    /// <para>
    /// 소켓 I/O는 스레드 풀에서 하고, 로그 이벤트 발행과 결과 반영은 메인 스레드에서 한다.
    /// 시도마다 ExternalApiCallEvent("tcp://IP:포트") / ExternalApiReturnEvent를 발행해 운영 로그로 남긴다(11장). 메시지 본문은 어떤 로그에도 남기지 않는다.
    /// </para>
    /// </summary>
    public sealed class MessageSender : IStartable, IDisposable
    {
        #region Constants

        private const string OutboxFolderName = "MessageOutbox";
        private const string OutboxExtension = ".json";
        // 읽을 수 없는 저장 파일은 이 확장자로 바꿔 다시 시도하지 않는다.
        private const string BrokenExtension = ".broken";
        private const int RetryDelayMs = 300;
        private const int FirstResendDelaySeconds = 5;

        #endregion

        #region Private Fields

        private readonly DeviceSettings _settings;
        private readonly DeviceProfile _deviceProfile;
        private readonly ILogger<MessageSender> _logger;
        private readonly IPublisher<ExternalApiCallEvent> _callPublisher;
        private readonly IPublisher<ExternalApiReturnEvent> _returnPublisher;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly string _endpoint;
        private string _outboxDirectory;

        #endregion

        #region Constructor & Lifecycle

        /// <summary>
        /// VContainer 생성자 주입으로 장치 설정, 부스 번호(Settings.json), 로거, 운영 로그 이벤트 발행자를 받음.
        /// </summary>
        public MessageSender(DeviceSettings settings, DeviceProfile deviceProfile, ILogger<MessageSender> logger,
            IPublisher<ExternalApiCallEvent> callPublisher, IPublisher<ExternalApiReturnEvent> returnPublisher)
        {
            _settings = settings ?? new DeviceSettings();
            _deviceProfile = deviceProfile ?? new DeviceProfile();
            _logger = logger;
            _callPublisher = callPublisher;
            _returnPublisher = returnPublisher;
            _endpoint = $"tcp://{_settings.messageServerHost}:{_settings.messageServerPort}";
        }

        /// <summary>
        /// 저장 폴더를 정하고 재전송 루프를 시작함. persistentDataPath는 메인 스레드에서만 읽을 수 있어 여기서 한 번 구한다.
        /// </summary>
        public void Start()
        {
            _outboxDirectory = Path.Combine(Application.persistentDataPath, OutboxFolderName);
            ResendLoopAsync(_cts.Token).Forget();
        }

        /// <summary>
        /// 진행 중인 전송과 재전송 루프를 멈춤. 아직 못 보낸 새 메시지는 저장되지 않을 수 있다(앱 종료 시점).
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 메시지 한 건을 보냄. 결과를 기다리지 않고 바로 돌아온다(관람객에게는 성공·실패와 상관없이 같은 완료 문구를 보여 준다).
        /// </summary>
        public void Send(string name, string text, ContentType content)
        {
            MessagePayload payload = new MessagePayload
            {
                id = Guid.NewGuid().ToString("N"),
                deviceNumber = _deviceProfile.ClampedDeviceNumber,
                content = ToContentName(content),
                name = name ?? string.Empty,
                text = text ?? string.Empty,
                sentAt = DateTimeOffset.Now.ToString("o")
            };

            if (_logger != null) _logger.ZLogInformation($"[MessageSender] Sending message {payload.id} (content {payload.content}, name {payload.name.Length} chars, text {payload.text.Length} chars) to {_endpoint}.");
            LogPayloadForDebug(payload);
            SendNewAsync(payload, _cts.Token).Forget();
        }

        /// <summary>
        /// 미션 종류를 전송용 이름으로 바꿈.
        /// </summary>
        public static string ToContentName(ContentType content)
        {
            return content switch
            {
                ContentType.Culture => "culture",
                ContentType.Truth => "truth",
                ContentType.Nation => "nation",
                _ => "none"
            };
        }

        #endregion

        #region Sending

        /// <summary>
        /// 에디터·개발 빌드에서만 보내는 JSON 전체를 Unity 콘솔에 남김(전송 확인용). 전시용 빌드에서는 본문을 어떤 로그에도 남기지 않는다(작업계획서 11장).
        /// ZLogger 파일 로그로 가지 않도록 Debug.Log를 쓴다.
        /// </summary>
        private static void LogPayloadForDebug(MessagePayload payload)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[MessageSender][Debug] Payload {payload.id}: {JsonUtility.ToJson(payload)}");
#endif
        }

        /// <summary>
        /// 처음 보낼 때 정해진 횟수만큼 시도하고, 모두 실패하면 저장함.
        /// </summary>
        private async UniTaskVoid SendNewAsync(MessagePayload payload, CancellationToken cancellationToken)
        {
            try
            {
                int attempts = Mathf.Max(1, _settings.messageSendAttempts);
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    if (await TrySendAsync(payload, cancellationToken)) return;

                    if (attempt < attempts)
                    {
                        await UniTask.Delay(RetryDelayMs, DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, cancellationToken);
                    }
                }

                await SaveToOutboxAsync(payload, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 앱 종료.
            }
        }

        /// <summary>
        /// 한 번 보내고 ACK를 받았는지 돌려줌. 앞뒤로 운영 로그 이벤트를 발행한다. 취소 외의 예외는 실패로 바꾼다.
        /// </summary>
        private async UniTask<bool> TrySendAsync(MessagePayload payload, CancellationToken cancellationToken)
        {
            _callPublisher?.Publish(new ExternalApiCallEvent(_endpoint));

            string failReason;
            try
            {
                string host = _settings.messageServerHost;
                int port = _settings.messageServerPort;
                int timeoutMs = Mathf.Max(100, _settings.messageSendTimeoutMs);

                // 소켓 연결·읽기는 스레드 풀에서 하고(Task.Run), 메인 스레드의 SynchronizationContext로 결과를 받아 돌아온다.
                // 스레드 풀 안에서 Task.AsUniTask()를 부르면 그 스레드에 SynchronizationContext가 없어 예외가 난다.
                bool isAcked = await Task.Run(
                    () => TcpMessageClient.SendAsync(host, port, payload, timeoutMs, cancellationToken),
                    cancellationToken).AsUniTask();

                failReason = isAcked ? null : "Response was not an ACK for this message";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                failReason = $"{e.GetType().Name}: {e.Message}";
            }

            bool isSuccess = failReason == null;
            _returnPublisher?.Publish(new ExternalApiReturnEvent(isSuccess, failReason));

            if (_logger != null)
            {
                if (isSuccess) _logger.ZLogInformation($"[MessageSender] Message {payload.id} acknowledged.");
                else _logger.ZLogWarning($"[MessageSender] Message {payload.id} send failed: {failReason}");
            }

            return isSuccess;
        }

        #endregion

        #region Outbox

        /// <summary>
        /// 메시지를 저장 폴더에 씀. 재전송 루프가 쓰다 만 파일을 읽지 않도록 임시 파일에 쓴 뒤 이름을 바꾼다.
        /// </summary>
        private async UniTask SaveToOutboxAsync(MessagePayload payload, CancellationToken cancellationToken)
        {
            string json = JsonUtility.ToJson(payload);
            string directory = _outboxDirectory;
            string path = Path.Combine(directory, payload.id + OutboxExtension);

            try
            {
                await UniTask.RunOnThreadPool(() =>
                {
                    Directory.CreateDirectory(directory);
                    string temp = path + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, path);
                }, cancellationToken: cancellationToken);

                if (_logger != null) _logger.ZLogWarning($"[MessageSender] Message {payload.id} saved to outbox for later resend.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                if (_logger != null) _logger.ZLogError($"[MessageSender] Failed to save message {payload.id} to outbox: {e.Message}");
            }
        }

        /// <summary>
        /// 시작 5초 뒤 한 번, 그 뒤 주기마다 저장된 메시지를 다시 보냄.
        /// </summary>
        private async UniTaskVoid ResendLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await UniTask.Delay(TimeSpan.FromSeconds(FirstResendDelaySeconds), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    await ResendOutboxAsync(cancellationToken);
                    int interval = Mathf.Max(5, _settings.messageResendIntervalSeconds);
                    await UniTask.Delay(TimeSpan.FromSeconds(interval), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 앱 종료.
            }
        }

        /// <summary>
        /// 저장된 메시지를 한 건씩 한 번 보내고, ACK를 받은 것은 지움. 첫 실패에서 이번 회차를 멈춘다(받는 쪽이 꺼져 있으면 나머지도 실패하므로).
        /// </summary>
        private async UniTask ResendOutboxAsync(CancellationToken cancellationToken)
        {
            string directory = _outboxDirectory;
            string[] files = await UniTask.RunOnThreadPool(
                () => Directory.Exists(directory) ? Directory.GetFiles(directory, "*" + OutboxExtension) : Array.Empty<string>(),
                cancellationToken: cancellationToken);
            if (files.Length == 0) return;

            if (_logger != null) _logger.ZLogInformation($"[MessageSender] Resending {files.Length} saved message(s).");

            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                MessagePayload payload = await ReadOutboxFileAsync(path, cancellationToken);
                if (payload == null) continue;

                if (!await TrySendAsync(payload, cancellationToken)) return;

                await UniTask.RunOnThreadPool(() => File.Delete(path), cancellationToken: cancellationToken);
            }
        }

        /// <summary>
        /// 저장 파일을 읽음. 읽을 수 없으면 .broken으로 바꿔 두고 null.
        /// </summary>
        private async UniTask<MessagePayload> ReadOutboxFileAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                string json = await UniTask.RunOnThreadPool(() => File.ReadAllText(path), cancellationToken: cancellationToken);
                MessagePayload payload = JsonUtility.FromJson<MessagePayload>(json);
                if (payload != null && !string.IsNullOrEmpty(payload.id)) return payload;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                if (_logger != null) _logger.ZLogWarning($"[MessageSender] Cannot read saved message {Path.GetFileName(path)}: {e.Message}");
            }

            try
            {
                File.Move(path, Path.ChangeExtension(path, BrokenExtension));
            }
            catch (IOException)
            {
                // 다음 회차에 다시 본다.
            }

            if (_logger != null) _logger.ZLogWarning($"[MessageSender] Saved message {Path.GetFileName(path)} is broken and will not be resent.");
            return null;
        }

        #endregion
    }
}
