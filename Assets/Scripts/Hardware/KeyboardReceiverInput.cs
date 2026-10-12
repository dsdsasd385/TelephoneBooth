using System;
using Microsoft.Extensions.Logging;
using R3;
using UnityEngine.InputSystem;
using ZLogger;

namespace Hardware
{
    /// <summary>
    /// 아두이노 없이 테스트하기 위한 수화기 입력 구현. Space(ProjectInputActions Receiver/Toggle)를 누를 때마다
    /// 수화기를 들기와 내려놓기를 번갈아 바꾼다. 앱 시작 시에는 내려놓은 상태다.
    /// </summary>
    public sealed class KeyboardReceiverInput : IReceiverInput, IDisposable
    {
        private readonly ProjectInputActions _inputActions;
        private readonly ILogger<KeyboardReceiverInput> _logger;
        private readonly ReactiveProperty<bool> _isLifted = new ReactiveProperty<bool>(false);
        private readonly Action<InputAction.CallbackContext> _onTogglePerformed;

        public ReadOnlyReactiveProperty<bool> IsLifted => _isLifted;

        /// <summary>
        /// Space 액션을 구독하고 Receiver 맵을 켬.
        /// </summary>
        public KeyboardReceiverInput(ProjectInputActions inputActions, ILogger<KeyboardReceiverInput> logger)
        {
            _inputActions = inputActions;
            _logger = logger;
            _onTogglePerformed = OnTogglePerformed;

            _inputActions.Receiver.Toggle.performed += _onTogglePerformed;
            _inputActions.Receiver.Enable();
        }

        /// <summary>
        /// Space를 누르면 수화기 상태를 반대로 바꿈.
        /// </summary>
        private void OnTogglePerformed(InputAction.CallbackContext context)
        {
            bool isLifted = !_isLifted.Value;
            if (_logger != null) _logger.ZLogInformation($"[KeyboardReceiverInput] Receiver {(isLifted ? "lifted" : "hung up")} (Space).");
            _isLifted.Value = isLifted;
        }

        /// <summary>
        /// 액션 구독을 풀고 맵을 끄며 상태 스트림을 해제함.
        /// </summary>
        public void Dispose()
        {
            _inputActions.Receiver.Toggle.performed -= _onTogglePerformed;
            _inputActions.Receiver.Disable();
            _isLifted.Dispose();
        }
    }
}
