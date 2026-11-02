using System;

namespace Data
{
    /// <summary>
    /// 9_Message 메시지 입력 설정. StreamingAssets/Data/MessageSettings.json에서 읽으며 현장에서 파일만 바꿔 적용한다.
    /// </summary>
    [Serializable]
    public class MessageSettings
    {
        // 다짐 최대 글자 수. 조합 중인 음절도 한 글자로 센다.
        public int maxLength = 15;
        // 이름 최대 글자 수.
        public int nameMaxLength = 10;
        // 보내기 후 MessageVideo에 재생할 로딩 영상(StreamingAssets 기준 상대 경로).
        public string loadingVideo = "Video/Message/수호메시지_로딩영상_v4.mp4";
        // 로딩 영상을 이어서 재생할 횟수. 다 재생하면 바로 0_Idle로 간다. 1 미만이면 1번.
        public int loadingVideoPlayCount = 2;
    }
}
