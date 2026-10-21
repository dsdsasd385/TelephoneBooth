using System;
using HuliacDev.Core;
using Media;
using Microsoft.Extensions.Logging;
using UnityEngine.SceneManagement;
using VContainer.Unity;
using ZLogger;

namespace Core
{
    /// <summary>
    /// 템플릿 InactivityTimer를 화면 상태에 맞춰 멈추고 다시 세게 하는 루트 서비스(작업계획서 5.1).
    /// <para>
    /// 씬 매니저가 버튼을 누를 수 있게 된 순간 OpenInput, 누를 수 없게 되는 순간 CloseInput을 부른다.
    /// 입력이 열려 있고 TTS가 재생 중이 아닐 때만 세고, 그 밖(① 대기, 자동 진행 화면, 연출, TTS 재생 중)에는 Pause한다.
    /// TTS가 끝나 다시 셀 때는 InactivityTimer.Resume이 처음부터 센다.
    /// </para>
    /// <para>
    /// OpenInput은 부를 때마다 카운트를 처음부터 다시 시작하므로 화면이 바뀔 때 부르면 초기화된다.
    /// 씬을 불러오면 이전 씬의 상태가 남지 않도록 자동으로 닫힌다.
    /// </para>
    /// </summary>
    public sealed class InactivityTimerGate : IStartable, ITickable, IDisposable
    {
        #region Private Fields

        private readonly InactivityTimer _timer;
        private readonly ILogger<InactivityTimerGate> _logger;
        private PageMediaPlayer _ttsPlayer;
        private bool _isInputOpen;
        // 템플릿 타이머는 멈추지 않은 상태로 시작하므로 첫 Refresh에서 Pause되게 true로 둔다.
        private bool _isCounting = true;

        #endregion

        #region Constructor & Lifecycle

        public InactivityTimerGate(InactivityTimer timer, ILogger<InactivityTimerGate> logger)
        {
            _timer = timer;
            _logger = logger;
        }

        /// <summary>
        /// 씬 로드를 구독하고 입력이 닫힌 상태(정지)로 시작함.
        /// </summary>
        public void Start()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            Refresh();
        }

        /// <summary>
        /// 매 프레임 TTS 재생 여부를 보고 타이머를 멈추거나 다시 셈.
        /// </summary>
        public void Tick()
        {
            Refresh();
        }

        public void Dispose()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 버튼을 누를 수 있게 됨. 카운트를 처음부터 다시 시작하고, ttsPlayer의 TTS가 재생되는 동안은 멈춘다(TTS가 없는 씬은 null).
        /// </summary>
        public void OpenInput(PageMediaPlayer ttsPlayer)
        {
            _ttsPlayer = ttsPlayer;
            _isInputOpen = true;
            if (_timer) _timer.ResetTimer();
            Refresh();
        }

        /// <summary>
        /// 버튼을 누를 수 없게 됨(화면 전환, 연출, 자동 진행 화면). 다시 열 때까지 타이머를 멈춘다.
        /// </summary>
        public void CloseInput()
        {
            _isInputOpen = false;
            _ttsPlayer = null;
            Refresh();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 새 씬은 씬 매니저가 다시 열 때까지 닫힌 상태로 둠. sceneLoaded는 새 씬의 Start보다 먼저 온다.
        /// </summary>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            CloseInput();
        }

        /// <summary>
        /// 셀지 말지가 바뀐 순간에만 Pause/Resume을 부름.
        /// </summary>
        private void Refresh()
        {
            bool shouldCount = _isInputOpen && !(_ttsPlayer && _ttsPlayer.IsTtsPlaying);
            if (shouldCount == _isCounting) return;

            _isCounting = shouldCount;
            if (!_timer) return;

            if (shouldCount)
            {
                _timer.Resume();
                if (_logger != null) _logger.ZLogInformation($"[InactivityTimerGate] Counting.");
            }
            else
            {
                _timer.Pause();
                if (_logger != null) _logger.ZLogInformation($"[InactivityTimerGate] Paused.");
            }
        }

        #endregion
    }
}
