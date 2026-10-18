#!/usr/bin/env python3
"""Claude Code PostToolUse hook: build the solution after a .NET source edit.

On build failure, prints the errors to stderr and exits 2 so Claude sees them and fixes the code.
Skips silently when the edited file isn't .NET source or when no .NET SDK is installed.
Tests are not run here (they need Docker and take longer); CLAUDE.md asks for them before finishing.
"""
import json
import os
import shutil
import subprocess
import sys

DOTNET_SUFFIXES = (".cs", ".csproj", ".props", ".slnx")
MAX_ERROR_LINES = 30


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return
    path = (payload.get("tool_input") or {}).get("file_path", "")
    if not path.endswith(DOTNET_SUFFIXES) or shutil.which("dotnet") is None:
        return

    root = os.environ.get("CLAUDE_PROJECT_DIR") or payload.get("cwd") or os.getcwd()
    result = subprocess.run(
        ["dotnet", "build", "FandomScore.slnx", "-nologo", "-v", "quiet", "-clp:ErrorsOnly"],
        cwd=root,
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        errors = sorted({line.strip() for line in result.stdout.splitlines() if "error" in line})
        print("dotnet build failed after this edit:", file=sys.stderr)
        print("\n".join(errors[:MAX_ERROR_LINES]) or result.stdout[-2000:], file=sys.stderr)
        sys.exit(2)


if __name__ == "__main__":
    main()
