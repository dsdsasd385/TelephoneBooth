using Core;
using Hardware;
using VContainer;
using VContainer.Unity;
using HuliacDev.App;

namespace App
{
    /// <summary>
    /// 프로젝트 전용 루트 DI 스코프.
    /// 템플릿 RootLifetimeScope의 로깅·MessagePipe·설정 등록에 더해 씬 매니저 주입 등록을 담당함.
    /// </summary>
    public class GameLifetimeScope : RootLifetimeScope
    {
        /// <summary>
        /// 템플릿 기본 등록(로깅, MessagePipe, AppSettingsProvider)에 프로젝트 매니저 등록을 추가함.
        /// </summary>
        protected override void Configure(IContainerBuilder builder)
        {
            base.Configure(builder);
            ConfigureManagers(builder);
            ConfigureReceiverInput(builder);
            ConfigureSession(builder);
        }

        /// <summary>
        /// 씬 계층에 배치된 매니저 컴포넌트를 컨테이너에 등록하여 메서드 주입이 동작하게 함.
        /// </summary>
        private void ConfigureManagers(IContainerBuilder builder)
        {
            builder.RegisterComponentInHierarchy<GameManager>();
        }

        /// <summary>
        /// 수화기 입력 서비스를 등록함. 아두이노 구현이 생기기 전까지는 Space 토글 키보드 구현을 쓴다.
        /// 씬이 바뀌어도 수화기 상태가 유지되도록 루트 스코프에 싱글톤으로 둔다.
        /// </summary>
        private void ConfigureReceiverInput(IContainerBuilder builder)
        {
            builder.Register<ProjectInputActions>(Lifetime.Singleton);
            builder.Register<KeyboardReceiverInput>(Lifetime.Singleton).As<IReceiverInput>();
        }

        /// <summary>
        /// 씬이 바뀌어도 유지되는 체험 세션(난이도·콘텐츠 선택)을 등록함.
        /// </summary>
        private void ConfigureSession(IContainerBuilder builder)
        {
            builder.Register<ExperienceSession>(Lifetime.Singleton);
        }
    }
}
