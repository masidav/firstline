#!/usr/bin/env python3
"""Contract version check for openapi.yaml (CI, .github/workflows/ci.yml, job "contract").

openapi.yaml is the API contract the web apps are generated from, and after the repository split
(docs/plans/2026-10-31-repo-split.md) each app vendors the copy it was built against. So every edit to the file
needs a new `info.version` (semver), and CI tags each version that reaches main as the release
`contract-v<version>` with the file attached. Rules, comparing the file at HEAD with the latest release's:
  - unchanged file: nothing to do (the version may stay);
  - changed file: `info.version` must be greater than the release's;
  - changed with a breaking change (oasdiff `breaking --fail-on ERR` failed; `--breaking` here): the MAJOR part
    must be greater. Removing or tightening something is allowed only once no deployed client uses it
    (expand, release the clients, contract), and the major bump is the deliberate marker of that step.

Usage: check_contract_version.py --current openapi.yaml [--previous <openapi.yaml of the latest release>] [--breaking]
Exit 1 with the reason on a violation. Prints `changed=<true|false>` and `version=<current>`, and appends them to
$GITHUB_OUTPUT when set, for the release job.
"""
import argparse
import os
import re
import sys

# `version:` directly under `info:` (two-space indent); a `version` nested deeper in the block doesn't match.
VERSION = re.compile(r"^info:\n(?:  .*\n)*?  version:\s*[\"']?([^\"'\s]+)", re.MULTILINE)
SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")


def contract_version(text: str) -> tuple[int, int, int]:
    """The `info.version` of an OpenAPI document as (major, minor, patch); ValueError when missing or not semver."""
    found = VERSION.search(text)
    if not found:
        raise ValueError("no info.version")
    parts = SEMVER.match(found.group(1))
    if not parts:
        raise ValueError(f"info.version '{found.group(1)}' is not MAJOR.MINOR.PATCH")
    return tuple(int(p) for p in parts.groups())  # type: ignore[return-value]


def check(current: str, previous: str | None, breaking: bool) -> tuple[bool, str | None]:
    """(changed, problem). `previous` is None when no release exists yet (the first version is always fine)."""
    version = contract_version(current)
    if previous is None:
        return True, None
    if current == previous:
        return False, None
    before = contract_version(previous)
    if version <= before:
        return True, (f"openapi.yaml changed but info.version is {fmt(version)}; the latest release is "
                      f"contract-v{fmt(before)}. Bump it (minor for additive changes, major for breaking ones).")
    if breaking and version[0] <= before[0]:
        return True, (f"openapi.yaml has a breaking change (oasdiff) but info.version {fmt(version)} keeps major "
                      f"{before[0]}. Either keep the old field or endpoint until no deployed client uses it, or bump "
                      f"to {before[0] + 1}.0.0 on purpose.")
    return True, None


def fmt(version: tuple[int, int, int]) -> str:
    return ".".join(str(p) for p in version)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--current", required=True, help="openapi.yaml at HEAD")
    parser.add_argument("--previous", help="openapi.yaml of the latest contract release (omit when there is none)")
    parser.add_argument("--breaking", action="store_true", help="oasdiff found a breaking change between the two")
    args = parser.parse_args(argv)
    with open(args.current, encoding="utf-8") as f:
        current = f.read()
    previous = None
    if args.previous:
        with open(args.previous, encoding="utf-8") as f:
            previous = f.read()
    try:
        changed, problem = check(current, previous, args.breaking)
        version = fmt(contract_version(current))
    except ValueError as error:
        print(f"::error file={args.current}::{error}")
        return 1
    outputs = f"changed={'true' if changed else 'false'}\nversion={version}\n"
    print(outputs, end="")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as f:
            f.write(outputs)
    if problem:
        print(f"::error file={args.current}::{problem}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
