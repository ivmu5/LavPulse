"""Allocate a calendar release version from existing Git tags (UTC, no source commits)."""
import datetime as dt
import os
from pathlib import Path
import re
import subprocess
import sys
import unittest
import xml.etree.ElementTree as ET

PATTERN = re.compile(r"v([1-9][0-9]?)\.([0-9]{2})\.(0[1-9]|1[0-2])\.([0-9]{2})$")


def parse(tag):
    match = PATTERN.fullmatch(tag)
    return tuple(map(int, match.groups())) if match else None


def allocate(major, today, tags, current_tags, release):
    if not 1 <= major <= 99:
        raise ValueError("Major version must be between 1 and 99")
    year, month = today.year % 100, today.month
    if not release:
        return major, year, month, 0
    existing = [parse(t) for t in current_tags if parse(t) and parse(t)[0] == major and parse(t)[3] > 0]
    if len(existing) > 1:
        raise ValueError("Multiple LavPulse release tags point to this commit")
    if existing:
        return existing[0]
    versions = [v for t in tags if (v := parse(t))]
    if versions and max(v[0] for v in versions) > major:
        raise ValueError("Major version cannot decrease")
    revision = max((v[3] for v in versions if v[1:3] == (year, month)), default=0) + 1
    if revision > 99:
        raise ValueError("Monthly release limit (99) reached")
    return major, year, month, revision


def git(*args):
    return subprocess.check_output(["git", *args], text=True).splitlines()


def main():
    major = int(ET.parse("Version.props").findtext(".//LavPulseVersionMajor"))
    release = os.environ.get("PUBLISH_RELEASE") == "true"
    v, yy, mm, vv = allocate(major, dt.datetime.now(dt.timezone.utc).date(),
                              git("tag", "--list"), git("tag", "--points-at", "HEAD"), release)
    version = f"{v}.{yy:02}.{mm:02}.{vv:02}"
    values = {"version": version, "tag": "v" + version, "year": f"{yy:02}",
              "month": f"{mm:02}", "revision": f"{vv:02}", "publish": str(release).lower()}
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        for key, value in values.items():
            output.write(f"{key}={value}\n")
    print("LavPulse", values["tag"])


class VersionTests(unittest.TestCase):
    def test_first_and_next(self):
        now = dt.date(2026, 11, 24)
        self.assertEqual(allocate(1, now, [], [], True), (1, 26, 11, 1))
        self.assertEqual(allocate(1, now, ["v1.26.11.01", "v1.26.11.03", "v1.13"], [], True), (1, 26, 11, 4))

    def test_month_and_major(self):
        self.assertEqual(allocate(1, dt.date(2026, 12, 1), ["v1.26.11.99"], [], True), (1, 26, 12, 1))
        self.assertEqual(allocate(2, dt.date(2026, 11, 24), ["v1.26.11.03"], [], True), (2, 26, 11, 4))

    def test_retry_and_pr(self):
        now = dt.date(2026, 12, 1)
        self.assertEqual(allocate(1, now, ["v1.26.11.02"], ["v1.26.11.02"], True), (1, 26, 11, 2))
        self.assertEqual(allocate(1, now, [], [], False), (1, 26, 12, 0))

    def test_overflow(self):
        with self.assertRaises(ValueError):
            allocate(1, dt.date(2026, 11, 24), ["v1.26.11.99"], [], True)


if __name__ == "__main__":
    if "--test" in sys.argv:
        unittest.main(argv=[sys.argv[0]])
    else:
        main()
