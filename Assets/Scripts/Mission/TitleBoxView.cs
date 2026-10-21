using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mission
{
    /// <summary>
    /// 미션 단계의 상단 문구 박스(TitleBox). 문구가 이미지에 들어 있어 상태마다 Sprite를 바꿔 보여 준다.
    /// <para>
    /// 기본 Sprite는 씬에 처음 적용된 이미지다. 오답·정답 문구 Sprite는 선택지마다 다르며 호출자가 넘긴다.
    /// 오답이면 페이드 없이 바로 바꾸고 가로로만 짧게 흔든다(작업계획서 7.4). 연달아 흔들면 이전 흔들림을
    /// 끝까지 진행(Complete)해 원위치로 돌린 뒤 다시 시작하므로 위치가 밀리지 않는다.
    /// </para>
    /// <para>
    /// ShowLookOf로 다른 TitleBox(예: Mission3 FailTitleImage)의 Sprite·크기·위치와 자식 문구(Text·글자 크기·위치)를
    /// 그대로 빌려 보여 줄 수 있다. ShowDefault가 씬에 처음 적용된 모습으로 되돌린다.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(Image))]
    public class TitleBoxView : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private float _shakeSeconds = 0.4f;
        [SerializeField] private float _shakeStrength = 20f;
        [SerializeField] private int _shakeVibrato = 20;

        #endregion

        #region Private Fields

        private Image _image;
        private RectTransform _rect;
        private TMP_Text _text;
        private RectTransform _textRect;
        private Sprite _defaultSprite;
        // 흔들림이 끝나면 돌아갈 위치. ShowLookOf 중에는 빌려 온 위치다.
        private Vector2 _originPosition;
        // 씬에 처음 적용된 위치·크기·문구. ShowLookOf 뒤 ShowDefault가 되돌린다.
        private Vector2 _defaultPosition;
        private Vector2 _defaultSize;
        private string _defaultText;
        private float _defaultFontSize;
        private Vector2 _defaultTextPosition;
        private Vector2 _defaultTextSize;
        private bool _isLookBorrowed;
        private Tween _shakeTween;
        private TweenCallback _onShakeComplete;
        private bool _isCached;

        #endregion

        #region Properties

        /// <summary>
        /// 흔들림 한 번의 길이(초). 오답 TTS가 없을 때 이 시간 뒤에 기본 Sprite로 되돌린다.
        /// </summary>
        public float ShakeSeconds => _shakeSeconds;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 기본 Sprite와 원위치를 기억함.
        /// </summary>
        private void Awake()
        {
            CacheDefaults();
        }

        /// <summary>
        /// 화면이 꺼질 때 흔들림을 원위치에서 끝냄.
        /// </summary>
        private void OnDisable()
        {
            StopShake();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 기본(안내 문구) Sprite로 되돌리고 흔들림을 멈춤.
        /// </summary>
        public void ShowDefault()
        {
            CacheDefaults();
            StopShake();
            _image.sprite = _defaultSprite;

            if (!_isLookBorrowed) return;

            _isLookBorrowed = false;
            ApplyLook(_defaultPosition, _defaultSize, _defaultText, _defaultFontSize, _defaultTextPosition, _defaultTextSize);
        }

        /// <summary>
        /// source의 씬 모습(Sprite·크기·위치, 자식 문구의 Text·글자 크기·위치)을 그대로 빌려 보여 줌. shake가 true면 가로로 흔든다.
        /// 두 TitleBox의 부모가 같은 배치(전체 화면 루트)라고 보고 anchoredPosition을 그대로 옮긴다.
        /// </summary>
        public void ShowLookOf(TitleBoxView source, bool shake)
        {
            if (!source) return;

            CacheDefaults();
            source.CacheDefaults();
            StopShake();

            _isLookBorrowed = true;
            _image.sprite = source._defaultSprite;
            ApplyLook(source._defaultPosition, source._defaultSize, source._defaultText, source._defaultFontSize,
                source._defaultTextPosition, source._defaultTextSize);

            if (shake) Shake();
        }

        /// <summary>
        /// 피드백 문구 Sprite로 바로 바꿈. 비어 있으면 Sprite는 그대로 둔다(호출자가 이미 바꾼 경우). shake가 true면 가로로 흔든다.
        /// </summary>
        public void ShowFeedback(Sprite sprite, bool shake)
        {
            CacheDefaults();
            if (sprite) _image.sprite = sprite;
            if (shake) Shake();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 처음 한 번 이미지·위치·기본 Sprite를 기억함. 꺼진 화면에서 Awake보다 먼저 불릴 수 있어 따로 둔다.
        /// </summary>
        private void CacheDefaults()
        {
            if (_isCached) return;

            _image = GetComponent<Image>();
            _rect = (RectTransform)transform;
            _defaultSprite = _image.sprite;
            _originPosition = _rect.anchoredPosition;
            _defaultPosition = _rect.anchoredPosition;
            _defaultSize = _rect.sizeDelta;

            _text = GetComponentInChildren<TMP_Text>(true);
            if (_text)
            {
                _textRect = _text.rectTransform;
                _defaultText = _text.text;
                _defaultFontSize = _text.fontSize;
                _defaultTextPosition = _textRect.anchoredPosition;
                _defaultTextSize = _textRect.sizeDelta;
            }

            _onShakeComplete = ResetPosition;
            _isCached = true;
        }

        /// <summary>
        /// 위치·크기와 자식 문구를 바꾸고 흔들림 원위치도 새 위치로 맞춤. 문구가 없으면 문구는 그대로 둔다.
        /// </summary>
        private void ApplyLook(Vector2 position, Vector2 size, string text, float fontSize, Vector2 textPosition, Vector2 textSize)
        {
            _originPosition = position;
            _rect.anchoredPosition = position;
            _rect.sizeDelta = size;

            if (!_text || text == null) return;

            _text.text = text;
            _text.fontSize = fontSize;
            _textRect.anchoredPosition = textPosition;
            _textRect.sizeDelta = textSize;
        }

        /// <summary>
        /// 이전 흔들림을 원위치에서 끝내고 가로 방향으로만 새로 흔듦.
        /// </summary>
        private void Shake()
        {
            StopShake();
            _shakeTween = _rect
                .DOShakeAnchorPos(_shakeSeconds, new Vector2(_shakeStrength, 0f), _shakeVibrato, 0f, false, true)
                .SetUpdate(true)
                .OnComplete(_onShakeComplete);
        }

        /// <summary>
        /// 진행 중인 흔들림을 끝내고 원위치로 돌림.
        /// </summary>
        private void StopShake()
        {
            if (_shakeTween != null && _shakeTween.IsActive()) _shakeTween.Complete();
            _shakeTween = null;
            ResetPosition();
        }

        /// <summary>
        /// 원위치로 돌림.
        /// </summary>
        private void ResetPosition()
        {
            if (_isCached) _rect.anchoredPosition = _originPosition;
        }

        #endregion
    }
}
