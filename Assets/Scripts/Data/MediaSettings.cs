using System;

namespace Data
{
    /// <summary>
    /// 영상 재생 방식 설정. StreamingAssets/Data/MediaSettings.json에서 읽으며 현장에서 파일만 바꿔 적용한다.
    /// </summary>
    [Serializable]
    public class MediaSettings
    {
        // true면 영상 플레이어 2개로 반복·전환을 끊김 없이 처리한다. 플레이어 1개와 화면 텍스처 약 8MB가 늘어난다.
        public bool doubleBuffering;
    }
}
