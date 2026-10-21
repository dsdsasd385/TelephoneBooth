using System;
using System.Threading;
using App;
using Core;
using Cysharp.Threading.Tasks;
using Data;
using Hardware;
using Media;
using MessagePipe;
using Microsoft.Extensions.Logging;
using Network;
using R3;
using UnityEngine;
using UnityEngine.SceneManagement;
using VContainer;
using HuliacDev.App;
using HuliacDev.Utils;
using ZLogger;

namespace Message
{
    /// <summary>
    /// 9_Message(공용 종료 흐름, 작업계획서 9장) 씬의 흐름을 관리하는 씬 매니저.
    /// <para>
    /// 화면 순서: CheckScreen("수화기를 내려놓아 주세요") → MessageScreen(이름·다짐 입력) → 로딩 영상 → 0_Idle.
    /// CheckScreen은 수화기를 내려놓는 순간(걸린 상태가 되면) 버튼 없이 바로 MessageScreen으로 넘어간다.
    /// </para>
    /// <para>
    /// 설정에서 최대 글자 수를 읽어 입력 뷰를 묶는다. 보내기(다짐 칸 Enter)를 받으면 MessageSender로 TCP 전송을 시작하고
    /// (결과는 기다리지 않음, 로그에 본문은 남기지 않는다) MessageVideo에 로딩 영상을 한 번 재생한 뒤 0_Idle로 간다.
    /// 영상이 덮고 있는 동안은 다시 보낼 수 없다.
    /// </para>
    /// <para>
    /// 무입력 타이머(5.1)는 입력 화면이 나온 순간부터 보내기 전까지 센다. 타임아웃이면 쓰던 메시지는 버리고 0_Idle로 간다(GameManager).
    /// </para>
    /// <para>
    /// 아직 없는 것: 완료 문구, 건너뛰기.
    /// </para>
    /// </summary>
    public class MessageSceneController : MonoBehaviour
    {
        #region Serialized Fields

        // 수화기 확인 화면 루트(CheckScreen). 씬을 어떤 상태로 저장했든 시작 시 이 화면부터 보인다.
        [SerializeField] private GameObject _checkScreen;
        // 입력 화면 루트(MessageScreen).
        [SerializeField] private GameObject _inputScreen;
        [SerializeField] private MessageInputView _inputView;
        [SerializeField] private OverlayVideoPlayer _loadingVideo;

        #endregion

        #region Private Fields

        private ILogger<MessageSceneController> _logger;
        private IReceiverInput _receiverInput;
        private MessageSender _messageSender;
        private ExperienceSession _session;
        private IPublisher<MoveIdleEvent> _moveIdlePublisher;
        private InactivityTimerGate _inactivityGate;
        private IDisposable _subscriptions;
        private IDisposable _checkSubscription;
        private MessageSettings _settings = new MessageSettings();
        private bool _isInputShown;
        private bool _isSubmitted;
        private bool _isReturning;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// MessageLifetimeScope가 메서드 주입으로 의존성을 넣음.
        /// </summary>
        [Inject]
        public void Construct(ILogger<MessageSceneController> logger, IReceiverInput receiverInput, MessageSender messageSender, ExperienceSession session,
            IPublisher<MoveIdleEvent> moveIdlePublisher, InactivityTimerGate inactivityGate)
        {
            _logger = logger;
            _receiverInput = receiverInput;
            _messageSender = messageSender;
            _session = session;
            _moveIdlePublisher = moveIdlePublisher;
            _inactivityGate = inactivityGate;
        }

        private void Start()
        {
            if (_logger == null || _receiverInput == null)
            {
                Debug.LogError("[MessageSceneController] Dependencies were not injected. Check that MessageLifetimeScope registers MessageSceneController and GameLifetimeScope registers IReceiverInput.");
            }

            // 설정을 읽는 동안에도 영상 화면이 다른 화면을 덮지 않게 먼저 끈다.
            if (_loadingVideo) _loadingVideo.Stop();
            ShowCheckScreen();
            BindCheckScreen();

            InitializeAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        /// <summary>
        /// 구독을 해제함.
        /// </summary>
        private void OnDestroy()
        {
            _checkSubscription?.Dispose();
            _checkSubscription = null;
            _subscriptions?.Dispose();
            _subscriptions = null;
        }

        #endregion

        #region Check Screen

        /// <summary>
        /// 수화기 확인 화면만 보이게 함.
        /// </summary>
        private void ShowCheckScreen()
        {
            if (_inputScreen) _inputScreen.SetActive(false);

            if (_checkScreen)
            {
                _checkScreen.SetActive(true);
            }
            else
            {
                Debug.LogError("[MessageSceneController] Check screen is not assigned.");
            }
        }

        /// <summary>
        /// 수화기 상태를 구독함. 수화기가 걸리면 바로 입력 화면으로 넘어간다.
        /// </summary>
        private void BindCheckScreen()
        {
            if (_receiverInput == null) return;

            // ReactiveProperty는 구독 즉시 현재 값을 보내므로 이미 걸려 있으면 바로 넘어간다.
            _checkSubscription = _receiverInput.IsLifted
                .Subscribe(this, static (isLifted, self) =>
                {
                    if (!isLifted) self.ShowInputScreen();
                });
        }

        /// <summary>
        /// 수화기가 걸려 입력 화면으로 넘어감. 한 번만 넘어가고 이후 수화기 상태는 보지 않는다.
        /// </summary>
        private void ShowInputScreen()
        {
            if (_isInputShown) return;
            _isInputShown = true;

            if (_logger != null) _logger.ZLogInformation($"[MessageSceneController] Receiver is hung up. Moving to message input.");

            if (_checkScreen) _checkScreen.SetActive(false);
            if (_inputScreen) _inputScreen.SetActive(true);
            if (_inputView) _inputView.ResetInput();

            // 이 씬에는 TTS가 없다. 키보드 입력이 들어올 때마다 템플릿 타이머가 처음부터 다시 센다.
            if (_inactivityGate != null) _inactivityGate.OpenInput(null);
        }

        #endregion

        #region Initialization

        /// <summary>
        /// 메시지 설정을 읽어 입력 뷰를 묶음. 설정을 읽지 못하면 기본값으로 진행한다.
        /// </summary>
        private async UniTaskVoid InitializeAsync(CancellationToken cancellationToken)
        {
            if (!_inputView)
            {
                Debug.LogError("[MessageSceneController] MessageInputView is not assigned.");
                return;
            }

            if (!_loadingVideo)
            {
                Debug.LogError("[MessageSceneController] Loading video player is not assigned.");
            }

            try
            {
                MessageSettings settings = await JsonLoader.LoadAsync<MessageSettings>(Constants.Files.MessageSettings, cancellationToken, _logger);
                if (settings != null) _settings = settings;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                if (_logger != null) _logger.ZLogWarning($"[MessageSceneController] Failed to load {Constants.Files.MessageSettings}, using defaults: {e.Message}");
            }

            _inputView.Bind(_settings.nameMaxLength, _settings.maxLength);
            BindEvents();
        }

        /// <summary>
        /// 보내기와 로딩 영상 종료를 구독함.
        /// </summary>
        private void BindEvents()
        {
            DisposableBuilder builder = new DisposableBuilder();
            _inputView.Submitted
                .Subscribe(this, static (submission, self) => self.OnMessageSubmitted(submission))
                .AddTo(ref builder);
            if (_loadingVideo)
            {
                _loadingVideo.Completed
                    .Subscribe(this, static (_, self) => self.OnLoadingVideoCompleted())
                    .AddTo(ref builder);
            }
            _subscriptions = builder.Build();
        }

        #endregion

        #region Message

        /// <summary>
        /// 보내기를 받으면 로딩 영상을 재생함. TCP 전송(작업계획서 10장)은 아직 없어 길이만 남긴다.
        /// </summary>
        private void OnMessageSubmitted(MessageSubmission submission)
        {
            if (_isSubmitted) return;
            _isSubmitted = true;

            // 로딩 영상이 끝나면 스스로 0_Idle로 가므로 그동안은 세지 않는다.
            if (_inactivityGate != null) _inactivityGate.CloseInput();

            if (_logger != null) _logger.ZLogInformation($"[MessageSceneController] Message submitted (name {submission.Name.Length} chars, message {submission.Message.Length} chars).");

            // 전송은 루트 서비스가 로딩 영상과 상관없이 이어 간다(실패하면 저장 후 재전송). 화면 흐름은 결과를 기다리지 않는다.
            if (_messageSender != null)
            {
                _messageSender.Send(submission.Name, submission.Message, _session != null ? _session.Content : ContentType.None);
            }
            else if (_logger != null)
            {
                _logger.ZLogError($"[MessageSceneController] MessageSender is not injected. Message was not sent.");
            }

            if (_loadingVideo)
            {
                _loadingVideo.Play(_settings.loadingVideo, _settings.loadingVideoPlayCount);
            }
            else
            {
                OnLoadingVideoCompleted();
            }
        }

        /// <summary>
        /// 로딩 영상을 설정의 loadingVideoPlayCount번 다 재생하면 바로 0_Idle로 감. 완료 문구 화면이 생기면 그 화면을 거친다.
        /// </summary>
        private void OnLoadingVideoCompleted()
        {
            if (_isReturning) return;
            _isReturning = true;

            if (_logger != null) _logger.ZLogInformation($"[MessageSceneController] Loading video finished. Moving to {Constants.Scenes.Idle}.");

            // 체험을 끝까지 마치고 대기로 돌아가는 경로라 move_idle 로그를 보낸다.
            if (_moveIdlePublisher != null) _moveIdlePublisher.Publish(new MoveIdleEvent());
            SceneManager.LoadScene(Constants.Scenes.Idle);
        }

        #endregion
    }
}
