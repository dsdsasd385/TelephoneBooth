using System;
using System.Collections.Generic;
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
    /// 미션2(진실수호) 씬의 흐름을 관리하는 씬 매니저. 대사 화면 Guide1·Guide2 → 보고서 화면 → 마무리 선택 화면 순서다.
    /// <para>
    /// 대사 화면: 대사 Sprite(진실-N 1, 2, …)를 차례로 보여 주며 대사마다 TTS를 한 번 재생하고, TTS가 끝나면 다음 대사로 바꾼다.
    /// 화면의 마지막 대사 TTS가 끝나면 다음 화면으로 넘어간다. 연출 대사(DialogueScreenView의 revealLineIndex)에 들어가면
    /// TTS와 동시에 TextImage 페이드·MoveImage 이동을 시작한다.
    /// </para>
    /// <para>
    /// 보고서 화면: 들어갈 때 안내 TTS를 재생한다. 오답이면 TitleImage를 오답 문구로 바꾸고 오답 TTS가 끝나면 기본 문구로 되돌린다.
    /// 정답이면 ReportQuizView의 정답 연출(완성 보고서·도장, 도장 효과음)을 재생하고, 도장이 끝나고 _endDelaySeconds(1초) 뒤
    /// 마무리 선택 화면(MissionEndView: 메시지 보내기 → 9_Message, 다른 미션 → 미션1·3)으로 넘어간다. 마무리 선택 화면은 TTS(endTts)가 끝나면 버튼을 페이드인한다.
    /// </para>
    /// <para>
    /// TTS 파일이 아직 없어 경로가 빈 대사·오답 TTS는 _noTtsLineSeconds 동안 보여 준다.
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
        // 대사 TTS가 끝난 뒤 다음 대사·화면으로 넘어가기 전에 기다리는 시간(초).
        [SerializeField, Min(0f)] private float _lineAdvanceDelaySeconds = 1f;

        [Header("보고서 화면")]
        // 대사 화면 다음 화면. 뷰의 오브젝트가 화면 루트다.
        [SerializeField] private ReportQuizView _quiz;

        [Header("마무리")]
        // 마무리 선택·다른 미션 선택 화면.
        [SerializeField] private MissionEndView _end;
        // 정답 연출(도장)이 끝난 뒤 마무리 선택 화면으로 넘어가기 전에 기다리는 시간(초).
        [SerializeField, Min(0f)] private float _endDelaySeconds = 1f;

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
        private CancellationTokenSource _feedbackCts;
        private Func<bool> _isTtsFinished;
        // 화면별·대사별 TTS. 경로가 비었거나 읽지 못한 대사는 null.
        private AudioClip[][] _lineClips = Array.Empty<AudioClip[]>();
        private AudioClip _quizIntroClip;
        private AudioClip _quizFailClip;
        private AudioClip _endClip;
        private GameObject _visibleRoot;
        private bool _isInitialized;
        private bool _isLeaving;
        private int _currentIndex = -1;

        private ProjectInputActions _inputActions;
        private InactivityTimerGate _inactivityGate;
        private Action<InputAction.CallbackContext> _onNextScreenKey;
        private Action<InputAction.CallbackContext> _onPreviousScreenKey;

        #endregion

        #region Properties

        /// <summary>
        /// 대사 화면 수 + 보고서 화면 + 마무리 선택 화면.
        /// </summary>
        private int PageCount => _screens.Length + (_quiz ? 1 : 0) + (_end ? 1 : 0);

        /// <summary>
        /// 보고서 화면의 페이지 번호. 대사 화면 다음이다.
        /// </summary>
        private int QuizPageIndex => _screens.Length;

        /// <summary>
        /// 마무리 선택 화면의 페이지 번호. 보고서 화면 다음이다.
        /// </summary>
        private int EndPageIndex => _screens.Length + (_quiz ? 1 : 0);

        #endregion

        #region Dependency Injection & Unity Lifecycle

        /// <summary>
        /// VContainer 의존성 주입으로 로거, 수화기 입력, 대기 화면 복귀 이벤트 발행자, 체험 세션, 입력 액션, 무입력 타이머 제어를 할당함.
        /// </summary>
        [Inject]
        public void Construct(ILogger<Mission2SceneController> logger, IReceiverInput receiverInput,
            IPublisher<MoveIdleEvent> moveIdlePublisher, ExperienceSession session, ProjectInputActions inputActions,
            InactivityTimerGate inactivityGate)
        {
            _logger = logger;
            _receiverInput = receiverInput;
            _moveIdlePublisher = moveIdlePublisher;
            _session = session;
            _inputActions = inputActions;
            _inactivityGate = inactivityGate;
        }

        /// <summary>
        /// 주입이 끝난 뒤 수화기·버튼 이벤트를 연결하고 콘텐츠 데이터를 읽어 첫 화면을 시작함.
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

            HideAllPages();
            ShowFirstPagePlaceholder();
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
            CancelFeedback();
            _subscriptions?.Dispose();
            _subscriptions = null;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// 수화기·홈 버튼·보고서 제출 이벤트를 구독함.
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

            if (_quiz)
            {
                _quiz.Bind();
                _quiz.Submitted
                    .Subscribe(this, static (isCorrect, self) => self.OnQuizSubmitted(isCorrect))
                    .AddTo(ref builder);
            }
            else if (_logger != null)
            {
                _logger.ZLogError($"[Mission2SceneController] ReportQuizView is not assigned.");
            }

            if (_end)
            {
                _end.Bind();
                _end.MessageRequested
                    .Subscribe(this, static (_, self) => self.OnMessageRequested())
                    .AddTo(ref builder);
                _end.MissionRequested
                    .Subscribe(this, static (mission, self) => self.OnMissionRequested(mission))
                    .AddTo(ref builder);
            }
            else if (_logger != null)
            {
                _logger.ZLogError($"[Mission2SceneController] MissionEndView is not assigned.");
            }

            _subscriptions = builder.Build();
        }

        /// <summary>
        /// Mission2Content.json을 읽어 대사·오답 TTS를 미리 읽은 뒤 첫 화면을 시작함.
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

                // 파일을 하나씩 기다리면 첫 대사가 늦어지므로 모두 동시에 읽는다.
                (_, AudioClip introClip, AudioClip failClip, AudioClip stampClip, AudioClip endClip) = await UniTask.WhenAll(
                    LoadLineClipsAsync(table.screens ?? Array.Empty<DialogueScreenEntry>(), cancellationToken).AsAsyncUnitUniTask(),
                    _mediaPlayer.LoadClipAsync(table.quizIntroTts, cancellationToken),
                    _mediaPlayer.LoadClipAsync(table.quizFailTts, cancellationToken),
                    _mediaPlayer.LoadClipAsync(table.stampSfx, cancellationToken),
                    _mediaPlayer.LoadClipAsync(table.endTts, cancellationToken));

                _quizIntroClip = introClip;
                _quizFailClip = failClip;
                _endClip = endClip;
                if (_quiz) _quiz.SetStampClip(stampClip);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_isLeaving) return;

            _isInitialized = true;
            ShowPage(0);
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
            List<UniTask> loads = new List<UniTask>();
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
                    loads.Add(LoadLineClipAsync(s, i, paths[i], cancellationToken));
                }
            }

            await UniTask.WhenAll(loads);
        }

        /// <summary>
        /// 대사 TTS 하나를 읽어 화면·대사 자리에 넣음.
        /// </summary>
        private async UniTask LoadLineClipAsync(int screenIndex, int lineIndex, string path, CancellationToken cancellationToken)
        {
            _lineClips[screenIndex][lineIndex] = await _mediaPlayer.LoadClipAsync(path, cancellationToken);
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
            if (!_isInitialized || _isLeaving || PageCount == 0) return;

            int next = (_currentIndex + 1) % PageCount;
            if (_logger != null) _logger.ZLogInformation($"[Mission2SceneController] Debug key: next screen {_currentIndex} -> {next}");
            ShowPage(next);
        }

        /// <summary>
        /// Ctrl+←를 누르면 이전 화면을 처음부터 진행함. 첫 화면이면 마지막 화면으로.
        /// </summary>
        private void OnPreviousScreenKey(InputAction.CallbackContext context)
        {
            if (!_isInitialized || _isLeaving || PageCount == 0) return;

            int previous = (_currentIndex - 1 + PageCount) % PageCount;
            if (_logger != null) _logger.ZLogInformation($"[Mission2SceneController] Debug key: previous screen {_currentIndex} -> {previous}");
            ShowPage(previous);
        }

        #endregion

        #region Page Flow

        /// <summary>
        /// 진행 중인 흐름을 끊고 지정한 화면부터 다시 시작함. 대사 화면이면 대사 화면을 차례로 진행한 뒤 보고서 화면으로 간다.
        /// </summary>
        private void ShowPage(int index)
        {
            CancelFlow();
            CancelFeedback();
            if (_inactivityGate != null) _inactivityGate.CloseInput();
            if (_mediaPlayer) _mediaPlayer.PlayClip(null);
            if (_end) _end.Hide();

            if (index == EndPageIndex && _end)
            {
                ShowEnd();
                return;
            }

            if (index == QuizPageIndex)
            {
                ShowQuiz();
                return;
            }

            _flowCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            RunScreensAsync(index, _flowCts).Forget();
        }

        /// <summary>
        /// startIndex 화면부터 대사 화면을 차례로 한 번씩 진행하고 보고서 화면으로 넘어감. 화면의 마지막 대사 TTS가 끝나면 다음 화면으로 바꾼다.
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
                    ShowRoot(view.gameObject);
                    _currentIndex = s;

                    await RunLinesAsync(view, _lineClips[s], token);
                }

                if (_quiz) ShowQuiz();
            }
            catch (OperationCanceledException)
            {
                // 화면 전환이나 오브젝트 파괴로 인한 정상 취소.
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
        /// 화면의 대사를 차례로 한 번씩 보여 줌. 대사마다 TTS를 재생하고 끝난 뒤 _lineAdvanceDelaySeconds만큼 기다렸다가 다음 대사로 바꾼다.
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

                await WaitTtsAsync(clip, token);
                if (clip && _lineAdvanceDelaySeconds > 0f)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(_lineAdvanceDelaySeconds), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, token);
                }
            }

            await reveal;
        }

        /// <summary>
        /// 보고서 화면을 처음 상태(무작위 보고서)로 띄우고 안내 TTS를 재생함. 무입력 타이머는 안내 TTS가 끝난 뒤부터 센다.
        /// </summary>
        private void ShowQuiz()
        {
            if (!_quiz) return;

            _quiz.ResetState();
            ShowRoot(_quiz.gameObject);
            _currentIndex = QuizPageIndex;
            _mediaPlayer.PlayClip(_quizIntroClip);
            if (_inactivityGate != null) _inactivityGate.OpenInput(_mediaPlayer);
        }

        /// <summary>
        /// 이전 화면 루트를 끄고 새 화면 루트를 켬.
        /// </summary>
        private void ShowRoot(GameObject root)
        {
            if (_visibleRoot && _visibleRoot != root) _visibleRoot.SetActive(false);
            root.SetActive(true);
            _visibleRoot = root;
        }

        /// <summary>
        /// TTS를 읽는 동안 빈 화면이 보이지 않게 첫 대사 화면을 처음 상태로 미리 켬. 대사 진행은 로딩이 끝난 뒤 시작한다.
        /// </summary>
        private void ShowFirstPagePlaceholder()
        {
            if (_screens.Length == 0 || !_screens[0]) return;

            _screens[0].ResetState();
            ShowRoot(_screens[0].gameObject);
            _currentIndex = 0;
        }

        /// <summary>
        /// 모든 화면을 끔.
        /// </summary>
        private void HideAllPages()
        {
            for (int i = 0; i < _screens.Length; i++)
            {
                if (_screens[i]) _screens[i].gameObject.SetActive(false);
            }

            if (_quiz) _quiz.gameObject.SetActive(false);
            if (_end) _end.Hide();
            _visibleRoot = null;
        }

        /// <summary>
        /// 보이던 화면을 끄고 마무리 선택 화면을 켠 뒤 버튼을 페이드인함.
        /// </summary>
        private void ShowEnd()
        {
            if (_visibleRoot) _visibleRoot.SetActive(false);
            _visibleRoot = null;
            _currentIndex = EndPageIndex;

            CancelFlow();
            if (_inactivityGate != null) _inactivityGate.CloseInput();
            _flowCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            RunEndAsync(_flowCts).Forget();
        }

        /// <summary>
        /// 마무리 선택 화면을 버튼 없이 켜고 TTS(endTts)가 끝나면 버튼을 페이드인함. 페이드가 끝나 누를 수 있게 되면 무입력 타이머를 센다.
        /// 취소되면 그 상태에 둔다.
        /// </summary>
        private async UniTaskVoid RunEndAsync(CancellationTokenSource cts)
        {
            try
            {
                _end.PrepareEndSelect();
                _mediaPlayer.PlayClip(_endClip);
                if (_endClip) await UniTask.WaitUntil(_isTtsFinished, PlayerLoopTiming.Update, cts.Token);

                await _end.ShowEndSelectAsync(cts.Token);
                if (_inactivityGate != null) _inactivityGate.OpenInput(_mediaPlayer);
            }
            catch (OperationCanceledException)
            {
                // 화면 전환이나 오브젝트 파괴로 인한 정상 취소.
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
        /// 진행 중인 화면 흐름(TTS 대기, 연출)을 취소함.
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

        #region Quiz Feedback

        /// <summary>
        /// 사진 버튼을 누르면 오답은 오답 문구·TTS, 정답은 완성 보고서·도장 연출을 재생함. 새 제출은 이전 오답 흐름을 끊는다.
        /// </summary>
        private void OnQuizSubmitted(bool isCorrect)
        {
            if (_isLeaving || _currentIndex != QuizPageIndex) return;

            if (_logger != null) _logger.ZLogInformation($"[Mission2SceneController] Report submitted ({(isCorrect ? "correct" : "wrong")}).");

            CancelFeedback();
            _feedbackCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());

            if (isCorrect)
            {
                // 정답 연출 동안은 누를 버튼이 없다. 마무리 선택 화면 버튼이 나오면 다시 센다.
                if (_inactivityGate != null) _inactivityGate.CloseInput();
                _mediaPlayer.PlayClip(null);
                RunCorrectAsync(_feedbackCts).Forget();
            }
            else
            {
                // 오답 TTS가 끝날 때까지 다시 고르거나 제출하지 못하게 한다.
                _quiz.SetInputBlocked(true);
                _quiz.ShowFailTitle();
                _mediaPlayer.PlayClip(_quizFailClip);
                RunWrongAsync(_feedbackCts).Forget();
            }
        }

        /// <summary>
        /// 오답 TTS가 끝나면(없으면 _noTtsLineSeconds 뒤) TitleImage를 기본 문구로, ReportImage를 원래 보고서로 되돌리고 버튼 입력을 다시 받음.
        /// </summary>
        private async UniTaskVoid RunWrongAsync(CancellationTokenSource cts)
        {
            try
            {
                await WaitTtsAsync(_quizFailClip, cts.Token);
                _quiz.ShowDefaultTitle();
                _quiz.RestoreReport();
                _quiz.SetInputBlocked(false);
            }
            catch (OperationCanceledException)
            {
                // 화면을 떠나 취소됨. 원복은 ResetState가 한다.
            }
            finally
            {
                ReleaseFeedbackCts(cts);
            }
        }

        /// <summary>
        /// 정답 연출을 재생함.
        /// </summary>
        private async UniTaskVoid RunCorrectAsync(CancellationTokenSource cts)
        {
            try
            {
                await _quiz.PlayCorrectAsync(cts.Token);
                if (_endDelaySeconds > 0f)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(_endDelaySeconds), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, cts.Token);
                }

                if (_end)
                {
                    ShowEnd();
                }
                else if (_logger != null)
                {
                    _logger.ZLogWarning($"[Mission2SceneController] Report completed but MissionEndView is not assigned.");
                }
            }
            catch (OperationCanceledException)
            {
                // 화면 전환이나 오브젝트 파괴로 인한 정상 취소.
            }
            finally
            {
                ReleaseFeedbackCts(cts);
            }
        }

        /// <summary>
        /// 진행 중인 보고서 피드백 흐름(오답 원복 대기, 정답 연출)을 취소함.
        /// </summary>
        private void CancelFeedback()
        {
            if (_feedbackCts == null) return;

            // 연출(DOTween) 중 취소하면 finally가 Cancel() 안에서 바로 돌아 필드를 비우므로 먼저 떼어 낸다.
            CancellationTokenSource cts = _feedbackCts;
            _feedbackCts = null;
            cts.Cancel();
            cts.Dispose();
        }

        /// <summary>
        /// 끝난 피드백 흐름의 CTS가 아직 현재 것이면 해제함.
        /// </summary>
        private void ReleaseFeedbackCts(CancellationTokenSource cts)
        {
            if (_feedbackCts != cts) return;

            _feedbackCts.Dispose();
            _feedbackCts = null;
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
        /// 마무리 선택 화면의 "메시지 보내기"로 9_Message로 감. 씬이 빌드에 없으면 경고를 남기고 0_Idle로 간다.
        /// </summary>
        private void OnMessageRequested()
        {
            if (_isLeaving || _currentIndex != EndPageIndex) return;

            if (Application.CanStreamedLevelBeLoaded(Constants.Scenes.Message))
            {
                LeaveScene(Constants.Scenes.Message);
                return;
            }

            if (_logger != null) _logger.ZLogWarning($"[Mission2SceneController] Scene {Constants.Scenes.Message} is not in build settings. Going to idle instead.");
            LeaveScene(Constants.Scenes.Idle);
        }

        /// <summary>
        /// 다른 미션 버튼으로 그 미션을 세션에 기록하고 미션 씬으로 이동함.
        /// </summary>
        private void OnMissionRequested(ContentType mission)
        {
            if (_isLeaving || _currentIndex != EndPageIndex) return;

            string sceneName = mission switch
            {
                ContentType.Culture => Constants.Scenes.Mission1,
                ContentType.Truth => Constants.Scenes.Mission2,
                ContentType.Nation => Constants.Scenes.Mission3,
                _ => null
            };

            if (string.IsNullOrEmpty(sceneName))
            {
                if (_logger != null) _logger.ZLogError($"[Mission2SceneController] No scene for mission {mission}. Check MissionEndView targets.");
                return;
            }

            if (_session != null) _session.SetContent(mission);
            if (_logger != null) _logger.ZLogInformation($"[Mission2SceneController] Other mission selected: {mission}. Loading scene {sceneName}.");
            LeaveScene(sceneName);
        }

        /// <summary>
        /// 진행 중인 흐름과 재생을 멈추고 다른 씬을 불러옴. 한 번만 동작한다.
        /// </summary>
        private void LeaveScene(string sceneName)
        {
            if (_isLeaving) return;
            _isLeaving = true;

            CancelFlow();
            CancelFeedback();
            if (_mediaPlayer) _mediaPlayer.Stop();
            SceneManager.LoadScene(sceneName);
        }

        #endregion
    }
}
