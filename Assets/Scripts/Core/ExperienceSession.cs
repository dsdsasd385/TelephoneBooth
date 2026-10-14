namespace Core
{
    /// <summary>
    /// 0_Idle ⑥에서 고르는 미션. 미션1 = 문화수호, 미션2 = 진실수호, 미션3 = 민족수호.
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
    /// 난이도는 기획에서 빠져 기록하지 않는다(작업계획서 3장).
    /// </summary>
    public sealed class ExperienceSession
    {
        public ContentType Content { get; private set; }

        /// <summary>
        /// 고른 미션을 기록함. 다른 미션으로 옮겨 가면 마지막 미션으로 덮어쓴다.
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
            Content = ContentType.None;
        }
    }
}
