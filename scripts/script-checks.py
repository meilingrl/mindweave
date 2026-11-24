#!/usr/bin/env python3
"""Checks of the test scripts themselves (docs/design/testing.md).

    scripts/script-checks.py

Test names, class names and failure messages hold Chinese. Windows gives a Python whose output is redirected the
ANSI code page (cp1252 on CI), which cannot write it, and decodes a child's output with the same code page. These
checks run test-shards.py and test-timings.py with that code page forced on their output and with Chinese in
everything they read, and fail if a script stops, loses text or passes a damaged class name on.

The output side fails on every system without the fix. The reading side (the class list) can only fail on Windows,
where the code page is real; CI runs these checks there.
"""
import os
import subprocess
import sys
import tempfile
import unittest

SCRIPTS = os.path.dirname(os.path.abspath(__file__))
CLASS = "Mindweave.Desktop.Tests.测试页Tests"
TEST = f'{CLASS}.Nothing_is_cut(language: "中文")'
MESSAGE = "“工作区” 被截断: 期望 120, 实际 96"

# Stands in for the xUnit runner: lists one class, then fails one test of it. It writes UTF-8 as the runner does,
# and says so if the class name it is asked to run is not the one it listed.
FAKE_RUNNER = f'''
import sys
out = lambda text: sys.stdout.buffer.write((text + "\\n").encode("utf-8"))
if "-list" in sys.argv:
    out("xUnit.net banner")
    out({CLASS!r})
    sys.exit(0)
if {CLASS!r} not in sys.argv:
    out("CLASS NAME DAMAGED")
    sys.exit(3)
out("    " + {TEST!r} + " [FAIL]")
out("      " + {MESSAGE!r})
out("  Mindweave.Desktop.Tests  Total: 1, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0, Time: 0.1s")
sys.exit(1)
'''

# Runs test-shards.py with the fake runner in place of `dotnet DLL`.
SHARDS_DRIVER = '''
import importlib.util, sys
spec = importlib.util.spec_from_file_location("test_shards", sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
fake = sys.argv[2]
module.runner = lambda dll: [sys.executable, fake]
sys.argv = [sys.argv[1], *sys.argv[3:]]
module.main()
'''

TRX = f'''<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testId="1" testName={TEST!r} duration="00:00:04.5000000" outcome="Passed" />
  </Results>
  <TestDefinitions>
    <UnitTest id="1" name="t"><TestMethod className="{CLASS}" name="t" /></UnitTest>
  </TestDefinitions>
</TestRun>
'''.replace("'", "&apos;").replace('testName=&apos;', "testName='").replace(")&apos; duration", ")' duration")


def run(command, cwd):
    """Runs a script as Windows CI does: output redirected, in a code page that cannot hold Chinese."""
    env = dict(os.environ, PYTHONIOENCODING="cp1252", PYTHONUTF8="0")
    done = subprocess.run(command, cwd=cwd, env=env, capture_output=True)
    return done.returncode, done.stdout.decode("utf-8", errors="replace"), done.stderr.decode("utf-8", errors="replace")


class ChineseTextTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)

    def path(self, name, text=None):
        path = os.path.join(self.dir.name, name)
        if text is not None:
            with open(path, "w", encoding="utf-8") as f:
                f.write(text)
        return path

    def test_a_failing_shard_with_chinese_text_is_reported_whole(self):
        fake = self.path("fake_runner.py", FAKE_RUNNER)
        driver = self.path("driver.py", SHARDS_DRIVER)
        code, out, err = run(
            [sys.executable, driver, os.path.join(SCRIPTS, "test-shards.py"), fake,
             "run", self.path("Fake.dll"), "--shards", "2", "--results", self.path("results")],
            self.dir.name)
        self.assertEqual(1, code, f"a failed shard is exit code 1, not a crash\n{out}\n{err}")
        self.assertNotIn("Traceback", err)
        self.assertNotIn("CLASS NAME DAMAGED", out)
        self.assertIn(TEST + " [FAIL]", out, err)
        self.assertIn(MESSAGE, out)
        self.assertIn("log: ", out)
        self.assertIn("test-shards: 1 shards in", out)
        self.assertIn("1 failed", out)

    def test_timings_with_chinese_test_names_are_listed_whole(self):
        os.mkdir(self.path("trx"))
        self.path(os.path.join("trx", "one.trx"), TRX)
        code, out, err = run(
            [sys.executable, os.path.join(SCRIPTS, "test-timings.py"), self.path("trx"), "--github"], self.dir.name)
        self.assertEqual(0, code, f"{out}\n{err}")
        self.assertNotIn("Traceback", err)
        self.assertIn(f"4.50 s  {TEST}", out)
        self.assertIn(f"::warning title=Slow test::{TEST} took 4.5 s", out)
        self.assertIn(CLASS, out)

    def test_a_message_that_ends_the_script_keeps_its_chinese(self):
        code, out, err = run(
            [sys.executable, os.path.join(SCRIPTS, "test-shards.py"), "record", self.path("没有结果"),
             "--assembly", "Mindweave.Desktop.Tests", "--out", self.path("out.json")],
            self.dir.name)
        self.assertEqual(1, code)
        self.assertNotIn("Traceback", err)
        self.assertIn("没有结果", err)


if __name__ == "__main__":
    unittest.main(verbosity=2)
