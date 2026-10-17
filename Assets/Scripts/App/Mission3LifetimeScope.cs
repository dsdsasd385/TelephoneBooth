using Media;
using Mission;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace App
{
    /// <summary>
    /// Mission3 씬 전용 DI 스코프. 부모는 VContainerSettings의 루트 스코프(GameLifetimeScope)이며,
    /// 씬에 배치된 재생기·씬 매니저에 로거·수화기 입력·세션 등 루트 등록 의존성을 주입한다.
    /// </summary>
    public class Mission3LifetimeScope : LifetimeScope
    {
        [SerializeField] private PageMediaPlayer _pageMediaPlayer;
        [SerializeField] private Mission3SceneController _sceneController;

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
                Debug.LogError("[Mission3LifetimeScope] PageMediaPlayer is not assigned.");
            }

            if (_sceneController)
            {
                builder.RegisterComponent(_sceneController);
            }
            else
            {
                Debug.LogError("[Mission3LifetimeScope] Mission3SceneController is not assigned.");
            }
        }
    }
}
