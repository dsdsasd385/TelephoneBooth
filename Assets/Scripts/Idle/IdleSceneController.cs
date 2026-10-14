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
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using VContainer;
using HuliacDev.App;
using HuliacDev.Utils;
using ZLogger;

namespace Idle
{
    /// <summary>
    /// 0_Idle 화면 한 장의 루트 오브젝트, 영상을 띄울 RawImage, TTS가 끝난 뒤 페이드인할 버튼들.
    /// </summary>
    [Serializable]
    public struct IdleScreenView
    {
        public GameObject root;
        public RawImage videoSurface;
        public CanvasGroup[] buttons;
    }

    /// <summary>
    /// 0_Idle 씬의 흐름을 관리하는 씬 매니저.
    /// IdlePages.json으로 화면별 영상·TTS를 적용하고, 수화기와 버튼 입력에 따라 화면을 바꾼다.
    /// <para>
    /// 수화기 규칙(작업계획서 4.3): ① 대기에서 들면 ② 나이 선택으로 가고, ②–⑥에서 내려놓으면
    /// MoveIdleEvent를 발행하고 ① 대기로 즉시 돌아간다.
    /// </para>
    /// <para>
    /// 페이지 규칙(작업계획서 5·6장): 화면에 들어가면 버튼을 숨기고, TTS가 끝나면 페이드인한 뒤에야 누를 수 있다.
    /// 버튼이 없는 ⑤는 TTS가 끝나고 _autoAdvanceDelaySeconds만큼 기다린 뒤 ⑥으로 자동 진행한다. ② 나이 선택은 난이도를, ⑥은 콘텐츠를 ExperienceSession에 기록한다.
    /// </para>
    /// <para>
    /// 화면 교체: ShowScreen은 영상 준비만 요청하고, 새 영상의 첫 프레임이 나왔다는 PageShown을 받은 순간
    /// 이전 화면을 끄고 새 화면을 켠다. 준비하는 동안 이전 화면이 그대로 보여 전환 사이에 검은 틈이 생기지 않는다.
    /// _currentIndex는 요청한(논리상 현재) 화면, _visibleIndex는 실제로 켜져 있는 화면이다.
    /// </para>
    /// </summary>
    public class IdleSceneController : MonoBehaviour
    {
        #region Constants

        private const int WaitingScreenIndex = 0;
        private const int AgeSelectScreenIndex = 1;
        private const int AutoAdvanceScreenIndex = 4;
        private const int ContentSelectScreenIndex = 5;

        #endregion

        #region Serialized Fields

        [SerializeField] private PageMediaPlayer _mediaPlayer;
        [SerializeField] private IdleScreenView[] _screens = Array.Empty<IdleScreenView>();
        [SerializeField] private float _buttonFadeSeconds = 0.5f;
        // ⑤의 TTS가 끝난 뒤 ⑥으로 자동 진행하기 전에 기다리는 시간(초). 0이면 바로 넘어간다.
        [SerializeField, Min(0f)] private float _autoAdvanceDelaySeconds = 1f;

        [Header("② 나이 선택")]
        [SerializeField] private Button _under14Button;
        [SerializeField] private Button _over14Button;

        [Header("③·④ 다음")]
        [SerializeField] private Button[] _nextButtons = Array.Empty<Button>();

        [Header("⑥ 콘텐츠 선택")]
        [SerializeField] private Button _cultureButton;
        [SerializeField] private Button _truthButton;
        [SerializeField] private Button _nationButton;

        #endregion

        #region Private Fields

        private ILogger<IdleSceneController> _logger;
        private IReceiverInput _receiverInput;
        private IPublisher<MoveIdleEvent> _moveIdlePublisher;
        private ExperienceSession _session;
        private ProjectInputActions _inputActions;
        private Action<InputAction.CallbackContext> _onToggleDoubleBuffering;
        private Action<InputAction.CallbackContext> _onNextScreenKey;
        private Action<InputAction.CallbackContext> _onPreviousScreenKey;
        private IDisposable _receiverSubscription;
        private IDisposable _pageShownSubscription;
        private CancellationTokenSource _screenCts;
        private Func<bool> _isTtsFinished;
        private bool _isInitialized;
        private int _currentIndex = -1;
        private int _visibleIndex = -1;

        private UnityAction _onUnder14Clicked;
        private UnityAction _onOver14Clicked;
        private UnityAction _onNextClicked;
        private UnityAction _onCultureClicked;
        private UnityAction _onTruthClicked;
        private UnityAction _onNationClicked;

        #endregion

        #region Properties

        public int CurrentIndex => _currentIndex;
        public int ScreenCount => _screens.Length;

        #endregion

        #region Dependency Injection & Unity Lifecycle

        /// <summary>
        /// VContainer 의존성 주입으로 로거, 수화기 입력, 대기 화면 복귀 이벤트 발행자, 체험 세션, 입력 액션을 할당함.
        /// </summary>
        [Inject]
        public void Construct(ILogger<IdleSceneController> logger, IReceiverInput receiverInput,
            IPublisher<MoveIdleEvent> moveIdlePublisher, ExperienceSession session, ProjectInputActions inputActions)
        {
            _logger = logger;
            _receiverInput = receiverInput;
            _moveIdlePublisher = moveIdlePublisher;
            _session = session;
            _inputActions = inputActions;
        }

        /// <summary>
        /// 주입이 끝난 뒤 버튼과 수화기 입력을 연결하고 페이지 데이터를 읽어 첫 화면을 띄움.
        /// </summary>
        private void Start()
        {
            if (_logger == null || _receiverInput == null || _moveIdlePublisher == null || _session == null)
            {
                Debug.LogError("[IdleSceneController] Dependencies were not injected. Check that IdleLifetimeScope registers IdleSceneController and GameLifetimeScope registers IReceiverInput and ExperienceSession.");
            }

            _isTtsFinished = IsTtsFinished;
            HideAllScreens();
            BindButtons();

            if (_receiverInput != null)
            {
                // ReactiveProperty는 구독 즉시 현재 값을 한 번 보내므로 건너뛰고, 바뀌는 순간만 받는다.
                _receiverSubscription = _receiverInput.IsLifted
                    .Skip(1)
                    .Subscribe(this, static (isLifted, self) => self.OnReceiverStateChanged(isLifted));
            }

            if (_mediaPlayer)
            {
                _pageShownSubscription = _mediaPlayer.PageShown
                    .Subscribe(this, static (pageIndex, self) => self.OnPageShown(pageIndex));
            }

            BindDebugInput();
            InitializeAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        /// <summary>
        /// 버튼·수화기·디버그 입력 구독을 해제하고 진행 중인 화면 흐름을 취소함.
        /// </summary>
        private void OnDestroy()
        {
            UnbindDebugInput();
            UnbindButtons();
            CancelScreenFlow();
            _receiverSubscription?.Dispose();
            _receiverSubscription = null;
            _pageShownSubscription?.Dispose();
            _pageShownSubscription = null;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// IdlePages.json을 읽어 화면 순서와 맞는지 확인하고 재생기에 넘긴 뒤 첫 화면을 보여 줌.
        /// </summary>
        private async UniTaskVoid InitializeAsync(CancellationToken cancellationToken)
        {
            if (!_mediaPlayer)
            {
                if (_logger != null) _logger.ZLogError($"[IdleSceneController] PageMediaPlayer is not assigned.");
                return;
            }

            try
            {
                MediaSettings mediaSettings = await JsonLoader.LoadAsync<MediaSettings>(Constants.Files.MediaSettings, cancellationToken, _logger);
                _mediaPlayer.SetDoubleBuffering(mediaSettings.doubleBuffering);

                PageMediaTable table = await JsonLoader.LoadAsync<PageMediaTable>(Constants.Files.IdlePages, cancellationToken, _logger);
                ValidateScreenOrder(table.pages);
                await _mediaPlayer.LoadAsync(table.pages, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _isInitialized = true;
            ShowScreen(WaitingScreenIndex);

            if (_receiverInput != null && _receiverInput.IsLifted.CurrentValue && _logger != null)
            {
                _logger.ZLogWarning($"[IdleSceneController] Receiver is already lifted on waiting screen. Hang up first (put-down guide UI is not implemented yet).");
            }
        }

        /// <summary>
        /// JSON의 screen 이름과 씬 화면 순서가 다르면 경고를 남김. 잘못된 리소스 번호를 일찍 찾기 위함.
        /// </summary>
        private void ValidateScreenOrder(PageMediaEntry[] pages)
        {
            if (_logger == null) return;

            if (pages.Length != _screens.Length)
            {
                _logger.ZLogWarning($"[IdleSceneController] Page count mismatch. json={pages.Length}, scene={_screens.Length}");
            }

            int count = Mathf.Min(pages.Length, _screens.Length);
            for (int i = 0; i < count; i++)
            {
                GameObject root = _screens[i].root;
                if (!root || root.name != pages[i].screen)
                {
                    _logger.ZLogWarning($"[IdleSceneController] Screen order mismatch at {i}. json={pages[i].screen}, scene={(root ? root.name : "null")}");
                }
            }
        }

        #endregion

        #region Debug Input

        /// <summary>
        /// 에디터·개발 빌드에서만 디버그 키를 연결함. F8은 더블 버퍼링 켜기/끄기(두 방식을 눈으로 비교),
        /// Ctrl+→/←는 다음·이전 화면(수화기·버튼 없이 화면 확인).
        /// </summary>
        private void BindDebugInput()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_inputActions == null) return;

            _onToggleDoubleBuffering = OnToggleDoubleBuffering;
            _onNextScreenKey = OnNextScreenKey;
            _onPreviousScreenKey = OnPreviousScreenKey;
            _inputActions.Debug.ToggleDoubleBuffering.performed += _onToggleDoubleBuffering;
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
            if (_inputActions == null || _onToggleDoubleBuffering == null) return;

            _inputActions.Debug.ToggleDoubleBuffering.performed -= _onToggleDoubleBuffering;
            _inputActions.Debug.NextScreen.performed -= _onNextScreenKey;
            _inputActions.Debug.PreviousScreen.performed -= _onPreviousScreenKey;
            _inputActions.Debug.Disable();
            _onToggleDoubleBuffering = null;
            _onNextScreenKey = null;
            _onPreviousScreenKey = null;
        }

        /// <summary>
        /// Ctrl+→를 누르면 다음 화면으로 감. 마지막 화면이면 첫 화면으로.
        /// </summary>
        private void OnNextScreenKey(InputAction.CallbackContext context)
        {
            if (!_isInitialized) return;

            int next = (_currentIndex + 1) % _screens.Length;
            if (_logger != null) _logger.ZLogInformation($"[IdleSceneController] Debug key: next screen {_currentIndex} -> {next}");
            ShowScreen(next);
        }

        /// <summary>
        /// Ctrl+←를 누르면 이전 화면으로 감. 첫 화면이면 마지막 화면으로.
        /// </summary>
        private void OnPreviousScreenKey(InputAction.CallbackContext context)
        {
            if (!_isInitialized) return;

            int previous = (_currentIndex - 1 + _screens.Length) % _screens.Length;
            if (_logger != null) _logger.ZLogInformation($"[IdleSceneController] Debug key: previous screen {_currentIndex} -> {previous}");
            ShowScreen(previous);
        }

        /// <summary>
        /// F8을 누르면 더블 버퍼링을 반대로 바꿈.
        /// </summary>
        private void OnToggleDoubleBuffering(InputAction.CallbackContext context)
        {
            if (!_mediaPlayer) return;
            _mediaPlayer.SetDoubleBuffering(!_mediaPlayer.IsDoubleBuffering);
        }

        #endregion

        #region Receiver Input

        /// <summary>
        /// 수화기 상태가 바뀌면 들기·내려놓기 규칙에 맞춰 화면을 바꿈.
        /// </summary>
        private void OnReceiverStateChanged(bool isLifted)
        {
            if (!_isInitialized)
            {
                if (_logger != null) _logger.ZLogWarning($"[IdleSceneController] Receiver state changed before page data was loaded. Ignored. lifted={isLifted}");
                return;
            }

            if (isLifted)
            {
                OnReceiverLifted();
            }
            else
            {
                OnReceiverHungUp();
            }
        }

        /// <summary>
        /// ① 대기에서 수화기를 들면 ② 나이 선택으로 진행함.
        /// </summary>
        private void OnReceiverLifted()
        {
            if (_currentIndex != WaitingScreenIndex)
            {
                if (_logger != null) _logger.ZLogWarning($"[IdleSceneController] Receiver lifted on screen {_currentIndex}, not waiting screen. Ignored.");
                return;
            }

            if (_logger != null) _logger.ZLogInformation($"[IdleSceneController] Receiver lifted on waiting screen. Moving to age selection.");
            ShowScreen(AgeSelectScreenIndex);
        }

        /// <summary>
        /// ②–⑥에서 수화기를 내려놓으면 대기 화면 복귀 이벤트를 발행하고 ① 대기로 즉시 돌아감.
        /// </summary>
        private void OnReceiverHungUp()
        {
            if (_currentIndex == WaitingScreenIndex) return;

            if (_logger != null) _logger.ZLogInformation($"[IdleSceneController] Receiver hung up on screen {_currentIndex}. Returning to waiting screen.");
            if (_moveIdlePublisher != null) _moveIdlePublisher.Publish(new MoveIdleEvent());
            ShowScreen(WaitingScreenIndex);
        }

        #endregion

        #region Buttons

        /// <summary>
        /// 버튼 클릭 핸들러를 한 번만 만들어 각 버튼에 연결함.
        /// </summary>
        private void BindButtons()
        {
            _onUnder14Clicked = OnUnder14Clicked;
            _onOver14Clicked = OnOver14Clicked;
            _onNextClicked = OnNextClicked;
            _onCultureClicked = OnCultureClicked;
            _onTruthClicked = OnTruthClicked;
            _onNationClicked = OnNationClicked;

            AddListener(_under14Button, _onUnder14Clicked, nameof(_under14Button));
            AddListener(_over14Button, _onOver14Clicked, nameof(_over14Button));
            for (int i = 0; i < _nextButtons.Length; i++)
            {
                AddListener(_nextButtons[i], _onNextClicked, nameof(_nextButtons));
            }
            AddListener(_cultureButton, _onCultureClicked, nameof(_cultureButton));
            AddListener(_truthButton, _onTruthClicked, nameof(_truthButton));
            AddListener(_nationButton, _onNationClicked, nameof(_nationButton));
        }

        /// <summary>
        /// 연결한 버튼 클릭 핸들러를 모두 해제함.
        /// </summary>
        private void UnbindButtons()
        {
            RemoveListener(_under14Button, _onUnder14Clicked);
            RemoveListener(_over14Button, _onOver14Clicked);
            for (int i = 0; i < _nextButtons.Length; i++)
            {
                RemoveListener(_nextButtons[i], _onNextClicked);
            }
            RemoveListener(_cultureButton, _onCultureClicked);
            RemoveListener(_truthButton, _onTruthClicked);
            RemoveListener(_nationButton, _onNationClicked);
        }

        /// <summary>
        /// 버튼에 클릭 핸들러를 붙임. 연결되지 않은 버튼은 오류 로그를 남김.
        /// </summary>
        private void AddListener(Button button, UnityAction action, string fieldName)
        {
            if (button)
            {
                button.onClick.AddListener(action);
            }
            else if (_logger != null)
            {
                _logger.ZLogError($"[IdleSceneController] Button is not assigned: {fieldName}");
            }
        }

        /// <summary>
        /// 버튼에서 클릭 핸들러를 뗌.
        /// </summary>
        private static void RemoveListener(Button button, UnityAction action)
        {
            if (button && action != null) button.onClick.RemoveListener(action);
        }

        /// <summary>
        /// 14세 미만을 고르면 저난이도로 기록하고 다음 화면으로 감.
        /// </summary>
        private void OnUnder14Clicked()
        {
            SelectDifficulty(Difficulty.Low);
        }

        /// <summary>
        /// 14세 이상을 고르면 고난이도로 기록하고 다음 화면으로 감.
        /// </summary>
        private void OnOver14Clicked()
        {
            SelectDifficulty(Difficulty.High);
        }

        /// <summary>
        /// ③·④의 다음 버튼으로 다음 화면으로 감.
        /// </summary>
        private void OnNextClicked()
        {
            LockCurrentButtons();
            ShowScreen(_currentIndex + 1);
        }

        /// <summary>
        /// 문화수호를 고름.
        /// </summary>
        private void OnCultureClicked()
        {
            SelectContent(ContentType.Culture);
        }

        /// <summary>
        /// 진실수호를 고름.
        /// </summary>
        private void OnTruthClicked()
        {
            SelectContent(ContentType.Truth);
        }

        /// <summary>
        /// 민족수호를 고름.
        /// </summary>
        private void OnNationClicked()
        {
            SelectContent(ContentType.Nation);
        }

        /// <summary>
        /// 난이도를 세션에 기록하고 ③으로 진행함.
        /// </summary>
        private void SelectDifficulty(Difficulty difficulty)
        {
            LockCurrentButtons();
            if (_session != null) _session.SetDifficulty(difficulty);
            if (_logger != null) _logger.ZLogInformation($"[IdleSceneController] Difficulty selected: {difficulty}");
            ShowScreen(AgeSelectScreenIndex + 1);
        }

        /// <summary>
        /// 콘텐츠를 세션에 기록함. 콘텐츠 씬은 아직 없어 이동하지 않고 ⑥에 머문다.
        /// </summary>
        private void SelectContent(ContentType content)
        {
            LockCurrentButtons();
            if (_session != null) _session.SetContent(content);
            if (_logger != null)
            {
                _logger.ZLogInformation($"[IdleSceneController] Content selected: {content} (difficulty {(_session != null ? _session.Difficulty : Difficulty.None)}). Content scenes are not created yet; staying on screen {ContentSelectScreenIndex}.");
            }
        }

        #endregion

        #region Screen Navigation

        /// <summary>
        /// 지정한 화면으로 전환을 요청함. 화면 루트는 새 영상의 첫 프레임이 나올 때(OnPageShown) 교체된다.
        /// </summary>
        public void ShowScreen(int index)
        {
            if (index < 0 || index >= _screens.Length)
            {
                if (_logger != null) _logger.ZLogWarning($"[IdleSceneController] Screen index out of range: {index}");
                return;
            }

            CancelScreenFlow();

            // 교체 전까지 이전 화면이 보이므로 그 버튼이 눌리지 않게 먼저 잠근다.
            if (_visibleIndex >= 0) SetButtonsInteractable(_visibleIndex, false);

            if (index == WaitingScreenIndex && _session != null)
            {
                _session.Reset();
            }

            _currentIndex = index;
            SetButtonsVisible(index, false);
            _mediaPlayer.Play(index, _screens[index].videoSurface);
        }

        /// <summary>
        /// 새 영상의 첫 프레임이 나온 순간 이전 화면을 끄고 새 화면을 켠 뒤, TTS 종료 대기 흐름을 시작함.
        /// </summary>
        private void OnPageShown(int pageIndex)
        {
            // 그사이 다른 화면이 요청됐으면 그 요청의 PageShown에서 교체한다.
            if (pageIndex != _currentIndex) return;

            if (_visibleIndex >= 0 && _visibleIndex != pageIndex)
            {
                SetScreenActive(_visibleIndex, false);
            }

            SetScreenActive(pageIndex, true);
            _visibleIndex = pageIndex;

            CancelScreenFlow();
            _screenCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            RunScreenFlowAsync(pageIndex, _screenCts).Forget();
        }

        /// <summary>
        /// TTS가 끝나기를 기다렸다가 버튼을 페이드인하거나, ⑤라면 다음 화면으로 넘어감.
        /// </summary>
        private async UniTaskVoid RunScreenFlowAsync(int index, CancellationTokenSource cts)
        {
            try
            {
                await UniTask.WaitUntil(_isTtsFinished, PlayerLoopTiming.Update, cts.Token);

                if (index == AutoAdvanceScreenIndex)
                {
                    // 화면 전환·오브젝트 파괴로 취소되면 넘어가지 않는다. timeScale과 무관하게 실제 시간으로 기다린다.
                    if (_autoAdvanceDelaySeconds > 0f)
                    {
                        await UniTask.Delay(TimeSpan.FromSeconds(_autoAdvanceDelaySeconds), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, cts.Token);
                    }

                    ShowScreen(index + 1);
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
                if (_screenCts == cts)
                {
                    _screenCts.Dispose();
                    _screenCts = null;
                }
            }
        }

        /// <summary>
        /// 현재 페이지의 TTS가 끝났는지 확인함. WaitUntil에 캐시된 대리자로 넘긴다.
        /// </summary>
        private bool IsTtsFinished()
        {
            return !_mediaPlayer || !_mediaPlayer.IsTtsPlaying;
        }

        /// <summary>
        /// 진행 중인 화면 흐름(TTS 대기, 버튼 페이드)을 취소함.
        /// </summary>
        private void CancelScreenFlow()
        {
            if (_screenCts == null) return;

            // Cancel()은 취소 콜백을 그 자리에서 실행한다. 버튼 페이드(DOTween) 중이면 RunScreenFlowAsync의 finally가
            // Cancel() 안에서 바로 돌아 필드를 비우므로, 필드를 먼저 떼어 낸 뒤 취소해야 이중 해제·NRE가 나지 않는다.
            CancellationTokenSource cts = _screenCts;
            _screenCts = null;
            cts.Cancel();
            cts.Dispose();
        }

        /// <summary>
        /// 모든 화면을 끔.
        /// </summary>
        private void HideAllScreens()
        {
            for (int i = 0; i < _screens.Length; i++)
            {
                SetScreenActive(i, false);
            }
        }

        /// <summary>
        /// 화면 루트의 활성 상태를 바꿈.
        /// </summary>
        private void SetScreenActive(int index, bool isActive)
        {
            GameObject root = _screens[index].root;
            if (root)
            {
                root.SetActive(isActive);
            }
            else if (_logger != null)
            {
                _logger.ZLogWarning($"[IdleSceneController] Screen root is not assigned at {index}.");
            }
        }

        #endregion

        #region Button Visibility

        /// <summary>
        /// 화면의 버튼을 페이드인한 뒤 누를 수 있게 함.
        /// </summary>
        private async UniTask FadeInButtonsAsync(int index, CancellationToken cancellationToken)
        {
            CanvasGroup[] buttons = _screens[index].buttons;
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
            CanvasGroup[] buttons = _screens[index].buttons;
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
            CanvasGroup[] buttons = _screens[index].buttons;
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
