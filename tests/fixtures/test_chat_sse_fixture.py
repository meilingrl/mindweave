"""Exercise the test-only fixture as a separate process; no remote service."""
import json
import pathlib
import subprocess
import unittest
import urllib.error
import urllib.request


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

    def request(self, path, body=None, key="mindweave-loopback-only"):
        return urllib.request.urlopen(urllib.request.Request(self.endpoint + path,
            data=None if body is None else json.dumps(body).encode(),
            headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"}), timeout=5)

    def test_discovery_is_authenticated_and_synthetic_only(self):
        with self.request("/models") as response:
            self.assertEqual(["fixture-normal", "fixture-slow"], [m["id"] for m in json.load(response)["data"]])
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


if __name__ == "__main__":
    unittest.main()
