using R3;

namespace Hardware
{
    /// <summary>
    /// 수화기 상태를 화면 흐름 코드에 알려 주는 입력 서비스.
    /// 구현체(키보드 테스트용, 아두이노)는 VContainer 등록으로 교체하며, 화면 코드는 이 인터페이스만 본다.
    /// </summary>
    public interface IReceiverInput
    {
        /// <summary>
        /// 수화기가 들려 있으면 true. 값이 바뀔 때만 발행되므로 들기·내려놓기 순간을 그대로 구독할 수 있다.
        /// </summary>
        ReadOnlyReactiveProperty<bool> IsLifted { get; }
    }
}
