using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cysharp.Text;
using DG.Tweening;
using TextInput;
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
    /// 미션3 집결지 암호(PasswordImage/PasswordText). 암호 문자열 하나를 텍스트 하나에 표시한다.
    /// <para>
    /// 암호는 암호표 규칙(CipherCodec)으로 묶음을 나눈다. 초성 다음에 모음이 오면 둘을 한 묶음(13 30), 받침은 혼자 한 묶음(0022)이고,
    /// 묶음 i는 텍스트 왼쪽에서 i × _groupPitch(px) 자리에서 시작한다(TMP pos 태그, 왼쪽 정렬 기준).
    /// 다리밑 1330143915390022 → [1330][1439][1539][0022], 산사이 1730001217301839 → [1730][0012][1730][1839]처럼 받침 위치가 달라도 맞는다.
    /// </para>
    /// <para>
    /// PlayTypingAsync는 한 글자씩 써 나가며 묶음이 바뀌면 다음 묶음 자리에 이어 쓴다.
    /// PlaySplitAsync는 숫자 묶음(토큰) 사이마다 공백 하나(_splitSpaceEm)를 0에서부터 벌려 넣어 뒤 글자를 민다(13 30 14 39 15 39 0022).
    /// 묶음 시작 위치는 고정이라 미는 범위는 그 묶음 안이다. 암호표로 풀리지 않는 암호는 묶음·공백 없이 그대로 쓴다.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(TMP_Text))]
    public class PasswordTextView : MonoBehaviour
    {
        #region Serialized Fields

        // 씬 매니저가 SetPassword로 바꾼다. 여기 값은 처음 상태·에디터 미리보기용.
        [SerializeField] private string _password = "1330143915390022";
        // 묶음 시작 위치 간격(px). 텍스트 왼쪽에서 묶음 i는 i × _groupPitch에서 시작한다.
        [SerializeField, Min(0f)] private float _groupPitch = 200f;
        // 글자 하나를 쓰는 간격(초). 0이면 한 번에 쓴다.
        [SerializeField, Min(0f)] private float _charIntervalSeconds = 0.08f;

        [Header("공백 나누기")]
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
        // 암호표로 끊은 숫자 묶음. 풀리지 않으면 비어 있다.
        private readonly List<CipherToken> _tokens = new List<CipherToken>();
        // 글자 인덱스별 묶음 번호(pos 자리). 이 값이 앞 글자와 달라지는 곳에 pos 태그를 넣는다.
        private int[] _groupOfChar = Array.Empty<int>();
        // 글자 인덱스별 뒤에 붙는 공백의 벌어진 정도(0–1). 토큰의 마지막 글자에만 쓴다.
        private float[] _gapProgress = Array.Empty<float>();
        private readonly Vector3[] _corners = new Vector3[4];
        private string _layoutPassword;
        private int _visibleCount;
        private Sequence _splitSequence;

        #endregion

        #region Properties

        public string Password => _password;

        /// <summary>
        /// 암호표로 끊은 숫자 묶음(토큰). 암호가 풀리지 않으면 비어 있다.
        /// </summary>
        public IReadOnlyList<CipherToken> Tokens
        {
            get
            {
                EnsureLayout();
                return _tokens;
            }
        }

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
        /// 암호를 바꾸고 묶음을 다시 계산함. 화면은 비운다.
        /// </summary>
        public void SetPassword(string password)
        {
            _password = password ?? string.Empty;
            ResetState();
        }

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
        /// 암호를 모두 보인 채 숫자 묶음(토큰) 사이 공백을 벌림. _splitMode에 따라 차례로 또는 동시에.
        /// 취소되면 트윈을 멈추고 그 상태에 둔다.
        /// </summary>
        public async UniTask PlaySplitAsync(CancellationToken cancellationToken)
        {
            KillSplit();
            ClearGaps();
            WriteUpTo(PasswordLength);

            Sequence sequence = DOTween.Sequence().SetUpdate(true);
            for (int i = 0; i < _tokens.Count - 1; i++)
            {
                int index = _tokens[i].Start + _tokens[i].Length - 1;
                float start = _splitMode == PasswordSplitMode.Sequential ? i * _splitIntervalSeconds : 0f;
                sequence = sequence.Insert(start, DOTween.To(() => _gapProgress[index], value => SetGap(index, value), 1f, _splitSeconds).SetEase(_splitEase));
            }

            _splitSequence = sequence;
            await sequence.ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, cancellationToken);
            _splitSequence = null;
        }

        /// <summary>
        /// 화면에 그려진 토큰마다 글자들의 가운데 x를 월드 좌표로 구함(토큰 순서). 해독 자모를 숫자 아래에 맞출 때 쓴다.
        /// </summary>
        public void GetTokenCenters(List<float> worldCenters)
        {
            worldCenters.Clear();
            if (!_text) _text = GetComponent<TMP_Text>();

            _text.ForceMeshUpdate();
            TMP_TextInfo info = _text.textInfo;
            for (int i = 0; i < _tokens.Count; i++)
            {
                // 암호는 숫자뿐이고 태그는 글자로 세지 않아 글자 정보 인덱스가 암호 인덱스와 같다.
                int first = _tokens[i].Start;
                int last = first + _tokens[i].Length - 1;
                if (last >= info.characterCount) break;

                float left = _text.transform.TransformPoint(info.characterInfo[first].bottomLeft).x;
                float right = _text.transform.TransformPoint(info.characterInfo[last].topRight).x;
                worldCenters.Add((left + right) * 0.5f);
            }
        }

        #endregion

        #region Private Methods

        private int PasswordLength => _password != null ? _password.Length : 0;

        /// <summary>
        /// 암호가 바뀌었으면 토큰·묶음 번호·공백 배열을 다시 만듦. 풀리지 않는 암호는 묶음 하나로 둔다.
        /// </summary>
        private void EnsureLayout()
        {
            if (_layoutPassword == _password) return;

            _layoutPassword = _password;
            int length = PasswordLength;
            _groupOfChar = new int[length];
            _gapProgress = new float[length];

            if (!CipherCodec.TryDecode(_password, _tokens))
            {
                _tokens.Clear();
                if (length > 0) Debug.LogWarning($"[PasswordTextView] Password does not match the cipher table: {_password}");
                return;
            }

            // 초성 다음에 모음이 오면 둘이 한 묶음, 그 밖의 토큰(받침 등)은 혼자 한 묶음.
            int group = -1;
            for (int i = 0; i < _tokens.Count; i++)
            {
                bool joinsPrevious = i > 0 && _tokens[i].Part == CipherPart.Vowel && _tokens[i - 1].Part == CipherPart.Initial;
                if (!joinsPrevious) group++;

                for (int c = _tokens[i].Start; c < _tokens[i].Start + _tokens[i].Length; c++)
                {
                    _groupOfChar[c] = group;
                }
            }
        }

        /// <summary>
        /// 공백 하나의 벌어진 정도를 바꾸고 다시 씀.
        /// </summary>
        private void SetGap(int index, float progress)
        {
            _gapProgress[index] = progress;
            WriteUpTo(_visibleCount);
        }

        /// <summary>
        /// 모든 공백을 닫음.
        /// </summary>
        private void ClearGaps()
        {
            EnsureLayout();
            Array.Clear(_gapProgress, 0, _gapProgress.Length);
        }

        /// <summary>
        /// 암호의 앞 count글자를 묶음 자리에 맞춰 씀. 벌어진 공백은 글자 크기 기준 px 폭의 space 태그로 넣는다.
        /// </summary>
        private void WriteUpTo(int count)
        {
            if (!_text) _text = GetComponent<TMP_Text>();
            EnsureLayout();

            _visibleCount = count;
            float spacePx = _splitSpaceEm * _text.fontSize;

            using (Utf16ValueStringBuilder builder = ZString.CreateStringBuilder())
            {
                for (int i = 0; i < count; i++)
                {
                    if (i > 0 && _groupOfChar[i] != _groupOfChar[i - 1])
                    {
                        builder.Append("<pos=");
                        builder.Append(Mathf.RoundToInt(_groupOfChar[i] * _groupPitch));
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
