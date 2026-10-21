using System;

namespace Data
{
    /// <summary>
    /// 미션3(민족수호) 콘텐츠를 적은 StreamingAssets JSON의 최상위 구조. 경로는 StreamingAssets 기준이며 비어 있으면 재생하지 않는다.
    /// guideTts = Guide1 안내, mapTts = Guide2(지도의 다섯 곳), passwordTts = SelectScreen(암호를 풀어 집결지를 찾아주시오),
    /// failTts = 오답(FailScreen), correctSfx = 정답 효과음, endTts = 마무리 선택 화면(EndSelectScreen).
    /// </summary>
    [Serializable]
    public class Mission3ContentTable
    {
        public string guideTts = "";
        public string mapTts = "";
        public string passwordTts = "";
        public string failTts = "";
        public string correctSfx = "";
        public string endTts = "";
    }
}
