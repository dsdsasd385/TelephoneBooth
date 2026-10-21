using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace Mission
{
    /// <summary>
    /// 이미지를 계속 깜빡이듯 페이드 아웃·인 반복하는 연출(Mission3 SelectScreen/Pointer).
    /// <para>
    /// 씬에서 정한 색의 알파(최대)와 _minAlpha 사이를 _fadeSeconds씩 오간다(Yoyo). 오브젝트가 켜지면 _minAlpha에서 시작해
    /// 페이드인하며 나타나고, 꺼지면 멈춘 뒤 최대 알파로 되돌린다. Time.timeScale의 영향을 받지 않는다.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(Graphic))]
    public class LoopFadeView : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField, Range(0f, 1f)] private float _minAlpha;
        // 한 방향(최대 → 최소 또는 최소 → 최대) 페이드 시간(초).
        [SerializeField, Min(0.01f)] private float _fadeSeconds = 1f;
        [SerializeField] private Ease _ease = Ease.InOutSine;

        #endregion

        #region Private Fields

        private Graphic _graphic;
        private float _maxAlpha = 1f;
        private Tween _tween;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 씬에서 정한 알파를 최대 알파로 기억함.
        /// </summary>
        private void Awake()
        {
            _graphic = GetComponent<Graphic>();
            _maxAlpha = _graphic.color.a;
        }

        private void OnEnable()
        {
            Play();
        }

        private void OnDisable()
        {
            Stop();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 최소 알파에서 시작해 최대 알파와 오가는 반복 트윈을 재생함. 켜지는 순간 갑자기 보이지 않고 페이드인한다.
        /// </summary>
        private void Play()
        {
            Stop();

            Color color = _graphic.color;
            color.a = _minAlpha;
            _graphic.color = color;

            _tween = _graphic
                .DOFade(_maxAlpha, _fadeSeconds)
                .SetEase(_ease)
                .SetLoops(-1, LoopType.Yoyo)
                .SetUpdate(true);
        }

        /// <summary>
        /// 트윈을 멈추고 최대 알파로 되돌림.
        /// </summary>
        private void Stop()
        {
            if (_tween != null)
            {
                _tween.Kill();
                _tween = null;
            }

            Color color = _graphic.color;
            color.a = _maxAlpha;
            _graphic.color = color;
        }

        #endregion
    }
}
