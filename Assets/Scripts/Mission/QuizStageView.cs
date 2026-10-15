using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using R3;
using UnityEngine;
using UnityEngine.UI;

namespace Mission
{
    /// <summary>
    /// 퀴즈 단계의 선택지 하나. 문구가 이미지에 들어 있어 고르면 TitleBox를 이 선택지의 피드백 Sprite로 바꾼다.
    /// </summary>
    [Serializable]
    public struct QuizChoice
    {
        public Button button;
        public bool isCorrect;
        // 이 선택지를 골랐을 때 TitleBox에 보여 줄 오답·정답 문구 Sprite.
        public Sprite feedbackTitle;
        // 골랐음을 표시하는 오브젝트(카드의 노란 점, 3단계 장소 이름표의 점). 처음엔 꺼 둔다.
        public GameObject selectedMark;
        // 오답일 때 버튼 이미지를 바꿀 Sprite(3단계 빨간 외곽선). 비어 있으면 바꾸지 않는다.
        public Sprite wrongSprite;
    }

    /// <summary>
    /// 미션 퀴즈 단계 한 화면(작업계획서 7.2·7.3)의 화면 요소와 상태 표시를 맡는 뷰.
    /// 판정·TTS·진행 순서는 씬 매니저가 정하고, 이 뷰는 선택지·Next 버튼 입력을 알리고 요청받은 대로 보여 준다.
    /// <para>
    /// 1·2단계(Swap Image): 고른 카드에만 선택 표시가 남는다. 3단계(_keepWrongMarks): 오답 장소는 빨간 외곽선과
    /// 선택 표시를 유지해 이미 고른 곳을 알 수 있게 한다. 화면에 다시 들어오면(ResetState) 처음 상태로 돌린다.
    /// </para>
    /// </summary>
    public class QuizStageView : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private TitleBoxView _titleBox;
        // 선택지 버튼들을 담은 그룹. 화면 TTS가 끝난 뒤 페이드인하고, 그 전에는 누를 수 없다.
        [SerializeField] private CanvasGroup _choicesGroup;
        [SerializeField] private QuizChoice[] _choices = Array.Empty<QuizChoice>();
        [SerializeField] private Button _nextButton;
        [SerializeField] private CanvasGroup _nextGroup;
        [SerializeField] private bool _keepWrongMarks;
        [SerializeField] private float _markFadeSeconds = 0.3f;

        #endregion

        #region Private Fields

        private readonly Subject<int> _choiceClicked = new Subject<int>();
        private readonly Subject<Unit> _nextClicked = new Subject<Unit>();
        private Sprite[] _originalSprites = Array.Empty<Sprite>();
        private IDisposable _buttonSubscriptions;
        private bool _isBound;

        #endregion

        #region Properties

        public TitleBoxView TitleBox => _titleBox;
        public int ChoiceCount => _choices.Length;

        /// <summary>
        /// 선택지 버튼을 누르면 그 선택지 번호를 발행함.
        /// </summary>
        public Observable<int> ChoiceClicked => _choiceClicked;

        /// <summary>
        /// Next 버튼을 누르면 발행함.
        /// </summary>
        public Observable<Unit> NextClicked => _nextClicked;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 버튼 구독과 Subject를 정리함.
        /// </summary>
        private void OnDestroy()
        {
            _buttonSubscriptions?.Dispose();
            _buttonSubscriptions = null;
            _choiceClicked.Dispose();
            _nextClicked.Dispose();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 버튼 입력을 연결하고 원래 Sprite를 기억함. 화면이 꺼져 있어도 동작하도록 씬 매니저가 시작 시 한 번 부른다.
        /// </summary>
        public void Bind()
        {
            if (_isBound) return;
            _isBound = true;

            _originalSprites = new Sprite[_choices.Length];
            DisposableBuilder builder = new DisposableBuilder();

            for (int i = 0; i < _choices.Length; i++)
            {
                Button button = _choices[i].button;
                if (!button)
                {
                    Debug.LogError($"[QuizStageView] {name}: choice {i} button is not assigned.");
                    continue;
                }

                _originalSprites[i] = button.image ? button.image.sprite : null;
                int index = i;
                button.OnClickAsObservable()
                    .Subscribe(_ => _choiceClicked.OnNext(index))
                    .AddTo(ref builder);
            }

            if (_nextButton)
            {
                _nextButton.OnClickAsObservable()
                    .Subscribe(_ => _nextClicked.OnNext(Unit.Default))
                    .AddTo(ref builder);
            }
            else
            {
                Debug.LogError($"[QuizStageView] {name}: Next button is not assigned.");
            }

            _buttonSubscriptions = builder.Build();
        }

        /// <summary>
        /// 이 선택지가 정답인지 여부.
        /// </summary>
        public bool IsCorrect(int index)
        {
            return _choices[index].isCorrect;
        }

        /// <summary>
        /// 이 선택지를 골랐을 때 보여 줄 TitleBox 문구 Sprite.
        /// </summary>
        public Sprite GetFeedbackTitle(int index)
        {
            return _choices[index].feedbackTitle;
        }

        /// <summary>
        /// 화면에 들어올 때의 처음 상태로 돌림. TitleBox 기본 문구, 선택 표시·오답 Sprite 해제, 선택지·Next 숨김.
        /// </summary>
        public void ResetState()
        {
            if (_titleBox) _titleBox.ShowDefault();

            for (int i = 0; i < _choices.Length; i++)
            {
                SetMarkVisible(i, false);
                Button button = _choices[i].button;
                if (button && button.image && i < _originalSprites.Length) button.image.sprite = _originalSprites[i];
            }

            SetGroupVisible(_choicesGroup, false);
            SetGroupVisible(_nextGroup, false);
        }

        /// <summary>
        /// 선택지를 페이드인한 뒤 누를 수 있게 함.
        /// </summary>
        public UniTask FadeInChoicesAsync(float seconds, CancellationToken cancellationToken)
        {
            return FadeInAsync(_choicesGroup, seconds, cancellationToken);
        }

        /// <summary>
        /// 선택지를 누를 수 있는지 바꿈. 보이는 상태는 그대로 둔다.
        /// </summary>
        public void SetChoicesInteractable(bool isInteractable)
        {
            SetGroupInteractable(_choicesGroup, isInteractable);
        }

        /// <summary>
        /// 고른 선택지에 선택 표시를 함. 1·2단계는 다른 선택지의 표시를 지우고, 3단계는 오답 표시를 남긴다.
        /// 오답이고 wrongSprite가 있으면 버튼 이미지를 바꾼다.
        /// </summary>
        public void MarkChoice(int index)
        {
            if (!_keepWrongMarks)
            {
                for (int i = 0; i < _choices.Length; i++)
                {
                    if (i != index) SetMarkVisible(i, false);
                }
            }

            QuizChoice choice = _choices[index];
            if (!choice.isCorrect && choice.wrongSprite && choice.button && choice.button.image)
            {
                choice.button.image.sprite = choice.wrongSprite;
            }

            if (choice.selectedMark && !choice.selectedMark.activeSelf) FadeInMark(choice.selectedMark);
        }

        /// <summary>
        /// Next 버튼을 페이드인한 뒤 누를 수 있게 함.
        /// </summary>
        public UniTask FadeInNextAsync(float seconds, CancellationToken cancellationToken)
        {
            return FadeInAsync(_nextGroup, seconds, cancellationToken);
        }

        /// <summary>
        /// Next 버튼을 누를 수 없게 함. 누른 직후 중복 클릭을 막는다.
        /// </summary>
        public void LockNext()
        {
            SetGroupInteractable(_nextGroup, false);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 선택 표시를 켜고 그래픽이 있으면 투명에서 페이드인함.
        /// </summary>
        private void FadeInMark(GameObject mark)
        {
            mark.SetActive(true);

            Graphic graphic = mark.GetComponent<Graphic>();
            if (!graphic) return;

            graphic.DOKill();
            Color color = graphic.color;
            color.a = 0f;
            graphic.color = color;
            graphic.DOFade(1f, _markFadeSeconds).SetUpdate(true);
        }

        /// <summary>
        /// 선택 표시를 켜거나 끔. 끌 때는 진행 중인 페이드를 멈추고 불투명으로 돌려 둔다.
        /// </summary>
        private void SetMarkVisible(int index, bool isVisible)
        {
            GameObject mark = _choices[index].selectedMark;
            if (!mark) return;

            if (!isVisible)
            {
                Graphic graphic = mark.GetComponent<Graphic>();
                if (graphic)
                {
                    graphic.DOKill();
                    Color color = graphic.color;
                    color.a = 1f;
                    graphic.color = color;
                }
            }

            mark.SetActive(isVisible);
        }

        /// <summary>
        /// 그룹을 페이드인한 뒤 누를 수 있게 함. 그룹이 없으면 바로 끝난다.
        /// </summary>
        private static async UniTask FadeInAsync(CanvasGroup group, float seconds, CancellationToken cancellationToken)
        {
            if (!group) return;

            await group.DOFade(1f, seconds).SetUpdate(true).ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, cancellationToken);
            SetGroupInteractable(group, true);
        }

        /// <summary>
        /// 그룹을 즉시 보이거나 숨김. 숨길 때는 누를 수 없게 한다.
        /// </summary>
        private static void SetGroupVisible(CanvasGroup group, bool isVisible)
        {
            if (!group) return;

            group.DOKill();
            group.alpha = isVisible ? 1f : 0f;
            SetGroupInteractable(group, isVisible);
        }

        /// <summary>
        /// 그룹을 누를 수 있는지 바꿈.
        /// </summary>
        private static void SetGroupInteractable(CanvasGroup group, bool isInteractable)
        {
            if (!group) return;

            group.interactable = isInteractable;
            group.blocksRaycasts = isInteractable;
        }

        #endregion
    }
}
