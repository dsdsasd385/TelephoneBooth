using System;
using System.Threading;
using App;
using Cysharp.Threading.Tasks;
using Data;
using Hardware;
using Media;
using Microsoft.Extensions.Logging;
using R3;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VContainer;
using HuliacDev.Utils;
using ZLogger;

namespace Message
{
    /// <summary>
    /// 9_Message(공용 종료 흐름, 작업계획서 9장) 씬의 흐름을 관리하는 씬 매니저.
    /// <para>
    /// 화면 순서: CheckScreen("수화기를 내려놓아 주세요") → MessageScreen(이름·다짐 입력) → 로딩 영상 → 0_Idle.
    /// CheckScreen의 Next 버튼은 수화기가 걸려 있을 때만 누를 수 있다(들려 있으면 interactable을 끈다).
    /// </para>
    /// <para>
    /// 설정에서 최대 글자 수를 읽어 입력 뷰를 묶는다. 보내기(다짐 칸 Enter)를 받으면 길이만 로그로 남기고
    /// (운영 로그에 본문은 남기지 않는다) MessageVideo에 로딩 영상을 한 번 재생한 뒤 0_Idle로 간다.
    /// 영상이 덮고 있는 동안은 다시 보낼 수 없다.
    /// </para>
    /// <para>
    /// 아직 없는 것: TCP 전송(로딩 영상 동안 보낼 예정), 완료 문구, 건너뛰기, 무입력 타임아웃.
    /// </para>
    /// </summary>
    public class MessageSceneController : MonoBehaviour
    {
        #region Serialized Fields

        // 수화기 확인 화면 루트(CheckScreen)와 다음 버튼. 씬을 어떤 상태로 저장했든 시작 시 이 화면부터 보인다.
        [SerializeField] private GameObject _checkScreen;
        [SerializeField] private Button _checkNextButton;
        // 입력 화면 루트(MessageScreen).
        [SerializeField] private GameObject _inputScreen;
        [SerializeField] private MessageInputView _inputView;
        [SerializeField] private OverlayVideoPlayer _loadingVideo;

        #endregion

        #region Private Fields

        private ILogger<MessageSceneController> _logger;
        private IReceiverInput _receiverInput;
        private IDisposable _subscriptions;
        private IDisposable _checkSubscriptions;
        private MessageSettings _settings = new MessageSettings();
        private bool _isSubmitted;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// MessageLifetimeScope가 메서드 주입으로 의존성을 넣음.
        /// </summary>
        [Inject]
        public void Construct(ILogger<MessageSceneController> logger, IReceiverInput receiverInput)
        {
            _logger = logger;
            _receiverInput = receiverInput;
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
            _checkSubscriptions?.Dispose();
            _checkSubscriptions = null;
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
        /// Next 버튼 클릭과 수화기 상태를 구독함. 수화기가 들려 있는 동안은 버튼을 누를 수 없다.
        /// </summary>
        private void BindCheckScreen()
        {
            if (!_checkNextButton)
            {
                Debug.LogError("[MessageSceneController] Check screen Next button is not assigned.");
                return;
            }

            DisposableBuilder builder = new DisposableBuilder();

            _checkNextButton.OnClickAsObservable()
                .Subscribe(this, static (_, self) => self.OnCheckNextClicked())
                .AddTo(ref builder);

            if (_receiverInput != null)
            {
                // ReactiveProperty는 구독 즉시 현재 값을 보내므로 시작 상태도 함께 반영된다.
                _receiverInput.IsLifted
                    .Subscribe(this, static (isLifted, self) => self._checkNextButton.interactable = !isLifted)
                    .AddTo(ref builder);
            }

            _checkSubscriptions = builder.Build();
        }

        /// <summary>
        /// 수화기가 걸려 있으면 입력 화면으로 넘어감. 버튼이 잠겨 있어도 한 번 더 확인한다.
        /// </summary>
        private void OnCheckNextClicked()
        {
            if (_receiverInput != null && _receiverInput.IsLifted.CurrentValue) return;

            if (_logger != null) _logger.ZLogInformation($"[MessageSceneController] Receiver is hung up. Moving to message input.");

            if (_checkScreen) _checkScreen.SetActive(false);
            if (_inputScreen) _inputScreen.SetActive(true);
            if (_inputView) _inputView.ResetInput();
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

            if (_logger != null) _logger.ZLogInformation($"[MessageSceneController] Message submitted (name {submission.Name.Length} chars, message {submission.Message.Length} chars). Sending is not implemented yet.");

            if (_loadingVideo)
            {
                _loadingVideo.Play(_settings.loadingVideo);
            }
            else
            {
                OnLoadingVideoCompleted();
            }
        }

        /// <summary>
        /// 로딩 영상이 끝나면 0_Idle로 감. 완료 문구 화면이 생기면 그 화면을 거친다.
        /// </summary>
        private void OnLoadingVideoCompleted()
        {
            if (_logger != null) _logger.ZLogInformation($"[MessageSceneController] Loading video finished. Moving to {Constants.Scenes.Idle}.");
            SceneManager.LoadScene(Constants.Scenes.Idle);
        }

        #endregion
    }
}
