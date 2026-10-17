"""전화부스 메시지 TCP 테스트용 간이 수신기(작업계획서 10장).

메시지마다 연결 → 4바이트 빅엔디언 길이 + UTF-8 JSON을 받고 → {"type":"ack","id":...}를 같은 형식으로 돌려준 뒤 연결을 닫는다.
실제 받는 프로그램을 만들기 전 Unity 쪽 전송을 확인하는 용도다. 표준 라이브러리만 쓴다.

사용:
    python test_receiver.py                 # 0.0.0.0:7000에서 ACK
    python test_receiver.py --port 7001
    python test_receiver.py --no-ack        # ACK 없이 닫기(재시도·저장 확인)
    python test_receiver.py --delay 3       # ACK 전에 3초 대기(시간 초과 확인)
    python test_receiver.py --show-text     # 이름·다짐 본문도 출력(기본은 길이만)
"""

import argparse
import json
import socket
import struct
import sys
import time

# Windows 콘솔 기본 인코딩(cp949)이나 파일로 넘길 때 한글이 깨지지 않게 한다.
sys.stdout.reconfigure(encoding="utf-8")


def read_exactly(conn: socket.socket, size: int) -> bytes:
    data = b""
    while len(data) < size:
        chunk = conn.recv(size - len(data))
        if not chunk:
            raise ConnectionError("connection closed before frame was complete")
        data += chunk
    return data


def send_frame(conn: socket.socket, body: str) -> None:
    payload = body.encode("utf-8")
    conn.sendall(struct.pack(">I", len(payload)) + payload)


def handle(conn: socket.socket, addr, args, seen_ids: set) -> None:
    length = struct.unpack(">I", read_exactly(conn, 4))[0]
    body = read_exactly(conn, length).decode("utf-8")
    message = json.loads(body)
    message_id = message.get("id", "")
    duplicate = message_id in seen_ids
    seen_ids.add(message_id)

    stamp = time.strftime("%H:%M:%S")
    name = message.get("name", "")
    text = message.get("text", "")
    summary = (f"name={name!r} text={text!r}" if args.show_text
               else f"name {len(name)} chars, text {len(text)} chars")
    print(f"[{stamp}] {addr[0]} id={message_id} device={message.get('deviceNumber')} "
          f"content={message.get('content')} {summary}{' (duplicate)' if duplicate else ''}", flush=True)

    if args.no_ack:
        print("          no ACK (--no-ack)", flush=True)
        return

    if args.delay > 0:
        time.sleep(args.delay)

    send_frame(conn, json.dumps({"type": "ack", "id": message_id}))


def main() -> None:
    parser = argparse.ArgumentParser(description="Telephone booth message test receiver")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=7000)
    parser.add_argument("--no-ack", action="store_true")
    parser.add_argument("--delay", type=float, default=0.0)
    parser.add_argument("--show-text", action="store_true")
    args = parser.parse_args()

    seen_ids: set = set()
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind((args.host, args.port))
        server.listen()
        print(f"Listening on {args.host}:{args.port} (Ctrl+C to stop)", flush=True)
        while True:
            conn, addr = server.accept()
            with conn:
                try:
                    handle(conn, addr, args, seen_ids)
                except (ConnectionError, ValueError, json.JSONDecodeError) as e:
                    print(f"          bad frame from {addr[0]}: {e}", flush=True)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
