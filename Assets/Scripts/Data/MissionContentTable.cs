using System;

namespace Data
{
    /// <summary>
    /// 미션 씬 하나의 페이지별 영상·TTS와 단계별 선택지 피드백 TTS를 적은 StreamingAssets JSON의 최상위 구조.
    /// pages 순서는 씬 매니저의 화면 순서와 같다.
    /// </summary>
    [Serializable]
    public class MissionContentTable
    {
        public PageMediaEntry[] pages = Array.Empty<PageMediaEntry>();
        public MissionStageEntry[] stages = Array.Empty<MissionStageEntry>();
    }

    /// <summary>
    /// 퀴즈 단계 하나의 선택지별 피드백 TTS(오답·정답 문구를 읽는 음성). choiceTts 순서는 씬의 선택지 순서와 같다.
    /// 경로가 비어 있으면 그 선택지는 TTS 없이 화면만 바뀐다. screen은 순서 확인용 화면 오브젝트 이름.
    /// </summary>
    [Serializable]
    public class MissionStageEntry
    {
        public string screen;
        public string[] choiceTts = Array.Empty<string>();
    }
}
