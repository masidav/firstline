#!/usr/bin/env python3
"""Claude Code PreToolUse hook: protect applied migrations and .env files.

Blocks (exit 2, reason on stderr, which Claude sees):
  - Edit/Write/MultiEdit of a migration that already exists on the base branch (origin/main, else main).
  - Read/Edit/Write of `.env` or `.env.*` (except `.env.example`).
  - Bash commands that mention `.env` files, or that mention an applied migration together with a
    file-modifying command. This is a best-effort check; CI's migration check is the real backstop.

Anything this script can't determine is allowed: it is a guard rail, not a sandbox.
"""
import json
import os
import re
import subprocess
import sys

MIGRATIONS_DIR = "database/migrations"
BASE_REFS = ("origin/main", "main")
ENV_NAME = re.compile(r"^\.env(\..+)?$")
ENV_IN_COMMAND = re.compile(r"(?:^|[\s/'\"=<>])(\.env(?:\.[\w.-]+)?)(?=$|[\s'\";|&)<>])")
MODIFYING_COMMAND = re.compile(
    r"\bsed\s+(-[a-zA-Z]*i|--in-place)|>|\btee\b|\bmv\b|\brm\b|\btruncate\b|\bcp\b|\bdd\b|\bperl\s+-[a-zA-Z]*i|\bgit\s+(rm|mv)\b"
)


def is_env_file(path: str) -> bool:
    name = os.path.basename(path)
    return bool(ENV_NAME.match(name)) and name != ".env.example"


def project_root(payload: dict) -> str:
    return os.environ.get("CLAUDE_PROJECT_DIR") or payload.get("cwd") or os.getcwd()


def applied_migration(root: str, path: str) -> str | None:
    """Return the repo-relative path if `path` is a migration present on the base branch."""
    absolute = os.path.realpath(os.path.join(root, path))
    relative = os.path.relpath(absolute, os.path.realpath(root))
    if not relative.startswith(MIGRATIONS_DIR + os.sep) or not relative.endswith(".sql"):
        return None
    for ref in BASE_REFS:
        result = subprocess.run(
            ["git", "-C", root, "cat-file", "-e", f"{ref}:{relative}"],
            capture_output=True,
            check=False,
        )
        if result.returncode == 0:
            return relative
    return None


def block(reason: str) -> None:
    print(reason, file=sys.stderr)
    sys.exit(2)


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return
    tool = payload.get("tool_name", "")
    tool_input = payload.get("tool_input") or {}
    root = project_root(payload)

    if tool in ("Read", "Edit", "Write", "MultiEdit", "NotebookEdit"):
        path = tool_input.get("file_path") or tool_input.get("notebook_path") or ""
        if path and is_env_file(path):
            block(f"Blocked: {os.path.basename(path)} may hold secrets. Use .env.example for placeholders.")
        if tool != "Read" and path:
            migration = applied_migration(root, path)
            if migration:
                block(
                    f"Blocked: {migration} is already on main, and applied migrations must never change. "
                    f"Add a new migration file in {MIGRATIONS_DIR}/ instead."
                )

    elif tool == "Bash":
        command = tool_input.get("command", "")
        for match in ENV_IN_COMMAND.finditer(command):
            if match.group(1) != ".env.example":
                block(f"Blocked: this command touches {match.group(1)}, which may hold secrets.")
        if MODIFYING_COMMAND.search(command):
            for candidate in re.findall(rf"[\w./-]*{re.escape(MIGRATIONS_DIR)}/[\w.-]+\.sql", command):
                migration = applied_migration(root, candidate)
                if migration:
                    block(
                        f"Blocked: this command may modify {migration}, which is already on main. "
                        f"Add a new migration instead."
                    )


if __name__ == "__main__":
    main()
