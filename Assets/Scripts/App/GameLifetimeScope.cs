using Core;
using Data;
using Hardware;
using Network;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using HuliacDev.App;
using HuliacDev.Hardware;
using HuliacDev.Utils;

namespace App
{
    /// <summary>
    /// 프로젝트 전용 루트 DI 스코프.
    /// 템플릿 RootLifetimeScope의 로깅·MessagePipe·설정 등록에 더해 씬 매니저 주입 등록을 담당함.
    /// </summary>
    public class GameLifetimeScope : RootLifetimeScope
    {
        // 이 프리팹 자식의 ArduinoManager. 수화기 입력 구현을 고를 때 배치 여부를 확인한다.
        [SerializeField] private ArduinoManager arduinoManager;

        /// <summary>
        /// 템플릿 기본 등록(로깅, MessagePipe, AppSettingsProvider)에 프로젝트 매니저 등록을 추가함.
        /// </summary>
        protected override void Configure(IContainerBuilder builder)
        {
            base.Configure(builder);
            ConfigureManagers(builder);
            ConfigureReceiverInput(builder);
            ConfigureSession(builder);
            ConfigureMessageSender(builder);
        }

        /// <summary>
        /// 메시지 TCP 전송 서비스를 등록함. 씬이 바뀌어도 재시도·재전송이 이어지도록 루트 스코프 싱글톤 진입점으로 둔다.
        /// DeviceSettings는 ConfigureReceiverInput에서 등록한 인스턴스를 쓴다.
        /// </summary>
        private void ConfigureMessageSender(IContainerBuilder builder)
        {
            builder.RegisterEntryPoint<MessageSender>(Lifetime.Singleton).AsSelf();
        }

        /// <summary>
        /// 씬 계층에 배치된 매니저 컴포넌트를 컨테이너에 등록하여 메서드 주입이 동작하게 함.
        /// </summary>
        private void ConfigureManagers(IContainerBuilder builder)
        {
            builder.RegisterComponentInHierarchy<GameManager>();
        }

        /// <summary>
        /// 수화기 입력 서비스를 등록함. DeviceSettings.json의 useArduino가 true면 아두이노 구현을,
        /// false거나 ArduinoManager가 연결되지 않았으면 Space 토글 키보드 구현을 쓴다.
        /// 씬이 바뀌어도 수화기 상태가 유지되도록 루트 스코프에 싱글톤으로 둔다.
        /// </summary>
        private void ConfigureReceiverInput(IContainerBuilder builder)
        {
            // 컨테이너 구성 시점에 구현을 골라야 해서 동기로 읽는다. 작은 파일이라 부팅 지연은 무시할 수준이다.
            DeviceSettings deviceSettings = JsonLoader.Load<DeviceSettings>(Constants.Files.DeviceSettings);
            builder.RegisterInstance(deviceSettings);
            builder.Register<ProjectInputActions>(Lifetime.Singleton);

            if (deviceSettings.useArduino && arduinoManager)
            {
                builder.RegisterEntryPoint<ArduinoReceiverInput>(Lifetime.Singleton).As<IReceiverInput>();
                return;
            }

            if (deviceSettings.useArduino)
            {
                Debug.LogWarning("[GameLifetimeScope] useArduino is true but ArduinoManager is not assigned. Falling back to keyboard receiver input.");
            }

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
