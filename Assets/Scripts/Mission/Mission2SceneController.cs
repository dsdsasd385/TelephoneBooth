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
using R3;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VContainer;
using HuliacDev.App;
using HuliacDev.Utils;
using ZLogger;

namespace Mission
{
    /// <summary>
    /// 미션2(진실수호) 씬의 흐름을 관리하는 씬 매니저. 지금은 대사 화면 1·2만 있다.
    /// <para>
    /// 대사 화면: 대사 Sprite(진실-N 1, 2, …)를 차례로 보여 주며 대사마다 TTS를 한 번 재생하고, TTS가 끝나면 다음 대사로 바꾼다.
    /// 화면의 마지막 대사 TTS가 끝나면 다음 화면으로 넘어간다. 연출 대사(DialogueScreenView의 revealLineIndex)에 들어가면
    /// TTS와 동시에 TextImage 페이드·MoveImage 이동을 시작한다.
    /// TTS 파일이 아직 없어 경로가 빈 대사는 _noTtsLineSeconds 동안 보여 준다. 화면 2 뒤의 화면은 아직 없다.
    /// </para>
    /// <para>
    /// 수화기를 내려놓으면 MoveIdleEvent를 발행하고 0_Idle ① 대기로 간다(4.3). 홈 버튼은 0_Idle ② 안내로 간다.
    /// </para>
    /// </summary>
    public class Mission2SceneController : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private PageMediaPlayer _mediaPlayer;

        [Header("대사 화면")]
        // 진행 순서대로(화면 1, 2). Mission2Content.json의 screens와 같은 순서이며 각 뷰의 오브젝트가 화면 루트다.
        [SerializeField] private DialogueScreenView[] _screens = Array.Empty<DialogueScreenView>();
        // TTS가 없는 대사를 보여 주는 시간(초). TTS 파일을 받으면 쓰이지 않는다.
        [SerializeField, Min(0f)] private float _noTtsLineSeconds = 3f;

        [Header("공통")]
        [SerializeField] private Button _homeButton;

        #endregion

        #region Private Fields

        private ILogger<Mission2SceneController> _logger;
        private IReceiverInput _receiverInput;
        private IPublisher<MoveIdleEvent> _moveIdlePublisher;
        private ExperienceSession _session;

        private IDisposable _subscriptions;
        private CancellationTokenSource _flowCts;
        private Func<bool> _isTtsFinished;
        // 화면별·대사별 TTS. 경로가 비었거나 읽지 못한 대사는 null.
        private AudioClip[][] _lineClips = Array.Empty<AudioClip[]>();
        private bool _isInitialized;
        private bool _isLeaving;
        private int _currentIndex = -1;
        private int _visibleIndex = -1;

        private ProjectInputActions _inputActions;
        private Action<InputAction.CallbackContext> _onNextScreenKey;
        private Action<InputAction.CallbackContext> _onPreviousScreenKey;

        #endregion

        #region Dependency Injection & Unity Lifecycle

        /// <summary>
        /// VContainer 의존성 주입으로 로거, 수화기 입력, 대기 화면 복귀 이벤트 발행자, 체험 세션, 입력 액션을 할당함.
        /// </summary>
        [Inject]
        public void Construct(ILogger<Mission2SceneController> logger, IReceiverInput receiverInput,
            IPublisher<MoveIdleEvent> moveIdlePublisher, ExperienceSession session, ProjectInputActions inputActions)
        {
            _logger = logger;
            _receiverInput = receiverInput;
            _moveIdlePublisher = moveIdlePublisher;
            _session = session;
            _inputActions = inputActions;
        }

        /// <summary>
        /// 주입이 끝난 뒤 수화기·버튼 이벤트를 연결하고 콘텐츠 데이터를 읽어 첫 대사 화면을 시작함.
        /// </summary>
        private void Start()
        {
            if (_logger == null || _receiverInput == null || _moveIdlePublisher == null || _session == null)
            {
                Debug.LogError("[Mission2SceneController] Dependencies were not injected. Check that Mission2LifetimeScope registers Mission2SceneController and GameLifetimeScope registers IReceiverInput and ExperienceSession.");
            }

            _isTtsFinished = IsTtsFinished;

            // 0_Idle을 거치지 않고 이 씬에서 바로 플레이해도 세션에 현재 미션이 남게 한다.
            if (_session != null) _session.SetContent(ContentType.Truth);

            HideAllScreens();
            BindEvents();
            BindDebugInput();
            InitializeAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        /// <summary>
        /// 구독을 해제하고 진행 중인 흐름을 취소함.
        /// </summary>
        private void OnDestroy()
        {
            UnbindDebugInput();
            CancelFlow();
            _subscriptions?.Dispose();
            _subscriptions = null;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// 수화기·홈 버튼 이벤트를 구독함.
        /// </summary>
        private void BindEvents()
        {
            DisposableBuilder builder = new DisposableBuilder();

            if (_receiverInput != null)
            {
                // ReactiveProperty는 구독 즉시 현재 값을 한 번 보내므로 건너뛰고, 바뀌는 순간만 받는다.
                _receiverInput.IsLifted
                    .Skip(1)
                    .Subscribe(this, static (isLifted, self) => self.OnReceiverStateChanged(isLifted))
                    .AddTo(ref builder);
            }

            if (_homeButton)
            {
                _homeButton.OnClickAsObservable()
                    .Subscribe(this, static (_, self) => self.OnHomeClicked())
                    .AddTo(ref builder);
            }
            else if (_logger != null)
            {
                _logger.ZLogError($"[Mission2SceneController] Button is not assigned: {nameof(_homeButton)}");
            }

            _subscriptions = builder.Build();
        }

        /// <summary>
        /// Mission2Content.json을 읽어 대사 TTS를 미리 읽은 뒤 첫 대사 화면을 시작함.
        /// </summary>
        private async UniTaskVoid InitializeAsync(CancellationToken cancellationToken)
        {
            if (!_mediaPlayer)
            {
                if (_logger != null) _logger.ZLogError($"[Mission2SceneController] PageMediaPlayer is not assigned.");
                return;
            }

            try
            {
                Mission2ContentTable table = await JsonLoader.LoadAsync<Mission2ContentTable>(Constants.Files.Mission2Content, cancellationToken, _logger);
                await LoadLineClipsAsync(table.screens ?? Array.Empty<DialogueScreenEntry>(), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_isLeaving) return;

            _isInitialized = true;
            StartScreens(0);
        }

        /// <summary>
        /// 화면별 대사 TTS를 모두 읽음. JSON의 화면 이름·대사 개수가 씬과 다르면 경고를 남기고, 모자란 대사는 TTS 없이 동작한다.
        /// </summary>
        private async UniTask LoadLineClipsAsync(DialogueScreenEntry[] entries, CancellationToken cancellationToken)
        {
            if (entries.Length != _screens.Length && _logger != null)
            {
                _logger.ZLogWarning($"[Mission2SceneController] Screen count mismatch. json={entries.Length}, scene={_screens.Length}");
            }

            _lineClips = new AudioClip[_screens.Length][];
            for (int s = 0; s < _screens.Length; s++)
            {
                DialogueScreenView view = _screens[s];
                int lineCount = view ? view.LineCount : 0;
                _lineClips[s] = new AudioClip[lineCount];
                if (s >= entries.Length) continue;

                DialogueScreenEntry entry = entries[s];
                string[] paths = entry.tts ?? Array.Empty<string>();
                if (_logger != null)
                {
                    if (!view || view.name != entry.screen)
                    {
                        _logger.ZLogWarning($"[Mission2SceneController] Screen order mismatch at {s}. json={entry.screen}, scene={(view ? view.name : "null")}");
                    }

                    if (paths.Length != lineCount)
                    {
                        _logger.ZLogWarning($"[Mission2SceneController] Screen {entry.screen} line count mismatch. json={paths.Length}, scene={lineCount}");
                    }
                }

                int count = Mathf.Min(lineCount, paths.Length);
                for (int i = 0; i < count; i++)
                {
                    _lineClips[s][i] = await _mediaPlayer.LoadClipAsync(paths[i], cancellationToken);
                }
            }
        }

        #endregion

        #region Debug Input

        /// <summary>
        /// 에디터·개발 빌드에서만 디버그 키를 연결함. Ctrl+→/←는 다음·이전 대사 화면(TTS·수화기 없이 화면 확인).
        /// </summary>
        private void BindDebugInput()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_inputActions == null) return;

            _onNextScreenKey = OnNextScreenKey;
            _onPreviousScreenKey = OnPreviousScreenKey;
            _inputActions.Debug.NextScreen.performed += _onNextScreenKey;
            _inputActions.Debug.PreviousScreen.performed += _onPreviousScreenKey;
            _inputActions.Debug.Enable();
#endif
        }

        /// <summary>
        /// 디버그 입력 구독을 해제함.
        /// </summary>
        private void UnbindDebugInput()
        {
            if (_inputActions == null || _onNextScreenKey == null) return;

            _inputActions.Debug.NextScreen.performed -= _onNextScreenKey;
            _inputActions.Debug.PreviousScreen.performed -= _onPreviousScreenKey;
            _inputActions.Debug.Disable();
            _onNextScreenKey = null;
            _onPreviousScreenKey = null;
        }

        /// <summary>
        /// Ctrl+→를 누르면 다음 화면을 처음부터 진행함. 마지막 화면이면 첫 화면으로.
        /// </summary>
        private void OnNextScreenKey(InputAction.CallbackContext context)
        {
            if (!_isInitialized || _isLeaving || _screens.Length == 0) return;

            int next = (_currentIndex + 1) % _screens.Length;
            if (_logger != null) _logger.ZLogInformation($"[Mission2SceneController] Debug key: next screen {_currentIndex} -> {next}");
            StartScreens(next);
        }

        /// <summary>
        /// Ctrl+←를 누르면 이전 화면을 처음부터 진행함. 첫 화면이면 마지막 화면으로.
        /// </summary>
        private void OnPreviousScreenKey(InputAction.CallbackContext context)
        {
            if (!_isInitialized || _isLeaving || _screens.Length == 0) return;

            int previous = (_currentIndex - 1 + _screens.Length) % _screens.Length;
            if (_logger != null) _logger.ZLogInformation($"[Mission2SceneController] Debug key: previous screen {_currentIndex} -> {previous}");
            StartScreens(previous);
        }

        #endregion

        #region Dialogue Flow

        /// <summary>
        /// 진행 중인 흐름을 끊고 지정한 화면부터 대사 화면 진행을 다시 시작함.
        /// </summary>
        private void StartScreens(int startIndex)
        {
            CancelFlow();
            _flowCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            RunScreensAsync(startIndex, _flowCts).Forget();
        }

        /// <summary>
        /// startIndex 화면부터 대사 화면을 차례로 한 번씩 진행함. 화면의 마지막 대사 TTS가 끝나면 다음 화면으로 바꾼다.
        /// </summary>
        private async UniTaskVoid RunScreensAsync(int startIndex, CancellationTokenSource cts)
        {
            CancellationToken token = cts.Token;

            try
            {
                for (int s = startIndex; s < _screens.Length; s++)
                {
                    DialogueScreenView view = _screens[s];
                    if (!view)
                    {
                        if (_logger != null) _logger.ZLogError($"[Mission2SceneController] Screen view is not assigned at {s}.");
                        continue;
                    }

                    view.ResetState();
                    if (_visibleIndex >= 0 && _visibleIndex != s && _screens[_visibleIndex]) _screens[_visibleIndex].gameObject.SetActive(false);
                    view.gameObject.SetActive(true);
                    _visibleIndex = s;
                    _currentIndex = s;

                    await RunLinesAsync(view, _lineClips[s], token);
                }

                if (_logger != null) _logger.ZLogInformation($"[Mission2SceneController] Dialogue screens finished. Next screen is not implemented yet.");
            }
            catch (OperationCanceledException)
            {
                // 씬을 떠나거나 오브젝트 파괴로 인한 정상 취소.
            }
            finally
            {
                if (_flowCts == cts)
                {
                    _flowCts.Dispose();
                    _flowCts = null;
                }
            }
        }

        /// <summary>
        /// 화면의 대사를 차례로 한 번씩 보여 줌. 대사마다 TTS를 재생하고 끝나면 다음 대사로 바꾼다.
        /// 연출 대사에서는 TTS와 동시에 TextImage 페이드·MoveImage 이동을 시작하고, 화면을 마치기 전에 연출이 끝나기를 기다린다.
        /// </summary>
        private async UniTask RunLinesAsync(DialogueScreenView view, AudioClip[] clips, CancellationToken token)
        {
            UniTask reveal = UniTask.CompletedTask;

            for (int i = 0; i < clips.Length; i++)
            {
                view.ShowLine(i);

                AudioClip clip = clips[i];
                _mediaPlayer.PlayClip(clip);
                if (view.IsRevealLine(i)) reveal = view.PlayRevealAsync(token);

                if (clip)
                {
                    await UniTask.WaitUntil(_isTtsFinished, PlayerLoopTiming.Update, token);
                }
                else
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(_noTtsLineSeconds), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, token);
                }
            }

            await reveal;
        }

        /// <summary>
        /// 모든 대사 화면을 끔.
        /// </summary>
        private void HideAllScreens()
        {
            for (int i = 0; i < _screens.Length; i++)
            {
                if (_screens[i]) _screens[i].gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// 현재 TTS가 끝났는지 확인함. WaitUntil에 캐시된 대리자로 넘긴다.
        /// </summary>
        private bool IsTtsFinished()
        {
            return !_mediaPlayer || !_mediaPlayer.IsTtsPlaying;
        }

        /// <summary>
        /// 진행 중인 흐름(TTS 대기, 연출)을 취소함.
        /// </summary>
        private void CancelFlow()
        {
            if (_flowCts == null) return;

            // 연출(DOTween) 중 취소하면 finally가 Cancel() 안에서 바로 돌아 필드를 비우므로 먼저 떼어 낸다.
            CancellationTokenSource cts = _flowCts;
            _flowCts = null;
            cts.Cancel();
            cts.Dispose();
        }

        #endregion

        #region Receiver & Home

        /// <summary>
        /// 미션 씬에서 수화기를 내려놓으면 대기 화면 복귀 이벤트를 발행하고 0_Idle로 즉시 돌아감(작업계획서 4.3).
        /// </summary>
        private void OnReceiverStateChanged(bool isLifted)
        {
            if (isLifted || _isLeaving) return;

            if (_logger != null) _logger.ZLogInformation($"[Mission2SceneController] Receiver hung up. Returning to idle.");
            if (_moveIdlePublisher != null) _moveIdlePublisher.Publish(new MoveIdleEvent());
            LeaveScene(Constants.Scenes.Idle);
        }

        /// <summary>
        /// 홈 버튼으로 0_Idle ② 안내로 돌아감. 대기 복귀가 아니라 move_idle 로그는 보내지 않는다.
        /// </summary>
        private void OnHomeClicked()
        {
            if (_isLeaving) return;

            if (_logger != null) _logger.ZLogInformation($"[Mission2SceneController] Home button clicked. Returning to idle guide screen.");
            if (_session != null) _session.RequestGuideReturn();
            LeaveScene(Constants.Scenes.Idle);
        }

        /// <summary>
        /// 진행 중인 흐름과 재생을 멈추고 다른 씬을 불러옴. 한 번만 동작한다.
        /// </summary>
        private void LeaveScene(string sceneName)
        {
            if (_isLeaving) return;
            _isLeaving = true;

            CancelFlow();
            if (_mediaPlayer) _mediaPlayer.Stop();
            SceneManager.LoadScene(sceneName);
        }

        #endregion
    }
}
