#!/usr/bin/env python3
"""S4 TEST ONLY: independent authenticated loopback protocol fixture, never a model.

Run: python3 tests/fixtures/summary_sse_fixture.py --port 18084 --delay 3
Public, non-access synthetic key: mindweave-loopback-only
Counts: GET /v1/fixture-stats (same synthetic authorization); no bodies/keys logged.
Stop with Ctrl+C. Old chat_sse_fixture.py modes and assets remain unchanged.
"""
import argparse
from collections import Counter
import json
import math
import re
import threading
import time
from http.server import ThreadingHTTPServer
from chat_sse_fixture import FixtureHandler, context_chunks, is_context_summary

MODELS = tuple("fixture-s4-" + mode for mode in ("short", "long", "delayed", "invalid", "truncated", "fail", "http-error", "after-stop-fail"))
DETAIL = re.compile(r"\[MW_DETAIL:([^\]\r\n]{1,60})\]")
SUMMARY_PREFIX = "Summarize only OwnConversation, using FrozenBranchBackground and DirectQuote only to interpret it."
NAMING_PREFIX = "Name this exact group of current knowledge summaries."


def classify(messages):
    systems = [m["content"] for m in messages if m["role"] == "system"]
    if any(s.startswith(SUMMARY_PREFIX) for s in systems): return "summary"
    if any(s.startswith(NAMING_PREFIX) for s in systems): return "naming"
    if any("conversation title" in s for s in systems): return "title"
    if is_context_summary(messages): return "context"
    return "reply"


def response_text(kind, model, messages):
    if kind == "title": return "S4 合成会话"
    if kind == "reply":
        labels = list(dict.fromkeys(DETAIL.findall(messages[-1]["content"])))
        return "【S4 本地合成回答，并非真实模型】\n\n" + " ".join("[MW_DETAIL:" + label + "]" for label in labels) + "\n\n这里只供生成、选区、总结与阅读流程验证。来源信息没有语义推断。"
    if kind == "context": return context_chunks("fixture-context", messages)[0][0]
    envelope = json.loads(messages[-1]["content"])
    if kind == "summary":
        rows = envelope["OwnConversation"]
        if not rows or any(not isinstance(row["Body"], str) for row in rows): raise ValueError()
        # Only owned bodies supply markers; branch/background text is never promoted.
        labels = list(dict.fromkeys(label for row in rows for label in DETAIL.findall(row["Body"])))
        markdown = "# 合成总结（非真实模型）\n\n" + " ".join("[MW_DETAIL:" + label + "]" for label in labels)
        markdown += "\n\n本材料仅验证知识总结协议与桌面流程，不证明模型理解或总结质量。"
        if model in ("fixture-s4-long", "fixture-s4-delayed"):
            markdown += "\n\n## 只读排版材料\n\n**粗体**、`代码`与 [惰性链接](https://example.invalid)。é 😀\n\n| 字段 | 说明 |\n| --- | --- |\n| 来源 | 合成 |\n\n```text\nsynthetic only\n```"
            markdown += "".join(f"\n\n### 阅读段落 {i}\n\n这是本地合成排版材料。用于滚动、选文、版本阅读位置恢复和长文覆盖验证，不包含额外来源事实。" for i in range(1, 31))
        value = {"Title": "S4 合成主题", "Markdown": markdown}
        if model == "fixture-s4-invalid": value = {"Title": "合成无效总结"}
    else:
        members = envelope["Members"]
        if not isinstance(members, list): raise ValueError()
        value = []
        for material in members:
            member = material["Member"]
            if not isinstance(member["NameIsManual"], bool): raise ValueError()
            if member["NameIsManual"]: continue
            detail = DETAIL.search(material["Content"]["Markdown"])
            base = material["BaseTitle"]
            name = base + "－" + detail[1] if detail else base
            if not isinstance(member["NodeId"], str) or not name or len(name) > 160 or any(ord(c) < 32 for c in name): raise ValueError()
            value.append({"NodeId": member["NodeId"], "DisplayName": name})
        if model == "fixture-s4-invalid": value = [{"NodeId": "00000000-0000-0000-0000-000000000000", "DisplayName": "合成错误目标"}]
    return json.dumps(value, ensure_ascii=False)


class SummaryFixtureHandler(FixtureHandler):
    # Reuse only the existing authenticated, bounded HTTP framing and SSE writer.
    # All S4 routing is local to this handler; no old mode is modified.
    def do_GET(self):
        if not self.authorized(): return
        if self.path == "/v1/models":
            self.json_response(200, {"object": "list", "data": [{"id": model, "object": "model"} for model in MODELS]})
        elif self.path == "/v1/fixture-stats":
            with self.server.count_lock:
                value = {"counts": dict(self.server.counts), "models": dict(self.server.model_counts)}
            self.json_response(200, value)
        else: self.json_response(404, {"error": "unknown fixture endpoint"})

    def do_POST(self):
        if not self.authorized(): return
        if self.path != "/v1/chat/completions":
            self.json_response(404, {"error": "unknown fixture endpoint"}); return
        try:
            request = json.loads(self.read_body())
            model, messages = request["model"], request["messages"]
            if model not in MODELS or request.get("stream") is not True or not isinstance(messages, list) or not messages: raise ValueError()
            if any(not isinstance(m, dict) or m.get("role") not in ("system", "user", "assistant") or not isinstance(m.get("content"), str) for m in messages): raise ValueError()
            kind = classify(messages)
            text = response_text(kind, model, messages)
        except (ValueError, KeyError, TypeError, OSError):
            self.json_response(400, {"error": {"code": "invalid_s4_fixture_request"}}); return
        with self.server.count_lock:
            self.server.counts[kind] += 1
            self.server.model_counts[model] += 1
        faultable = kind in ("summary", "naming")
        if faultable and model == "fixture-s4-http-error":
            self.json_response(503, {"error": {"code": "synthetic_s4_failure"}}); return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        if faultable and model in ("fixture-s4-truncated", "fixture-s4-fail"): text = text[:max(1, len(text) // 2)]
        try:
            for offset in range(0, len(text), 256):
                self.event({"model": model, "choices": [{"delta": {"content": text[offset:offset + 256]}, "finish_reason": None}]})
                if faultable and model == "fixture-s4-delayed": time.sleep(self.server.delay)
            if faultable and model == "fixture-s4-fail":
                self.close_connection = True; return
            finish = "length" if faultable and model == "fixture-s4-truncated" else "stop"
            self.event({"model": model, "choices": [{"delta": {}, "finish_reason": finish}]})
            if faultable and model == "fixture-s4-after-stop-fail":
                self.wfile.write(b"data: {synthetic-malformed\n\n"); self.wfile.flush(); self.close_connection = True; return
            self.wfile.write(b"data: [DONE]\n\n"); self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError): pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=18084)
    parser.add_argument("--delay", type=float, default=3, help="seconds per delayed Summary/Naming chunk (0..60)")
    args = parser.parse_args()
    if not 0 <= args.port <= 65535 or not math.isfinite(args.delay) or not 0 <= args.delay <= 60: parser.error("invalid loopback controls")
    server = ThreadingHTTPServer(("127.0.0.1", args.port), SummaryFixtureHandler)
    server.daemon_threads = True
    server.delay = args.delay
    server.count_lock = threading.Lock(); server.counts = Counter(); server.model_counts = Counter()
    print(f"TEST_ONLY_ENDPOINT=http://127.0.0.1:{server.server_port}/v1", flush=True)
    try: server.serve_forever()
    except KeyboardInterrupt: pass
    finally: server.server_close()


if __name__ == "__main__": main()
