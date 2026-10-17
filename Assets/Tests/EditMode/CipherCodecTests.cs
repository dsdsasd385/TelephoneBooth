using System.Collections.Generic;
using NUnit.Framework;
using TextInput;

namespace Tests
{
    /// <summary>
    /// 미션3 암호표 찾기 규칙 확인.
    /// </summary>
    public class CipherCodecTests
    {
        [TestCase("ㄷㅏㄹㅣㅁㅣㅌ", "1330143915390022")]
        [TestCase("ㅅㅏㄴㅅㅏㅇㅣ", "1730001217301839")]
        [TestCase("ㄷ ㅏ ㄹ ㅣ ㅁ ㅣ ㅌ", "1330143915390022")]
        public void Encode_AnswerJamo_ReturnsCode(string jamo, string expected)
        {
            Assert.IsTrue(CipherCodec.TryEncode(jamo, out string code));
            Assert.AreEqual(expected, code);
        }

        [TestCase("1330143915390022", "ㄷㅏㄹㅣㅁㅣㅌ")]
        [TestCase("1730001217301839", "ㅅㅏㄴㅅㅏㅇㅣ")]
        public void Decode_AnswerCode_ReturnsJamo(string code, string expected)
        {
            List<CipherToken> tokens = new List<CipherToken>();

            Assert.IsTrue(CipherCodec.TryDecode(code, tokens));
            Assert.AreEqual(expected, string.Concat(tokens.ConvertAll(t => t.Jamo.ToString())));
        }

        [Test]
        public void Decode_SanSaI_FinalInMiddle()
        {
            List<CipherToken> tokens = new List<CipherToken>();

            Assert.IsTrue(CipherCodec.TryDecode("1730001217301839", tokens));

            Assert.AreEqual(new[] { 0, 2, 4, 8, 10, 12, 14 }, tokens.ConvertAll(t => t.Start).ToArray());
            Assert.AreEqual(CipherPart.Final, tokens[2].Part);
            Assert.AreEqual(4, tokens[2].Length);
        }

        [Test]
        public void Decode_PartsByRange()
        {
            List<CipherToken> tokens = new List<CipherToken>();

            Assert.IsTrue(CipherCodec.TryDecode("1129304900110035", tokens));

            Assert.AreEqual(new[] { 'ㄱ', 'ㅉ', 'ㅏ', 'ㅞ', 'ㄱ', 'ㅄ' }, tokens.ConvertAll(t => t.Jamo).ToArray());
            Assert.AreEqual(new[] { CipherPart.Initial, CipherPart.Initial, CipherPart.Vowel, CipherPart.Vowel, CipherPart.Final, CipherPart.Final },
                tokens.ConvertAll(t => t.Part).ToArray());
        }

        [TestCase("ㄱㅏㄱ", "11300011")]
        [TestCase("ㄱㅏㄴㄷ", "113000120013")]
        public void Encode_ConsonantNotBeforeVowel_IsFinal(string jamo, string expected)
        {
            Assert.IsTrue(CipherCodec.TryEncode(jamo, out string code));
            Assert.AreEqual(expected, code);
        }

        [TestCase("10")]
        [TestCase("50")]
        [TestCase("0010")]
        [TestCase("0036")]
        [TestCase("133")]
        [TestCase("00")]
        [TestCase("13a0")]
        [TestCase("")]
        public void Decode_InvalidCode_ReturnsFalse(string code)
        {
            Assert.IsFalse(CipherCodec.TryDecode(code, new List<CipherToken>()));
        }

        [TestCase("ㄱㅒ")]
        [TestCase("ㄸ")]
        [TestCase("A")]
        [TestCase("")]
        public void Encode_NotInTable_ReturnsFalse(string jamo)
        {
            // ㅒ는 모음표에 없고, ㄸ은 초성표에만 있어 받침 자리에 올 수 없다.
            Assert.IsFalse(CipherCodec.TryEncode(jamo, out _));
        }
    }
}
