namespace App
{
    /// <summary>
    /// 여러 파일에서 함께 참조하는 식별자 상수 모음.
    /// </summary>
    public static class Constants
    {
        public static class Scenes
        {
            public const string Idle = "0_Idle";
        }

        public static class Files
        {
            // StreamingAssets 기준 상대 경로. 0_Idle 페이지별 영상·TTS 파일 목록.
            public const string IdlePages = "Data/IdlePages.json";
        }
    }
}
