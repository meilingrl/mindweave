#!/usr/bin/env python3
"""TEST ONLY: loopback Chat Completions protocol fixture; never a real model.

python3 tests/fixtures/chat_sse_fixture.py --port 18081
Base URL: http://127.0.0.1:18081/v1
Synthetic key: mindweave-loopback-only
Models include fixture-context / fixture-context-invalid / fixture-context-fail.
Context modes only echo explicit MW markers from supplied source; no semantic understanding.
Titles always return a fixed test title.
Stop with Ctrl+C after native validation. Does not store bodies, keys or chat data.
"""
import argparse
import json
import re
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SYNTHETIC_KEY = "mindweave-loopback-only"
MAX_BODY_BYTES = 1_000_000
MAX_FRAMING_BYTES = 65_536

MODELS = ("fixture-normal", "fixture-slow", "fixture-markdown", "fixture-context", "fixture-context-invalid", "fixture-context-fail")
MARKERS = re.compile(r"\[MW_(SOURCE|REQUIREMENT|CORRECTION|QUESTION|REFERENCE):([^\]\r\n]{1,120})\]")
FIELDS = {"SOURCE": "Background", "REQUIREMENT": "Requirements", "CORRECTION": "Corrections", "QUESTION": "OpenQuestions", "REFERENCE": "References"}


def context_chunks(model, messages):
    summary = any(m["role"] == "system" and m["content"].startswith("Summarize the supplied original conversation slices") for m in messages)
    if summary:
        envelope = json.loads(messages[-1]["content"])
        # Inspect Body only: metadata and instructions cannot invent source evidence.
        bodies = [s["Body"] for s in envelope["OriginalSlices"]]
        if not bodies or any(not isinstance(body, str) for body in bodies):
            raise ValueError()
        if model == "fixture-context-invalid":
            return ['{"Background":"SYNTHETIC invalid summary"}'], False
        if model == "fixture-context-fail":
            return ['{"Background":"SYNTHETIC interrupted'], True
        content = {field: "" for field in FIELDS.values()}
        content["Background"] = "SYNTHETIC protocol echo; no semantic understanding."
        for body in bodies:
            for marker in MARKERS.finditer(body):
                field = FIELDS[marker[1]]
                if marker[0] not in content[field]:
                    content[field] = (content[field] + " " + marker[0]).strip()
        return [json.dumps(content, ensure_ascii=False)], False
    # Exclude current question: requests for expected markers are not preserved context.
    material = "\n".join(m["content"] for m in messages[:-1])
    found = {}
    for marker in MARKERS.finditer(material):
        found.setdefault(marker[1].lower(), []).append(marker[2])
    names = ("source", "correction", "question", "reference")
    present = " ".join(name + "=" + ",".join(dict.fromkeys(found[name])) for name in names if name in found)
    missing = ",".join(name for name in names if name not in found) or "none"
    return ["SYNTHETIC protocol inspection; no semantic understanding.\n", "PRESENT " + (present or "none") + "\nMISSING " + missing], False


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
            {"id": model, "object": "model"} for model in MODELS]})

    def read_body(self):
        # JsonContent may stream HTTP/1.1 without Content-Length. Bound both the
        # decoded JSON and framing/trailers, and reject ambiguous framing.
        self.connection.settimeout(5)
        encoding = self.headers.get("Transfer-Encoding", "").lower().strip()
        if not encoding:
            size = int(self.headers.get("Content-Length", "0"))
            if not 0 < size <= MAX_BODY_BYTES:
                raise ValueError()
            body = self.rfile.read(size)
            if len(body) != size:
                raise ValueError()
            return body
        if encoding != "chunked" or self.headers.get("Content-Length") is not None:
            raise ValueError()
        body = bytearray()
        framing = 0
        while True:
            line = self.rfile.readline(4097)
            framing += len(line)
            if len(line) > 4096 or not line.endswith(b"\r\n") or framing > MAX_FRAMING_BYTES:
                raise ValueError()
            token = line[:-2].split(b";", 1)[0]
            if not token or any(c not in b"0123456789abcdefABCDEF" for c in token):
                raise ValueError()
            size = int(token, 16)
            if size == 0:
                while True:
                    trailer = self.rfile.readline(4097)
                    framing += len(trailer)
                    if len(trailer) > 4096 or not trailer.endswith(b"\r\n") or framing > MAX_FRAMING_BYTES:
                        raise ValueError()
                    if trailer == b"\r\n":
                        return bytes(body)
            if size > MAX_BODY_BYTES - len(body):
                raise ValueError()
            chunk = self.rfile.read(size)
            if len(chunk) != size or self.rfile.read(2) != b"\r\n":
                raise ValueError()
            body.extend(chunk)
            framing += 2

    def do_POST(self):
        if not self.authorized():
            return
        if self.path != "/v1/chat/completions":
            self.json_response(404, {"error": "unknown fixture endpoint"})
            return
        try:
            request = json.loads(self.read_body())
            model = request.get("model")
            if model not in MODELS or request.get("stream") is not True:
                raise ValueError()
            messages = request["messages"]
            if not isinstance(messages, list) or not messages or any(not isinstance(m, dict) or m.get("role") not in ("system", "user", "assistant") or not isinstance(m.get("content"), str) for m in messages):
                raise ValueError()
            early_failure = False
            context = context_chunks(model, messages) if model.startswith("fixture-context") else None
            title = any(m.get("role") == "system" and "conversation title" in m.get("content", "") for m in messages)
        except (ValueError, KeyError, TypeError, OSError):
            self.json_response(400, {"error": {"code": "invalid_fixture_request"}})
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        chunks = ["本地协议测试标题"] if title else (["【本地协议夹具，并非真实模型】\n", "正常流式正文。", "可选择、复制；所有内容仅为测试。"] if model == "fixture-normal" else
            ["【本地慢流夹具，并非真实模型】\n"] + [f"测试片段 {i}。" for i in range(1, self.server.slow_chunks + 1)])
        if model == "fixture-markdown" and not title:
            chunks = ["【合成 Markdown 夹具，并非真实模型】\n\n",
                      "第一段 重复词 **粗体** [链接](https://example.invalid) `代码`。\n\n",
                      "第二段 重复词。这是另一个相同词的来源位置。\n\n"] + [
                f"第 {i} 个测试段落：只供本地滚动、选区、分支与返回验收。阅读位置应保持在正在看的段落。\n\n"
                for i in range(1, 31)]
        if context is not None and not title:
            chunks, early_failure = context
        delay = 0.03 if title or model in ("fixture-normal", "fixture-markdown") or model.startswith("fixture-context") else self.server.slow_delay
        print("fixture request: " + ("title" if title else model), flush=True)
        try:
            for text in chunks:
                self.event({"model": model, "choices": [{"delta": {"content": text}, "finish_reason": None}]})
                time.sleep(delay)
            if early_failure:
                self.close_connection = True
                return
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
