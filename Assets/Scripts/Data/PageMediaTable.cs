using System;

namespace Data
{
    /// <summary>
    /// 페이지 순서대로 영상·TTS 파일을 적은 StreamingAssets JSON의 최상위 구조.
    /// </summary>
    [Serializable]
    public class PageMediaTable
    {
        public PageMediaEntry[] pages = Array.Empty<PageMediaEntry>();
    }

    /// <summary>
    /// 한 페이지의 영상(무음 반복)과 TTS(1회) 파일. 경로는 StreamingAssets 기준 상대 경로이며,
    /// screen은 씬의 화면 오브젝트 이름으로 순서가 어긋났는지 확인하는 데만 쓴다.
    /// tts가 비어 있으면 그 페이지는 TTS 없이 영상만 재생한다.
    /// </summary>
    [Serializable]
    public class PageMediaEntry
    {
        public string screen;
        public string video;
        public string tts;
    }
}
