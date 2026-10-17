using System;
using System.Threading;
using App;
using Core;
using Cysharp.Threading.Tasks;
using Data;
using DG.Tweening;
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
    /// 미션3(민족수호) 씬의 흐름을 관리하는 씬 매니저. Guide1 → Guide2 → SelectScreen(암호 풀이) 순서로 진행한다.
    /// <para>
    /// Guide1: 안내 TTS가 끝나면 Guide2로. Guide2: TTS와 동시에 Title1MapImage의 아웃라인 5개가 차례로 나타난다(버튼 입력 없음).
    /// TTS가 끝나면 TitleImage1을 끄고 SelectScreen(TitleImage2)을 BG 없이 Guide2 위에 겹쳐 켠 뒤, TTS와 동시에 Guide2 지도(Title1MapImage)를
    /// Title2MapImage 자리·크기로 옮긴다(CipherMapView.MorphTo: 지도·아웃라인·버튼이 각각 Title2MapImage의 같은 요소 값으로, Scale은 1 유지). 도착하면 Title2MapImage를 보여 주고(_mapSwapFadeSeconds 크로스페이드) Guide2를 끈다.
    /// 지도가 도착하기 전에는 SelectScreen 지도가 보이지 않아 중간에 지도 Sprite가 바뀌는 순간이 보이지 않는다(두 BG는 같은 검정 단색).
    /// 이어서 PasswordImage 페이드 → 암호 타이핑 → PawwordTable 페이드. 연출과 TTS가 모두 끝나야 지도 버튼을 누를 수 있다.
    /// </para>
    /// <para>
    /// 지도 버튼을 누르면 선택 표시를 켜고 바로 판정한다. 정답(_answerButton, 다리 밑)이면 정답 효과음을 재생한다(그 뒤 화면은 아직 없다).
    /// 오답이면 FailScreen(오답 문구를 TitleBoxView로 가로로 흔듦)과 오답 TTS를 보여 준 뒤 SelectScreen으로 돌아와 다시 고르게 한다.
    /// </para>
    /// <para>
    /// 경로가 빈 TTS는 _noTtsLineSeconds 동안 보여 준다.
    /// 수화기를 내려놓으면 MoveIdleEvent를 발행하고 0_Idle ① 대기로 간다(4.3). 홈 버튼은 0_Idle ② 안내로 간다.
    /// </para>
    /// </summary>
    public class Mission3SceneController : MonoBehaviour
    {
        #region Constants

        private const int GuidePageIndex = 0;
        private const int MapPageIndex = 1;
        private const int SelectPageIndex = 2;
        private const int PageCount = 3;

        #endregion

        #region Serialized Fields

        [SerializeField] private PageMediaPlayer _mediaPlayer;

        [Header("화면")]
        [SerializeField] private GameObject _guideScreen;
        [SerializeField] private GameObject _mapScreen;
        [SerializeField] private GameObject _selectScreen;
        [SerializeField] private GameObject _failScreen;
        // FailScreen의 오답 문구(FailTitleImage). 오답일 때 Mission2처럼 가로로 흔든다.
        [SerializeField] private TitleBoxView _failTitleBox;

        [Header("Guide2")]
        // TitleImage1. SelectScreen으로 넘어갈 때 끈다.
        [SerializeField] private GameObject _mapTitle;
        // 연출용 지도(Title1MapImage). 버튼 입력을 받지 않는다.
        [SerializeField] private CipherMapView _introMap;

        [Header("SelectScreen")]
        // 지도가 옮겨지는 동안 아래의 Guide2가 보이도록 끄는 SelectScreen 배경.
        [SerializeField] private GameObject _selectBackground;
        [SerializeField] private CipherMapView _selectMap;
        [SerializeField] private CanvasGroup _passwordGroup;
        [SerializeField] private PasswordTextView _passwordText;
        [SerializeField] private CanvasGroup _tableGroup;
        // 정답 장소 버튼(SelectScreen 지도의 3_RiverBtn, 다리 밑).
        [SerializeField] private Button _answerButton;

        [Header("연출")]
        [SerializeField, Min(0f)] private float _mapMoveSeconds = 1f;
        [SerializeField] private Ease _mapMoveEase = Ease.InOutQuad;
        // Guide2 지도가 도착한 뒤 SelectScreen 지도로 바꾸는 크로스페이드 시간(초). 0이면 바로 바꾼다.
        [SerializeField, Min(0f)] private float _mapSwapFadeSeconds = 0.3f;
        [SerializeField, Min(0f)] private float _fadeSeconds = 0.5f;

        [Header("시간")]
        // TTS가 없는 화면을 보여 주는 시간(초). TTS 파일이 있으면 쓰이지 않는다.
        [SerializeField, Min(0f)] private float _noTtsLineSeconds = 3f;
        // TTS가 끝난 뒤 다음 화면으로 넘어가기 전에 기다리는 시간(초).
        [SerializeField, Min(0f)] private float _advanceDelaySeconds = 1f;

        [Header("공통")]
        [SerializeField] private Button _homeButton;

        #endregion

        #region Private Fields

        private ILogger<Mission3SceneController> _logger;
        private IReceiverInput _receiverInput;
        private IPublisher<MoveIdleEvent> _moveIdlePublisher;
        private ExperienceSession _session;

        private IDisposable _subscriptions;
        private CancellationTokenSource _flowCts;
        private Func<bool> _isTtsFinished;
        private AudioClip _guideClip;
        private AudioClip _mapClip;
        private AudioClip _passwordClip;
        private AudioClip _failClip;
        private AudioClip _correctClip;
        private Sequence _introSequence;
        private bool _isInitialized;
        private bool _isLeaving;
        private bool _isSolved;
        private int _currentIndex = -1;

        private ProjectInputActions _inputActions;
        private Action<InputAction.CallbackContext> _onNextScreenKey;
        private Action<InputAction.CallbackContext> _onPreviousScreenKey;

        #endregion

        #region Dependency Injection & Unity Lifecycle

        /// <summary>
        /// VContainer 의존성 주입으로 로거, 수화기 입력, 대기 화면 복귀 이벤트 발행자, 체험 세션, 입력 액션을 할당함.
        /// </summary>
        [Inject]
        public void Construct(ILogger<Mission3SceneController> logger, IReceiverInput receiverInput,
            IPublisher<MoveIdleEvent> moveIdlePublisher, ExperienceSession session, ProjectInputActions inputActions)
        {
            _logger = logger;
            _receiverInput = receiverInput;
            _moveIdlePublisher = moveIdlePublisher;
            _session = session;
            _inputActions = inputActions;
        }

        /// <summary>
        /// 주입이 끝난 뒤 지도 위치를 기억하고 이벤트를 연결한 뒤 콘텐츠 데이터를 읽어 첫 화면을 시작함.
        /// </summary>
        private void Start()
        {
            if (_logger == null || _receiverInput == null || _moveIdlePublisher == null || _session == null)
            {
                Debug.LogError("[Mission3SceneController] Dependencies were not injected. Check that Mission3LifetimeScope registers Mission3SceneController and GameLifetimeScope registers IReceiverInput and ExperienceSession.");
            }

            _isTtsFinished = IsTtsFinished;

            // 0_Idle을 거치지 않고 이 씬에서 바로 플레이해도 세션에 현재 미션이 남게 한다.
            if (_session != null) _session.SetContent(ContentType.Nation);

            HideAllPages();
            ShowGuidePlaceholder();
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
            KillIntroSequence();
            _subscriptions?.Dispose();
            _subscriptions = null;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// 수화기·홈 버튼·지도 버튼 이벤트를 구독함.
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
                _logger.ZLogError($"[Mission3SceneController] Button is not assigned: {nameof(_homeButton)}");
            }

            if (_selectMap)
            {
                _selectMap.Bind();
                _selectMap.Clicked
                    .Subscribe(this, static (button, self) => self.OnPlaceClicked(button))
                    .AddTo(ref builder);
            }
            else if (_logger != null)
            {
                _logger.ZLogError($"[Mission3SceneController] CipherMapView is not assigned: {nameof(_selectMap)}");
            }

            _subscriptions = builder.Build();
        }

        /// <summary>
        /// Mission3Content.json을 읽어 TTS·효과음을 미리 읽은 뒤 첫 화면을 시작함.
        /// </summary>
        private async UniTaskVoid InitializeAsync(CancellationToken cancellationToken)
        {
            if (!_mediaPlayer)
            {
                if (_logger != null) _logger.ZLogError($"[Mission3SceneController] PageMediaPlayer is not assigned.");
                return;
            }

            try
            {
                Mission3ContentTable table = await JsonLoader.LoadAsync<Mission3ContentTable>(Constants.Files.Mission3Content, cancellationToken, _logger);

                // 파일을 하나씩 기다리면 첫 화면이 늦어지므로 모두 동시에 읽는다.
                (_guideClip, _mapClip, _passwordClip, _failClip, _correctClip) = await UniTask.WhenAll(
                    _mediaPlayer.LoadClipAsync(table.guideTts, cancellationToken),
                    _mediaPlayer.LoadClipAsync(table.mapTts, cancellationToken),
                    _mediaPlayer.LoadClipAsync(table.passwordTts, cancellationToken),
                    _mediaPlayer.LoadClipAsync(table.failTts, cancellationToken),
                    _mediaPlayer.LoadClipAsync(table.correctSfx, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_isLeaving) return;

            _isInitialized = true;
            ShowPage(GuidePageIndex);
        }

        #endregion

        #region Debug Input

        /// <summary>
        /// 에디터·개발 빌드에서만 디버그 키를 연결함. Ctrl+→/←는 다음·이전 화면(TTS·정답·수화기 없이 화면 확인).
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
            if (!_isInitialized || _isLeaving) return;

            int next = (_currentIndex + 1) % PageCount;
            if (_logger != null) _logger.ZLogInformation($"[Mission3SceneController] Debug key: next screen {_currentIndex} -> {next}");
            ShowPage(next);
        }

        /// <summary>
        /// Ctrl+←를 누르면 이전 화면을 처음부터 진행함. 첫 화면이면 마지막 화면으로.
        /// </summary>
        private void OnPreviousScreenKey(InputAction.CallbackContext context)
        {
            if (!_isInitialized || _isLeaving) return;

            int previous = (_currentIndex - 1 + PageCount) % PageCount;
            if (_logger != null) _logger.ZLogInformation($"[Mission3SceneController] Debug key: previous screen {_currentIndex} -> {previous}");
            ShowPage(previous);
        }

        #endregion

        #region Page Flow

        /// <summary>
        /// 진행 중인 흐름을 끊고 지정한 화면부터 다시 시작함. SelectScreen을 바로 열면 연출 없이 고를 수 있는 상태로 연다(디버그 키용).
        /// </summary>
        private void ShowPage(int index)
        {
            CancelFlow();
            KillIntroSequence();
            if (_mediaPlayer) _mediaPlayer.PlayClip(null);

            _flowCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            RunFlowAsync(index, _flowCts).Forget();
        }

        /// <summary>
        /// startIndex 화면부터 차례로 진행함.
        /// </summary>
        private async UniTaskVoid RunFlowAsync(int startIndex, CancellationTokenSource cts)
        {
            CancellationToken token = cts.Token;

            try
            {
                if (startIndex <= GuidePageIndex)
                {
                    await RunGuideAsync(token);
                }

                if (startIndex <= MapPageIndex)
                {
                    await RunMapAsync(token);
                    await RunPasswordIntroAsync(token);
                }
                else
                {
                    ShowSelectReady();
                }
            }
            catch (OperationCanceledException)
            {
                // 화면 전환이나 오브젝트 파괴로 인한 정상 취소.
            }
            finally
            {
                ReleaseFlowCts(cts);
            }
        }

        /// <summary>
        /// Guide1: 안내 TTS가 끝날 때까지 보여 줌.
        /// </summary>
        private async UniTask RunGuideAsync(CancellationToken token)
        {
            ShowRoot(_guideScreen);
            _currentIndex = GuidePageIndex;

            await PlayTtsAndWaitAsync(_guideClip, token);
        }

        /// <summary>
        /// Guide2: TTS와 동시에 지도 아웃라인을 차례로 띄우고, 둘 다 끝날 때까지 기다림.
        /// </summary>
        private async UniTask RunMapAsync(CancellationToken token)
        {
            RestoreTransitionState();
            if (_introMap) _introMap.ResetState(false);
            ShowRoot(_mapScreen);
            _currentIndex = MapPageIndex;

            _mediaPlayer.PlayClip(_mapClip);
            UniTask outlines = _introMap ? _introMap.PlayOutlinesAsync(token) : UniTask.CompletedTask;

            await WaitTtsAsync(_mapClip, token);
            await outlines;
            await DelayAdvanceAsync(_mapClip, token);
        }

        /// <summary>
        /// SelectScreen 등장: TitleImage1을 끄고 SelectScreen(TitleImage2)을 BG 없이 Guide2 위에 켠 뒤, TTS와 동시에 Guide2 지도를
        /// SelectScreen 지도 자리로 옮김. 도착하면 SelectScreen 지도로 바꾸고 Guide2를 끈 다음 PasswordImage 페이드 → 암호 타이핑 →
        /// PawwordTable 페이드. TTS와 연출이 모두 끝나면 지도 입력을 연다.
        /// </summary>
        private async UniTask RunPasswordIntroAsync(CancellationToken token)
        {
            ResetSelectState(true);
            if (_mapTitle) _mapTitle.SetActive(false);
            if (_selectBackground) _selectBackground.SetActive(false);
            // Guide2는 켜 둔 채 SelectScreen을 위에 겹친다. 지도가 도착하면 ShowRoot로 Guide2를 끈다.
            if (_selectScreen) _selectScreen.SetActive(true);
            _currentIndex = SelectPageIndex;

            _mediaPlayer.PlayClip(_passwordClip);
            UniTask tts = WaitTtsAsync(_passwordClip, token);

            if (_introMap && _selectMap)
            {
                await PlayIntroSequenceAsync(DOTween.Sequence().Append(_introMap.MorphTo(_selectMap, _mapMoveSeconds, _mapMoveEase)), token);
            }

            if (_selectMap) await PlayIntroSequenceAsync(DOTween.Sequence().Append(_selectMap.Group.DOFade(1f, _mapSwapFadeSeconds)), token);

            ShowRoot(_selectScreen);
            RestoreTransitionState();

            if (_passwordGroup) await PlayIntroSequenceAsync(DOTween.Sequence().Append(_passwordGroup.DOFade(1f, _fadeSeconds)), token);
            if (_passwordText) await _passwordText.PlayTypingAsync(token);
            if (_tableGroup) await PlayIntroSequenceAsync(DOTween.Sequence().Append(_tableGroup.DOFade(1f, _fadeSeconds)), token);

            await tts;

            if (_selectMap) _selectMap.SetInputEnabled(true);
        }

        /// <summary>
        /// 등장 연출 한 단계를 재생하고 끝날 때까지 기다림. 취소하면 트윈을 멈춘다.
        /// </summary>
        private async UniTask PlayIntroSequenceAsync(Sequence sequence, CancellationToken token)
        {
            _introSequence = sequence.SetUpdate(true);
            await sequence.ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, token);
            _introSequence = null;
        }

        /// <summary>
        /// 지도 이동 연출이 바꾼 것을 되돌림. TitleImage1·SelectScreen BG 켬, Guide2 지도 제자리·원래 크기, SelectScreen 지도 보임.
        /// 연출 도중 화면을 바꿔도(디버그 키) 다음 진입이 깨지지 않게 한다.
        /// </summary>
        private void RestoreTransitionState()
        {
            if (_mapTitle) _mapTitle.SetActive(true);
            if (_selectBackground) _selectBackground.SetActive(true);

            if (_introMap) _introMap.RestoreLayout();

            if (_selectMap) _selectMap.Group.alpha = 1f;
        }

        /// <summary>
        /// SelectScreen을 연출 없이 고를 수 있는 상태로 엶(디버그 키용).
        /// </summary>
        private void ShowSelectReady()
        {
            ResetSelectState(false);
            ShowRoot(_selectScreen);
            _currentIndex = SelectPageIndex;

            if (_selectMap) _selectMap.SetInputEnabled(true);
        }

        /// <summary>
        /// SelectScreen을 처음 상태로 돌림. isIntro면 지도·암호·암호표를 숨기고, 아니면 모두 보이는 상태로 둔다. 지도 입력은 막는다.
        /// </summary>
        private void ResetSelectState(bool isIntro)
        {
            _isSolved = false;

            if (!isIntro) RestoreTransitionState();

            if (_selectMap)
            {
                _selectMap.ResetState(true);
                _selectMap.Group.alpha = isIntro ? 0f : 1f;
            }

            if (_passwordGroup) _passwordGroup.alpha = isIntro ? 0f : 1f;
            if (_tableGroup) _tableGroup.alpha = isIntro ? 0f : 1f;

            if (_passwordText)
            {
                if (isIntro) _passwordText.ResetState();
                else _passwordText.ShowAll();
            }
        }

        /// <summary>
        /// 다른 화면 루트를 모두 끄고 새 화면 루트를 켬. 지도 이동 중에는 Guide2와 SelectScreen이 함께 켜져 있어 둘 다 정리한다.
        /// </summary>
        private void ShowRoot(GameObject root)
        {
            if (!root) return;

            SetScreenActive(_guideScreen, root);
            SetScreenActive(_mapScreen, root);
            SetScreenActive(_selectScreen, root);
            SetScreenActive(_failScreen, root);
        }

        /// <summary>
        /// screen이 root면 켜고 아니면 끔.
        /// </summary>
        private static void SetScreenActive(GameObject screen, GameObject root)
        {
            if (screen) screen.SetActive(screen == root);
        }

        /// <summary>
        /// TTS를 읽는 동안 빈 화면이 보이지 않게 Guide1을 미리 켬. 진행은 로딩이 끝난 뒤 시작한다.
        /// </summary>
        private void ShowGuidePlaceholder()
        {
            ShowRoot(_guideScreen);
            _currentIndex = GuidePageIndex;
        }

        /// <summary>
        /// 모든 화면을 끔.
        /// </summary>
        private void HideAllPages()
        {
            if (_guideScreen) _guideScreen.SetActive(false);
            if (_mapScreen) _mapScreen.SetActive(false);
            if (_selectScreen) _selectScreen.SetActive(false);
            if (_failScreen) _failScreen.SetActive(false);
        }

        /// <summary>
        /// TTS를 재생하고 끝날 때까지(없으면 _noTtsLineSeconds) 기다린 뒤 _advanceDelaySeconds만큼 더 기다림.
        /// </summary>
        private async UniTask PlayTtsAndWaitAsync(AudioClip clip, CancellationToken token)
        {
            _mediaPlayer.PlayClip(clip);
            await WaitTtsAsync(clip, token);
            await DelayAdvanceAsync(clip, token);
        }

        /// <summary>
        /// TTS가 있었으면 _advanceDelaySeconds만큼 기다림.
        /// </summary>
        private UniTask DelayAdvanceAsync(AudioClip clip, CancellationToken token)
        {
            return clip && _advanceDelaySeconds > 0f
                ? UniTask.Delay(TimeSpan.FromSeconds(_advanceDelaySeconds), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, token)
                : UniTask.CompletedTask;
        }

        /// <summary>
        /// TTS가 있으면 끝날 때까지, 없으면 _noTtsLineSeconds 동안 기다림.
        /// </summary>
        private UniTask WaitTtsAsync(AudioClip clip, CancellationToken token)
        {
            return clip
                ? UniTask.WaitUntil(_isTtsFinished, PlayerLoopTiming.Update, token)
                : UniTask.Delay(TimeSpan.FromSeconds(_noTtsLineSeconds), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, token);
        }

        /// <summary>
        /// 현재 TTS가 끝났는지 확인함. WaitUntil에 캐시된 대리자로 넘긴다.
        /// </summary>
        private bool IsTtsFinished()
        {
            return !_mediaPlayer || !_mediaPlayer.IsTtsPlaying;
        }

        /// <summary>
        /// 진행 중인 화면 흐름(TTS 대기, 연출, 판정 피드백)을 취소함.
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

        /// <summary>
        /// 끝난 흐름의 CTS가 아직 현재 것이면 해제함.
        /// </summary>
        private void ReleaseFlowCts(CancellationTokenSource cts)
        {
            if (_flowCts != cts) return;

            _flowCts.Dispose();
            _flowCts = null;
        }

        /// <summary>
        /// SelectScreen 등장 연출 트윈을 멈춤.
        /// </summary>
        private void KillIntroSequence()
        {
            if (_introSequence != null && _introSequence.IsActive()) _introSequence.Kill();
            _introSequence = null;
        }

        #endregion

        #region Answer

        /// <summary>
        /// 지도 장소를 누르면 바로 판정함. 판정 피드백이 끝날 때까지 지도 입력을 막는다.
        /// </summary>
        private void OnPlaceClicked(Button button)
        {
            if (_isLeaving || _isSolved || _currentIndex != SelectPageIndex) return;

            bool isCorrect = button && button == _answerButton;
            if (_logger != null) _logger.ZLogInformation($"[Mission3SceneController] Place selected: {(button ? button.name : "null")} ({(isCorrect ? "correct" : "wrong")}).");

            _selectMap.SetInputEnabled(false);
            CancelFlow();
            _flowCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());

            if (isCorrect)
            {
                _isSolved = true;
                RunCorrectAsync(_flowCts).Forget();
            }
            else
            {
                RunWrongAsync(_flowCts).Forget();
            }
        }

        /// <summary>
        /// 오답: FailScreen(오답 문구 흔들기)과 오답 TTS를 보여 준 뒤 SelectScreen으로 돌아와 선택 표시를 지우고 다시 고르게 함.
        /// </summary>
        private async UniTaskVoid RunWrongAsync(CancellationTokenSource cts)
        {
            try
            {
                ShowRoot(_failScreen);
                if (_failTitleBox) _failTitleBox.ShowFeedback(null, true);
                await PlayTtsAndWaitAsync(_failClip, cts.Token);

                ShowRoot(_selectScreen);
                if (_selectMap)
                {
                    _selectMap.ClearSelection();
                    _selectMap.SetInputEnabled(true);
                }
            }
            catch (OperationCanceledException)
            {
                // 화면을 떠나 취소됨. 원복은 ResetSelectState가 한다.
            }
            finally
            {
                ReleaseFlowCts(cts);
            }
        }

        /// <summary>
        /// 정답: 정답 효과음을 재생함. 그 뒤 화면은 아직 없다.
        /// </summary>
        private async UniTaskVoid RunCorrectAsync(CancellationTokenSource cts)
        {
            try
            {
                _mediaPlayer.PlayClip(_correctClip);
                if (_correctClip) await UniTask.WaitUntil(_isTtsFinished, PlayerLoopTiming.Update, cts.Token);
                if (_logger != null) _logger.ZLogInformation($"[Mission3SceneController] Password solved. Next screen is not implemented yet.");
            }
            catch (OperationCanceledException)
            {
                // 화면 전환이나 오브젝트 파괴로 인한 정상 취소.
            }
            finally
            {
                ReleaseFlowCts(cts);
            }
        }

        #endregion

        #region Receiver & Home

        /// <summary>
        /// 미션 씬에서 수화기를 내려놓으면 대기 화면 복귀 이벤트를 발행하고 0_Idle로 즉시 돌아감(작업계획서 4.3).
        /// </summary>
        private void OnReceiverStateChanged(bool isLifted)
        {
            if (isLifted || _isLeaving) return;

            if (_logger != null) _logger.ZLogInformation($"[Mission3SceneController] Receiver hung up. Returning to idle.");
            if (_moveIdlePublisher != null) _moveIdlePublisher.Publish(new MoveIdleEvent());
            LeaveScene(Constants.Scenes.Idle);
        }

        /// <summary>
        /// 홈 버튼으로 0_Idle ② 안내로 돌아감. 대기 복귀가 아니라 move_idle 로그는 보내지 않는다.
        /// </summary>
        private void OnHomeClicked()
        {
            if (_isLeaving) return;

            if (_logger != null) _logger.ZLogInformation($"[Mission3SceneController] Home button clicked. Returning to idle guide screen.");
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
            KillIntroSequence();
            if (_mediaPlayer) _mediaPlayer.Stop();
            SceneManager.LoadScene(sceneName);
        }

        #endregion
    }
}
