namespace Core
{
    /// <summary>
    /// 0_Idle ⑤에서 고르는 미션. 미션1 = 문화수호, 미션2 = 진실수호, 미션3 = 민족수호.
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

        // 미션 씬의 홈 버튼으로 0_Idle에 돌아갈 때, ① 대기 대신 ② 안내부터 시작하라는 1회용 요청.
        private bool _isGuideReturnRequested;

        /// <summary>
        /// 고른 미션을 기록함. 다른 미션으로 옮겨 가면 마지막 미션으로 덮어쓴다.
        /// </summary>
        public void SetContent(ContentType content)
        {
            Content = content;
        }

        /// <summary>
        /// 다음에 0_Idle에 들어갈 때 ② 안내부터 시작하도록 요청함. 미션 씬의 홈 버튼에서 씬을 바꾸기 직전에 부른다.
        /// </summary>
        public void RequestGuideReturn()
        {
            _isGuideReturnRequested = true;
        }

        /// <summary>
        /// ② 안내부터 시작하라는 요청이 있었는지 돌려주고 요청을 지움. 0_Idle이 시작할 때 한 번 부른다.
        /// </summary>
        public bool ConsumeGuideReturn()
        {
            bool isRequested = _isGuideReturnRequested;
            _isGuideReturnRequested = false;
            return isRequested;
        }

        /// <summary>
        /// 다음 관람객을 위해 선택값을 지움.
        /// </summary>
        public void Reset()
        {
            Content = ContentType.None;
            _isGuideReturnRequested = false;
        }
    }
}
