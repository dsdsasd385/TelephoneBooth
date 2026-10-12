namespace Core
{
    /// <summary>
    /// 0_Idle ② 나이 선택으로 정해지는 난이도.
    /// </summary>
    public enum Difficulty
    {
        None,
        Low,
        High
    }

    /// <summary>
    /// 0_Idle ⑥에서 고르는 콘텐츠.
    /// </summary>
    public enum ContentType
    {
        None,
        Culture,
        Truth,
        Nation
    }

    /// <summary>
    /// 관람객 한 명의 체험 동안 씬이 바뀌어도 유지해야 하는 선택값.
    /// 루트 스코프(GameLifetimeScope)에 싱글톤으로 등록하며, ① 대기 화면에 들어갈 때 초기화한다.
    /// </summary>
    public sealed class ExperienceSession
    {
        public Difficulty Difficulty { get; private set; }
        public ContentType Content { get; private set; }

        /// <summary>
        /// 나이 선택 결과로 난이도를 기록함.
        /// </summary>
        public void SetDifficulty(Difficulty difficulty)
        {
            Difficulty = difficulty;
        }

        /// <summary>
        /// 콘텐츠 선택 결과를 기록함.
        /// </summary>
        public void SetContent(ContentType content)
        {
            Content = content;
        }

        /// <summary>
        /// 다음 관람객을 위해 선택값을 지움.
        /// </summary>
        public void Reset()
        {
            Difficulty = Difficulty.None;
            Content = ContentType.None;
        }
    }
}
