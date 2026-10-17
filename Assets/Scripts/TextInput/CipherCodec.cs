using System.Collections.Generic;
using System.Text;

namespace TextInput
{
    /// <summary>
    /// 암호 숫자 한 묶음이 나타내는 자모의 종류.
    /// </summary>
    public enum CipherPart
    {
        // 초성(자음), 2자리 11–29.
        Initial,
        // 모음, 2자리 30–49.
        Vowel,
        // 받침, 4자리 0011–0035.
        Final
    }

    /// <summary>
    /// 암호 숫자 한 묶음(토큰). Start·Length는 암호 문자열 안의 위치다.
    /// </summary>
    public readonly struct CipherToken
    {
        public readonly int Start;
        public readonly int Length;
        public readonly char Jamo;
        public readonly CipherPart Part;

        public CipherToken(int start, int length, char jamo, CipherPart part)
        {
            Start = start;
            Length = length;
            Jamo = jamo;
            Part = part;
        }
    }

    /// <summary>
    /// 미션3 암호표. 자모 하나를 표에서 찾아 숫자로 바꾸거나 숫자를 다시 자모로 찾는다(실제 암호화가 아닌 표 찾기).
    /// <para>
    /// 표: 초성 ㄱ11 ~ ㅉ29, 모음 ㅏ30 ~ ㅞ49, 받침 ㄱ0011 ~ ㅄ0035.
    /// 자모 → 숫자(TryEncode): 모음은 모음표, 바로 뒤에 모음이 오는 자음은 초성표, 그렇지 않은 자음(뒤가 자음이거나 끝)은 받침표에서 찾는다.
    /// ㄷㅏㄹㅣㅁㅣㅌ → 13 30 14 39 15 39 0022, ㅅㅏㄴㅅㅏㅇㅣ → 17 30 0012 17 30 18 39.
    /// 숫자 → 자모(TryDecode): 받침은 항상 "00"으로 시작하고 2자리 코드는 0으로 시작하지 않으므로, 다음 두 글자가 "00"이면 4자리 받침,
    /// 아니면 2자리이고 29 이하면 초성, 30 이상이면 모음으로 읽는다. 풀이가 하나로 정해진다.
    /// </para>
    /// </summary>
    public static class CipherCodec
    {
        #region Constants

        // 암호표. 코드 순서대로 첫 번호부터 1씩 늘어난다.
        // 초성 11–29.
        private const int InitialFirstCode = 11;
        private const string InitialTable = "ㄱㄴㄷㄹㅁㅂㅅㅇㅈㅊㅋㅌㅍㅎㄲㅆㄸㅃㅉ";
        // 모음 30–49. 받은 표에는 44와 48이 모두 ㅟ로 적혀 있어 48은 빠진 ㅝ로 두었다(확인 필요).
        private const int VowelFirstCode = 30;
        private const string VowelTable = "ㅏㅑㅓㅕㅗㅛㅜㅠㅡㅣㅐㅔㅖㅚㅟㅢㅘㅙㅝㅞ";
        // 받침 0011–0035. 받은 표에는 0018이 ㅐ(모음)로 적혀 있어 받침이 될 수 있는 ㅇ으로 두었다(확인 필요).
        private const int FinalFirstCode = 11;
        private const string FinalTable = "ㄱㄴㄷㄹㅁㅂㅅㅇㅈㅊㅋㅌㅍㅎㄲㅆㄵㄶㄺㄻㄼㄾㄿㅀㅄ";

        #endregion

        #region Public Methods

        /// <summary>
        /// 자모 문자열(ㄷㅏㄹㅣㅁㅣㅌ)을 표에서 찾아 숫자 암호로 바꿈. 공백은 건너뛴다. 표에 없는 자모가 있으면 false.
        /// </summary>
        public static bool TryEncode(string jamo, out string code)
        {
            StringBuilder builder = new StringBuilder();
            code = string.Empty;
            if (string.IsNullOrEmpty(jamo)) return false;

            for (int i = 0; i < jamo.Length; i++)
            {
                char c = jamo[i];
                if (char.IsWhiteSpace(c)) continue;

                if (VowelTable.IndexOf(c) >= 0)
                {
                    AppendCode(builder, VowelTable, VowelFirstCode, c, "00");
                }
                else if (IsVowel(NextJamo(jamo, i)))
                {
                    if (!AppendCode(builder, InitialTable, InitialFirstCode, c, "00")) return false;
                }
                else if (!AppendCode(builder, FinalTable, FinalFirstCode, c, "0000"))
                {
                    return false;
                }
            }

            code = builder.ToString();
            return code.Length > 0;
        }

        /// <summary>
        /// 숫자 암호를 앞에서부터 토큰으로 끊어 자모로 찾음. 표에 없는 번호가 있거나 길이가 맞지 않으면 false이고
        /// tokens에는 그 앞까지만 들어 있다.
        /// </summary>
        public static bool TryDecode(string code, List<CipherToken> tokens)
        {
            tokens.Clear();
            if (string.IsNullOrEmpty(code)) return false;

            int index = 0;
            while (index < code.Length)
            {
                bool isFinal = index + 1 < code.Length && code[index] == '0' && code[index + 1] == '0';
                int length = isFinal ? 4 : 2;
                if (index + length > code.Length || !TryParseNumber(code, index, length, out int number)) return false;

                char jamo;
                CipherPart part;
                if (isFinal)
                {
                    if (!TryLookup(FinalTable, FinalFirstCode, number, out jamo)) return false;
                    part = CipherPart.Final;
                }
                else if (number < VowelFirstCode)
                {
                    if (!TryLookup(InitialTable, InitialFirstCode, number, out jamo)) return false;
                    part = CipherPart.Initial;
                }
                else
                {
                    if (!TryLookup(VowelTable, VowelFirstCode, number, out jamo)) return false;
                    part = CipherPart.Vowel;
                }

                tokens.Add(new CipherToken(index, length, jamo, part));
                index += length;
            }

            return true;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// index 다음의 공백이 아닌 자모. 없으면 '\0'.
        /// </summary>
        private static char NextJamo(string jamo, int index)
        {
            for (int i = index + 1; i < jamo.Length; i++)
            {
                if (!char.IsWhiteSpace(jamo[i])) return jamo[i];
            }

            return '\0';
        }

        /// <summary>
        /// 모음표에 있는 자모인지.
        /// </summary>
        private static bool IsVowel(char c)
        {
            return c != '\0' && VowelTable.IndexOf(c) >= 0;
        }

        /// <summary>
        /// code의 start부터 length글자가 모두 숫자면 정수로 읽음.
        /// </summary>
        private static bool TryParseNumber(string code, int start, int length, out int number)
        {
            number = 0;
            for (int i = start; i < start + length; i++)
            {
                char c = code[i];
                if (c < '0' || c > '9') return false;
                number = number * 10 + (c - '0');
            }

            return true;
        }

        /// <summary>
        /// 표에서 번호에 해당하는 자모를 찾음.
        /// </summary>
        private static bool TryLookup(string table, int firstCode, int number, out char jamo)
        {
            int index = number - firstCode;
            jamo = '\0';
            if (index < 0 || index >= table.Length) return false;

            jamo = table[index];
            return true;
        }

        /// <summary>
        /// 자모의 번호를 format 자릿수(앞을 0으로 채움)로 덧붙임. 표에 없으면 false.
        /// </summary>
        private static bool AppendCode(StringBuilder builder, string table, int firstCode, char jamo, string format)
        {
            int index = table.IndexOf(jamo);
            if (index < 0) return false;

            builder.Append((firstCode + index).ToString(format));
            return true;
        }

        #endregion
    }
}
