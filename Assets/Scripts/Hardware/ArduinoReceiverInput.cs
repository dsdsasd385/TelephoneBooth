using System;
using System.Threading;
using App;
using Cysharp.Threading.Tasks;
using Data;
using HuliacDev.Hardware;
using Microsoft.Extensions.Logging;
using R3;
using UnityEngine;
using VContainer.Unity;
using ZLogger;

namespace Hardware
{
    /// <summary>
    /// 아두이노 시리얼 통신으로 수화기 상태를 읽는 구현(작업계획서 4.2).
    /// 연결되면 State를 주기적으로 보내고, 응답 On/Off를 IsLifted로 바꾼다. 응답 해석은 이 클래스에서만 한다.
    /// 응답이 끊겨도 마지막 상태를 유지해 케이블 문제로 체험이 갑자기 초기화되지 않게 한다.
    /// </summary>
    public sealed class ArduinoReceiverInput : IReceiverInput, IStartable, IDisposable
    {
        // 템플릿 ConnectAsync가 정해진 횟수만큼 포트를 훑고도 못 찾았을 때 다시 시도하기까지의 대기 시간.
        private const int ConnectRetryDelayMs = 3000;

        private readonly ArduinoManager _arduinoManager;
        private readonly DeviceSettings _deviceSettings;
        private readonly ILogger<ArduinoReceiverInput> _logger;
        private readonly ReactiveProperty<bool> _isLifted = new ReactiveProperty<bool>(false);
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

        private IDisposable _connectedSubscription;
        private IDisposable _disconnectedSubscription;
        private IDisposable _dataSubscription;
        private CancellationTokenSource _pollCts;
        private double _lastResponseTime;
        private bool _isResponseTimedOut;

        public ReadOnlyReactiveProperty<bool> IsLifted => _isLifted;

        /// <summary>
        /// 아두이노 매니저, 장치 설정, 로거를 생성자 주입으로 받음.
        /// </summary>
        public ArduinoReceiverInput(ArduinoManager arduinoManager, DeviceSettings deviceSettings, ILogger<ArduinoReceiverInput> logger)
        {
            _arduinoManager = arduinoManager;
            _deviceSettings = deviceSettings;
            _logger = logger;
        }

        /// <summary>
        /// 연결·끊김·수신 이벤트를 구독하고 아두이노 연결을 시작함.
        /// </summary>
        public void Start()
        {
            _connectedSubscription = _arduinoManager.OnConnected
                .Subscribe(this, static (_, self) => self.OnConnected());
            _disconnectedSubscription = _arduinoManager.OnDisconnected
                .Subscribe(this, static (_, self) => self.OnDisconnected());
            _dataSubscription = _arduinoManager.OnDataReceived
                .Subscribe(this, static (line, self) => self.OnLineReceived(line));

            ConnectUntilSuccessAsync(_lifetimeCts.Token).Forget();
        }

        /// <summary>
        /// 처음 연결될 때까지 포트 탐색을 반복함. 연결된 뒤 끊기면 템플릿의 자동 재연결이 맡는다.
        /// </summary>
        private async UniTaskVoid ConnectUntilSuccessAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && !_arduinoManager.IsConnected)
                {
                    await _arduinoManager.ConnectAsync(Constants.Arduino.BaudRate, Constants.Arduino.Handshake);
                    if (_arduinoManager.IsConnected) break;

                    if (_logger != null) _logger.ZLogWarning($"[ArduinoReceiverInput] Arduino not found. Retrying in {ConnectRetryDelayMs}ms.");
                    await UniTask.Delay(ConnectRetryDelayMs, DelayType.Realtime, cancellationToken: cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// 연결되면 상태 요청 루프를 시작함.
        /// </summary>
        private void OnConnected()
        {
            if (_logger != null) _logger.ZLogInformation($"[ArduinoReceiverInput] Connected. Polling every {_deviceSettings.receiverRequestIntervalMs}ms.");

            StopPolling();
            _lastResponseTime = Time.realtimeSinceStartupAsDouble;
            _isResponseTimedOut = false;
            _pollCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            PollStateAsync(_pollCts).Forget();
        }

        /// <summary>
        /// 연결이 끊기면 요청 루프를 멈춤. 수화기 상태는 마지막 값을 유지한다.
        /// </summary>
        private void OnDisconnected()
        {
            if (_logger != null) _logger.ZLogWarning($"[ArduinoReceiverInput] Disconnected. Keeping last receiver state (lifted: {_isLifted.Value}).");
            StopPolling();
        }

        /// <summary>
        /// 정해진 주기로 State를 보내고 응답 제한 시간을 넘겼는지 확인함.
        /// </summary>
        private async UniTaskVoid PollStateAsync(CancellationTokenSource cts)
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    _arduinoManager.Send(Constants.Arduino.RequestState);
                    CheckResponseTimeout();
                    await UniTask.Delay(_deviceSettings.receiverRequestIntervalMs, DelayType.Realtime, cancellationToken: cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                // 재연결로 새 루프가 이미 시작됐으면 필드는 새 CTS이므로 건드리지 않고 자기 CTS만 해제한다.
                if (_pollCts == cts) _pollCts = null;
                cts.Dispose();
            }
        }

        /// <summary>
        /// 마지막 응답 후 제한 시간이 지났으면 연결 이상 로그를 한 번만 남김.
        /// </summary>
        private void CheckResponseTimeout()
        {
            if (_isResponseTimedOut) return;

            double elapsedMs = (Time.realtimeSinceStartupAsDouble - _lastResponseTime) * 1000.0;
            if (elapsedMs < _deviceSettings.receiverResponseTimeoutMs) return;

            _isResponseTimedOut = true;
            if (_logger != null) _logger.ZLogWarning($"[ArduinoReceiverInput] No response for {_deviceSettings.receiverResponseTimeoutMs}ms. Keeping last receiver state (lifted: {_isLifted.Value}).");
        }

        /// <summary>
        /// 받은 한 줄을 정리해 On/Off면 수화기 상태에 반영함.
        /// </summary>
        private void OnLineReceived(string line)
        {
            string response = line.Trim();

            if (response == Constants.Arduino.ResponseHungUp)
            {
                MarkResponseReceived();
                ApplyLifted(false);
            }
            else if (response == Constants.Arduino.ResponseLifted)
            {
                MarkResponseReceived();
                ApplyLifted(true);
            }
            else if (response.Contains(Constants.Arduino.Handshake))
            {
                // 펌웨어가 연결 직후까지 핸드셰이크를 반복해 보낼 수 있어 정상으로 보고 무시한다.
            }
            else if (_logger != null)
            {
                _logger.ZLogWarning($"[ArduinoReceiverInput] Unknown response '{response}'. Receiver state unchanged.");
            }
        }

        /// <summary>
        /// 응답 시각을 기록하고, 응답이 끊겼다가 돌아왔으면 복구 로그를 남김.
        /// </summary>
        private void MarkResponseReceived()
        {
            _lastResponseTime = Time.realtimeSinceStartupAsDouble;

            if (!_isResponseTimedOut) return;

            _isResponseTimedOut = false;
            if (_logger != null) _logger.ZLogInformation($"[ArduinoReceiverInput] Response recovered.");
        }

        /// <summary>
        /// 수화기 상태가 이전과 다를 때만 값을 바꾸고 로그를 남김.
        /// </summary>
        private void ApplyLifted(bool isLifted)
        {
            if (_isLifted.Value == isLifted) return;

            if (_logger != null) _logger.ZLogInformation($"[ArduinoReceiverInput] Receiver {(isLifted ? "lifted" : "hung up")}.");
            _isLifted.Value = isLifted;
        }

        /// <summary>
        /// 진행 중인 상태 요청 루프를 취소함. CTS 정리는 루프의 finally가 맡는다.
        /// </summary>
        private void StopPolling()
        {
            _pollCts?.Cancel();
        }

        /// <summary>
        /// 연결 시도와 요청 루프를 멈추고 구독과 상태 스트림을 해제함. 포트는 ArduinoManager가 닫는다.
        /// </summary>
        public void Dispose()
        {
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
            _connectedSubscription?.Dispose();
            _disconnectedSubscription?.Dispose();
            _dataSubscription?.Dispose();
            _isLifted.Dispose();
        }
    }
}
