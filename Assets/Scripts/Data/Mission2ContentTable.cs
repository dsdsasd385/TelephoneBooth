using System;

namespace Data
{
    /// <summary>
    /// 미션2(진실수호) 콘텐츠를 적은 StreamingAssets JSON의 최상위 구조.
    /// screens 순서는 씬 매니저의 대사 화면 순서와 같다.
    /// </summary>
    [Serializable]
    public class Mission2ContentTable
    {
        public DialogueScreenEntry[] screens = Array.Empty<DialogueScreenEntry>();
    }

    /// <summary>
    /// 대사 화면 하나의 대사별 TTS 경로(StreamingAssets 기준). 순서는 화면의 대사 Sprite 순서와 같다.
    /// 경로가 비어 있으면 그 대사는 TTS 없이 씬 매니저의 대기 시간만큼 보여 준다. screen은 순서 확인용 화면 오브젝트 이름.
    /// </summary>
    [Serializable]
    public class DialogueScreenEntry
    {
        public string screen;
        public string[] tts = Array.Empty<string>();
    }
}
