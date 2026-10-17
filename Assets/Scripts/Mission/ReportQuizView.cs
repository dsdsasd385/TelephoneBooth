using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using R3;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace Mission
{
    /// <summary>
    /// 보고서 문제 하나. 사진 버튼과, 그 사진이 정답인 보고서 Sprite, 그 사진을 넣은 완성 보고서(XXX_Result, 650×706) Sprite,
    /// 확대한 뒤 도장을 찍을 완성 보고서(XXX_Stemp, 1000×902) Sprite. stemp가 비어 있으면 result를 계속 쓴다.
    /// </summary>
    [Serializable]
    public struct ReportAnswer
    {
        public Button button;
        public Sprite report;
        public Sprite result;
        public Sprite stemp;
    }

    /// <summary>
    /// 미션2 화면 3(보고서 완성). 들어올 때마다 보고서 5종 중 하나를 골라 ReportImage에 띄우고,
    /// 사진 버튼을 고르면 ReportImage의 빈 칸(ChangeImage)에 그 사진을 넣고, 그때까지 숨겨 둔 선택 완료 버튼을 보여 준다.
    /// 빈 칸은 크기를 유지한 채 Sprite만 바꾼다.
    /// <para>
    /// 선택 완료를 누르면 Submitted로 정답 여부를 알린다. 오답 문구·TTS 대기는 씬 매니저가 ShowFailTitle/ShowDefaultTitle로 처리한다.
    /// 정답 연출(PlayCorrectAsync): ReportImage를 고른 사진의 XXX_Result로 교체(빈 칸·선택 완료는 숨김)하고 TitleImage·Btns 페이드아웃 →
    /// _moveDelaySeconds 뒤 XXX_Stemp로 교체하며 ReportImage 이동·크기 변경 → StempImage 도장 찍기.
    /// Result 교체는 바로 하고, Stemp 교체는 이동·크기 변경 동안 크로스페이드 이미지(_crossfadeImage)로 겹쳐 페이드한다.
    /// </para>
    /// <para>
    /// StempImage는 ReportImage의 자식이며 이동·크기 변경이 끝난 뒤에 켜므로, 위치는 커진 결과 이미지의 왼쪽 위 기준 값이다.
    /// </para>
    /// </summary>
    public class ReportQuizView : MonoBehaviour
    {
        #region Serialized Fields

        [Header("문구")]
        [SerializeField] private Image _titleImage;
        [SerializeField] private CanvasGroup _titleGroup;
        [SerializeField] private Sprite _failTitleSprite;
        // 오답일 때 TitleImage를 Mission1과 같은 방식으로 가로로 흔든다.
        [SerializeField] private TitleBoxView _titleBox;

        [Header("사진 선택")]
        [SerializeField] private CanvasGroup _choicesGroup;
        [SerializeField] private ReportAnswer[] _answers = Array.Empty<ReportAnswer>();

        [Header("보고서")]
        [SerializeField] private Image _reportImage;
        [SerializeField] private CanvasGroup _reportGroup;
        // 고른 사진을 넣는 보고서의 빈 칸. 고르기 전에는 씬에 넣어 둔 Sprite·색(반투명 회색 칸)을 보여 준다.
        [SerializeField] private Image _slotImage;
        [SerializeField] private Button _submitButton;

        [Header("정답 연출")]
        [SerializeField] private Vector2 _resultTargetPosition = new Vector2(491f, -118f);
        [SerializeField] private Vector2 _resultTargetSize = new Vector2(1000f, 902f);
        // 결과 이미지로 바꾼 뒤 이동·크기 변경을 시작하기까지 기다리는 시간(초).
        [SerializeField, Min(0f)] private float _moveDelaySeconds = 1f;
        [SerializeField, Min(0f)] private float _hideSeconds = 0.5f;
        [SerializeField, Min(0f)] private float _resultMoveSeconds = 1f;
        [SerializeField] private Ease _resultMoveEase = Ease.OutQuad;

        [Header("도장용 이미지 크로스페이드")]
        // ReportImage 자식으로 같은 크기(꽉 채움)로 겹친 이미지. 이동·크기 변경 동안 XXX_Stemp를 여기서 페이드인한 뒤 ReportImage에 넘긴다.
        // 비어 있으면 페이드 없이 바로 바꾼다.
        [SerializeField] private Image _crossfadeImage;

        [Header("도장")]
        [SerializeField] private RectTransform _stampImage;
        [SerializeField] private CanvasGroup _stampGroup;
        // 찍기 전 크기 배율. 이 크기에서 1로 줄어들며 찍힌다.
        [SerializeField, Min(1f)] private float _stampStartScale = 2.5f;
        [SerializeField, Min(0f)] private float _stampDropSeconds = 0.25f;
        [SerializeField] private float _stampPunch = 0.12f;
        [SerializeField, Min(0f)] private float _stampPunchSeconds = 0.25f;
        // 도장이 찍히는 순간 효과음을 재생할 소스. TTS 소스와 따로 둔다.
        [SerializeField] private AudioSource _sfxSource;

        #endregion

        #region Private Fields

        private readonly Subject<bool> _submitted = new Subject<bool>();
        private IDisposable _subscriptions;
        private Sprite _defaultTitleSprite;
        private Sprite _emptySlotSprite;
        private Color _emptySlotColor;
        private RectTransform _reportRect;
        private Vector2 _reportStartPosition;
        private Vector2 _reportStartSize;
        private Sequence _sequence;
        private AudioClip _stampClip;
        private TweenCallback _onSwapToStemp;
        private TweenCallback _onBeginStempFade;
        private TweenCallback _onCommitCrossfade;
        private int _reportIndex = -1;
        private int _selectedIndex = -1;
        private bool _isLocked;
        // 오답 TTS를 기다리는 동안 잠시 입력을 막았는지.
        private bool _isBlocked;
        private bool _isCached;

        #endregion

        #region Properties

        /// <summary>
        /// 선택 완료를 누를 때마다 정답 여부를 발행함.
        /// </summary>
        public Observable<bool> Submitted => _submitted;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 씬에 넣어 둔 기본 문구·빈 칸 상태와 ReportImage 처음 위치·크기를 기억함.
        /// </summary>
        private void Awake()
        {
            CacheDefaults();
        }

        /// <summary>
        /// 구독과 연출을 정리함.
        /// </summary>
        private void OnDestroy()
        {
            KillSequence();
            _subscriptions?.Dispose();
            _submitted.Dispose();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 사진·선택 완료 버튼 클릭을 구독함. 씬 매니저가 시작할 때 한 번 부른다.
        /// </summary>
        public void Bind()
        {
            if (_subscriptions != null) return;

            DisposableBuilder builder = new DisposableBuilder();
            for (int i = 0; i < _answers.Length; i++)
            {
                Button button = _answers[i].button;
                if (!button)
                {
                    Debug.LogError($"[ReportQuizView] Answer button is not assigned at {i}.");
                    continue;
                }

                int index = i;
                button.OnClickAsObservable()
                    .Subscribe(_ => OnChoiceClicked(index))
                    .AddTo(ref builder);
            }

            if (_submitButton)
            {
                _submitButton.OnClickAsObservable()
                    .Subscribe(this, static (_, self) => self.OnSubmitClicked())
                    .AddTo(ref builder);
            }
            else
            {
                Debug.LogError("[ReportQuizView] Submit button is not assigned.");
            }

            _subscriptions = builder.Build();
        }

        /// <summary>
        /// 화면에 들어가기 전 상태로 돌림. 보고서를 무작위로 하나 고르고, 선택·연출을 모두 처음으로 되돌린다.
        /// </summary>
        public void ResetState()
        {
            CacheDefaults();
            KillSequence();

            _isLocked = false;
            _isBlocked = false;
            _selectedIndex = -1;
            _reportIndex = _answers.Length > 0 ? Random.Range(0, _answers.Length) : -1;

            ShowDefaultTitle();
            SetGroup(_titleGroup, 1f, false);
            SetGroup(_choicesGroup, 1f, true);

            if (_reportImage)
            {
                if (_reportIndex >= 0 && _answers[_reportIndex].report) _reportImage.sprite = _answers[_reportIndex].report;
                _reportRect.anchoredPosition = _reportStartPosition;
                _reportRect.sizeDelta = _reportStartSize;
            }

            SetGroup(_reportGroup, 1f, true);
            SetCrossfadeAlpha(0f);

            if (_slotImage)
            {
                _slotImage.sprite = _emptySlotSprite;
                _slotImage.color = _emptySlotColor;
                _slotImage.gameObject.SetActive(true);
            }

            // 선택 완료는 사진을 골라 빈 칸에 이미지가 들어갈 때 나타난다.
            if (_submitButton) _submitButton.gameObject.SetActive(false);

            if (_stampImage)
            {
                _stampImage.localScale = Vector3.one;
                _stampImage.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// 도장이 찍힐 때 재생할 효과음을 정함. null이면 소리 없이 찍는다.
        /// </summary>
        public void SetStampClip(AudioClip clip)
        {
            _stampClip = clip;
        }

        /// <summary>
        /// 사진·선택 완료 버튼 클릭만 막거나 다시 받음. 버튼이 비활성 색으로 바뀌지 않게 interactable은 건드리지 않는다.
        /// 오답 TTS가 끝날 때까지 다시 고르지 못하게 할 때 쓴다.
        /// </summary>
        public void SetInputBlocked(bool isBlocked)
        {
            _isBlocked = isBlocked;
            if (_choicesGroup) _choicesGroup.blocksRaycasts = !isBlocked;
            if (_reportGroup) _reportGroup.blocksRaycasts = !isBlocked;
        }

        /// <summary>
        /// TitleImage를 오답 문구로 바꾸고 흔듦.
        /// </summary>
        public void ShowFailTitle()
        {
            SetTitle(_failTitleSprite);
            if (_titleBox) _titleBox.ShowFeedback(null, true);
        }

        /// <summary>
        /// TitleImage를 씬에 넣어 둔 기본 문구로 되돌리고 흔들림을 멈춤.
        /// </summary>
        public void ShowDefaultTitle()
        {
            CacheDefaults();
            if (_titleBox) _titleBox.ShowDefault();
            SetTitle(_defaultTitleSprite);
        }

        /// <summary>
        /// 정답 연출. 입력을 잠그고 ReportImage를 고른 사진의 결과 이미지(XXX_Result)로 바로 바꾸고 TitleImage·Btns를 숨긴 뒤,
        /// _moveDelaySeconds 뒤 XXX_Stemp로 바꾸며 ReportImage를 목표 위치·크기로 옮기고 도장을 찍는다.
        /// </summary>
        public async UniTask PlayCorrectAsync(CancellationToken cancellationToken)
        {
            CacheDefaults();
            KillSequence();
            Lock();

            // Join·Append는 Sequence를 돌려주는데 DOTween Sequence가 awaitable이라 결과를 버리면 CS4014 경고가 나서 다시 받는다.
            Sequence sequence = DOTween.Sequence().SetUpdate(true);

            // 1) 보고서 → 결과 이미지 바로 교체
            Sprite stemp = _selectedIndex >= 0 ? _answers[_selectedIndex].stemp : null;
            HideSlotAndSubmit();
            SwapToResult();

            // 2) 문구·사진 버튼 페이드아웃(이동 전 대기 시간 동안)
            if (_titleGroup) sequence = sequence.Join(_titleGroup.DOFade(0f, _hideSeconds));
            if (_choicesGroup) sequence = sequence.Join(_choicesGroup.DOFade(0f, _hideSeconds));

            // 3) 대기 뒤 도장용 결과 이미지로 바꾸며 이동·크기 변경. 크로스페이드 이미지가 있으면 이동하는 동안 겹쳐 페이드한다.
            // (두 이미지의 가로세로 비율이 달라 바로 바꾸면 내용이 튀어 보인다)
            if (_crossfadeImage && _reportImage && stemp && _resultMoveSeconds > 0f)
            {
                sequence = sequence
                    .InsertCallback(_moveDelaySeconds, _onBeginStempFade)
                    .Insert(_moveDelaySeconds, _crossfadeImage.DOFade(1f, _resultMoveSeconds))
                    .InsertCallback(_moveDelaySeconds + _resultMoveSeconds, _onCommitCrossfade);
            }
            else
            {
                sequence = sequence.InsertCallback(_moveDelaySeconds, _onSwapToStemp);
            }

            if (_reportRect)
            {
                sequence = sequence
                    .Insert(_moveDelaySeconds, _reportRect.DOAnchorPos(_resultTargetPosition, _resultMoveSeconds).SetEase(_resultMoveEase))
                    .Insert(_moveDelaySeconds, _reportRect.DOSizeDelta(_resultTargetSize, _resultMoveSeconds).SetEase(_resultMoveEase));
            }

            // 4) 도장: 크게 떠 있다가 내려찍히고 살짝 튄다.
            if (_stampImage)
            {
                sequence = sequence
                    .AppendCallback(ShowStamp)
                    .Append(_stampImage.DOScale(1f, _stampDropSeconds).SetEase(Ease.InQuad));
                if (_stampGroup) sequence = sequence.Join(_stampGroup.DOFade(1f, _stampDropSeconds * 0.5f));
                sequence = sequence
                    .AppendCallback(PlayStampSound)
                    .Append(_stampImage.DOPunchScale(Vector3.one * _stampPunch, _stampPunchSeconds, 1, 0f));
            }

            _sequence = sequence;
            await sequence.ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, cancellationToken);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 사진을 고르면 빈 칸의 크기는 그대로 두고 Sprite만 그 사진으로 바꾼 뒤(반투명 색은 걷어 냄) 선택 완료 버튼을 보이게 함.
        /// </summary>
        private void OnChoiceClicked(int index)
        {
            if (_isLocked || _isBlocked) return;

            _selectedIndex = index;
            Image buttonImage = _answers[index].button ? _answers[index].button.image : null;
            if (_slotImage && buttonImage)
            {
                _slotImage.sprite = buttonImage.sprite;
                _slotImage.color = Color.white;
            }

            if (_submitButton)
            {
                _submitButton.interactable = true;
                _submitButton.gameObject.SetActive(true);
            }
        }

        /// <summary>
        /// 고른 사진이 지금 보고서의 정답인지 발행함. 사진을 고르지 않았으면 무시한다.
        /// </summary>
        private void OnSubmitClicked()
        {
            if (_isLocked || _isBlocked || _selectedIndex < 0) return;

            _submitted.OnNext(_selectedIndex == _reportIndex);
        }

        /// <summary>
        /// ReportImage를 고른 사진의 결과 이미지(XXX_Result)로 바꾸고, 결과 이미지에 이미 사진이 있어 빈 칸·선택 완료를 숨김.
        /// </summary>
        private void SwapToResult()
        {
            if (_reportImage && _selectedIndex >= 0 && _answers[_selectedIndex].result)
            {
                _reportImage.sprite = _answers[_selectedIndex].result;
            }
        }

        /// <summary>
        /// 빈 칸과 선택 완료 버튼을 숨김(완성 보고서에는 사진이 이미 들어 있다).
        /// </summary>
        private void HideSlotAndSubmit()
        {
            if (_slotImage) _slotImage.gameObject.SetActive(false);
            if (_submitButton) _submitButton.gameObject.SetActive(false);
        }

        /// <summary>
        /// 크로스페이드 이미지에 다음 Sprite를 투명하게 올림. 이어서 알파를 1로 페이드한 뒤 CommitCrossfade로 넘긴다.
        /// </summary>
        private void BeginCrossfade(Sprite sprite)
        {
            _crossfadeImage.sprite = sprite;
            SetCrossfadeAlpha(0f);
        }

        /// <summary>
        /// 도장용 결과 이미지(XXX_Stemp) 크로스페이드를 시작함. 정답 연출 Sequence의 콜백.
        /// </summary>
        private void BeginStempFade()
        {
            if (_selectedIndex >= 0) BeginCrossfade(_answers[_selectedIndex].stemp);
        }

        /// <summary>
        /// 다 나타난 크로스페이드 이미지의 Sprite를 ReportImage로 넘기고 크로스페이드 이미지를 다시 투명하게 함.
        /// </summary>
        private void CommitCrossfade()
        {
            if (_reportImage && _crossfadeImage.sprite) _reportImage.sprite = _crossfadeImage.sprite;
            SetCrossfadeAlpha(0f);
        }

        /// <summary>
        /// 크로스페이드 이미지 알파만 바꿈.
        /// </summary>
        private void SetCrossfadeAlpha(float alpha)
        {
            if (!_crossfadeImage) return;

            Color color = _crossfadeImage.color;
            color.a = alpha;
            _crossfadeImage.color = color;
        }

        /// <summary>
        /// ReportImage를 고른 사진의 도장용 결과 이미지(XXX_Stemp)로 바꿈. 없으면 XXX_Result를 그대로 둔다.
        /// </summary>
        private void SwapToStemp()
        {
            if (_reportImage && _selectedIndex >= 0 && _answers[_selectedIndex].stemp)
            {
                _reportImage.sprite = _answers[_selectedIndex].stemp;
            }
        }

        /// <summary>
        /// 사진·선택 완료 버튼을 누를 수 없게 함.
        /// </summary>
        private void Lock()
        {
            _isLocked = true;
            if (_choicesGroup)
            {
                _choicesGroup.interactable = false;
                _choicesGroup.blocksRaycasts = false;
            }

            if (_submitButton) _submitButton.interactable = false;
        }

        /// <summary>
        /// 도장을 찍기 전 크기·투명 상태로 켬.
        /// </summary>
        private void ShowStamp()
        {
            _stampImage.localScale = Vector3.one * _stampStartScale;
            if (_stampGroup) _stampGroup.alpha = 0f;
            _stampImage.gameObject.SetActive(true);
        }

        /// <summary>
        /// 도장이 종이에 닿는 순간 효과음을 재생함.
        /// </summary>
        private void PlayStampSound()
        {
            if (_sfxSource && _stampClip) _sfxSource.PlayOneShot(_stampClip);
        }

        /// <summary>
        /// TitleImage Sprite를 바꾸고 원본 크기에 맞춤. 비어 있으면 그대로 둔다.
        /// </summary>
        private void SetTitle(Sprite sprite)
        {
            if (!_titleImage || !sprite) return;

            _titleImage.sprite = sprite;
            _titleImage.SetNativeSize();
        }

        /// <summary>
        /// CanvasGroup의 투명도와 입력 가능 여부를 바로 바꿈.
        /// </summary>
        private static void SetGroup(CanvasGroup group, float alpha, bool isInteractable)
        {
            if (!group) return;

            group.DOKill();
            group.alpha = alpha;
            group.interactable = isInteractable;
            group.blocksRaycasts = isInteractable;
        }

        /// <summary>
        /// 처음 한 번 씬의 기본 상태를 기억함. 꺼진 화면에서 Awake보다 먼저 불릴 수 있어 따로 둔다.
        /// </summary>
        private void CacheDefaults()
        {
            if (_isCached) return;

            _onSwapToStemp = SwapToStemp;
            _onBeginStempFade = BeginStempFade;
            _onCommitCrossfade = CommitCrossfade;
            if (_titleImage) _defaultTitleSprite = _titleImage.sprite;
            if (_slotImage)
            {
                _emptySlotSprite = _slotImage.sprite;
                _emptySlotColor = _slotImage.color;
            }

            if (_reportImage)
            {
                _reportRect = _reportImage.rectTransform;
                _reportStartPosition = _reportRect.anchoredPosition;
                _reportStartSize = _reportRect.sizeDelta;
            }

            _isCached = true;
        }

        /// <summary>
        /// 진행 중인 정답 연출을 멈춤.
        /// </summary>
        private void KillSequence()
        {
            if (_sequence != null && _sequence.IsActive()) _sequence.Kill();
            _sequence = null;
        }

        #endregion
    }
}
