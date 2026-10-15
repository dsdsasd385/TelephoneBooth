using NUnit.Framework;
using TextInput;

namespace Tests
{
    /// <summary>
    /// HangulComposer 조합 규칙 테스트. 원본에 테스트가 없어 이식하면서 추가했다(작업계획서 9장).
    /// </summary>
    public class HangulComposerTests
    {
        private static string Type(string keys)
        {
            HangulComposer composer = new HangulComposer();
            foreach (char key in keys) composer.Input(key);
            return composer.Text;
        }

        [TestCase("ㅎㅏㄴㄱㅡㄹ", "한글")]
        [TestCase("ㄱㅏㄱㅏ", "가가")]
        [TestCase("ㄱㅏㄱ", "각")]
        [TestCase("ㄷㅗㄱㄹㅣㅂ", "독립")]
        [TestCase("ㄱㅣㄴㅕㅁㄱㅘㄴ", "기념관")]
        public void Input_ComposesSyllables(string keys, string expected)
        {
            Assert.AreEqual(expected, Type(keys));
        }

        [TestCase("ㄱㅏㄱㅏ", "가가")]          // 종성이 다음 음절 초성으로 이동
        [TestCase("ㄱㅏㅂㅅㅏ", "갑사")]        // 겹받침 ㅄ에서 뒤 자모만 이동
        [TestCase("ㄷㅏㄹㄱㅏ", "달가")]        // ㄺ → ㄹ + 가
        public void Input_MovesFinalToNextSyllable(string keys, string expected)
        {
            Assert.AreEqual(expected, Type(keys));
        }

        [TestCase("ㄷㅏㄹㄱ", "닭")]
        [TestCase("ㅇㅓㅂㅅ", "없")]
        [TestCase("ㅇㅏㄴㅈ", "앉")]
        public void Input_CombinesDoubleFinal(string keys, string expected)
        {
            Assert.AreEqual(expected, Type(keys));
        }

        [TestCase("ㄱㅗㅏ", "과")]
        [TestCase("ㅇㅜㅣ", "위")]
        [TestCase("ㅇㅡㅣ", "의")]
        [TestCase("ㅇㅗㅐ", "왜")]
        public void Input_CombinesDoubleVowel(string keys, string expected)
        {
            Assert.AreEqual(expected, Type(keys));
        }

        [TestCase("ㄷㅏㄸ", "다ㄸ")]           // ㄸ은 종성이 될 수 없다
        [TestCase("ㄱㄱ", "ㄱㄱ")]             // 초성끼리는 합치지 않는다
        [TestCase("ㅏ", "ㅏ")]                 // 초성 없는 모음 단독
        [TestCase("ㅏㅏ", "ㅏㅏ")]
        [TestCase("ㄱㅏㄱㄱ", "각ㄱ")]          // ㄱ+ㄱ은 겹받침이 아니다
        public void Input_KeepsUncombinableJamo(string keys, string expected)
        {
            Assert.AreEqual(expected, Type(keys));
        }

        [Test]
        public void Input_NonHangulCommitsComposingSyllable()
        {
            Assert.AreEqual("한 글", Type("ㅎㅏㄴ ㄱㅡㄹ"));
            Assert.AreEqual("가A", Type("ㄱㅏA"));
        }

        [Test]
        public void Backspace_RemovesOneJamoWhileComposing()
        {
            HangulComposer composer = new HangulComposer();
            foreach (char key in "ㄷㅏㄹㄱ") composer.Input(key);

            composer.Backspace();
            Assert.AreEqual("달", composer.Text);
            composer.Backspace();
            Assert.AreEqual("다", composer.Text);
            composer.Backspace();
            Assert.AreEqual("ㄷ", composer.Text);
            composer.Backspace();
            Assert.AreEqual("", composer.Text);
        }

        [Test]
        public void Backspace_SplitsDoubleVowel()
        {
            HangulComposer composer = new HangulComposer();
            foreach (char key in "ㄱㅗㅏ") composer.Input(key);

            composer.Backspace();
            Assert.AreEqual("고", composer.Text);
        }

        [Test]
        public void Backspace_RemovesCommittedCharacter()
        {
            HangulComposer composer = new HangulComposer();
            foreach (char key in "ㅎㅏㄴ ") composer.Input(key);

            composer.Backspace();
            Assert.AreEqual("한", composer.Text);
            composer.Backspace();
            Assert.AreEqual("", composer.Text);
        }

        [Test]
        public void TryInput_RejectsInputBeyondMaxLength()
        {
            HangulComposer composer = new HangulComposer();
            Assert.IsTrue(composer.TryInput('ㄱ', 1));
            Assert.IsTrue(composer.TryInput('ㅏ', 1));

            // 받침은 길이가 늘지 않으므로 최대 길이에서도 받는다.
            Assert.IsTrue(composer.TryInput('ㄱ', 1));
            Assert.AreEqual("각", composer.Text);

            // 모음이 오면 받침이 다음 음절로 넘어가 두 글자가 되므로 거부하고 이전 상태로 되돌린다.
            Assert.IsFalse(composer.TryInput('ㅏ', 1));
            Assert.AreEqual("각", composer.Text);

            Assert.IsFalse(composer.TryInput(' ', 1));
            Assert.AreEqual("각", composer.Text);
        }

        [Test]
        public void TryInput_StopsAtThirtyCharacters()
        {
            HangulComposer composer = new HangulComposer();
            for (int i = 0; i < 30; i++) Assert.IsTrue(composer.TryInput('a', 30));

            Assert.IsFalse(composer.TryInput('a', 30));
            Assert.AreEqual(30, composer.Length);
        }

        [Test]
        public void Clear_ResetsTextAndComposingState()
        {
            HangulComposer composer = new HangulComposer();
            foreach (char key in "ㅎㅏㄴㄱ") composer.Input(key);

            composer.Clear();
            Assert.AreEqual("", composer.Text);

            composer.Input('ㅏ');
            Assert.AreEqual("ㅏ", composer.Text);
        }
    }
}
