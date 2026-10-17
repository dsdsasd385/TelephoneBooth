using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cysharp.Text;
using DG.Tweening;
using TMPro;
using UnityEngine;

namespace Mission
{
    /// <summary>
    /// 텍스트의 글자를 앞에서부터 하나씩 페이드인함(미션3 SuccessScreen의 해독 자모 "ㄷ ㅏ ㄹ ㅣ …").
    /// 씬에 적어 둔 텍스트나 SetText로 넘긴 텍스트를 쓰며, 공백·리치 텍스트 태그는 건너뛰고 보이는 글자에만 TMP alpha 태그를 붙인다.
    /// </summary>
    [RequireComponent(typeof(TMP_Text))]
    public class FadeInTextView : MonoBehaviour
    {
        #region Constants

        private const string HexDigits = "0123456789ABCDEF";

        #endregion

        #region Serialized Fields

        // 글자 하나가 나타나기 시작한 뒤 다음 글자가 시작하기까지의 시간(초).
        [SerializeField, Min(0f)] private float _charIntervalSeconds = 0.3f;
        [SerializeField, Min(0f)] private float _charFadeSeconds = 0.4f;

        #endregion

        #region Private Fields

        private TMP_Text _text;
        // 씬에 적어 둔 원래 텍스트. 처음 한 번 기억한다.
        private string _source;
        // 글자별 알파(0–1). 공백은 쓰지 않는다.
        private float[] _alphas = Array.Empty<float>();
        // 글자별 페이드 대상 여부. 공백과 리치 텍스트 태그 안의 글자는 그대로 쓴다.
        private bool[] _isFadeChar = Array.Empty<bool>();
        private Tween _tween;
        private float _progress;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 진행 중인 연출을 멈춤.
        /// </summary>
        private void OnDestroy()
        {
            KillTween();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 표시할 텍스트를 바꾸고 모든 글자를 숨김. 리치 텍스트 태그(pos 등)를 넣을 수 있으며 태그는 페이드하지 않는다.
        /// </summary>
        public void SetText(string text)
        {
            CacheSource();
            SetSource(text);
            ResetState();
        }

        /// <summary>
        /// 모든 글자를 숨김.
        /// </summary>
        public void ResetState()
        {
            CacheSource();
            KillTween();
            Array.Clear(_alphas, 0, _alphas.Length);
            Write();
        }

        /// <summary>
        /// 보이는 글자를 하나씩 페이드인함. 취소되면 그 상태에 둔다.
        /// </summary>
        public async UniTask PlayAsync(CancellationToken cancellationToken)
        {
            ResetState();

            int visibleCount = 0;
            for (int i = 0; i < _source.Length; i++)
            {
                if (_isFadeChar[i]) visibleCount++;
            }

            if (visibleCount == 0) return;

            float total = (visibleCount - 1) * _charIntervalSeconds + _charFadeSeconds;
            _progress = 0f;
            _tween = DOTween.To(() => _progress, value => SetProgress(value, total), total, total).SetEase(Ease.Linear).SetUpdate(true);
            await _tween.ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, cancellationToken);
            _tween = null;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 경과 시간(초)에 맞춰 글자마다 알파를 정하고 다시 씀.
        /// </summary>
        private void SetProgress(float elapsed, float total)
        {
            _progress = elapsed;
            int order = 0;
            for (int i = 0; i < _source.Length; i++)
            {
                if (!_isFadeChar[i]) continue;

                float start = order * _charIntervalSeconds;
                _alphas[i] = _charFadeSeconds > 0f ? Mathf.Clamp01((elapsed - start) / _charFadeSeconds) : (elapsed >= start ? 1f : 0f);
                order++;
            }

            Write();
        }

        /// <summary>
        /// 원래 텍스트에 글자별 alpha 태그를 붙여 씀.
        /// </summary>
        private void Write()
        {
            using (Utf16ValueStringBuilder builder = ZString.CreateStringBuilder())
            {
                for (int i = 0; i < _source.Length; i++)
                {
                    char c = _source[i];
                    if (_isFadeChar[i])
                    {
                        int alpha = Mathf.RoundToInt(_alphas[i] * 255f);
                        builder.Append("<alpha=#");
                        builder.Append(HexDigits[alpha >> 4]);
                        builder.Append(HexDigits[alpha & 0xF]);
                        builder.Append('>');
                    }

                    builder.Append(c);
                }

                _text.text = builder.ToString();
            }
        }

        /// <summary>
        /// 처음 한 번 TMP와 씬에 적어 둔 텍스트를 기억함.
        /// </summary>
        private void CacheSource()
        {
            if (_source != null) return;

            _text = GetComponent<TMP_Text>();
            SetSource(_text.text);
        }

        /// <summary>
        /// 원래 텍스트를 바꾸고 글자마다 페이드 대상인지 정함. 공백과 &lt;…&gt; 태그 안의 글자는 대상이 아니다.
        /// </summary>
        private void SetSource(string text)
        {
            _source = text ?? string.Empty;
            _alphas = new float[_source.Length];
            _isFadeChar = new bool[_source.Length];

            bool inTag = false;
            for (int i = 0; i < _source.Length; i++)
            {
                char c = _source[i];
                if (c == '<') inTag = true;
                _isFadeChar[i] = !inTag && !char.IsWhiteSpace(c);
                if (c == '>') inTag = false;
            }
        }

        /// <summary>
        /// 진행 중인 연출을 멈춤.
        /// </summary>
        private void KillTween()
        {
            if (_tween != null && _tween.IsActive()) _tween.Kill();
            _tween = null;
        }

        #endregion
    }
}
