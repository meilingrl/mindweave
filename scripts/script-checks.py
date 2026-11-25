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
Every folder it uses is a temporary one, its call that tells Launch Services about a bundle is taken out, and `open`
and `dotnet` are stand-ins.
"""
import json
import os
import plistlib
import re
import signal
import subprocess
import sys
import tempfile
import time
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


MIGRATIONS = """
    Migration.Sql(1, "one", "SELECT 1;"),
"""

# Stands in for `open`, as far as a trial needs it. The real one gives a bundle its caller's environment, then what the
# bundle's LSEnvironment adds to it, then --env over both (tried on macOS 27.2.1). With OPEN_PLAIN the bundle gets only
# what it carries itself, as when it is started from the Dock. The program starts a moment later and out of the caller's
# reach, so a script that does not wait for it is seen.
FAKE_OPEN = f"""#!{sys.executable}
import json, os, plistlib, signal, subprocess, sys, time
arguments, given, bundle, errors, wait = sys.argv[1:], {{}}, None, None, False
while arguments:
    argument = arguments.pop(0)
    if argument == "--env":
        name, _, value = arguments.pop(0).partition("=")
        given[name] = value
    elif argument == "--stderr":
        errors = arguments.pop(0)
    elif argument.startswith("-"):
        wait = wait or argument == "-W"
    else:
        bundle = argument
with open(os.environ["OPEN_RECORD"], "w", encoding="utf-8") as f:
    json.dump({{"arguments": sys.argv[1:], "bundle": bundle}}, f)
if os.environ.get("OPEN_FAILS"):
    sys.exit("no such application")
with open(os.path.join(bundle, "Contents", "Info.plist"), "rb") as f:
    plist = plistlib.load(f)
own = plist.get("LSEnvironment", {{}})
plain = {{"PATH": os.environ["PATH"]}}
environment = {{**plain, **own}} if os.environ.get("OPEN_PLAIN") else {{**own, **os.environ, **given}}
time.sleep(0.5)
signal.signal(signal.SIGINT, signal.SIG_DFL)
program = subprocess.Popen(
    [os.path.join(bundle, "Contents", "MacOS", plist["CFBundleExecutable"])], cwd="/", env=environment, start_new_session=True,
    stdout=subprocess.DEVNULL, stderr=open(errors, "wb") if errors else subprocess.DEVNULL)
signal.signal(signal.SIGINT, signal.SIG_IGN)
if wait:
    program.wait()
"""

# Stands in for `dotnet publish -o FOLDER`: puts the made-up program there, stamped with the daily channel.
FAKE_DOTNET = """#!/bin/sh
while [ "$1" != "-o" ]; do shift; done
mkdir -p "$2"
cp "$BUILT_PROGRAM" "$2/Mindweave"
printf 'MindweaveChannel\\013claude-auto' > "$2/Mindweave.dll"
printf icon > "$2/Mindweave.icns"
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
            MINDWEAVE_DAILY_BACKUP_DIR=self.path("backups"), MINDWEAVE_DAILY_DATA=self.path("data"), TMPDIR=self.path("tmp"),
            PATH=self.path("bin") + os.pathsep + own.get("PATH", ""), OPEN_RECORD=self.path("opened"),
            BUILT_PROGRAM=self.path("built"))
        os.makedirs(self.path("tmp"))
        self.write("bin/open", FAKE_OPEN, executable=True)
        self.write("bin/dotnet", FAKE_DOTNET, executable=True)
        self.write("built", self.program(), executable=True)
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

    def program(self):
        """A made-up Mindweave that lists the data it was given."""
        return f'''#!/bin/sh
cd "${{MINDWEAVE_DATA_DIR:?}}" && pwd > "{self.path("where")}" && find . -type f | sort > "{self.path("seen")}"
echo "said by the program" >&2
'''

    def release(self, name, commit):
        folder = f"builds/releases/{name}"
        self.write(f"{folder}/Mindweave", self.program(), executable=True)
        self.write(f"{folder}/release.txt", f"commit={commit}\nsubject=t\nschema=1\n")
        return f"releases/{name}"

    def script(self, *arguments):
        return ["bash", os.path.join(self.repo, "scripts", "daily.sh"), *arguments]

    def daily(self, *arguments, **environment):
        done = subprocess.run(self.script(*arguments), cwd=self.repo, env=dict(self.env, **environment), capture_output=True, text=True)
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        return done.stdout + done.stderr

    def install(self):
        os.symlink(self.release("new", self.second), self.path("builds", "current"))
        self.daily("app")

    def read(self, name):
        with open(self.path(name), encoding="utf-8") as f:
            return f.read().strip()

    def opened(self):
        """What the last trial asked `open` for, and the Info.plist of the bundle it opened."""
        record = json.loads(self.read("opened"))
        with open(os.path.join(record["bundle"], "Contents", "Info.plist"), "rb") as f:
            return record, plistlib.load(f)

    def files(self, name):
        """Every file under a folder with its content, its mode and the time it was last changed."""
        found = {}
        for folder, _, names in os.walk(self.path(name)):
            for file in names:
                path = os.path.join(folder, file)
                with open(path, "rb") as f:
                    found[path] = (f.read(), os.stat(path).st_mode, os.stat(path).st_mtime_ns)
        return found

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

    def test_a_trial_is_an_app_of_its_own_and_leaves_the_daily_one_alone(self):
        self.install()
        before = self.files("Mindweave.app")
        with open(self.path("Mindweave.app", "Contents", "Info.plist"), "rb") as f:
            daily = plistlib.load(f)
        for ref in ("current", "main"):
            with self.subTest(ref):
                self.daily("trial", ref)
                record, plist = self.opened()
                bundle = record["bundle"]
                self.assertTrue(bundle.endswith(".app"))
                self.assertTrue(bundle.startswith(os.path.realpath(self.path("tmp")) + os.sep), bundle)
                self.assertIn("-n", record["arguments"], "a running trial is not brought forward instead")
                self.assertEqual("APPL", plist["CFBundlePackageType"])
                self.assertNotEqual(daily["CFBundleIdentifier"], plist["CFBundleIdentifier"])
                self.assertNotEqual(daily["CFBundleName"], plist["CFBundleName"])
                self.assertFalse(os.path.islink(os.path.join(bundle, "Contents", "MacOS")))
                self.assertNotIn(self.path("Mindweave.app"), os.path.realpath(os.path.join(bundle, "Contents", "MacOS", plist["CFBundleExecutable"])))
                self.assertEqual(before, self.files("Mindweave.app"))

    def test_a_trial_shows_the_icon_its_build_brings(self):
        self.daily("trial", "main")
        record, plist = self.opened()
        self.assertEqual("Mindweave", plist["CFBundleIconFile"])
        self.assertTrue(os.path.exists(os.path.join(record["bundle"], "Contents", "Resources", "Mindweave.icns")))
        self.install()
        self.daily("trial", "current")
        _, plist = self.opened()
        self.assertNotIn("CFBundleIconFile", plist)

    def test_a_trial_finds_the_copy_whatever_starts_it(self):
        self.install()
        os.makedirs(self.path("elsewhere"))
        for how, environment in {
                "started by the script": {},
                "started from the Dock": {"OPEN_PLAIN": "1"},
                "with another folder set in the terminal": {"MINDWEAVE_DATA_DIR": self.path("elsewhere")},
        }.items():
            with self.subTest(how):
                said = self.daily("trial", "current", **environment)
                where = self.read("where")
                self.assertIn(where, said, "the terminal says where the copy is")
                self.assertTrue(where.startswith(os.path.realpath(self.path("tmp")) + os.sep), where)
                record, _ = self.opened()
                self.assertEqual(os.path.dirname(record["bundle"]), os.path.dirname(where))

    def test_a_trial_passes_on_the_other_switches_set_in_the_terminal(self):
        self.install()
        self.write("builds/releases/new/Mindweave", f'#!/bin/sh\necho "$MINDWEAVE_SESSION_NETWORK" > "{self.path("seen")}"\n', executable=True)
        self.daily("app")
        self.daily("trial", "current", MINDWEAVE_SESSION_NETWORK="1")
        self.assertEqual("1", self.read("seen"))

    def test_the_terminal_shows_what_the_trial_says(self):
        self.install()
        self.assertIn("said by the program", self.daily("trial", "current"))

    def test_a_trial_that_cannot_start_is_an_error(self):
        self.install()
        done = subprocess.run(self.script("trial", "current"), cwd=self.repo, env=dict(self.env, OPEN_FAILS="1"), capture_output=True, text=True)
        self.assertEqual(1, done.returncode, done.stdout + done.stderr)

    def test_ending_the_script_ends_the_trial(self):
        self.install()
        self.write("builds/releases/new/Mindweave", f'#!/bin/sh\necho $$ > "{self.path("pid.new")}"\nmv "{self.path("pid.new")}" "{self.path("pid")}"\nexec 3<"$0"\nwhile :; do sleep 0.2; done\n', executable=True)
        self.daily("app")
        for number in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
            with self.subTest(number.name):
                running = subprocess.Popen(
                    self.script("trial", "current"), cwd=self.repo, env=self.env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
                self.wait_for(lambda: os.path.exists(self.path("pid")), "the trial did not start")
                pid = int(self.read("pid"))
                os.remove(self.path("pid"))
                try:
                    self.assertIsNone(running.poll(), "the script waits while the trial runs")
                    os.killpg(running.pid, number)
                    running.wait(timeout=20)
                    self.assertEqual(128 + number, running.returncode)
                    self.wait_for(lambda: not alive(pid), "the trial is still running")
                finally:
                    if alive(pid):
                        os.kill(pid, signal.SIGKILL)

    def wait_for(self, condition, message):
        for _ in range(200):
            if condition():
                return
            time.sleep(0.1)
        self.fail(message)


def alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


if __name__ == "__main__":
    unittest.main(verbosity=2)
