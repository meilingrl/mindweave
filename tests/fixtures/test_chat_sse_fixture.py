"""Exercise the test-only fixture as a separate process; no remote service."""
import http.client
import json
import pathlib
import subprocess
import unittest
import urllib.error
import urllib.request
import urllib.parse


class FixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.process = subprocess.Popen(["python3", str(pathlib.Path(__file__).with_name("chat_sse_fixture.py")),
            "--port", "0", "--slow-delay", "0", "--slow-chunks", "4"], stdout=subprocess.PIPE, text=True)
        cls.endpoint = cls.process.stdout.readline().strip().split("=", 1)[1]

    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        cls.process.wait(timeout=5)
        cls.process.stdout.close()

    def request(self, path, body=None, key="mindweave-loopback-only", chunked=False):
        payload = None if body is None else json.dumps(body, ensure_ascii=False).encode()
        # An iterable has no Content-Length: urllib sends real HTTP/1.1 chunk framing.
        data = iter(payload[i:i + 7] for i in range(0, len(payload), 7)) if chunked else payload
        return urllib.request.urlopen(urllib.request.Request(self.endpoint + path,
            data=data,
            headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"}), timeout=5)

    def test_discovery_is_authenticated_and_synthetic_only(self):
        with self.request("/models") as response:
            self.assertEqual(["fixture-normal", "fixture-slow", "fixture-markdown", "fixture-context", "fixture-context-invalid", "fixture-context-fail"], [m["id"] for m in json.load(response)["data"]])
        with self.assertRaises(urllib.error.HTTPError) as error:
            self.request("/models", key="invalid")
        self.assertEqual(401, error.exception.code)
        error.exception.close()

    def test_normal_slow_and_title_streams_have_valid_terminal_events(self):
        for model, messages, expected in [
            ("fixture-normal", [{"role": "user", "content": "test"}], "正常流式正文"),
            ("fixture-slow", [{"role": "user", "content": "test"}], "测试片段 4"),
            ("fixture-slow", [{"role": "system", "content": "Generate a conversation title"}], "本地协议测试标题")]:
            with self.request("/chat/completions", {"model": model, "stream": True, "messages": messages, "max_tokens": 128}) as response:
                text = response.read().decode()
            self.assertIn(expected, text)
            self.assertIn('"finish_reason": "stop"', text)
            self.assertTrue(text.endswith("data: [DONE]\n\n"))

    def test_chunked_json_body_matches_normal_slow_and_title_fixed_length_behavior(self):
        for model, messages, expected in [
            ("fixture-normal", [{"role": "user", "content": "分块请求"}], "正常流式正文"),
            ("fixture-slow", [{"role": "user", "content": "分块请求"}], "测试片段 4"),
            ("fixture-normal", [{"role": "system", "content": "Generate a conversation title"}], "本地协议测试标题")]:
            with self.request("/chat/completions", {"model": model, "stream": True, "messages": messages,
                    "max_tokens": 128}, chunked=True) as response:
                text = response.read().decode()
            self.assertIn(expected, text)
            self.assertIn('"finish_reason": "stop"', text)
            self.assertTrue(text.endswith("data: [DONE]\n\n"))

    def test_markdown_fixture_has_explicit_label_formats_duplicates_and_scroll_extent(self):
        with self.request("/chat/completions", {"model": "fixture-markdown", "stream": True,
                "messages": [{"role": "user", "content": "synthetic"}]}, chunked=True) as response:
            raw = response.read().decode()
        events = [json.loads(line[6:]) for line in raw.splitlines() if line.startswith("data: {")]
        body = "".join(event["choices"][0]["delta"].get("content", "") for event in events)
        self.assertIn("合成 Markdown 夹具，并非真实模型", body)
        self.assertIn("**粗体**", body)
        self.assertIn("[链接](https://example.invalid)", body)
        self.assertIn("`代码`", body)
        self.assertGreaterEqual(body.count("重复词"), 2)
        self.assertGreaterEqual(body.count("\n\n"), 20)
        self.assertTrue(raw.endswith("data: [DONE]\n\n"))

    def context_stream(self, model, messages):
        with self.request("/chat/completions", {"model": model, "stream": True, "messages": messages,
                "max_tokens": 128}, chunked=True) as response:
            raw = response.read().decode()
        events = [json.loads(line[6:]) for line in raw.splitlines() if line.startswith("data: {")]
        body = "".join(event["choices"][0]["delta"].get("content", "") for event in events)
        return raw, events, body

    def summary_messages(self, body):
        # Exact envelope used by the C# planner; markers are test data, not inferred facts.
        return [{"role": "system", "content": "Summarize the supplied original conversation slices as quoted background"},
                {"role": "user", "content": json.dumps({"OriginalSlices": [{"Source": {"MessageId": "source-1", "Start": 0,
                    "Length": len(body)}, "ConversationId": "owner", "Role": "User", "Status": "Completed", "Body": body}]})}]

    def test_context_summary_echoes_only_supplied_markers_and_final_detects_actual_material(self):
        markers = "[MW_SOURCE:orchid] [MW_CORRECTION:blue] [MW_QUESTION:when] [MW_REFERENCE:it=orchid]"
        raw, events, body = self.context_stream("fixture-context", self.summary_messages(markers))
        summary = json.loads(body)
        self.assertEqual({"Background", "Requirements", "Corrections", "OpenQuestions", "References"}, set(summary))
        self.assertTrue(all(isinstance(v, str) for v in summary.values()))
        self.assertIn("[MW_SOURCE:orchid]", summary["Background"])
        self.assertEqual("[MW_CORRECTION:blue]", summary["Corrections"])
        self.assertEqual("[MW_QUESTION:when]", summary["OpenQuestions"])
        self.assertEqual("[MW_REFERENCE:it=orchid]", summary["References"])
        self.assertEqual("stop", events[-1]["choices"][0]["finish_reason"])
        self.assertTrue(raw.endswith("data: [DONE]\n\n"))
        _, _, absent = self.context_stream("fixture-context", self.summary_messages("unmarked original"))
        self.assertNotIn("MW_SOURCE", absent)
        self.assertNotIn("MW_CORRECTION", absent)
        for content, expected, missing in [(body, "PRESENT source=orchid correction=blue question=when reference=it=orchid", "MISSING none"),
                ("[MW_SOURCE:orchid]", "PRESENT source=orchid", "MISSING correction,question,reference")]:
            _, _, answer = self.context_stream("fixture-context", [{"role": "system", "content": "<context-summary>" + content + "</context-summary>"},
                {"role": "user", "content": "protocol check"}])
            self.assertIn("SYNTHETIC", answer)
            self.assertIn(expected, answer)
            self.assertIn(missing, answer)
        # The final user may ask about markers; an expectation is not evidence of their presence.
        _, _, answer = self.context_stream("fixture-context", [{"role": "user", "content": markers}])
        self.assertIn("MISSING source,correction,question,reference", answer)

    def test_context_title_routing_keeps_normal_terminal_title(self):
        raw, events, body = self.context_stream("fixture-context", [{"role": "system", "content": "Generate a conversation title"}, {"role": "user", "content": "ordinary title material"}])
        self.assertEqual("本地协议测试标题", body)
        self.assertEqual("stop", events[-1]["choices"][0]["finish_reason"])
        self.assertTrue(raw.endswith("data: [DONE]\n\n"))

    def test_context_invalid_and_early_failure_are_distinct_complete_and_incomplete_sse(self):
        raw, events, body = self.context_stream("fixture-context-invalid", self.summary_messages("[MW_SOURCE:orchid]"))
        self.assertEqual({"Background": "SYNTHETIC invalid summary"}, json.loads(body))
        self.assertEqual("stop", events[-1]["choices"][0]["finish_reason"])
        self.assertTrue(raw.endswith("data: [DONE]\n\n"))
        raw, events, body = self.context_stream("fixture-context-fail", self.summary_messages("[MW_SOURCE:orchid]"))
        self.assertTrue(body)
        self.assertFalse(any(event["choices"][0]["finish_reason"] for event in events))
        self.assertNotIn("data: [DONE]", raw)

    def test_oversized_chunk_is_rejected_before_payload_read(self):
        address = urllib.parse.urlparse(self.endpoint)
        connection = http.client.HTTPConnection(address.hostname, address.port, timeout=5)
        try:
            connection.putrequest("POST", "/v1/chat/completions")
            connection.putheader("Authorization", "Bearer mindweave-loopback-only")
            connection.putheader("Transfer-Encoding", "chunked")
            connection.endheaders()
            # Declared size exceeds the 1,000,000-byte fixture bound; do not send a body.
            connection.send(b"f4241\r\n")
            response = connection.getresponse()
            self.assertEqual(400, response.status)
            response.read()
        finally:
            connection.close()


if __name__ == "__main__":
    unittest.main()
