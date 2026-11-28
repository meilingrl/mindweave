#!/usr/bin/env python3
"""Runs one xUnit v3 test assembly as several processes at once (docs/design/testing.md).

    scripts/test-shards.py run DLL --shards N [--only I] [--timings FILE] [--results DIR] [--hang SECONDS] [-- RUNNER-ARGS...]
    scripts/test-shards.py record TRX-DIR --assembly NAME --out FILE

Avalonia's headless tests run on one UI thread per process, so the desktop tests cannot run in parallel inside one
process. Separate processes can: each test already has its own data directory and single-instance lock.

`run` lists the assembly's classes, spreads them over N shards by their recorded time (longest first, each to the
least loaded shard; a class without a record counts as the median), runs the shards at once and fails if any shard
fails. Classes are listed at run time, so a new class is never left out; the timings only balance the shards.
RUNNER-ARGS go to every shard (for example `-trait- Category=Quarantine`).

With `--only I` (0 to N-1) it runs just shard I. CI uses this to give each shard a machine of its own: the same
commit lists the same classes and reads the same timings on every machine of one system, so the N runs together
cover every class exactly once.

With `--hang SECONDS` a shard in which no test has finished for that long, while tests are running, is ended and fails,
and the tests it was running are named, longest first. A hanging test then costs minutes and says which it was instead
of running into the job's time limit unnamed (CI uses 300; tests-7 in docs/design/diagnostics-and-upkeep.md). The runner
reports each test starting and finishing (`-reporter verbose`). Not the time one test has run: a shard runs several
test classes at once, all waiting their turn on the one UI thread, so under load a test that is only waiting runs long
while the others finish; ending the shard then (as `-longRunning` did, 2026-11-28) failed three healthy shards.

`record` sums the per-class durations of NAME's tests in the TRX files under TRX-DIR and writes them to FILE.
"""
import argparse
import glob
import json
import os
import re
import statistics
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

# A name part starts with a letter of any script or "_": a class named in Chinese is a class too.
CLASS_NAME = re.compile(r"[^\W\d][\w`]*(\.[^\W\d][\w`+]*)+")
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
# What the xUnit v3 runner writes with -reporter verbose, one line each: "    Namespace.Class.Method(x: 1) [STARTING]",
# then "... [FINISHED] Time: 0.01s" (a failure or skip line comes between).
TEST_STEP = re.compile(r"^\s*(?P<name>\S.*?) \[(?P<step>STARTING|FINISHED)\]", re.MULTILINE)


def runner(dll):
    return ["dotnet", dll]


def list_classes(dll, extra):
    cmd = runner(dll) + ["-list", "classes", "-noLogo", "-noColor", *extra]
    # The runner writes UTF-8; the system's code page (the default here) would damage a class name outside ASCII.
    out = subprocess.run(cmd, capture_output=True, encoding="utf-8", errors="replace", check=True).stdout
    # Only full type names: a banner or escape code taken as a class would match nothing and hide an empty assembly.
    return sorted({line.strip() for line in out.splitlines() if CLASS_NAME.fullmatch(line.strip())})


def assign(classes, timings, n):
    known = [timings[c] for c in classes if c in timings]
    default = statistics.median(known) if known else 1.0
    weight = {c: timings.get(c, default) for c in classes}
    shards = [{"seconds": 0.0, "classes": []} for _ in range(n)]
    for c in sorted(classes, key=lambda c: (-weight[c], c)):
        shard = min(shards, key=lambda s: s["seconds"])
        shard["seconds"] += weight[c]
        shard["classes"].append(c)
    return [s for s in shards if s["classes"]]  # an empty shard must not start: no -class runs every class


def run(args):
    timings = {}
    if args.timings and os.path.exists(args.timings):
        with open(args.timings, encoding="utf-8") as f:
            timings = json.load(f)
    extra = args.runner_args
    classes = list_classes(args.dll, extra)
    if not classes:
        sys.exit(f"test-shards: {args.dll} has no test classes")
    shards = list(enumerate(assign(classes, timings, max(1, args.shards))))
    if args.only is not None:
        if not 0 <= args.only < max(1, args.shards):
            sys.exit(f"test-shards: --only {args.only} is outside 0..{max(1, args.shards) - 1}")
        # Fewer classes than shards leaves the last shards empty; their machines have nothing to run.
        shards = [(i, shard) for i, shard in shards if i == args.only]
        if not shards:
            print(f"test-shards: shard {args.only} of {args.shards} has no classes")
            sys.exit(0)
    logs = args.results or os.path.join(os.path.dirname(os.path.abspath(args.dll)), "shards")
    os.makedirs(logs, exist_ok=True)

    started = time.monotonic()
    procs = []
    for i, shard in shards:
        cmd = runner(args.dll) + ["-noLogo", "-noColor", *extra]
        for c in shard["classes"]:
            cmd += ["-class", c]
        if args.results:
            cmd += ["-trx", os.path.join(args.results, f"shard-{i}.trx")]
        if args.hang:
            cmd += ["-reporter", "verbose"]
        log = open(os.path.join(logs, f"shard-{i}.log"), "w", encoding="utf-8")
        procs.append((i, shard, log, subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT)))

    hung = watch(procs, args.hang) if args.hang else {}
    failed = 0
    for i, shard, log, proc in procs:
        code = proc.wait()
        log.close()
        with open(log.name, encoding="utf-8", errors="replace") as f:
            lines = f.read().splitlines()
        summary = next((l.strip() for l in reversed(lines) if "Total:" in l), "no summary")
        state = "passed" if code == 0 and i not in hung else "FAILED"
        print(f"shard {i} ({len(shard['classes'])} classes, ~{shard['seconds']:.0f} s): {state} - {summary}")
        if i in hung:
            print(f"    no test finished for {args.hang} s; the shard was ended")
            for name, seconds in hung[i]:
                print(f"    HUNG: {name} (running for {seconds:.0f} s)")
                if os.environ.get("GITHUB_ACTIONS") == "true":
                    print(f"::error title=Hung test::{name}: running for {seconds:.0f} s, and no test finished for {args.hang} s")
        if code != 0 or i in hung:
            failed += 1
            # The failure and the first lines of its message and stack: on CI the log file is gone after the job.
            shown = 0
            for n, line in enumerate(lines):
                if "[FAIL]" in line and shown < 5:
                    shown += 1
                    for detail in lines[n:n + 16]:
                        print("    " + detail.rstrip())
            print(f"    log: {log.name}")
    print(f"test-shards: {len(shards)} shards in {time.monotonic() - started:.0f} s, {failed} failed")
    sys.exit(1 if failed else 0)


def watch(procs, limit):
    """Follows the shards' logs until they end; a shard in which no test finished for `limit` seconds is ended.

    Returns, by shard, the tests it was running then, longest first with how long each had run (at most three). A
    hanging test never ends on its own, so its process is killed; the other tests of that shard are not run, which the
    shard's failure says. A shard that is only slow keeps finishing tests and is left alone.
    """
    hung = {}
    offsets = {i: 0 for i, _, _, _ in procs}
    running = {i: {} for i, _, _, _ in procs}
    progress = {i: time.monotonic() for i, _, _, _ in procs}
    while True:
        alive = 0
        now = time.monotonic()
        for i, _, log, proc in procs:
            done = proc.poll() is not None
            with open(log.name, "rb") as f:
                f.seek(offsets[i])
                data = f.read()
            # Only whole lines: a name cut in half would not match, so the rest is read again with its end next time.
            cut = data.rfind(b"\n") + 1
            offsets[i] += cut
            for m in TEST_STEP.finditer(data[:cut].decode("utf-8", errors="replace")):
                if m.group("step") == "STARTING":
                    running[i].setdefault(m.group("name"), now)
                else:
                    running[i].pop(m.group("name"), None)
                    progress[i] = now
            if not done and running[i] and now - progress[i] > limit:
                longest = sorted(running[i].items(), key=lambda item: item[1])[:3]
                hung[i] = [(name, now - since) for name, since in longest]
                proc.kill()
                done = True
            alive += not done
        if not alive:
            return hung
        time.sleep(1)


def record(args):
    seconds = {}
    for path in glob.glob(os.path.join(args.trx_dir, "**", "*.trx"), recursive=True):
        root = ET.parse(path).getroot()
        class_of = {d.get("id"): d.find("t:TestMethod", NS).get("className") for d in root.iterfind(".//t:UnitTest", NS)}
        for result in root.iterfind(".//t:UnitTestResult", NS):
            cls = class_of.get(result.get("testId"), "")
            if not cls.startswith(args.assembly + "."):
                continue
            h, m, s = (result.get("duration") or "0:0:0").split(":")
            seconds[cls] = seconds.get(cls, 0.0) + int(h) * 3600 + int(m) * 60 + float(s)
    if not seconds:
        sys.exit(f"test-shards: no {args.assembly} results under {args.trx_dir}")
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump({c: round(v, 1) for c, v in sorted(seconds.items())}, f, indent=1)
        f.write("\n")
    print(f"test-shards: recorded {len(seconds)} classes in {args.out}")


def whole_output():
    """Makes every print and exit message arrive whole, whatever the system's code page.

    Windows gives a Python whose output is redirected the ANSI code page (cp1252 on CI), and a test name, class name
    or failure message with Chinese in it then raised UnicodeEncodeError in the middle of the report. Redirected
    output is written as UTF-8, which is what a CI log or a pipe to another tool reads; a console keeps its own
    encoding, and a character it cannot show is written as an escape and never raises.
    """
    for stream in (sys.stdout, sys.stderr):
        if stream.isatty():
            stream.reconfigure(errors="backslashreplace")
        else:
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")


def main():
    whole_output()
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("run")
    p.add_argument("dll")
    p.add_argument("--shards", type=int, required=True)
    p.add_argument("--only", type=int)
    p.add_argument("--timings")
    p.add_argument("--results")
    p.add_argument("--hang", type=int, help="end a shard in which no test finished for this many seconds")
    p.set_defaults(func=run)
    r = sub.add_parser("record")
    r.add_argument("trx_dir")
    r.add_argument("--assembly", required=True)
    r.add_argument("--out", required=True)
    r.set_defaults(func=record)
    # Everything after "--" goes to the runner unparsed (argparse.REMAINDER would also take the options before it).
    argv = sys.argv[1:]
    cut = argv.index("--") if "--" in argv else len(argv)
    args = parser.parse_args(argv[:cut])
    args.runner_args = argv[cut + 1:]
    args.func(args)


if __name__ == "__main__":
    main()
