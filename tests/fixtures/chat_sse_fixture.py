#!/usr/bin/env python3
"""TEST ONLY: loopback Chat Completions protocol fixture; never a real model.

python3 tests/fixtures/chat_sse_fixture.py --port 18081
Base URL: http://127.0.0.1:18081/v1
Synthetic key: mindweave-loopback-only
Models: fixture-normal, fixture-slow. Titles always return a fixed test title.
Stop with Ctrl+C after native validation. Does not store bodies, keys or chat data.
"""
import argparse
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SYNTHETIC_KEY = "mindweave-loopback-only"


class FixtureHandler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def authorized(self):
        if self.headers.get("Authorization") != "Bearer " + SYNTHETIC_KEY:
            self.json_response(401, {"error": {"code": "synthetic_key_required"}})
            return False
        return True

    def json_response(self, status, body):
        payload = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        if not self.authorized():
            return
        if self.path != "/v1/models":
            self.json_response(404, {"error": "unknown fixture endpoint"})
            return
        self.json_response(200, {"object": "list", "data": [
            {"id": "fixture-normal", "object": "model"}, {"id": "fixture-slow", "object": "model"}]})

    def do_POST(self):
        if not self.authorized():
            return
        if self.path != "/v1/chat/completions":
            self.json_response(404, {"error": "unknown fixture endpoint"})
            return
        try:
            size = int(self.headers.get("Content-Length", "0"))
            if not 0 < size <= 1_000_000:
                raise ValueError()
            request = json.loads(self.rfile.read(size))
            model = request.get("model")
            if model not in ("fixture-normal", "fixture-slow") or request.get("stream") is not True:
                raise ValueError()
            messages = request["messages"]
            title = any(m.get("role") == "system" and "conversation title" in m.get("content", "") for m in messages)
        except (ValueError, KeyError, TypeError):
            self.json_response(400, {"error": {"code": "invalid_fixture_request"}})
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        chunks = ["本地协议测试标题"] if title else (["【本地协议夹具，并非真实模型】\n", "正常流式正文。", "可选择、复制；所有内容仅为测试。"] if model == "fixture-normal" else
            ["【本地慢流夹具，并非真实模型】\n"] + [f"测试片段 {i}。" for i in range(1, self.server.slow_chunks + 1)])
        delay = 0.03 if title or model == "fixture-normal" else self.server.slow_delay
        print("fixture request: " + ("title" if title else model), flush=True)
        try:
            for text in chunks:
                self.event({"model": model, "choices": [{"delta": {"content": text}, "finish_reason": None}]})
                time.sleep(delay)
            self.event({"model": model, "choices": [{"delta": {}, "finish_reason": "stop"}]})
            self.wfile.write(b"data: [DONE]\n\n")
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            print("fixture stream: client stopped", flush=True)

    def event(self, body):
        self.wfile.write(("data: " + json.dumps(body, ensure_ascii=False) + "\n\n").encode())
        self.wfile.flush()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=18081)
    parser.add_argument("--slow-delay", type=float, default=0.6)
    parser.add_argument("--slow-chunks", type=int, default=40)
    args = parser.parse_args()
    if args.slow_delay < 0 or not 1 <= args.slow_chunks <= 1000:
        parser.error("invalid slow stream controls")
    server = ThreadingHTTPServer(("127.0.0.1", args.port), FixtureHandler)
    server.daemon_threads = True
    server.slow_delay, server.slow_chunks = args.slow_delay, args.slow_chunks
    print(f"TEST_ONLY_ENDPOINT=http://127.0.0.1:{server.server_port}/v1", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
