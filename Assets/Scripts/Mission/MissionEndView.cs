using System;
using System.Threading;
using Core;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using R3;
using UnityEngine;
using UnityEngine.UI;

namespace Mission
{
    /// <summary>
    /// 미션 마무리 화면(작업계획서 8장). 마무리 선택 화면(메시지 보내기 / 다른 미션)과 다른 미션 선택 화면 두 개를 맡는다.
    /// <para>
    /// ShowEndSelectAsync로 마무리 선택 화면을 켜고 버튼을 페이드인한다(5장 규칙: 보인 뒤에만 누를 수 있다).
    /// "다른 미션"을 누르면 다른 미션 선택 화면으로 바꾸고 그 버튼을 페이드인한다. 누른 버튼은 바로 잠근다.
    /// 씬 이동은 하지 않고 MessageRequested·MissionRequested로 씬 매니저에 알린다.
    /// </para>
    /// </summary>
    public class MissionEndView : MonoBehaviour
    {
        #region Serialized Fields

        [Header("마무리 선택")]
        [SerializeField] private GameObject _endSelectScreen;
        [SerializeField] private CanvasGroup _messageButton;
        [SerializeField] private CanvasGroup _otherMissionButton;

        [Header("다른 미션 선택")]
        [SerializeField] private GameObject _otherMissionScreen;
        // 현재 미션을 뺀 두 미션.
        [SerializeField] private MissionTarget[] _missionTargets = Array.Empty<MissionTarget>();

        [SerializeField, Min(0f)] private float _buttonFadeSeconds = 0.5f;

        #endregion

        #region Private Fields

        private readonly Subject<Unit> _messageRequested = new Subject<Unit>();
        private readonly Subject<ContentType> _missionRequested = new Subject<ContentType>();
        private IDisposable _subscriptions;
        private CancellationTokenSource _fadeCts;
        // 마무리 선택 화면을 열기 전·숨긴 뒤에는 잠가 둔다.
        private bool _isLocked = true;

        #endregion

        #region Properties

        /// <summary>
        /// "메시지 보내기"를 눌렀을 때 발행함.
        /// </summary>
        public Observable<Unit> MessageRequested => _messageRequested;

        /// <summary>
        /// 다른 미션 버튼을 눌렀을 때 그 미션을 발행함.
        /// </summary>
        public Observable<ContentType> MissionRequested => _missionRequested;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 구독과 진행 중인 페이드를 정리함.
        /// </summary>
        private void OnDestroy()
        {
            CancelFade();
            _subscriptions?.Dispose();
            _messageRequested.Dispose();
            _missionRequested.Dispose();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 버튼 클릭을 구독함. 씬 매니저가 시작할 때 한 번 부른다.
        /// </summary>
        public void Bind()
        {
            if (_subscriptions != null) return;

            DisposableBuilder builder = new DisposableBuilder();
            BindClick(_messageButton, OnMessageClicked, ref builder);
            BindClick(_otherMissionButton, OnOtherMissionClicked, ref builder);

            for (int i = 0; i < _missionTargets.Length; i++)
            {
                MissionTarget target = _missionTargets[i];
                if (!target.button) continue;

                target.button.OnClickAsObservable()
                    .Subscribe(_ => OnMissionClicked(target.mission))
                    .AddTo(ref builder);
            }

            _subscriptions = builder.Build();
        }

        /// <summary>
        /// 두 화면을 끄고 버튼을 숨김.
        /// </summary>
        public void Hide()
        {
            CancelFade();
            _isLocked = true;
            if (_endSelectScreen) _endSelectScreen.SetActive(false);
            if (_otherMissionScreen) _otherMissionScreen.SetActive(false);
        }

        /// <summary>
        /// 마무리 선택 화면을 켜고 버튼을 페이드인함. 페이드가 끝나야 누를 수 있다.
        /// </summary>
        public UniTask ShowEndSelectAsync(CancellationToken cancellationToken)
        {
            if (_otherMissionScreen) _otherMissionScreen.SetActive(false);
            if (_endSelectScreen) _endSelectScreen.SetActive(true);

            return FadeInAsync(cancellationToken, _messageButton, _otherMissionButton);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// "메시지 보내기": 잠그고 알림.
        /// </summary>
        private void OnMessageClicked()
        {
            if (_isLocked) return;

            Lock();
            _messageRequested.OnNext(Unit.Default);
        }

        /// <summary>
        /// "다른 미션": 잠그고 다른 미션 선택 화면으로 바꾼 뒤 그 버튼을 페이드인함.
        /// </summary>
        private void OnOtherMissionClicked()
        {
            if (_isLocked) return;

            Lock();
            if (_endSelectScreen) _endSelectScreen.SetActive(false);
            if (_otherMissionScreen) _otherMissionScreen.SetActive(true);

            CanvasGroup[] groups = new CanvasGroup[_missionTargets.Length];
            for (int i = 0; i < _missionTargets.Length; i++)
            {
                groups[i] = _missionTargets[i].button ? _missionTargets[i].button.GetComponent<CanvasGroup>() : null;
            }

            FadeInAsync(this.GetCancellationTokenOnDestroy(), groups).Forget();
        }

        /// <summary>
        /// 다른 미션 버튼: 잠그고 그 미션을 알림.
        /// </summary>
        private void OnMissionClicked(ContentType mission)
        {
            if (_isLocked) return;

            Lock();
            _missionRequested.OnNext(mission);
        }

        /// <summary>
        /// 버튼을 숨긴 채 누를 수 없게 하고 함께 페이드인한 뒤 누를 수 있게 함(그때 잠금도 푼다). 취소되면 잠긴 채 그 상태에 둔다.
        /// </summary>
        private async UniTask FadeInAsync(CancellationToken cancellationToken, params CanvasGroup[] groups)
        {
            CancelFade();
            _isLocked = true;
            _fadeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.GetCancellationTokenOnDestroy());
            CancellationToken token = _fadeCts.Token;

            for (int i = 0; i < groups.Length; i++)
            {
                SetGroup(groups[i], 0f, false);
            }

            UniTask[] fades = new UniTask[groups.Length];
            for (int i = 0; i < groups.Length; i++)
            {
                CanvasGroup group = groups[i];
                fades[i] = group
                    ? group.DOFade(1f, _buttonFadeSeconds).SetUpdate(true).ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, token)
                    : UniTask.CompletedTask;
            }

            await UniTask.WhenAll(fades);

            for (int i = 0; i < groups.Length; i++)
            {
                SetGroup(groups[i], 1f, true);
            }

            _isLocked = false;
        }

        /// <summary>
        /// 모든 버튼 입력을 막음.
        /// </summary>
        private void Lock()
        {
            _isLocked = true;
            SetInteractable(_messageButton, false);
            SetInteractable(_otherMissionButton, false);
            for (int i = 0; i < _missionTargets.Length; i++)
            {
                if (_missionTargets[i].button) _missionTargets[i].button.interactable = false;
            }
        }

        /// <summary>
        /// 버튼 그룹의 알파와 입력 가능 여부를 정함.
        /// </summary>
        private static void SetGroup(CanvasGroup group, float alpha, bool isInteractable)
        {
            if (!group) return;

            group.alpha = alpha;
            SetInteractable(group, isInteractable);
        }

        /// <summary>
        /// 그룹과 그 버튼의 입력 가능 여부를 정함. 버튼 Disabled Color는 흰색이라 잠가도 색은 그대로다.
        /// </summary>
        private static void SetInteractable(CanvasGroup group, bool isInteractable)
        {
            if (!group) return;

            group.interactable = isInteractable;
            group.blocksRaycasts = isInteractable;
            Button button = group.GetComponent<Button>();
            if (button) button.interactable = isInteractable;
        }

        /// <summary>
        /// 버튼 그룹의 버튼 클릭을 구독함.
        /// </summary>
        private void BindClick(CanvasGroup group, Action onClick, ref DisposableBuilder builder)
        {
            Button button = group ? group.GetComponent<Button>() : null;
            if (!button)
            {
                Debug.LogError($"[MissionEndView] Button is not assigned on {name}.");
                return;
            }

            button.OnClickAsObservable()
                .Subscribe(onClick, static (_, action) => action())
                .AddTo(ref builder);
        }

        /// <summary>
        /// 진행 중인 버튼 페이드를 멈춤.
        /// </summary>
        private void CancelFade()
        {
            if (_fadeCts == null) return;

            CancellationTokenSource cts = _fadeCts;
            _fadeCts = null;
            cts.Cancel();
            cts.Dispose();
        }

        #endregion
    }
}
