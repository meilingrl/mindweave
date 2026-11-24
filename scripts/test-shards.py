#!/usr/bin/env python3
"""Runs one xUnit v3 test assembly as several processes at once (docs/design/testing.md).

    scripts/test-shards.py run DLL --shards N [--only I] [--timings FILE] [--results DIR] [-- RUNNER-ARGS...]
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

CLASS_NAME = re.compile(r"[A-Za-z_][\w`]*(\.[A-Za-z_][\w`+]*)+")
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def runner(dll):
    return ["dotnet", dll]


def list_classes(dll, extra):
    cmd = runner(dll) + ["-list", "classes", "-noLogo", "-noColor", *extra]
    out = subprocess.run(cmd, capture_output=True, text=True, check=True).stdout
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
        log = open(os.path.join(logs, f"shard-{i}.log"), "w", encoding="utf-8")
        procs.append((i, shard, log, subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT)))

    failed = 0
    for i, shard, log, proc in procs:
        code = proc.wait()
        log.close()
        with open(log.name, encoding="utf-8", errors="replace") as f:
            lines = f.read().splitlines()
        summary = next((l.strip() for l in reversed(lines) if "Total:" in l), "no summary")
        state = "passed" if code == 0 else "FAILED"
        print(f"shard {i} ({len(shard['classes'])} classes, ~{shard['seconds']:.0f} s): {state} - {summary}")
        if code != 0:
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


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("run")
    p.add_argument("dll")
    p.add_argument("--shards", type=int, required=True)
    p.add_argument("--only", type=int)
    p.add_argument("--timings")
    p.add_argument("--results")
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
