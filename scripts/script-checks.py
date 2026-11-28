#!/usr/bin/env python3
"""Checks of the test scripts themselves (docs/design/testing.md).

    scripts/script-checks.py

Test names, class names and failure messages hold Chinese. Windows gives a Python whose output is redirected the
ANSI code page (cp1252 on CI), which cannot write it, and decodes a child's output with the same code page. These
checks run test-shards.py and test-timings.py with that code page forced on their output and with Chinese in
everything they read, and fail if a script stops, loses text or passes a damaged class name on.

The output side fails on every system without the fix. The reading side (the class list) can only fail on Windows,
where the code page is real; CI runs these checks there.

On macOS they also run daily.sh (not in the source package) in a throw-away repository with made-up builds and data.
Every folder it uses is a temporary one, and its call that tells Launch Services about a bundle is taken out.
"""
import os
import plistlib
import re
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

HUNG_TEST = f"{CLASS}.等待永不结束"

# Stands in for the xUnit runner's verbose reporter with one test that never ends: it starts it (half a line at a time),
# finishes one other test, then waits for ever. The script must name it and end the process. With SLOW_BUT_MOVING in its
# arguments the long test ends after 4 s while another test finishes every half second: a shard that is only slow, which
# must be left alone.
HANGING_RUNNER = f'''
import sys, time
out = lambda text: (sys.stdout.buffer.write(text.encode("utf-8")), sys.stdout.flush())
if "-list" in sys.argv:
    out({CLASS!r} + "\\n")
    sys.exit(0)
if "-reporter" not in sys.argv or sys.argv[sys.argv.index("-reporter") + 1] != "verbose":
    out("NO VERBOSE REPORTER\\n")
    sys.exit(3)
out("    " + {HUNG_TEST!r}[:20])
time.sleep(0.5)
out({HUNG_TEST!r}[20:] + " [STARTING]\\n")
out("    " + {TEST!r} + " [STARTING]\\n")
out("    " + {TEST!r} + " [FINISHED] Time: 0.1s\\n")
if "SLOW_BUT_MOVING" in sys.argv:
    for n in range(8):
        time.sleep(0.5)
        out(f"    {CLASS}.其他测试{{n}} [STARTING]\\n")
        out(f"    {CLASS}.其他测试{{n}} [FINISHED] Time: 0.5s\\n")
    out("    " + {HUNG_TEST!r} + " [FINISHED] Time: 4.5s\\n")
    out("  Mindweave.Desktop.Tests  Total: 10, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 4.5s\\n")
    sys.exit(0)
time.sleep(3600)
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

    def test_a_hanging_test_is_named_and_its_shard_ended(self):
        code, out, err = self.run_hanging()
        self.assertEqual(1, code, f"a hung shard fails\n{out}\n{err}")
        self.assertNotIn("Traceback", err)
        self.assertNotIn("NO VERBOSE REPORTER", out)
        self.assertIn("no test finished for 2 s; the shard was ended", out)
        self.assertIn(f"HUNG: {HUNG_TEST} (running for", out)
        self.assertNotIn(f"HUNG: {TEST}", out)
        self.assertIn("FAILED", out)

    def test_a_slow_shard_whose_tests_still_finish_is_left_alone(self):
        code, out, err = self.run_hanging("SLOW_BUT_MOVING")
        self.assertEqual(0, code, f"a slow shard passes\n{out}\n{err}")
        self.assertNotIn("HUNG", out)
        self.assertIn("passed", out)

    def run_hanging(self, *runner_args):
        fake = self.path("fake_runner.py", HANGING_RUNNER)
        driver = self.path("driver.py", SHARDS_DRIVER)
        return run(
            [sys.executable, driver, os.path.join(SCRIPTS, "test-shards.py"), fake,
             "run", self.path("Fake.dll"), "--shards", "1", "--hang", "2", "--results", self.path("results"), "--", *runner_args],
            self.dir.name)

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


MIGRATIONS = """
    Migration.Sql(1, "one", "SELECT 1;"),
"""


@unittest.skipUnless(sys.platform == "darwin" and os.path.exists(os.path.join(SCRIPTS, "daily.sh")), "daily.sh is for macOS only")
class DailyBuildTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.repo = self.path("repo")
        with open(os.path.join(SCRIPTS, "daily.sh"), encoding="utf-8") as f:
            script = re.sub(r'^LSREGISTER=.*$', 'LSREGISTER="/usr/bin/true"', f.read(), flags=re.M)
        self.assertIn('LSREGISTER="/usr/bin/true"', script)
        self.write("repo/scripts/daily.sh", script)
        self.write("repo/src/Mindweave.Infrastructure/Sqlite/Migrations/SchemaMigrations.cs", MIGRATIONS)
        # Nothing of the repository the checks run in (git sets GIT_DIR for hooks and rebase --exec) reaches the made-up one.
        own = {name: value for name, value in os.environ.items() if not name.startswith("GIT_")}
        self.env = dict(
            own, MINDWEAVE_DAILY_HOME=self.path("builds"), MINDWEAVE_DAILY_APP=self.path("Mindweave.app"),
            MINDWEAVE_DAILY_BACKUP_DIR=self.path("backups"), MINDWEAVE_DAILY_DATA=self.path("data"), TMPDIR=self.path("tmp"))
        os.makedirs(self.path("tmp"))
        self.git("init", "-q", "-b", "main")
        self.first = self.commit("1.0.0")
        self.second = self.commit("2.0.0")

    def path(self, *parts):
        return os.path.join(self.dir.name, *parts)

    def write(self, name, text, executable=False):
        path = self.path(*name.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        if executable:
            os.chmod(path, 0o755)
        return path

    def git(self, *arguments):
        command = ["git", "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", *arguments]
        return subprocess.run(command, cwd=self.repo, env=self.env, check=True, capture_output=True, text=True).stdout.strip()

    def commit(self, version):
        self.write("repo/Directory.Build.props", f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", version)
        return self.git("rev-parse", "HEAD")

    def release(self, name, commit):
        """A made-up build whose program lists the data it was given."""
        folder = f"builds/releases/{name}"
        self.write(f"{folder}/Mindweave", f'#!/bin/sh\ncd "$MINDWEAVE_DATA_DIR" && find . -type f | sort > "{self.path("seen")}"\n', executable=True)
        self.write(f"{folder}/release.txt", f"commit={commit}\nsubject=t\nschema=1\n")
        return f"releases/{name}"

    def daily(self, *arguments):
        done = subprocess.run(["bash", os.path.join(self.repo, "scripts", "daily.sh"), *arguments], cwd=self.repo, env=self.env, capture_output=True, text=True)
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)

    def version(self):
        with open(self.path("Mindweave.app", "Contents", "Info.plist"), "rb") as f:
            return plistlib.load(f)["CFBundleShortVersionString"]

    def test_the_launcher_has_the_version_of_its_build_not_of_the_working_tree(self):
        os.symlink(self.release("old", self.first), self.path("builds", "previous"))
        os.symlink(self.release("new", self.second), self.path("builds", "current"))
        self.write("repo/Directory.Build.props", "<Project><PropertyGroup><Version>9.9.9</Version></PropertyGroup></Project>")
        self.daily("app")
        self.assertEqual("2.0.0", self.version())
        self.daily("rollback")
        self.assertEqual("1.0.0", self.version())

    def test_the_launcher_shows_the_icon_its_build_brings(self):
        os.symlink(self.release("old", self.first), self.path("builds", "previous"))
        os.symlink(self.release("new", self.second), self.path("builds", "current"))
        self.write("builds/releases/new/Mindweave.icns", "icon")
        self.daily("app")
        with open(self.path("Mindweave.app", "Contents", "Info.plist"), "rb") as f:
            self.assertEqual("Mindweave", plistlib.load(f)["CFBundleIconFile"])
        self.assertTrue(os.path.exists(self.path("Mindweave.app", "Contents", "Resources", "Mindweave.icns")))
        # A build from before the icon has none, and the bundle says so instead of pointing at a missing file.
        self.daily("rollback")
        with open(self.path("Mindweave.app", "Contents", "Info.plist"), "rb") as f:
            self.assertNotIn("CFBundleIconFile", plistlib.load(f))
        self.assertFalse(os.path.exists(self.path("Mindweave.app", "Contents", "Resources", "Mindweave.icns")))

    def test_a_trial_runs_on_a_copy_of_everything_the_app_reads(self):
        os.symlink(self.release("new", self.second), self.path("builds", "current"))
        self.daily("app")
        self.write("data/language", "zh-Hans")
        self.write("data/resources/ab/abcd", "an original")
        self.write("data/logs/app.log", "a log")
        subprocess.run(["sqlite3", self.path("data", "mindweave.db"), "PRAGMA user_version=1; CREATE TABLE t (x);"], check=True)
        self.daily("trial", "current")
        with open(self.path("seen"), encoding="utf-8") as f:
            self.assertEqual(["./language", "./mindweave.db", "./resources/ab/abcd"], f.read().split())
        self.assertTrue(os.path.exists(self.path("data", "resources", "ab", "abcd")))

    def test_a_trial_without_attachments_or_a_chosen_language_runs_too(self):
        os.symlink(self.release("new", self.second), self.path("builds", "current"))
        self.daily("app")
        os.makedirs(self.path("data"))
        subprocess.run(["sqlite3", self.path("data", "mindweave.db"), "PRAGMA user_version=1; CREATE TABLE t (x);"], check=True)
        self.daily("trial", "current")
        with open(self.path("seen"), encoding="utf-8") as f:
            self.assertEqual(["./mindweave.db"], f.read().split())


if __name__ == "__main__":
    unittest.main(verbosity=2)
