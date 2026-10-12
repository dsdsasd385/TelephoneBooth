using Idle;
using Media;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace App
{
    /// <summary>
    /// 0_Idle 씬 전용 DI 스코프. 부모는 VContainerSettings의 루트 스코프(GameLifetimeScope)이며,
    /// 씬에 배치된 화면 컴포넌트에 로거 등 루트 등록 의존성을 주입한다.
    /// </summary>
    public class IdleLifetimeScope : LifetimeScope
    {
        [SerializeField] private PageMediaPlayer _pageMediaPlayer;
        [SerializeField] private IdleSceneController _idleSceneController;

        /// <summary>
        /// 인스펙터로 연결한 씬 컴포넌트를 등록해 메서드 주입이 동작하게 함.
        /// </summary>
        protected override void Configure(IContainerBuilder builder)
        {
            if (_pageMediaPlayer)
            {
                builder.RegisterComponent(_pageMediaPlayer);
            }
            else
            {
                Debug.LogError("[IdleLifetimeScope] PageMediaPlayer is not assigned.");
            }

            if (_idleSceneController)
            {
                builder.RegisterComponent(_idleSceneController);
            }
            else
            {
                Debug.LogError("[IdleLifetimeScope] IdleSceneController is not assigned.");
            }
        }
    }
}
