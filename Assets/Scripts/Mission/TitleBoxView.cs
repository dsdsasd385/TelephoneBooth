using DG.Tweening;
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
        private Sprite _defaultSprite;
        private Vector2 _originPosition;
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
        }

        /// <summary>
        /// 피드백 문구 Sprite로 바로 바꿈. 비어 있으면 Sprite는 그대로 둔다. shake가 true면 가로로 흔든다.
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
            _onShakeComplete = ResetPosition;
            _isCached = true;
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
