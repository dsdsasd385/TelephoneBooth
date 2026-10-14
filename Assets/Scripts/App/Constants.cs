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
            // 미션 씬. 파일 이름은 가칭(작업계획서 3장)이라 바꾸면 여기만 고친다.
            public const string Mission1 = "Culture_high";
            public const string Mission2 = "TruthBtn_high";
            public const string Mission3 = "Nation_high";
        }

        public static class Files
        {
            // StreamingAssets 기준 상대 경로. 0_Idle 페이지별 영상·TTS 파일 목록.
            public const string IdlePages = "Data/IdlePages.json";
            // 영상 재생 방식(더블 버퍼링 on/off) 설정.
            public const string MediaSettings = "Data/MediaSettings.json";
        }
    }
}
