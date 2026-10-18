using System;
using System.Threading;
using App;
using Cysharp.Threading.Tasks;
using Data;
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
        // 포트를 한 바퀴 훑고도 못 찾았을 때 다시 훑기까지의 대기 시간.
        private const int ConnectRetryDelayMs = 1000;

        private readonly ArduinoSerialPort _serialPort;
        private readonly DeviceSettings _deviceSettings;
        private readonly ILogger<ArduinoReceiverInput> _logger;
        private readonly ReactiveProperty<bool> _isLifted = new ReactiveProperty<bool>(false);
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

        private IDisposable _disconnectedSubscription;
        private IDisposable _dataSubscription;
        private CancellationTokenSource _pollCts;
        private double _lastResponseTime;
        private bool _isResponseTimedOut;
        private bool _hasFirstResponse;
        private bool _isConnecting;
        private bool _isDisposed;

        public ReadOnlyReactiveProperty<bool> IsLifted => _isLifted;

        /// <summary>
        /// 시리얼 연결, 장치 설정, 로거를 생성자 주입으로 받음.
        /// </summary>
        public ArduinoReceiverInput(ArduinoSerialPort serialPort, DeviceSettings deviceSettings, ILogger<ArduinoReceiverInput> logger)
        {
            _serialPort = serialPort;
            _deviceSettings = deviceSettings;
            _logger = logger;
        }

        /// <summary>
        /// 끊김·수신 이벤트를 구독하고 아두이노 연결을 시작함.
        /// </summary>
        public void Start()
        {
            _disconnectedSubscription = _serialPort.OnDisconnected
                .Subscribe(this, static (_, self) => self.OnDisconnected());
            _dataSubscription = _serialPort.OnLineReceived
                .Subscribe(this, static (line, self) => self.OnLineReceived(line));

            ConnectUntilSuccessAsync(_lifetimeCts.Token).Forget();
        }

        /// <summary>
        /// 연결될 때까지 포트 탐색을 반복함. 처음 연결할 때와 끊긴 뒤 다시 연결할 때 모두 쓴다.
        /// </summary>
        private async UniTaskVoid ConnectUntilSuccessAsync(CancellationToken cancellationToken)
        {
            if (_isConnecting) return;
            _isConnecting = true;

            double startTime = Time.realtimeSinceStartupAsDouble;
            int scanCount = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    scanCount++;
                    bool isConnected = await _serialPort.TryConnectAsync(
                        Constants.Arduino.BaudRate, Constants.Arduino.Handshake, _deviceSettings.receiverHandshakeTimeoutMs, cancellationToken);
                    if (cancellationToken.IsCancellationRequested) break;

                    if (isConnected)
                    {
                        double elapsedMs = (Time.realtimeSinceStartupAsDouble - startTime) * 1000.0;
                        OnConnected(scanCount, elapsedMs);
                        break;
                    }

                    // 아두이노가 빠져 있으면 매 초 실패하므로 처음 한 번만 남기고, 연결되면 몇 번 만에 붙었는지 남긴다.
                    if (scanCount == 1 && _logger != null)
                    {
                        _logger.ZLogWarning($"[ArduinoReceiverInput] Arduino not found. Retrying every {ConnectRetryDelayMs}ms.");
                    }

                    await UniTask.Delay(ConnectRetryDelayMs, DelayType.Realtime, cancellationToken: cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _isConnecting = false;
            }
        }

        /// <summary>
        /// 연결되면 상태 요청 루프를 시작함.
        /// </summary>
        private void OnConnected(int scanCount, double elapsedMs)
        {
            if (_logger != null) _logger.ZLogInformation($"[ArduinoReceiverInput] Arduino connected (handshake '{Constants.Arduino.Handshake}' OK) after {scanCount} scan(s), {elapsedMs:F0}ms. Polling every {_deviceSettings.receiverRequestIntervalMs}ms.");

            StopPolling();
            _lastResponseTime = Time.realtimeSinceStartupAsDouble;
            _isResponseTimedOut = false;
            _hasFirstResponse = false;
            _pollCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            PollStateAsync(_pollCts).Forget();
        }

        /// <summary>
        /// 연결이 끊기면 요청 루프를 멈추고 다시 연결을 시도함. 수화기 상태는 마지막 값을 유지한다.
        /// </summary>
        private void OnDisconnected()
        {
            if (_isDisposed) return;

            if (_logger != null) _logger.ZLogWarning($"[ArduinoReceiverInput] Disconnected. Keeping last receiver state (lifted: {_isLifted.Value}). Reconnecting.");
            StopPolling();
            ConnectUntilSuccessAsync(_lifetimeCts.Token).Forget();
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
                    _serialPort.Send(Constants.Arduino.RequestState);
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

            // On/Off 응답만 남긴다. 핸드셰이크·알 수 없는 줄은 아래 분기에서 따로 처리한다.
            bool isStateResponse = response == Constants.Arduino.ResponseHungUp || response == Constants.Arduino.ResponseLifted;
            if (isStateResponse && _deviceSettings.logReceiverResponses && _logger != null)
            {
                _logger.ZLogInformation($"[ArduinoReceiverInput] {Constants.Arduino.RequestState} -> '{response}'");
            }

            if (response == Constants.Arduino.ResponseHungUp)
            {
                MarkResponseReceived(response);
                ApplyLifted(false);
            }
            else if (response == Constants.Arduino.ResponseLifted)
            {
                MarkResponseReceived(response);
                ApplyLifted(true);
            }
            else if (response.Length == 0 || response.Contains(Constants.Arduino.Handshake) || Constants.Arduino.Handshake.Contains(response))
            {
                // 펌웨어는 첫 State를 받을 때까지 핸드셰이크를 반복해 보낸다. 연결 직후 그 줄이나 앞이 잘린 조각이 들어올 수 있어 무시한다.
            }
            else if (_logger != null)
            {
                _logger.ZLogWarning($"[ArduinoReceiverInput] Unknown response '{response}'. Receiver state unchanged.");
            }
        }

        /// <summary>
        /// 응답 시각을 기록하고, 응답이 끊겼다가 돌아왔으면 복구 로그를 남김.
        /// </summary>
        private void MarkResponseReceived(string response)
        {
            _lastResponseTime = Time.realtimeSinceStartupAsDouble;

            if (!_hasFirstResponse)
            {
                // 연결 후 첫 On/Off는 상태가 바뀌지 않아도 남겨 State 요청·응답이 통하는지 확인할 수 있게 한다.
                _hasFirstResponse = true;
                if (_logger != null) _logger.ZLogInformation($"[ArduinoReceiverInput] First state response '{response}' received. Communication OK.");
            }

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
        /// 연결 시도와 요청 루프를 멈추고 구독과 상태 스트림을 해제함. 포트는 ArduinoSerialPort가 닫는다.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            _disconnectedSubscription?.Dispose();
            _dataSubscription?.Dispose();
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
            _isLifted.Dispose();
        }
    }
}
