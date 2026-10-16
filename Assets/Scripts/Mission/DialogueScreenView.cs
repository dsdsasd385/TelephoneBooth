using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace Mission
{
    /// <summary>
    /// 미션2 대사 화면 하나(화면 1·2). TitleImage를 대사 Sprite(진실-N 1, 2, …)로 차례로 바꾼다.
    /// 연출 대사(revealLineIndex, -1이면 없음)에 들어갈 때 하단 TextImage를 페이드인하고 MoveImage를 켜서 목표 위치·크기로 옮긴다.
    /// <para>
    /// 대사 순서·TTS 대기는 씬 매니저가 맡고, 이 뷰는 화면만 바꾼다. 대사 Sprite마다 높이가 달라 바꿀 때 원본 크기에 맞춘다.
    /// MoveImage 위치는 부모(PageImage) 기준 anchoredPosition이다.
    /// </para>
    /// </summary>
    public class DialogueScreenView : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private Image _titleImage;
        // 대사 순서대로. Mission2Content.json의 이 화면 tts 순서와 같다.
        [SerializeField] private Sprite[] _lineSprites = System.Array.Empty<Sprite>();

        [Header("연출 시작 대사")]
        // 이 대사에 들어갈 때 TextImage 페이드와 MoveImage 이동을 시작한다(0부터). -1이면 연출 없음.
        [SerializeField, Min(-1)] private int _revealLineIndex = -1;

        [Header("TextImage 페이드")]
        [SerializeField] private CanvasGroup _textGroup;
        [SerializeField, Min(0f)] private float _textFadeSeconds = 1f;

        [Header("MoveImage 이동")]
        [SerializeField] private RectTransform _moveImage;
        [SerializeField] private Vector2 _moveTargetPosition = new Vector2(-596f, -205f);
        [SerializeField] private Vector2 _moveTargetSize = new Vector2(940f, 82f);
        [SerializeField, Min(0f)] private float _moveSeconds = 1f;
        [SerializeField] private Ease _moveEase = Ease.OutQuad;

        #endregion

        #region Private Fields

        private Vector2 _moveStartPosition;
        private Vector2 _moveStartSize;
        private Sequence _revealSequence;
        private bool _isCached;

        #endregion

        #region Properties

        public int LineCount => _lineSprites.Length;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// MoveImage의 처음 위치·크기를 기억함.
        /// </summary>
        private void Awake()
        {
            CacheDefaults();
        }

        /// <summary>
        /// 진행 중인 연출을 멈춤.
        /// </summary>
        private void OnDestroy()
        {
            KillReveal();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 화면에 들어가기 전 상태로 돌림. 첫 대사, TextImage 투명, MoveImage 꺼짐·처음 위치.
        /// 연출이 없는 화면은 TextImage·MoveImage가 비어 있어 건드리지 않는다.
        /// </summary>
        public void ResetState()
        {
            CacheDefaults();
            KillReveal();

            ShowLine(0);

            if (_textGroup) _textGroup.alpha = 0f;

            if (_moveImage)
            {
                _moveImage.anchoredPosition = _moveStartPosition;
                _moveImage.sizeDelta = _moveStartSize;
                _moveImage.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// TitleImage를 대사 Sprite로 바꾸고 원본 크기에 맞춤. 범위 밖이거나 비어 있으면 그대로 둔다.
        /// </summary>
        public void ShowLine(int lineIndex)
        {
            if (!_titleImage || lineIndex < 0 || lineIndex >= _lineSprites.Length) return;

            Sprite sprite = _lineSprites[lineIndex];
            if (!sprite) return;

            _titleImage.sprite = sprite;
            _titleImage.SetNativeSize();
        }

        /// <summary>
        /// 이 대사에서 TextImage 페이드·MoveImage 이동을 시작하는지 여부.
        /// </summary>
        public bool IsRevealLine(int lineIndex)
        {
            return _revealLineIndex >= 0 && lineIndex == _revealLineIndex;
        }

        /// <summary>
        /// TextImage를 페이드인하고 MoveImage를 켜서 목표 위치·크기로 옮김. 둘은 동시에 시작한다.
        /// 취소되면 트윈을 멈추고 그 상태에 둔다(ResetState가 되돌림).
        /// </summary>
        public async UniTask PlayRevealAsync(CancellationToken cancellationToken)
        {
            CacheDefaults();
            KillReveal();

            // Join은 Sequence를 돌려주는데 DOTween Sequence가 awaitable이라 결과를 버리면 CS4014 경고가 나서 다시 받는다.
            Sequence sequence = DOTween.Sequence().SetUpdate(true);

            if (_textGroup)
            {
                sequence = sequence.Join(_textGroup.DOFade(1f, _textFadeSeconds));
            }

            if (_moveImage)
            {
                _moveImage.gameObject.SetActive(true);
                sequence = sequence
                    .Join(_moveImage.DOAnchorPos(_moveTargetPosition, _moveSeconds).SetEase(_moveEase))
                    .Join(_moveImage.DOSizeDelta(_moveTargetSize, _moveSeconds).SetEase(_moveEase));
            }

            _revealSequence = sequence;
            await sequence.ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, cancellationToken);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 처음 한 번 MoveImage의 시작 위치·크기를 기억함. 꺼진 화면에서 Awake보다 먼저 불릴 수 있어 따로 둔다.
        /// </summary>
        private void CacheDefaults()
        {
            if (_isCached) return;

            if (_moveImage)
            {
                _moveStartPosition = _moveImage.anchoredPosition;
                _moveStartSize = _moveImage.sizeDelta;
            }

            _isCached = true;
        }

        /// <summary>
        /// 진행 중인 연출 트윈을 멈춤.
        /// </summary>
        private void KillReveal()
        {
            if (_revealSequence != null && _revealSequence.IsActive()) _revealSequence.Kill();
            _revealSequence = null;
        }

        #endregion
    }
}
