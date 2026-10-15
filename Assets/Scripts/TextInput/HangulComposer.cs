using System.Text;

namespace TextInput
{
    /// <summary>
    /// 두벌식 한글 조합기. 호환 자모(ㄱ, ㅏ …)를 순서대로 넣으면 완성형 음절로 실시간 조합한다.
    /// KoreanLiberationArmy 프로젝트의 KLA.Text.HangulComposer를 옮겼다. 순수 C#이라 로직은 그대로 두고
    /// 네임스페이스와 var(프로젝트 규칙)만 바꿨다.
    ///
    /// 상태는 "확정된 문자열(_done) + 조합 중인 한 음절(_cho/_jung/_jong)" 두 부분이다.
    /// <see cref="Text"/> 는 둘을 합친 현재 표시 문자열이다.
    ///
    /// 조합 규칙은 Windows 기본 IME 동작을 따른다:
    ///  - 종성 뒤에 모음이 오면 종성이 다음 음절의 초성으로 이동한다(각 + ㅏ → "가가").
    ///  - 겹받침/겹모음은 결합 가능한 조합만 만든다(ㄹ+ㄱ=ㄺ, ㅗ+ㅏ=ㅘ).
    ///  - 백스페이스는 조합 중이면 자모 한 개씩, 아니면 확정 문자 한 개를 지운다.
    ///
    /// 한글이 아닌 문자(영문/공백)는 조합 중인 음절을 확정한 뒤 그대로 덧붙인다.
    /// </summary>
    public class HangulComposer
    {
        private const int SyllableBase = 0xAC00;
        private const int JungCount = 21;
        private const int JongCount = 28;

        // 인덱스 순서는 유니코드 한글 음절 조합 공식과 일치해야 한다.
        private const string Cho = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
        private const string Jung = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
        private const string Jong = " ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

        // 겹받침 표. 각 항목은 "앞자모, 뒤자모, 합쳐진 종성" 3글자다.
        private static readonly string[] JongPairs =
        {
            "ㄱㅅㄳ", "ㄴㅈㄵ", "ㄴㅎㄶ", "ㄹㄱㄺ", "ㄹㅁㄻ", "ㄹㅂㄼ",
            "ㄹㅅㄽ", "ㄹㅌㄾ", "ㄹㅍㄿ", "ㄹㅎㅀ", "ㅂㅅㅄ"
        };

        // 겹모음 표. 각 항목은 "앞모음, 뒤모음, 합쳐진 중성" 3글자다.
        private static readonly string[] JungPairs =
        {
            "ㅗㅏㅘ", "ㅗㅐㅙ", "ㅗㅣㅚ", "ㅜㅓㅝ", "ㅜㅔㅞ", "ㅜㅣㅟ", "ㅡㅣㅢ"
        };

        private readonly StringBuilder _done = new StringBuilder();
        private int _cho = -1;   // Cho 인덱스, -1 = 없음
        private int _jung = -1;  // Jung 인덱스, -1 = 없음
        private int _jong = 0;   // Jong 인덱스, 0 = 없음

        /// <summary>확정 문자열 + 조합 중인 음절을 합친 현재 표시 문자열.</summary>
        public string Text
        {
            get
            {
                if (_cho < 0 && _jung < 0) return _done.ToString();
                return _done.ToString() + Composing();
            }
        }

        /// <summary>표시 문자열 길이(조합 중인 음절도 1글자로 센다).</summary>
        public int Length => Text.Length;

        public void Clear()
        {
            _done.Length = 0;
            _cho = _jung = -1;
            _jong = 0;
        }

        /// <summary>조합 중인 음절을 확정한다(입력 종료 시 호출).</summary>
        public void Commit() => Flush();

        /// <summary>
        /// 자모/문자 한 개를 입력하되, 결과 길이가 maxLength 를 넘으면 되돌리고 false 를 반환한다.
        /// 종성 추가처럼 길이가 늘지 않는 입력은 최대 길이에서도 허용된다.
        /// </summary>
        public bool TryInput(char c, int maxLength)
        {
            State snapshot = Save();
            Input(c);
            if (Text.Length > maxLength)
            {
                Restore(snapshot);
                return false;
            }
            return true;
        }

        public void Input(char c)
        {
            if (Cho.IndexOf(c) >= 0 || Jong.IndexOf(c) > 0) InputConsonant(c);
            else if (Jung.IndexOf(c) >= 0) InputVowel(c);
            else { Flush(); _done.Append(c); }   // 영문·공백 등
        }

        /// <summary>조합 중이면 자모 한 개, 아니면 확정 문자 한 개를 지운다.</summary>
        public void Backspace()
        {
            if (_jong > 0)
            {
                // 겹받침이면 뒤 자모만 떼어낸다(ㄺ → ㄹ).
                SplitJong(_jong, out int keep, out _);
                _jong = keep;
                return;
            }
            if (_jung >= 0)
            {
                // 겹모음이면 뒤 모음만 떼어낸다(ㅘ → ㅗ).
                _jung = SplitJung(_jung);
                return;
            }
            if (_cho >= 0)
            {
                _cho = -1;
                return;
            }
            if (_done.Length > 0) _done.Remove(_done.Length - 1, 1);
        }

        // ── 조합 로직 ──────────────────────────────────────────────

        private void InputConsonant(char c)
        {
            int choIdx = Cho.IndexOf(c);
            int jongIdx = Jong.IndexOf(c);

            // 아직 초성+중성이 갖춰지지 않았으면 앞을 확정하고 새 음절을 시작한다.
            if (_cho < 0 || _jung < 0)
            {
                Flush();
                StartCho(c, choIdx);
                return;
            }

            if (_jong == 0)
            {
                if (jongIdx > 0) { _jong = jongIdx; return; }
            }
            else
            {
                int combined = CombineJong(_jong, c);
                if (combined > 0) { _jong = combined; return; }
            }

            Flush();
            StartCho(c, choIdx);
        }

        private void InputVowel(char c)
        {
            int jungIdx = Jung.IndexOf(c);

            if (_jung < 0)
            {
                // 초성만 있거나 비어 있음 → 중성을 채운다(초성이 없으면 모음 단독 조합).
                _jung = jungIdx;
                return;
            }

            if (_jong == 0)
            {
                int combined = CombineJung(_jung, c);
                if (combined >= 0) { _jung = combined; return; }
                Flush();
                _jung = jungIdx;
                return;
            }

            // 종성이 있는데 모음이 왔다 → 종성(겹받침이면 뒤 자모)이 다음 음절의 초성이 된다.
            SplitJong(_jong, out int keep, out int move);
            _jong = keep;
            Flush();
            _cho = Cho.IndexOf(Jong[move]);
            _jung = jungIdx;
        }

        private void StartCho(char c, int choIdx)
        {
            if (choIdx >= 0) _cho = choIdx;
            else _done.Append(c);   // 초성이 될 수 없는 자모(ㄳ 등) — 두벌식 키보드엔 없지만 방어
        }

        private void Flush()
        {
            if (_cho >= 0 && _jung >= 0)
                _done.Append((char)(SyllableBase + (_cho * JungCount + _jung) * JongCount + _jong));
            else if (_cho >= 0) _done.Append(Cho[_cho]);
            else if (_jung >= 0) _done.Append(Jung[_jung]);

            _cho = _jung = -1;
            _jong = 0;
        }

        private char Composing()
        {
            if (_cho >= 0 && _jung >= 0)
                return (char)(SyllableBase + (_cho * JungCount + _jung) * JongCount + _jong);
            if (_cho >= 0) return Cho[_cho];
            return Jung[_jung];
        }

        // ── 자모 결합/분리 표 ──────────────────────────────────────

        /// <summary>현재 종성에 자음을 더해 겹받침을 만든다. 불가하면 0.</summary>
        private static int CombineJong(int jong, char add)
        {
            char cur = Jong[jong];
            foreach (string entry in JongPairs)
                if (entry[0] == cur && entry[1] == add) return Jong.IndexOf(entry[2]);
            return 0;
        }

        /// <summary>겹받침을 앞/뒤 자모로 나눈다. 홑받침이면 keep=0, move=자기 자신.</summary>
        private static void SplitJong(int jong, out int keep, out int move)
        {
            char cur = Jong[jong];
            foreach (string entry in JongPairs)
            {
                if (entry[2] != cur) continue;
                keep = Jong.IndexOf(entry[0]);
                move = Jong.IndexOf(entry[1]);
                return;
            }
            keep = 0;
            move = jong;
        }

        /// <summary>현재 중성에 모음을 더해 겹모음을 만든다. 불가하면 -1.</summary>
        private static int CombineJung(int jung, char add)
        {
            char cur = Jung[jung];
            foreach (string entry in JungPairs)
                if (entry[0] == cur && entry[1] == add) return Jung.IndexOf(entry[2]);
            return -1;
        }

        /// <summary>겹모음에서 뒤 모음을 뗀 앞 모음 인덱스. 홑모음이면 -1.</summary>
        private static int SplitJung(int jung)
        {
            char cur = Jung[jung];
            foreach (string entry in JungPairs)
                if (entry[2] == cur) return Jung.IndexOf(entry[0]);
            return -1;
        }

        // ── 되돌리기용 스냅샷 ──────────────────────────────────────

        private struct State
        {
            public string Done;
            public int Cho, Jung, Jong;
        }

        private State Save() => new State { Done = _done.ToString(), Cho = _cho, Jung = _jung, Jong = _jong };

        private void Restore(State s)
        {
            _done.Length = 0;
            _done.Append(s.Done);
            _cho = s.Cho;
            _jung = s.Jung;
            _jong = s.Jong;
        }
    }
}
