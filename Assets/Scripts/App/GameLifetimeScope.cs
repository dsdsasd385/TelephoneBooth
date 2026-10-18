using Core;
using Data;
using Hardware;
using Network;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using HuliacDev.App;
using HuliacDev.Utils;

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
            ConfigureMessageSender(builder);
        }

        /// <summary>
        /// 메시지 TCP 전송 서비스를 등록함. 씬이 바뀌어도 재시도·재전송이 이어지도록 루트 스코프 싱글톤 진입점으로 둔다.
        /// DeviceSettings는 ConfigureReceiverInput에서 등록한 인스턴스를 쓴다.
        /// </summary>
        private void ConfigureMessageSender(IContainerBuilder builder)
        {
            // 부스 번호와 부스별 운영 로그 URL. TCP 메시지의 deviceNumber와 APIManager의 apiUrl이 같은 번호를 따른다.
            DeviceProfile deviceProfile = JsonLoader.Load<DeviceProfile>(Constants.Files.AppSettings);
            if (deviceProfile.deviceNumber != deviceProfile.ClampedDeviceNumber)
            {
                Debug.LogWarning($"[GameLifetimeScope] deviceNumber {deviceProfile.deviceNumber} in Settings.json is out of range (1–3). Using {deviceProfile.ClampedDeviceNumber}.");
            }

            builder.RegisterInstance(deviceProfile);
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
        /// false면 Space 토글 키보드 구현을 쓴다.
        /// 씬이 바뀌어도 수화기 상태가 유지되도록 루트 스코프에 싱글톤으로 둔다.
        /// </summary>
        private void ConfigureReceiverInput(IContainerBuilder builder)
        {
            // 컨테이너 구성 시점에 구현을 골라야 해서 동기로 읽는다. 작은 파일이라 부팅 지연은 무시할 수준이다.
            DeviceSettings deviceSettings = JsonLoader.Load<DeviceSettings>(Constants.Files.DeviceSettings);
            builder.RegisterInstance(deviceSettings);
            builder.Register<ProjectInputActions>(Lifetime.Singleton);

            if (deviceSettings.useArduino)
            {
                // 템플릿 ArduinoManager 대신 프로젝트의 ArduinoSerialPort로 연결한다(핸드셰이크 대기 시간 때문, ArduinoSerialPort 참고).
                builder.Register<ArduinoSerialPort>(Lifetime.Singleton);
                builder.RegisterEntryPoint<ArduinoReceiverInput>(Lifetime.Singleton).As<IReceiverInput>();
                return;
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
