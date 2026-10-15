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
    /// 미션 씬 화면 한 장의 루트, 영상을 띄울 RawImage(영상 화면만), TTS가 끝난 뒤 페이드인할 버튼들.
    /// 퀴즈 단계 화면의 선택지·Next 버튼은 QuizStageView가 따로 관리하므로 buttons를 비워 둔다.
    /// </summary>
    [Serializable]
    public struct MissionPageView
    {
        public GameObject root;
        public RawImage videoSurface;
        public CanvasGroup[] buttons;
    }

    /// <summary>
    /// 다른 미션 선택 화면의 버튼 하나와 그 버튼이 여는 미션.
    /// </summary>
    [Serializable]
    public struct MissionTarget
    {
        public Button button;
        public ContentType mission;
    }

    /// <summary>
    /// 미션1(문화수호, 작업계획서 7·8장) 씬의 흐름을 관리하는 씬 매니저.
    /// <para>
    /// 화면 순서: 타이틀 영상 → 1단계 포장 → 2단계 보관 → 3단계 은닉 → 영상 → 역사 → 마무리 선택 → 다른 미션 선택.
    /// 타이틀·영상 화면은 TTS가 끝나고 영상을 한 번 다 본 뒤 자동으로 넘어간다. 역사 화면은 확인 버튼으로 넘어간다.
    /// </para>
    /// <para>
    /// 단계 규칙(7.2): 화면 TTS가 끝나면 선택지가 페이드인된다. 오답은 TitleBox를 그 오답 문구로 바꾸고 흔든 뒤
    /// 오답 TTS가 끝나면(TTS가 없으면 흔들림이 끝나면) 기본 문구로 돌린다. 정답은 정답 문구로 바꾸고 선택지를 잠근 뒤
    /// 정답 TTS가 끝나면 Next를 페이드인한다. 피드백 TTS는 이전 TTS를 끊는다.
    /// </para>
    /// <para>
    /// 수화기를 내려놓으면 MoveIdleEvent를 발행하고 0_Idle ① 대기로 간다(4.3). 홈 버튼은 0_Idle ② 안내로 간다.
    /// 9_Message 씬이 아직 없어 "메시지 보내기"는 0_Idle로 대신 간다.
    /// </para>
    /// </summary>
    public class Mission1SceneController : MonoBehaviour
    {
        #region Constants

        private const int TitlePageIndex = 0;
        private const int FirstStagePageIndex = 1;
        private const int VideoPageIndex = 4;
        private const int HistoryPageIndex = 5;
        private const int EndSelectPageIndex = 6;
        private const int OtherMissionPageIndex = 7;

        #endregion

        #region Serialized Fields

        [SerializeField] private PageMediaPlayer _mediaPlayer;
        // Mission1Content.json의 pages와 같은 순서.
        [SerializeField] private MissionPageView[] _pages = Array.Empty<MissionPageView>();
        // 1–3단계. 페이지 번호는 FirstStagePageIndex부터 차례로.
        [SerializeField] private QuizStageView[] _stages = Array.Empty<QuizStageView>();
        [SerializeField] private float _buttonFadeSeconds = 0.5f;
        // 타이틀·영상 화면이 끝난 뒤 다음 화면으로 넘어가기 전에 기다리는 시간(초).
        [SerializeField, Min(0f)] private float _autoAdvanceDelaySeconds = 0.5f;

        [Header("역사 화면")]
        [SerializeField] private Button _historyNextButton;

        [Header("마무리 선택")]
        [SerializeField] private Button _messageButton;
        [SerializeField] private Button _otherMissionButton;
        [SerializeField] private MissionTarget[] _missionTargets = Array.Empty<MissionTarget>();

        [Header("공통")]
        [SerializeField] private Button _homeButton;

        #endregion

        #region Private Fields

        private ILogger<Mission1SceneController> _logger;
        private IReceiverInput _receiverInput;
        private IPublisher<MoveIdleEvent> _moveIdlePublisher;
        private ExperienceSession _session;
        private ProjectInputActions _inputActions;
        private Action<InputAction.CallbackContext> _onNextScreenKey;
        private Action<InputAction.CallbackContext> _onPreviousScreenKey;

        private IDisposable _subscriptions;
        private CancellationTokenSource _pageCts;
        private CancellationTokenSource _feedbackCts;
        private Func<bool> _isTtsFinished;
        private Func<bool> _isPageMediaFinished;
        // 단계별·선택지별 피드백 TTS. 경로가 비었거나 읽지 못한 선택지는 null.
        private AudioClip[][] _choiceClips = Array.Empty<AudioClip[]>();
        private bool _isInitialized;
        private bool _isLeaving;
        // 현재 화면의 영상이 끝까지 한 번 재생됐는지. 화면을 바꿀 때마다 지운다.
        private bool _hasVideoCompleted;
        private int _currentIndex = -1;
        private int _visibleIndex = -1;

        #endregion

        #region Properties

        public int CurrentIndex => _currentIndex;

        #endregion

        #region Dependency Injection & Unity Lifecycle

        /// <summary>
        /// VContainer 의존성 주입으로 로거, 수화기 입력, 대기 화면 복귀 이벤트 발행자, 체험 세션, 입력 액션을 할당함.
        /// </summary>
        [Inject]
        public void Construct(ILogger<Mission1SceneController> logger, IReceiverInput receiverInput,
            IPublisher<MoveIdleEvent> moveIdlePublisher, ExperienceSession session, ProjectInputActions inputActions)
        {
            _logger = logger;
            _receiverInput = receiverInput;
            _moveIdlePublisher = moveIdlePublisher;
            _session = session;
            _inputActions = inputActions;
        }

        /// <summary>
        /// 주입이 끝난 뒤 버튼·수화기·재생기 이벤트를 연결하고 콘텐츠 데이터를 읽어 첫 화면을 띄움.
        /// </summary>
        private void Start()
        {
            if (_logger == null || _receiverInput == null || _moveIdlePublisher == null || _session == null)
            {
                Debug.LogError("[Mission1SceneController] Dependencies were not injected. Check that Mission1LifetimeScope registers Mission1SceneController and GameLifetimeScope registers IReceiverInput and ExperienceSession.");
            }

            _isTtsFinished = IsTtsFinished;
            _isPageMediaFinished = IsPageMediaFinished;

            // 0_Idle을 거치지 않고 이 씬에서 바로 플레이해도 세션에 현재 미션이 남게 한다.
            if (_session != null) _session.SetContent(ContentType.Culture);

            HideAllPages();
            BindEvents();
            BindDebugInput();
            InitializeAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        /// <summary>
        /// 구독을 해제하고 진행 중인 화면·피드백 흐름을 취소함.
        /// </summary>
        private void OnDestroy()
        {
            UnbindDebugInput();
            CancelPageFlow();
            CancelFeedback();
            _subscriptions?.Dispose();
            _subscriptions = null;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// 버튼·단계·수화기·재생기 이벤트를 구독함. 구독은 시작 시 한 번만 만든다.
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

            if (_mediaPlayer)
            {
                _mediaPlayer.PageShown
                    .Subscribe(this, static (pageIndex, self) => self.OnPageShown(pageIndex))
                    .AddTo(ref builder);
                _mediaPlayer.VideoLoopCompleted
                    .Subscribe(this, static (_, self) => self._hasVideoCompleted = true)
                    .AddTo(ref builder);
            }

            for (int i = 0; i < _stages.Length; i++)
            {
                QuizStageView stage = _stages[i];
                if (!stage)
                {
                    if (_logger != null) _logger.ZLogError($"[Mission1SceneController] Stage view is not assigned at {i}.");
                    continue;
                }

                int stageIndex = i;
                stage.Bind();
                stage.ChoiceClicked
                    .Subscribe(choiceIndex => OnChoiceClicked(stageIndex, choiceIndex))
                    .AddTo(ref builder);
                stage.NextClicked
                    .Subscribe(_ => OnStageNextClicked(stageIndex))
                    .AddTo(ref builder);
            }

            BindClick(_historyNextButton, nameof(_historyNextButton), OnHistoryNextClicked, ref builder);
            BindClick(_messageButton, nameof(_messageButton), OnMessageClicked, ref builder);
            BindClick(_otherMissionButton, nameof(_otherMissionButton), OnOtherMissionClicked, ref builder);
            BindClick(_homeButton, nameof(_homeButton), OnHomeClicked, ref builder);

            for (int i = 0; i < _missionTargets.Length; i++)
            {
                ContentType mission = _missionTargets[i].mission;
                BindClick(_missionTargets[i].button, nameof(_missionTargets), () => OnMissionTargetClicked(mission), ref builder);
            }

            _subscriptions = builder.Build();
        }

        /// <summary>
        /// 버튼 클릭을 구독함. 연결되지 않은 버튼은 오류 로그를 남김.
        /// </summary>
        private void BindClick(Button button, string fieldName, Action onClick, ref DisposableBuilder builder)
        {
            if (!button)
            {
                if (_logger != null) _logger.ZLogError($"[Mission1SceneController] Button is not assigned: {fieldName}");
                return;
            }

            button.OnClickAsObservable()
                .Subscribe(onClick, static (_, action) => action())
                .AddTo(ref builder);
        }

        /// <summary>
        /// 영상 설정과 Mission1Content.json을 읽어 재생기에 넘기고, 선택지 피드백 TTS를 미리 읽은 뒤 첫 화면을 보여 줌.
        /// </summary>
        private async UniTaskVoid InitializeAsync(CancellationToken cancellationToken)
        {
            if (!_mediaPlayer)
            {
                if (_logger != null) _logger.ZLogError($"[Mission1SceneController] PageMediaPlayer is not assigned.");
                return;
            }

            try
            {
                MediaSettings mediaSettings = await JsonLoader.LoadAsync<MediaSettings>(Constants.Files.MediaSettings, cancellationToken, _logger);
                _mediaPlayer.SetDoubleBuffering(mediaSettings.doubleBuffering);

                MissionContentTable table = await JsonLoader.LoadAsync<MissionContentTable>(Constants.Files.Mission1Content, cancellationToken, _logger);
                ValidateContent(table);
                await _mediaPlayer.LoadAsync(table.pages, cancellationToken);
                await LoadChoiceClipsAsync(table.stages, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _isInitialized = true;
            ShowPage(TitlePageIndex);
        }

        /// <summary>
        /// 단계별 선택지 피드백 TTS를 모두 읽음. JSON에 없는 단계·선택지는 TTS 없이 동작한다.
        /// </summary>
        private async UniTask LoadChoiceClipsAsync(MissionStageEntry[] stages, CancellationToken cancellationToken)
        {
            _choiceClips = new AudioClip[_stages.Length][];

            for (int s = 0; s < _stages.Length; s++)
            {
                int choiceCount = _stages[s] ? _stages[s].ChoiceCount : 0;
                _choiceClips[s] = new AudioClip[choiceCount];

                string[] paths = s < stages.Length && stages[s].choiceTts != null ? stages[s].choiceTts : Array.Empty<string>();
                int count = Mathf.Min(choiceCount, paths.Length);
                for (int c = 0; c < count; c++)
                {
                    _choiceClips[s][c] = await _mediaPlayer.LoadClipAsync(paths[c], cancellationToken);
                }
            }
        }

        /// <summary>
        /// JSON의 screen 이름·개수가 씬 화면·단계와 맞는지 확인해 다르면 경고를 남김. 잘못된 리소스 순서를 일찍 찾기 위함.
        /// </summary>
        private void ValidateContent(MissionContentTable table)
        {
            if (_logger == null) return;

            if (table.pages.Length != _pages.Length)
            {
                _logger.ZLogWarning($"[Mission1SceneController] Page count mismatch. json={table.pages.Length}, scene={_pages.Length}");
            }

            int pageCount = Mathf.Min(table.pages.Length, _pages.Length);
            for (int i = 0; i < pageCount; i++)
            {
                GameObject root = _pages[i].root;
                if (!root || root.name != table.pages[i].screen)
                {
                    _logger.ZLogWarning($"[Mission1SceneController] Page order mismatch at {i}. json={table.pages[i].screen}, scene={(root ? root.name : "null")}");
                }
            }

            for (int s = 0; s < _stages.Length; s++)
            {
                if (!_stages[s]) continue;

                if (s >= table.stages.Length)
                {
                    _logger.ZLogWarning($"[Mission1SceneController] Stage {s} has no entry in json. Feedback TTS is off.");
                    continue;
                }

                MissionStageEntry entry = table.stages[s];
                int ttsCount = entry.choiceTts != null ? entry.choiceTts.Length : 0;
                if (ttsCount != _stages[s].ChoiceCount)
                {
                    _logger.ZLogWarning($"[Mission1SceneController] Stage {s} choice count mismatch. json={ttsCount}, scene={_stages[s].ChoiceCount}");
                }

                if (_stages[s].name != entry.screen)
                {
                    _logger.ZLogWarning($"[Mission1SceneController] Stage order mismatch at {s}. json={entry.screen}, scene={_stages[s].name}");
                }
            }
        }

        #endregion

        #region Debug Input

        /// <summary>
        /// 에디터·개발 빌드에서만 디버그 키를 연결함. Ctrl+→/←는 다음·이전 화면(정답·수화기 없이 화면 확인).
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
        /// Ctrl+→를 누르면 다음 화면으로 감. 마지막 화면이면 첫 화면으로.
        /// </summary>
        private void OnNextScreenKey(InputAction.CallbackContext context)
        {
            if (!_isInitialized || _isLeaving) return;

            int next = (_currentIndex + 1) % _pages.Length;
            if (_logger != null) _logger.ZLogInformation($"[Mission1SceneController] Debug key: next screen {_currentIndex} -> {next}");
            ShowPage(next);
        }

        /// <summary>
        /// Ctrl+←를 누르면 이전 화면으로 감. 첫 화면이면 마지막 화면으로.
        /// </summary>
        private void OnPreviousScreenKey(InputAction.CallbackContext context)
        {
            if (!_isInitialized || _isLeaving) return;

            int previous = (_currentIndex - 1 + _pages.Length) % _pages.Length;
            if (_logger != null) _logger.ZLogInformation($"[Mission1SceneController] Debug key: previous screen {_currentIndex} -> {previous}");
            ShowPage(previous);
        }

        #endregion

        #region Receiver & Home

        /// <summary>
        /// 미션 씬에서 수화기를 내려놓으면 대기 화면 복귀 이벤트를 발행하고 0_Idle로 즉시 돌아감(작업계획서 4.3).
        /// </summary>
        private void OnReceiverStateChanged(bool isLifted)
        {
            if (isLifted) return;

            if (_logger != null) _logger.ZLogInformation($"[Mission1SceneController] Receiver hung up on screen {_currentIndex}. Returning to idle.");
            ReturnToIdle();
        }

        /// <summary>
        /// 홈 버튼으로 0_Idle ② 안내로 돌아감. 수화기는 들려 있으므로 ① 대기를 건너뛰며, 대기 복귀가 아니라 move_idle 로그는 보내지 않는다.
        /// </summary>
        private void OnHomeClicked()
        {
            if (_isLeaving) return;

            if (_logger != null) _logger.ZLogInformation($"[Mission1SceneController] Home button clicked on screen {_currentIndex}. Returning to idle guide screen.");
            if (_session != null) _session.RequestGuideReturn();
            LeaveScene(Constants.Scenes.Idle);
        }

        /// <summary>
        /// move_idle 로그용 이벤트를 발행하고 0_Idle 씬을 불러옴.
        /// </summary>
        private void ReturnToIdle()
        {
            if (_isLeaving) return;

            if (_moveIdlePublisher != null) _moveIdlePublisher.Publish(new MoveIdleEvent());
            LeaveScene(Constants.Scenes.Idle);
        }

        /// <summary>
        /// 진행 중인 흐름과 재생을 멈추고 다른 씬을 불러옴. 한 번만 동작한다.
        /// </summary>
        private void LeaveScene(string sceneName)
        {
            if (_isLeaving) return;
            _isLeaving = true;

            CancelPageFlow();
            CancelFeedback();
            if (_mediaPlayer) _mediaPlayer.Stop();
            SceneManager.LoadScene(sceneName);
        }

        #endregion

        #region Stage Input

        /// <summary>
        /// 선택지를 누르면 정답·오답에 맞춰 TitleBox·피드백 TTS·Next를 처리함.
        /// </summary>
        private void OnChoiceClicked(int stageIndex, int choiceIndex)
        {
            if (_isLeaving || _currentIndex != FirstStagePageIndex + stageIndex) return;

            QuizStageView stage = _stages[stageIndex];
            bool isCorrect = stage.IsCorrect(choiceIndex);
            AudioClip clip = GetChoiceClip(stageIndex, choiceIndex);
            if (_logger != null) _logger.ZLogInformation($"[Mission1SceneController] Stage {stageIndex + 1} choice {choiceIndex} ({(isCorrect ? "correct" : "wrong")}).");

            // 이전 오답의 원복 대기를 끊는다. 새 피드백 TTS가 이전 TTS를 끊는 것과 같다(5장).
            CancelFeedback();
            _feedbackCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());

            stage.MarkChoice(choiceIndex);

            if (isCorrect)
            {
                stage.SetChoicesInteractable(false);
                if (stage.TitleBox) stage.TitleBox.ShowFeedback(stage.GetFeedbackTitle(choiceIndex), false);
                _mediaPlayer.PlayClip(clip);
                RunCorrectFeedbackAsync(stage, _feedbackCts).Forget();
            }
            else
            {
                if (stage.TitleBox) stage.TitleBox.ShowFeedback(stage.GetFeedbackTitle(choiceIndex), true);
                _mediaPlayer.PlayClip(clip);
                RunWrongFeedbackAsync(stage, clip != null, _feedbackCts).Forget();
            }
        }

        /// <summary>
        /// 정답 TTS가 끝나면 Next 버튼을 페이드인함.
        /// </summary>
        private async UniTaskVoid RunCorrectFeedbackAsync(QuizStageView stage, CancellationTokenSource cts)
        {
            try
            {
                await UniTask.WaitUntil(_isTtsFinished, PlayerLoopTiming.Update, cts.Token);
                await stage.FadeInNextAsync(_buttonFadeSeconds, cts.Token);
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
        /// 오답 TTS가 끝나면(없으면 흔들림이 끝나면) TitleBox를 기본 문구로 되돌림.
        /// 다른 선택지를 누르면 취소되고, 그 선택지의 피드백이 이어받는다.
        /// </summary>
        private async UniTaskVoid RunWrongFeedbackAsync(QuizStageView stage, bool hasTts, CancellationTokenSource cts)
        {
            try
            {
                if (hasTts)
                {
                    await UniTask.WaitUntil(_isTtsFinished, PlayerLoopTiming.Update, cts.Token);
                }
                else if (stage.TitleBox)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(stage.TitleBox.ShakeSeconds), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, cts.Token);
                }

                if (stage.TitleBox) stage.TitleBox.ShowDefault();
            }
            catch (OperationCanceledException)
            {
                // 다른 선택지를 눌렀거나 화면을 떠나 취소됨. 원복은 이어받은 쪽(또는 ResetState)이 한다.
            }
            finally
            {
                ReleaseFeedbackCts(cts);
            }
        }

        /// <summary>
        /// 정답을 맞힌 단계의 Next를 누르면 다음 화면으로 감.
        /// </summary>
        private void OnStageNextClicked(int stageIndex)
        {
            int pageIndex = FirstStagePageIndex + stageIndex;
            if (_isLeaving || _currentIndex != pageIndex) return;

            _stages[stageIndex].LockNext();
            ShowPage(pageIndex + 1);
        }

        /// <summary>
        /// 단계·선택지의 피드백 TTS를 돌려줌. 없으면 null.
        /// </summary>
        private AudioClip GetChoiceClip(int stageIndex, int choiceIndex)
        {
            if (stageIndex >= _choiceClips.Length) return null;

            AudioClip[] clips = _choiceClips[stageIndex];
            return choiceIndex < clips.Length ? clips[choiceIndex] : null;
        }

        /// <summary>
        /// 진행 중인 피드백 흐름(오답 원복 대기, 정답 후 Next 페이드)을 취소함.
        /// </summary>
        private void CancelFeedback()
        {
            if (_feedbackCts == null) return;

            // 페이드(DOTween) 중 취소하면 finally가 Cancel() 안에서 바로 돌아 필드를 비우므로 먼저 떼어 낸다.
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

        #region End Buttons

        /// <summary>
        /// 역사 화면의 확인 버튼으로 마무리 선택 화면으로 감.
        /// </summary>
        private void OnHistoryNextClicked()
        {
            if (_isLeaving || _currentIndex != HistoryPageIndex) return;

            LockCurrentButtons();
            ShowPage(EndSelectPageIndex);
        }

        /// <summary>
        /// "메시지 보내기"로 9_Message로 감. 씬이 아직 없으면 경고를 남기고 0_Idle로 간다.
        /// </summary>
        private void OnMessageClicked()
        {
            if (_isLeaving || _currentIndex != EndSelectPageIndex) return;

            LockCurrentButtons();
            if (Application.CanStreamedLevelBeLoaded(Constants.Scenes.Message))
            {
                LeaveScene(Constants.Scenes.Message);
                return;
            }

            if (_logger != null) _logger.ZLogWarning($"[Mission1SceneController] Scene {Constants.Scenes.Message} is not in build settings yet. Going to idle instead.");
            LeaveScene(Constants.Scenes.Idle);
        }

        /// <summary>
        /// "다른 미션"으로 다른 미션 선택 화면으로 감.
        /// </summary>
        private void OnOtherMissionClicked()
        {
            if (_isLeaving || _currentIndex != EndSelectPageIndex) return;

            LockCurrentButtons();
            ShowPage(OtherMissionPageIndex);
        }

        /// <summary>
        /// 다른 미션 버튼으로 그 미션을 세션에 기록하고 미션 씬으로 이동함.
        /// </summary>
        private void OnMissionTargetClicked(ContentType mission)
        {
            if (_isLeaving || _currentIndex != OtherMissionPageIndex) return;

            string sceneName = GetMissionScene(mission);
            if (string.IsNullOrEmpty(sceneName))
            {
                if (_logger != null) _logger.ZLogError($"[Mission1SceneController] No scene for mission {mission}. Check _missionTargets.");
                return;
            }

            LockCurrentButtons();
            if (_session != null) _session.SetContent(mission);
            if (_logger != null) _logger.ZLogInformation($"[Mission1SceneController] Other mission selected: {mission}. Loading scene {sceneName}.");
            LeaveScene(sceneName);
        }

        /// <summary>
        /// 미션에 해당하는 씬 이름. 없으면 null.
        /// </summary>
        private static string GetMissionScene(ContentType mission)
        {
            return mission switch
            {
                ContentType.Culture => Constants.Scenes.Mission1,
                ContentType.Truth => Constants.Scenes.Mission2,
                ContentType.Nation => Constants.Scenes.Mission3,
                _ => null
            };
        }

        #endregion

        #region Page Navigation

        /// <summary>
        /// 지정한 화면으로 전환함. 영상 화면은 새 영상의 첫 프레임이 나올 때(OnPageShown) 교체하고,
        /// 이미지 화면은 영상을 멈추고 바로 교체한다.
        /// </summary>
        public void ShowPage(int index)
        {
            if (index < 0 || index >= _pages.Length)
            {
                if (_logger != null) _logger.ZLogWarning($"[Mission1SceneController] Page index out of range: {index}");
                return;
            }

            CancelPageFlow();
            CancelFeedback();

            // 교체 전까지 이전 화면이 보일 수 있으므로 그 버튼·선택지가 눌리지 않게 먼저 잠근다.
            if (_visibleIndex >= 0) LockPage(_visibleIndex);

            _currentIndex = index;
            _hasVideoCompleted = false;
            ResetPage(index);

            RawImage surface = _pages[index].videoSurface;
            if (surface)
            {
                // 영상을 다 보고 넘어가는 화면은 한 번만 재생해, 넘어가기 전 처음부터 다시 재생되는 장면이 보이지 않게 한다.
                _mediaPlayer.Play(index, surface, !IsAutoAdvancePage(index));
                return;
            }

            _mediaPlayer.PlayTtsOnly(index);
            RevealPage(index);
        }

        /// <summary>
        /// 새 영상의 첫 프레임이 나온 순간 화면을 교체함.
        /// </summary>
        private void OnPageShown(int pageIndex)
        {
            // 그사이 다른 화면이 요청됐으면 그 요청에서 교체한다.
            if (pageIndex != _currentIndex || _isLeaving) return;

            RevealPage(pageIndex);
        }

        /// <summary>
        /// 이전 화면을 끄고 새 화면을 켠 뒤 TTS 종료 대기 흐름을 시작함.
        /// </summary>
        private void RevealPage(int pageIndex)
        {
            if (_visibleIndex >= 0 && _visibleIndex != pageIndex)
            {
                SetPageActive(_visibleIndex, false);
            }

            SetPageActive(pageIndex, true);
            _visibleIndex = pageIndex;
            // 준비하는 동안 이전 영상이 끝나 기록된 값은 이 화면 것이 아니다.
            _hasVideoCompleted = false;

            CancelPageFlow();
            _pageCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            RunPageFlowAsync(pageIndex, _pageCts).Forget();
        }

        /// <summary>
        /// TTS가 끝나기를 기다렸다가, 타이틀·영상 화면은 영상을 한 번 본 뒤 다음 화면으로 넘어가고,
        /// 단계 화면은 선택지를, 그 밖의 화면은 버튼을 페이드인함.
        /// </summary>
        private async UniTaskVoid RunPageFlowAsync(int index, CancellationTokenSource cts)
        {
            try
            {
                await UniTask.WaitUntil(_isTtsFinished, PlayerLoopTiming.Update, cts.Token);

                if (IsAutoAdvancePage(index))
                {
                    await UniTask.WaitUntil(_isPageMediaFinished, PlayerLoopTiming.Update, cts.Token);
                    if (_autoAdvanceDelaySeconds > 0f)
                    {
                        await UniTask.Delay(TimeSpan.FromSeconds(_autoAdvanceDelaySeconds), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, cts.Token);
                    }

                    ShowPage(index + 1);
                    return;
                }

                QuizStageView stage = GetStage(index);
                if (stage)
                {
                    await stage.FadeInChoicesAsync(_buttonFadeSeconds, cts.Token);
                    return;
                }

                await FadeInButtonsAsync(index, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 화면 전환이나 오브젝트 파괴로 인한 정상 취소.
            }
            finally
            {
                if (_pageCts == cts)
                {
                    _pageCts.Dispose();
                    _pageCts = null;
                }
            }
        }

        /// <summary>
        /// 영상을 한 번 다 본 뒤 다음 화면으로 자동 진행하는 화면(타이틀·영상)인지 여부.
        /// </summary>
        private static bool IsAutoAdvancePage(int index)
        {
            return index == TitlePageIndex || index == VideoPageIndex;
        }

        /// <summary>
        /// 현재 TTS(페이지·피드백)가 끝났는지 확인함. WaitUntil에 캐시된 대리자로 넘긴다.
        /// </summary>
        private bool IsTtsFinished()
        {
            return !_mediaPlayer || !_mediaPlayer.IsTtsPlaying;
        }

        /// <summary>
        /// 현재 화면의 영상이 한 번 다 재생됐거나 재생 중인 영상이 없는지 확인함(영상 준비 실패 시 멈추지 않게).
        /// </summary>
        private bool IsPageMediaFinished()
        {
            return _hasVideoCompleted || !_mediaPlayer || !_mediaPlayer.IsVideoPlaying;
        }

        /// <summary>
        /// 진행 중인 화면 흐름(TTS 대기, 페이드, 자동 진행 대기)을 취소함.
        /// </summary>
        private void CancelPageFlow()
        {
            if (_pageCts == null) return;

            // 페이드(DOTween) 중 취소하면 finally가 Cancel() 안에서 바로 돌아 필드를 비우므로 먼저 떼어 낸다.
            CancellationTokenSource cts = _pageCts;
            _pageCts = null;
            cts.Cancel();
            cts.Dispose();
        }

        /// <summary>
        /// 페이지 번호가 단계 화면이면 그 단계 뷰를, 아니면 null을 돌려줌.
        /// </summary>
        private QuizStageView GetStage(int pageIndex)
        {
            int stageIndex = pageIndex - FirstStagePageIndex;
            return stageIndex >= 0 && stageIndex < _stages.Length ? _stages[stageIndex] : null;
        }

        /// <summary>
        /// 화면에 들어가기 전의 처음 상태로 돌림. 단계는 선택·TitleBox를 초기화하고, 버튼은 숨긴다.
        /// </summary>
        private void ResetPage(int index)
        {
            QuizStageView stage = GetStage(index);
            if (stage) stage.ResetState();
            SetButtonsVisible(index, false);
        }

        /// <summary>
        /// 화면의 버튼·선택지를 누를 수 없게 함.
        /// </summary>
        private void LockPage(int index)
        {
            SetButtonsInteractable(index, false);

            QuizStageView stage = GetStage(index);
            if (!stage) return;

            stage.SetChoicesInteractable(false);
            stage.LockNext();
        }

        /// <summary>
        /// 모든 화면을 끔.
        /// </summary>
        private void HideAllPages()
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                SetPageActive(i, false);
            }
        }

        /// <summary>
        /// 화면 루트의 활성 상태를 바꿈.
        /// </summary>
        private void SetPageActive(int index, bool isActive)
        {
            GameObject root = _pages[index].root;
            if (root)
            {
                root.SetActive(isActive);
            }
            else if (_logger != null)
            {
                _logger.ZLogWarning($"[Mission1SceneController] Page root is not assigned at {index}.");
            }
        }

        #endregion

        #region Button Visibility

        /// <summary>
        /// 화면의 버튼을 페이드인한 뒤 누를 수 있게 함.
        /// </summary>
        private async UniTask FadeInButtonsAsync(int index, CancellationToken cancellationToken)
        {
            CanvasGroup[] buttons = _pages[index].buttons;
            if (buttons == null || buttons.Length == 0) return;

            UniTask[] fades = new UniTask[buttons.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                CanvasGroup group = buttons[i];
                fades[i] = group
                    ? group.DOFade(1f, _buttonFadeSeconds).SetUpdate(true).ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, cancellationToken)
                    : UniTask.CompletedTask;
            }

            await UniTask.WhenAll(fades);
            SetButtonsInteractable(index, true);
        }

        /// <summary>
        /// 화면의 버튼을 즉시 보이거나 숨김. 숨길 때는 누를 수 없게 한다.
        /// </summary>
        private void SetButtonsVisible(int index, bool isVisible)
        {
            CanvasGroup[] buttons = _pages[index].buttons;
            if (buttons == null) return;

            for (int i = 0; i < buttons.Length; i++)
            {
                CanvasGroup group = buttons[i];
                if (!group) continue;

                group.DOKill();
                group.alpha = isVisible ? 1f : 0f;
            }

            SetButtonsInteractable(index, isVisible);
        }

        /// <summary>
        /// 화면의 버튼을 누를 수 있는지 바꿈.
        /// </summary>
        private void SetButtonsInteractable(int index, bool isInteractable)
        {
            CanvasGroup[] buttons = _pages[index].buttons;
            if (buttons == null) return;

            for (int i = 0; i < buttons.Length; i++)
            {
                CanvasGroup group = buttons[i];
                if (!group) continue;

                group.interactable = isInteractable;
                group.blocksRaycasts = isInteractable;
            }
        }

        /// <summary>
        /// 버튼을 누른 직후 현재 화면의 버튼을 잠가 중복 클릭을 막음.
        /// </summary>
        private void LockCurrentButtons()
        {
            if (_currentIndex >= 0) SetButtonsInteractable(_currentIndex, false);
        }

        #endregion
    }
}
