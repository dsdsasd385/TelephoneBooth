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
        public int maxLength = 30;
        // 이름 최대 글자 수.
        public int nameMaxLength = 10;
        // 보내기 후 MessageVideo에 한 번 재생할 로딩 영상(StreamingAssets 기준 상대 경로).
        public string loadingVideo = "Video/Message/수호메시지_로딩영상_v2.mp4";
        // 로딩 영상이 끝난 뒤 0_Idle로 넘어가기 전에 기다리는 시간(초). 기다리는 동안 영상 마지막 프레임이 보인다.
        public float idleReturnDelaySeconds = 2f;
    }
}
