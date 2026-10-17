using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Network
{
    /// <summary>
    /// 메시지 한 건을 TCP로 보내고 ACK를 받는다(작업계획서 10장). 메시지마다 연결 → 전송 → ACK 수신 → 종료.
    /// <para>
    /// 데이터 경계: 4바이트 길이(빅엔디언) + UTF-8 본문. 보내는 쪽·받는 쪽 모두 같은 형식이다.
    /// 소켓 I/O만 하는 순수 클래스라 Unity 메인 스레드에 묶이지 않는다(모든 await는 ConfigureAwait(false)).
    /// 재시도·저장·로그는 호출하는 쪽(MessageSender)이 맡는다.
    /// </para>
    /// </summary>
    public static class TcpMessageClient
    {
        #region Constants

        // 길이 접두 크기(바이트).
        public const int LengthPrefixSize = 4;
        // 받는 응답 본문의 최대 크기(바이트). 이보다 크다고 하면 잘못된 응답으로 본다.
        public const int MaxResponseBytes = 64 * 1024;

        #endregion

        #region Public Methods

        /// <summary>
        /// 메시지를 보내고 받는 쪽이 같은 id의 ACK를 돌려주면 true. ACK가 아니거나 id가 다르면 false.
        /// 연결 실패·시간 초과·연결 끊김은 예외로 던진다(시간 초과는 TimeoutException).
        /// </summary>
        public static async Task<bool> SendAsync(string host, int port, MessagePayload payload, int timeoutMs, CancellationToken cancellationToken)
        {
            string response = await SendRawAsync(host, port, JsonUtility.ToJson(payload), timeoutMs, cancellationToken).ConfigureAwait(false);
            return IsAckFor(response, payload.id);
        }

        /// <summary>
        /// 본문 문자열 하나를 길이 접두로 보내고 길이 접두 응답 하나를 받아 문자열로 돌려줌. timeoutMs는 연결부터 응답까지 전체 제한 시간이다.
        /// </summary>
        public static async Task<string> SendRawAsync(string host, int port, string body, int timeoutMs, CancellationToken cancellationToken)
        {
            using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (TcpClient client = new TcpClient())
            {
                timeoutCts.CancelAfter(timeoutMs);
                // 취소되면 소켓을 닫아 진행 중인 연결·읽기를 끝낸다(.NET Standard 2.1의 소켓 API는 토큰을 받지 않는 것이 있다).
                using (timeoutCts.Token.Register(() => client.Close()))
                {
                    try
                    {
                        await client.ConnectAsync(host, port).ConfigureAwait(false);
                        NetworkStream stream = client.GetStream();

                        byte[] frame = Frame(body);
                        await stream.WriteAsync(frame, 0, frame.Length, timeoutCts.Token).ConfigureAwait(false);
                        await stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

                        byte[] prefix = new byte[LengthPrefixSize];
                        await ReadExactlyAsync(stream, prefix, timeoutCts.Token).ConfigureAwait(false);
                        int length = ReadLength(prefix);
                        if (length < 0 || length > MaxResponseBytes)
                        {
                            throw new InvalidDataException($"Invalid response length: {length}");
                        }

                        byte[] response = new byte[length];
                        await ReadExactlyAsync(stream, response, timeoutCts.Token).ConfigureAwait(false);
                        return Encoding.UTF8.GetString(response);
                    }
                    catch (Exception e) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !(e is TimeoutException))
                    {
                        throw new TimeoutException($"No response within {timeoutMs} ms.", e);
                    }
                }
            }
        }

        /// <summary>
        /// 본문을 4바이트 빅엔디언 길이 + UTF-8 바이트로 만듦.
        /// </summary>
        public static byte[] Frame(string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            byte[] frame = new byte[LengthPrefixSize + bytes.Length];
            WriteLength(frame, bytes.Length);
            Buffer.BlockCopy(bytes, 0, frame, LengthPrefixSize, bytes.Length);
            return frame;
        }

        /// <summary>
        /// 4바이트 빅엔디언 길이를 읽음.
        /// </summary>
        public static int ReadLength(byte[] prefix)
        {
            return (prefix[0] << 24) | (prefix[1] << 16) | (prefix[2] << 8) | prefix[3];
        }

        /// <summary>
        /// 응답이 그 id의 ACK인지 확인함. JSON이 아니면 false.
        /// </summary>
        public static bool IsAckFor(string response, string id)
        {
            if (string.IsNullOrEmpty(response)) return false;

            try
            {
                AckPayload ack = JsonUtility.FromJson<AckPayload>(response);
                return ack != null && ack.type == AckPayload.AckType && ack.id == id;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 버퍼 앞 4바이트에 빅엔디언 길이를 씀.
        /// </summary>
        private static void WriteLength(byte[] buffer, int length)
        {
            buffer[0] = (byte)(length >> 24);
            buffer[1] = (byte)(length >> 16);
            buffer[2] = (byte)(length >> 8);
            buffer[3] = (byte)length;
        }

        /// <summary>
        /// 버퍼가 찰 때까지 읽음. 상대가 그 전에 연결을 닫으면(0바이트) EndOfStreamException.
        /// </summary>
        private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Connection closed before the response was complete.");
                }

                offset += read;
            }
        }

        #endregion
    }
}
