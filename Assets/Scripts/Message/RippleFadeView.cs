using System;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace Message
{
    /// <summary>
    /// 이미지 여러 장을 안쪽부터 차례로 나타났다 사라지게 해 물결이 퍼지는 것처럼 보이게 하는 연출(CheckScreen 전화기 아래 원 1·2·3).
    /// <para>
    /// 한 주기: i번째 이미지가 i × _stepSeconds 뒤에 _fadeInSeconds 동안 나타나고 _fadeOutSeconds 동안 사라진다.
    /// 마지막 이미지가 사라지고 _cycleGapSeconds를 쉰 뒤 처음부터 반복한다. 최대 알파는 씬에서 정한 각 이미지 색의 알파다.
    /// 오브젝트가 켜지면 시작하고 꺼지면 멈춘 뒤 모두 투명하게 되돌린다. Time.timeScale의 영향을 받지 않는다.
    /// </para>
    /// </summary>
    public class RippleFadeView : MonoBehaviour
    {
        #region Serialized Fields

        // 안쪽(작은 원)부터 바깥쪽 순서로 연결한다.
        [SerializeField] private Graphic[] _rings = Array.Empty<Graphic>();
        [SerializeField] private float _stepSeconds = 0.35f;
        [SerializeField] private float _fadeInSeconds = 0.35f;
        [SerializeField] private float _fadeOutSeconds = 0.7f;
        [SerializeField] private float _cycleGapSeconds = 0.3f;

        #endregion

        #region Private Fields

        private float[] _maxAlphas = Array.Empty<float>();
        private Sequence _sequence;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 씬에서 정한 각 이미지의 알파를 최대 알파로 기억함.
        /// </summary>
        private void Awake()
        {
            _maxAlphas = new float[_rings.Length];
            for (int i = 0; i < _rings.Length; i++)
            {
                if (!_rings[i])
                {
                    Debug.LogError($"[RippleFadeView] {name}: ring {i} is not assigned.");
                    continue;
                }

                _maxAlphas[i] = _rings[i].color.a;
            }
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
        /// 모든 이미지를 투명하게 한 뒤 반복 시퀀스를 만들어 재생함.
        /// </summary>
        private void Play()
        {
            Stop();

            _sequence = DOTween.Sequence().SetUpdate(true).SetLoops(-1, LoopType.Restart);

            float cycleEnd = 0f;
            for (int i = 0; i < _rings.Length; i++)
            {
                Graphic ring = _rings[i];
                if (!ring) continue;

                float start = i * _stepSeconds;
                _sequence.Insert(start, ring.DOFade(_maxAlphas[i], _fadeInSeconds).SetEase(Ease.OutQuad));
                _sequence.Insert(start + _fadeInSeconds, ring.DOFade(0f, _fadeOutSeconds).SetEase(Ease.InQuad));
                cycleEnd = Mathf.Max(cycleEnd, start + _fadeInSeconds + _fadeOutSeconds);
            }

            // 마지막 원이 사라진 뒤 잠깐 쉬었다가 다시 시작한다.
            _sequence.AppendInterval(Mathf.Max(0f, cycleEnd + _cycleGapSeconds - _sequence.Duration(false)));
        }

        /// <summary>
        /// 시퀀스를 멈추고 모든 이미지를 투명하게 되돌림.
        /// </summary>
        private void Stop()
        {
            if (_sequence != null)
            {
                _sequence.Kill();
                _sequence = null;
            }

            for (int i = 0; i < _rings.Length; i++)
            {
                Graphic ring = _rings[i];
                if (!ring) continue;

                Color color = ring.color;
                color.a = 0f;
                ring.color = color;
            }
        }

        #endregion
    }
}
