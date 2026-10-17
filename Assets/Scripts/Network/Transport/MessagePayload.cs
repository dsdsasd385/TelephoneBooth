using System;

namespace Network
{
    /// <summary>
    /// 메시지 TCP 전송 본문(작업계획서 10장). UTF-8 JSON으로 보낸다.
    /// 예: {"type":"message","id":"…","deviceNumber":1,"content":"culture","name":"…","text":"…","sentAt":"2026-10-18T12:00:00.0000000+09:00"}
    /// content는 마지막으로 한 미션(culture·truth·nation, 모르면 none). 받는 쪽은 id로 중복을 거른다.
    /// </summary>
    [Serializable]
    public class MessagePayload
    {
        public const string MessageType = "message";

        public string type = MessageType;
        public string id;
        public int deviceNumber;
        public string content;
        public string name;
        public string text;
        public string sentAt;
    }

    /// <summary>
    /// 받는 쪽 응답. {"type":"ack","id":"…"}이고 id가 보낸 메시지와 같으면 성공이다.
    /// </summary>
    [Serializable]
    public class AckPayload
    {
        public const string AckType = "ack";

        public string type;
        public string id;
    }
}
