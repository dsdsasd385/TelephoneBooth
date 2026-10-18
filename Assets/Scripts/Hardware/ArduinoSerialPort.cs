using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using R3;
using ZLogger;

namespace Hardware
{
    /// <summary>
    /// 수화기 아두이노와의 시리얼 연결. 템플릿 ArduinoManager 대신 쓴다.
    /// 템플릿은 포트를 연 뒤 첫 줄을 500ms만 기다리는데, Uno·Nano는 포트를 열 때마다 재부팅되고 부트로더가 그보다 오래 걸려
    /// 연결이 운에 맡겨졌다(측정 평균 24초, 최대 73초). 여기서는 핸드셰이크를 정해진 시간 동안 여러 줄에 걸쳐 기다리고,
    /// 모든 포트를 동시에 시도한다. 템플릿 저장소는 이 프로젝트에서 고칠 수 없어 프로젝트에 둔다.
    /// </summary>
    public sealed class ArduinoSerialPort : IDisposable
    {
        // ReadLine 한 번의 대기 시간. 핸드셰이크 대기 중 취소를 확인하는 간격이자, 종료 시 읽기 스레드가 빠져나오는 시간이다.
        private const int ReadTimeoutMs = 100;
        private const int WriteTimeoutMs = 500;
        private const int ReadThreadJoinTimeoutMs = 500;

        private readonly ILogger<ArduinoSerialPort> _logger;
        private readonly object _connectionLock = new object();
        private readonly Subject<string> _lineSubject = new Subject<string>();
        private readonly Subject<Unit> _disconnectedSubject = new Subject<Unit>();

        private SerialPort _port;
        private Thread _readThread;
        private volatile bool _isRunning;
        private volatile bool _isDisposed;

        /// <summary>받은 한 줄. 메인 스레드에서 발행된다.</summary>
        public Observable<string> OnLineReceived => _lineSubject.ObserveOnMainThread();

        /// <summary>연결된 포트에서 읽기 오류로 연결이 끊겼을 때 발행된다. 메인 스레드에서 발행된다.</summary>
        public Observable<Unit> OnDisconnected => _disconnectedSubject.ObserveOnMainThread();

        /// <summary>
        /// 포트가 열려 있는지 여부. 읽기 스레드가 필드를 언제든 비울 수 있어 한 번 복사해 검사한다.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                SerialPort port = _port;
                return port != null && port.IsOpen;
            }
        }

        /// <summary>
        /// 로거를 생성자 주입으로 받음.
        /// </summary>
        public ArduinoSerialPort(ILogger<ArduinoSerialPort> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 모든 시리얼 포트를 한 바퀴 훑어 핸드셰이크가 오는 포트에 연결함. 찾으면 true를 돌려준다.
        /// </summary>
        public async UniTask<bool> TryConnectAsync(int baudRate, string handshake, int handshakeTimeoutMs, CancellationToken cancellationToken)
        {
            if (IsConnected) return true;

            // 취소 토큰을 RunOnThreadPool에 넘기지 않는다. 넘기면 포트를 찾은 직후 취소될 때 열린 포트를 받지 못해 OS 점유가 남는다.
            SerialPort foundPort = await UniTask.RunOnThreadPool(
                () => FindPort(baudRate, handshake, handshakeTimeoutMs, cancellationToken));

            if (foundPort == null) return false;

            if (cancellationToken.IsCancellationRequested || _isDisposed)
            {
                DisposePort(foundPort);
                return false;
            }

            StartReading(foundPort);
            return true;
        }

        /// <summary>
        /// 아두이노로 한 줄을 보냄. 연결돼 있지 않으면 아무것도 하지 않는다.
        /// </summary>
        public void Send(string message)
        {
            SerialPort port = _port;
            if (port == null || !port.IsOpen) return;

            try
            {
                port.WriteLine(message);
            }
            catch (Exception e)
            {
                if (_logger != null) _logger.ZLogWarning($"[ArduinoSerialPort] Send failed: {e.Message}");
            }
        }

        /// <summary>
        /// 모든 포트를 동시에 열어 가장 먼저 핸드셰이크가 온 포트를 돌려줌. 스레드 풀에서 실행된다.
        /// 포트마다 핸드셰이크를 수 초 기다리므로 차례로 열면 아두이노가 아닌 포트 수만큼 연결이 늦어진다.
        /// </summary>
        private SerialPort FindPort(int baudRate, string handshake, int handshakeTimeoutMs, CancellationToken cancellationToken)
        {
            string[] portNames = SerialPort.GetPortNames();
            if (portNames.Length == 0) return null;

            Stopwatch stopwatch = Stopwatch.StartNew();
            using CancellationTokenSource scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<SerialPort>[] tasks = new Task<SerialPort>[portNames.Length];
            for (int i = 0; i < portNames.Length; i++)
            {
                string portName = portNames[i];
                tasks[i] = Task.Run(() => TryOpenWithHandshake(portName, baudRate, handshake, handshakeTimeoutMs, scanCts.Token));
            }

            SerialPort foundPort = WaitForFirstPort(tasks);

            // 찾았으면 나머지 포트의 대기를 끊는다. 모두 끝날 때까지 기다려 늦게 성공한 포트도 닫는다.
            scanCts.Cancel();
            Task.WaitAll(tasks);
            foreach (Task<SerialPort> task in tasks)
            {
                if (task.Result != null && task.Result != foundPort) DisposePort(task.Result);
            }

            if (foundPort != null && _logger != null)
            {
                _logger.ZLogInformation($"[ArduinoSerialPort] Handshake received on {foundPort.PortName} in {stopwatch.ElapsedMilliseconds}ms ({portNames.Length} port(s) scanned).");
            }

            return foundPort;
        }

        /// <summary>
        /// 포트별 시도 중 가장 먼저 성공한 포트를 돌려줌. 모두 실패하면 null이다.
        /// </summary>
        private static SerialPort WaitForFirstPort(Task<SerialPort>[] tasks)
        {
            List<Task<SerialPort>> pending = new List<Task<SerialPort>>(tasks);
            while (pending.Count > 0)
            {
                int index = Task.WaitAny(pending.ToArray());
                Task<SerialPort> completed = pending[index];
                if (completed.Result != null) return completed.Result;
                pending.RemoveAt(index);
            }

            return null;
        }

        /// <summary>
        /// 포트 하나를 열고 핸드셰이크가 담긴 줄이 올 때까지 정해진 시간 동안 읽음. 실패하면 포트를 닫고 null을 돌려준다.
        /// </summary>
        private SerialPort TryOpenWithHandshake(string portName, int baudRate, string handshake, int handshakeTimeoutMs, CancellationToken cancellationToken)
        {
            SerialPort port = null;

            try
            {
                // DTR은 템플릿과 같이 켠다. Leonardo 같은 USB 내장 보드는 DTR이 꺼져 있으면 데이터를 보내지 않는다.
                port = new SerialPort(portName, baudRate)
                {
                    DtrEnable = true,
                    ReadTimeout = ReadTimeoutMs,
                    WriteTimeout = WriteTimeoutMs
                };
                port.Open();

                Stopwatch stopwatch = Stopwatch.StartNew();
                while (stopwatch.ElapsedMilliseconds < handshakeTimeoutMs && !cancellationToken.IsCancellationRequested)
                {
                    string line;
                    try
                    {
                        line = port.ReadLine();
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }

                    if (line.Contains(handshake))
                    {
                        // 성공한 포트는 닫지 않고 소유권을 넘긴다.
                        SerialPort connectedPort = port;
                        port = null;
                        return connectedPort;
                    }
                }
            }
            catch (Exception)
            {
                // 다른 프로그램이 쓰는 포트나 분리된 장치는 열기·읽기에서 예외가 난다. 다음 포트로 넘어간다.
            }
            finally
            {
                if (port != null) DisposePort(port);
            }

            return null;
        }

        /// <summary>
        /// 연결된 포트를 필드에 두고 수신 스레드를 시작함.
        /// </summary>
        private void StartReading(SerialPort port)
        {
            // 핸드셰이크를 받은 뒤 메인 스레드로 돌아오는 사이 쌓인 핸드셰이크 줄을 버린다. 남기면 연결 직후 한꺼번에 쏟아진다.
            try
            {
                port.DiscardInBuffer();
            }
            catch (Exception e)
            {
                if (_logger != null) _logger.ZLogWarning($"[ArduinoSerialPort] Failed to discard input buffer on {port.PortName}: {e.Message}");
            }

            lock (_connectionLock)
            {
                _port = port;
                _isRunning = true;
                _readThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                    Name = "ArduinoSerialPort.Read"
                };
                _readThread.Start(port);
            }
        }

        /// <summary>
        /// 연결이 유지되는 동안 줄 단위로 읽어 발행함. 읽기 오류가 나면 연결을 닫고 끊김을 알린다.
        /// </summary>
        private void ReadLoop(object state)
        {
            SerialPort port = (SerialPort)state;

            while (_isRunning && port.IsOpen)
            {
                try
                {
                    string line = port.ReadLine();
                    if (line.Length > 0) _lineSubject.OnNext(line);
                }
                catch (TimeoutException)
                {
                    // 응답이 없는 구간의 정상 대기 시간 초과.
                }
                catch (Exception e)
                {
                    // 정상 종료 중에는 포트를 닫으며 나는 예외라 무시한다.
                    if (!_isRunning) return;

                    if (_logger != null) _logger.ZLogWarning($"[ArduinoSerialPort] Read error on {port.PortName}: {e.Message}");
                    Close(true);
                    return;
                }
            }
        }

        /// <summary>
        /// 수신 스레드를 멈추고 포트를 닫음. 메인 스레드와 읽기 스레드 양쪽에서 불려도 한 번만 정리한다.
        /// </summary>
        private void Close(bool notifyDisconnected)
        {
            Thread threadToJoin;
            SerialPort portToDispose;

            lock (_connectionLock)
            {
                if (_port == null) return;

                _isRunning = false;
                threadToJoin = _readThread;
                portToDispose = _port;
                _readThread = null;
                _port = null;
            }

            // 읽기 스레드가 스스로 Close를 부른 경우 자기 자신을 기다리지 않는다.
            if (threadToJoin != null && threadToJoin != Thread.CurrentThread && threadToJoin.IsAlive)
            {
                threadToJoin.Join(ReadThreadJoinTimeoutMs);
            }

            string portName = portToDispose.PortName;
            DisposePort(portToDispose);
            if (_logger != null) _logger.ZLogInformation($"[ArduinoSerialPort] Port {portName} closed.");

            if (notifyDisconnected && !_isDisposed) _disconnectedSubject.OnNext(Unit.Default);
        }

        /// <summary>
        /// 포트를 해제함. 장치가 분리된 상태에서는 해제에서도 예외가 날 수 있어 무시한다.
        /// </summary>
        private static void DisposePort(SerialPort port)
        {
            try
            {
                port.Dispose();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 포트를 닫고 스트림을 해제함. 다음 실행에서 포트를 바로 열 수 있도록 점유를 남기지 않는다.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            Close(false);
            _lineSubject.Dispose();
            _disconnectedSubject.Dispose();
        }
    }
}
