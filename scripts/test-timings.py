#!/usr/bin/env python3
"""Reads the TRX files of a test run and reports where the time went.

    scripts/test-timings.py DIR [--budget SECONDS] [--top N] [--github]

Prints the time per project, the slowest classes and tests, and every test over the budget (default 3 s). Tests in
a Faults or Performance namespace are expected to be slow and are not held to the budget. With --github, each test
over the budget is also printed as a GitHub Actions warning. A test over budget never fails the run: timings differ
too much between machines for that (docs/design/testing.md).
"""
import argparse
import collections
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
EXEMPT = (".Faults.", ".Performance.")


def seconds(duration):
    h, m, s = duration.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def read(directory):
    rows = []
    for path in glob.glob(os.path.join(directory, "**", "*.trx"), recursive=True):
        root = ET.parse(path).getroot()
        classes = {
            test.get("id"): test.find("t:TestMethod", NS).get("className")
            for test in root.find("t:TestDefinitions", NS)
        }
        for result in root.find("t:Results", NS):
            rows.append(
                (
                    seconds(result.get("duration", "0:0:0")),
                    result.get("outcome"),
                    classes.get(result.get("testId"), "?"),
                    result.get("testName"),
                )
            )
    return rows


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
    parser = argparse.ArgumentParser()
    parser.add_argument("directory")
    parser.add_argument("--budget", type=float, default=3.0)
    parser.add_argument("--top", type=int, default=15)
    parser.add_argument("--github", action="store_true")
    args = parser.parse_args()

    rows = read(args.directory)
    if not rows:
        print(f"no TRX results under {args.directory}", file=sys.stderr)
        return 1

    outcomes = collections.Counter(outcome for _, outcome, _, _ in rows)
    print(f"{len(rows)} results: " + ", ".join(f"{k} {v}" for k, v in sorted(outcomes.items())))

    projects = collections.defaultdict(lambda: [0, 0.0])
    classes = collections.defaultdict(lambda: [0, 0.0])
    for duration, _, cls, _ in rows:
        project = cls.split(".")[1] if cls.count(".") >= 1 else cls
        projects[project][0] += 1
        projects[project][1] += duration
        classes[cls][0] += 1
        classes[cls][1] += duration

    print("\nTime per project (summed; projects and parallel classes overlap in wall time):")
    for project, (count, total) in sorted(projects.items()):
        print(f"  {project:20} {count:5} tests {total:8.1f} s")

    print(f"\nSlowest {args.top} classes:")
    for cls, (count, total) in sorted(classes.items(), key=lambda kv: -kv[1][1])[: args.top]:
        print(f"  {total:7.1f} s {count:4}  {cls}")

    print(f"\nSlowest {args.top} tests:")
    for duration, outcome, _, name in sorted(rows, reverse=True)[: args.top]:
        print(f"  {duration:6.2f} s  {name}")

    over = [
        (duration, name)
        for duration, outcome, cls, name in rows
        if duration > args.budget and not any(part in name for part in EXEMPT)
    ]
    print(f"\nOver the {args.budget:g} s budget (Faults and Performance exempt): {len(over)}")
    for duration, name in sorted(over, reverse=True):
        print(f"  {duration:6.2f} s  {name}")
        if args.github:
            print(f"::warning title=Slow test::{name} took {duration:.1f} s (budget {args.budget:g} s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
