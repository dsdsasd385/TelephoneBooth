using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network;
using NUnit.Framework;
using UnityEngine;

namespace Tests
{
    /// <summary>
    /// 메시지 TCP 전송 규약(4바이트 빅엔디언 길이 + UTF-8 JSON, ACK) 확인. 루프백에 임시 수신기를 띄워 실제 소켓으로 주고받는다.
    /// </summary>
    public class TcpMessageClientTests
    {
        private const int TimeoutMs = 2000;

        [Test]
        public void Frame_WritesBigEndianLengthAndUtf8Body()
        {
            byte[] frame = TcpMessageClient.Frame("가A");

            // "가"는 UTF-8 3바이트, "A"는 1바이트.
            Assert.AreEqual(new byte[] { 0, 0, 0, 4 }, new[] { frame[0], frame[1], frame[2], frame[3] });
            Assert.AreEqual(4, TcpMessageClient.ReadLength(frame));
            Assert.AreEqual("가A", Encoding.UTF8.GetString(frame, 4, frame.Length - 4));
        }

        [Test]
        public void ReadLength_LargeValue()
        {
            Assert.AreEqual(0x01020304, TcpMessageClient.ReadLength(new byte[] { 1, 2, 3, 4 }));
        }

        [Test]
        public void IsAckFor_MatchesTypeAndId()
        {
            Assert.IsTrue(TcpMessageClient.IsAckFor("{\"type\":\"ack\",\"id\":\"abc\"}", "abc"));
            Assert.IsFalse(TcpMessageClient.IsAckFor("{\"type\":\"ack\",\"id\":\"other\"}", "abc"));
            Assert.IsFalse(TcpMessageClient.IsAckFor("{\"type\":\"nack\",\"id\":\"abc\"}", "abc"));
            Assert.IsFalse(TcpMessageClient.IsAckFor("not json", "abc"));
            Assert.IsFalse(TcpMessageClient.IsAckFor("", "abc"));
        }

        [Test]
        public void SendAsync_ReceiverAcks_ReturnsTrueAndDeliversPayload()
        {
            using (TestReceiver receiver = TestReceiver.Start(ReceiverMode.Ack))
            {
                MessagePayload payload = CreatePayload();

                bool isAcked = Run(() => TcpMessageClient.SendAsync("127.0.0.1", receiver.Port, payload, TimeoutMs, CancellationToken.None));

                Assert.IsTrue(isAcked);
                MessagePayload received = JsonUtility.FromJson<MessagePayload>(receiver.WaitForBody());
                Assert.AreEqual(payload.id, received.id);
                Assert.AreEqual("message", received.type);
                Assert.AreEqual("홍길동", received.name);
                Assert.AreEqual("독립 정신을 기억할게요", received.text);
                Assert.AreEqual("truth", received.content);
                Assert.AreEqual(2, received.deviceNumber);
            }
        }

        [Test]
        public void SendAsync_AckForOtherId_ReturnsFalse()
        {
            using (TestReceiver receiver = TestReceiver.Start(ReceiverMode.WrongId))
            {
                bool isAcked = Run(() => TcpMessageClient.SendAsync("127.0.0.1", receiver.Port, CreatePayload(), TimeoutMs, CancellationToken.None));
                Assert.IsFalse(isAcked);
            }
        }

        [Test]
        public void SendAsync_ReceiverClosesWithoutAck_Throws()
        {
            using (TestReceiver receiver = TestReceiver.Start(ReceiverMode.CloseWithoutAck))
            {
                Exception e = RunExpectingException(() => TcpMessageClient.SendAsync("127.0.0.1", receiver.Port, CreatePayload(), TimeoutMs, CancellationToken.None));
                Assert.IsInstanceOf<IOException>(e);
            }
        }

        [Test]
        public void SendAsync_ReceiverNeverAnswers_ThrowsTimeoutWithinLimit()
        {
            using (TestReceiver receiver = TestReceiver.Start(ReceiverMode.Silent))
            {
                DateTime start = DateTime.UtcNow;
                Exception e = RunExpectingException(() => TcpMessageClient.SendAsync("127.0.0.1", receiver.Port, CreatePayload(), 500, CancellationToken.None));
                double elapsedMs = (DateTime.UtcNow - start).TotalMilliseconds;

                Assert.IsInstanceOf<TimeoutException>(e);
                Assert.Less(elapsedMs, 1500);
            }
        }

        [Test]
        public void SendAsync_NobodyListening_Throws()
        {
            int port = TestReceiver.GetFreePort();
            Exception e = RunExpectingException(() => TcpMessageClient.SendAsync("127.0.0.1", port, CreatePayload(), TimeoutMs, CancellationToken.None));
            Assert.IsNotNull(e);
        }

        private static MessagePayload CreatePayload()
        {
            return new MessagePayload
            {
                id = Guid.NewGuid().ToString("N"),
                deviceNumber = 2,
                content = "truth",
                name = "홍길동",
                text = "독립 정신을 기억할게요",
                sentAt = "2026-10-18T12:00:00.0000000+09:00"
            };
        }

        // 메인 스레드에서 막고 기다려도 교착이 없도록 스레드 풀에서 실행한다(TcpMessageClient는 ConfigureAwait(false)).
        private static T Run<T>(Func<Task<T>> action)
        {
            return Task.Run(action).GetAwaiter().GetResult();
        }

        private static Exception RunExpectingException(Func<Task<bool>> action)
        {
            try
            {
                Run(action);
            }
            catch (Exception e)
            {
                return e;
            }

            Assert.Fail("Expected an exception.");
            return null;
        }

        private enum ReceiverMode
        {
            Ack,
            WrongId,
            CloseWithoutAck,
            Silent
        }

        /// <summary>
        /// 연결 하나를 받아 본문을 읽고 모드에 따라 응답하는 루프백 수신기.
        /// </summary>
        private sealed class TestReceiver : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Task<string> _task;

            public int Port { get; }

            private TestReceiver(ReceiverMode mode)
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _task = Task.Run(() => Serve(mode));
            }

            public static TestReceiver Start(ReceiverMode mode)
            {
                return new TestReceiver(mode);
            }

            public static int GetFreePort()
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }

            public string WaitForBody()
            {
                Assert.IsTrue(_task.Wait(TimeoutMs), "Receiver did not get a message.");
                return _task.Result;
            }

            public void Dispose()
            {
                _listener.Stop();
            }

            private string Serve(ReceiverMode mode)
            {
                using (TcpClient client = _listener.AcceptTcpClient())
                {
                    NetworkStream stream = client.GetStream();
                    byte[] prefix = ReadExactly(stream, 4);
                    string body = Encoding.UTF8.GetString(ReadExactly(stream, TcpMessageClient.ReadLength(prefix)));
                    string id = JsonUtility.FromJson<MessagePayload>(body).id;

                    switch (mode)
                    {
                        case ReceiverMode.Ack:
                            Write(stream, "{\"type\":\"ack\",\"id\":\"" + id + "\"}");
                            break;
                        case ReceiverMode.WrongId:
                            Write(stream, "{\"type\":\"ack\",\"id\":\"someone-else\"}");
                            break;
                        case ReceiverMode.Silent:
                            Thread.Sleep(TimeoutMs);
                            break;
                    }

                    return body;
                }
            }

            private static void Write(NetworkStream stream, string body)
            {
                byte[] frame = TcpMessageClient.Frame(body);
                stream.Write(frame, 0, frame.Length);
            }

            private static byte[] ReadExactly(NetworkStream stream, int size)
            {
                byte[] buffer = new byte[size];
                int offset = 0;
                while (offset < size)
                {
                    int read = stream.Read(buffer, offset, size - offset);
                    if (read <= 0) throw new EndOfStreamException();
                    offset += read;
                }

                return buffer;
            }
        }
    }
}
