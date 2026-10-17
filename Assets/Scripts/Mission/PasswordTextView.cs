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
    /// 암호 사이 공백을 벌리는 방식.
    /// </summary>
    public enum PasswordSplitMode
    {
        // 공백이 앞에서부터 한 칸씩 차례로 벌어진다.
        Sequential,
        // 모든 공백이 동시에 벌어진다.
        Simultaneous
    }

    /// <summary>
    /// 미션3 집결지 암호(PasswordImage/PasswordText). 암호 문자열 하나를 텍스트 하나에 표시하되,
    /// _charsPerGroup(4)글자씩 묶어 묶음마다 텍스트 왼쪽에서 _groupPitch(px)씩 떨어진 자리에 쓴다(TMP pos 태그, 왼쪽 정렬 기준).
    /// <para>
    /// PlayTypingAsync는 한 글자씩 써 나가며, 한 묶음이 4글자로 차면 다음 묶음 자리에 이어 쓴다.
    /// PlaySplitAsync는 _splitAfterIndices(암호 문자열 인덱스) 뒤마다 공백 하나(_splitSpaceEm)를 0에서부터 벌려 넣어 뒤 글자를 민다
    /// (1330143915390022 → 13 30 14 39 15 39 0022). 묶음 시작 위치는 pos 태그로 고정이라 미는 범위는 그 묶음 안이다.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(TMP_Text))]
    public class PasswordTextView : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private string _password = "1330143915390022";
        [SerializeField, Min(1)] private int _charsPerGroup = 4;
        // 묶음 시작 위치 간격(px). 텍스트 왼쪽에서 묶음 i는 i × _groupPitch에서 시작한다.
        [SerializeField, Min(0f)] private float _groupPitch = 200f;
        // 글자 하나를 쓰는 간격(초). 0이면 한 번에 쓴다.
        [SerializeField, Min(0f)] private float _charIntervalSeconds = 0.08f;

        [Header("공백 나누기")]
        // 이 인덱스의 글자 뒤에 공백을 넣는다.
        [SerializeField] private int[] _splitAfterIndices = { 1, 3, 5, 7, 9, 11 };
        // 공백 하나의 폭(글자 크기 배율). 일반 공백은 약 0.25em.
        [SerializeField, Min(0f)] private float _splitSpaceEm = 0.25f;
        [SerializeField] private PasswordSplitMode _splitMode = PasswordSplitMode.Sequential;
        // 공백 하나가 다 벌어지는 시간(초).
        [SerializeField, Min(0f)] private float _splitSeconds = 0.3f;
        // Sequential에서 다음 공백이 벌어지기 시작하기까지의 간격(초).
        [SerializeField, Min(0f)] private float _splitIntervalSeconds = 0.15f;
        [SerializeField] private Ease _splitEase = Ease.OutQuad;

        #endregion

        #region Private Fields

        private TMP_Text _text;
        // 글자 인덱스별 뒤에 붙는 공백의 벌어진 정도(0–1). _splitAfterIndices에 없는 글자는 늘 0.
        private float[] _gapProgress = Array.Empty<float>();
        private int _visibleCount;
        private Sequence _splitSequence;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 진행 중인 공백 연출을 멈춤.
        /// </summary>
        private void OnDestroy()
        {
            KillSplit();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 텍스트를 비우고 공백을 닫음.
        /// </summary>
        public void ResetState()
        {
            KillSplit();
            ClearGaps();
            WriteUpTo(0);
        }

        /// <summary>
        /// 공백 없이 암호를 한 번에 모두 씀.
        /// </summary>
        public void ShowAll()
        {
            KillSplit();
            ClearGaps();
            WriteUpTo(PasswordLength);
        }

        /// <summary>
        /// 암호를 한 글자씩 씀. 취소되면 그때까지 쓴 상태에 둔다.
        /// </summary>
        public async UniTask PlayTypingAsync(CancellationToken cancellationToken)
        {
            KillSplit();
            ClearGaps();

            int length = PasswordLength;
            if (_charIntervalSeconds <= 0f)
            {
                WriteUpTo(length);
                return;
            }

            WriteUpTo(0);
            for (int count = 1; count <= length; count++)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(_charIntervalSeconds), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, cancellationToken);
                WriteUpTo(count);
            }
        }

        /// <summary>
        /// 암호를 모두 보인 채 _splitAfterIndices 뒤 공백을 벌림. _splitMode에 따라 차례로 또는 동시에.
        /// 취소되면 트윈을 멈추고 그 상태에 둔다.
        /// </summary>
        public async UniTask PlaySplitAsync(CancellationToken cancellationToken)
        {
            KillSplit();
            ClearGaps();
            WriteUpTo(PasswordLength);

            Sequence sequence = DOTween.Sequence().SetUpdate(true);
            int order = 0;
            for (int i = 0; i < _splitAfterIndices.Length; i++)
            {
                int index = _splitAfterIndices[i];
                if (index < 0 || index >= _gapProgress.Length - 1) continue;

                float start = _splitMode == PasswordSplitMode.Sequential ? order * _splitIntervalSeconds : 0f;
                sequence = sequence.Insert(start, DOTween.To(() => _gapProgress[index], value => SetGap(index, value), 1f, _splitSeconds).SetEase(_splitEase));
                order++;
            }

            _splitSequence = sequence;
            await sequence.ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, cancellationToken);
            _splitSequence = null;
        }

        #endregion

        #region Private Methods

        private int PasswordLength => _password != null ? _password.Length : 0;

        /// <summary>
        /// 공백 하나의 벌어진 정도를 바꾸고 다시 씀.
        /// </summary>
        private void SetGap(int index, float progress)
        {
            _gapProgress[index] = progress;
            WriteUpTo(_visibleCount);
        }

        /// <summary>
        /// 모든 공백을 닫음. 암호 길이가 바뀌었으면 배열을 다시 만든다.
        /// </summary>
        private void ClearGaps()
        {
            if (_gapProgress.Length != PasswordLength) _gapProgress = new float[PasswordLength];
            Array.Clear(_gapProgress, 0, _gapProgress.Length);
        }

        /// <summary>
        /// 암호의 앞 count글자를 묶음 단위로 자리를 잡아 씀. 벌어진 공백은 글자 크기 기준 px 폭의 space 태그로 넣는다.
        /// </summary>
        private void WriteUpTo(int count)
        {
            if (!_text) _text = GetComponent<TMP_Text>();
            if (_gapProgress.Length != PasswordLength) ClearGaps();

            _visibleCount = count;
            float spacePx = _splitSpaceEm * _text.fontSize;

            using (Utf16ValueStringBuilder builder = ZString.CreateStringBuilder())
            {
                for (int i = 0; i < count; i++)
                {
                    if (i > 0 && i % _charsPerGroup == 0)
                    {
                        builder.Append("<pos=");
                        builder.Append(Mathf.RoundToInt(i / _charsPerGroup * _groupPitch));
                        builder.Append("px>");
                    }

                    builder.Append(_password[i]);

                    int gapPx = Mathf.RoundToInt(_gapProgress[i] * spacePx);
                    if (gapPx > 0)
                    {
                        builder.Append("<space=");
                        builder.Append(gapPx);
                        builder.Append("px>");
                    }
                }

                _text.text = builder.ToString();
            }
        }

        /// <summary>
        /// 진행 중인 공백 연출을 멈춤.
        /// </summary>
        private void KillSplit()
        {
            if (_splitSequence != null && _splitSequence.IsActive()) _splitSequence.Kill();
            _splitSequence = null;
        }

        #endregion
    }
}
