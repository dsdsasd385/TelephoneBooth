using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Cysharp.Text;
using TMPro;
using UnityEngine;

namespace Mission
{
    /// <summary>
    /// 미션3 집결지 암호(PasswordImage/PasswordText). 암호 문자열 하나를 텍스트 하나에 표시하되,
    /// _charsPerGroup(4)글자씩 묶어 묶음마다 텍스트 왼쪽에서 _groupPitch(px)씩 떨어진 자리에 쓴다(TMP pos 태그, 왼쪽 정렬 기준).
    /// <para>
    /// PlayTypingAsync는 한 글자씩 써 나가며, 한 묶음이 4글자로 차면 다음 묶음 자리에 이어 쓴다.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(TMP_Text))]
    public class PasswordTextView : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private string _password = "0024003000420054";
        [SerializeField, Min(1)] private int _charsPerGroup = 4;
        // 묶음 시작 위치 간격(px). 텍스트 왼쪽에서 묶음 i는 i × _groupPitch에서 시작한다.
        [SerializeField, Min(0f)] private float _groupPitch = 200f;
        // 글자 하나를 쓰는 간격(초). 0이면 한 번에 쓴다.
        [SerializeField, Min(0f)] private float _charIntervalSeconds = 0.08f;

        #endregion

        #region Private Fields

        private TMP_Text _text;

        #endregion

        #region Public Methods

        /// <summary>
        /// 텍스트를 비움.
        /// </summary>
        public void ResetState()
        {
            WriteUpTo(0);
        }

        /// <summary>
        /// 암호를 한 번에 모두 씀.
        /// </summary>
        public void ShowAll()
        {
            WriteUpTo(PasswordLength);
        }

        /// <summary>
        /// 암호를 한 글자씩 씀. 취소되면 그때까지 쓴 상태에 둔다.
        /// </summary>
        public async UniTask PlayTypingAsync(CancellationToken cancellationToken)
        {
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

        #endregion

        #region Private Methods

        private int PasswordLength => _password != null ? _password.Length : 0;

        /// <summary>
        /// 암호의 앞 count글자를 묶음 단위로 자리를 잡아 씀.
        /// </summary>
        private void WriteUpTo(int count)
        {
            if (!_text) _text = GetComponent<TMP_Text>();

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
                }

                _text.text = builder.ToString();
            }
        }

        #endregion
    }
}
