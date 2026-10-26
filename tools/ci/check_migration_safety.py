#!/usr/bin/env python3
"""Rollout safety check for NEW migrations (CI, .github/workflows/ci.yml).

deploy.sh runs a commit's migrations before its pods take traffic, and a rollback is the previous application version
against the current schema. Both only work while every migration keeps the previous release running: add, don't
remove or rename, and never add a NOT NULL column without a default (the previous release's INSERTs don't know it).
Remove things in a later migration, once no deployed version reads them (expand, release, contract).

Flags, per statement:
  DROP TABLE / TYPE / SCHEMA, DROP COLUMN, RENAME, ALTER COLUMN ... TYPE, ALTER COLUMN ... SET NOT NULL,
  ADD COLUMN ... NOT NULL without a DEFAULT, TRUNCATE, DELETE FROM.
A migration that must do one of these (the contract step) says so with a comment line anywhere in the file:
  -- allow-breaking: <what the previous release stopped using, and when it shipped>
A migration already on main can't be edited to add that line, so a reviewed allowance for it goes in
tools/ci/migration_safety_allowances.txt instead: one line per file, "<file name>: <why it is safe>".

Usage: check_migration_safety.py database/migrations/0017_x.sql [...]   (exit 1 when a flagged file has no allowance)
Comments and dollar-quoted bodies (functions, triggers) are not statements and are ignored.
"""
import os
import re
import sys

ALLOWANCE = re.compile(r"^\s*--\s*allow-breaking:\s*\S", re.MULTILINE)
ALLOWANCES_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "migration_safety_allowances.txt")


def reviewed_allowances(path: str = ALLOWANCES_FILE) -> dict:
    """File name -> reason, from the allowances file. Lines without a reason are ignored (a reason is the point)."""
    allowances = {}
    if not os.path.exists(path):
        return allowances
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or ":" not in line:
                continue
            name, reason = line.split(":", 1)
            if reason.strip():
                allowances[name.strip()] = reason.strip()
    return allowances

RULES = [
    (re.compile(r"\bDROP\s+(TABLE|TYPE|SCHEMA)\b", re.I), "drops a table, type, or schema the previous release may use"),
    (re.compile(r"\bALTER\s+TABLE\b.*\bDROP\s+(COLUMN\s+)?(IF\s+EXISTS\s+)?(?!CONSTRAINT\b|DEFAULT\b|NOT\b|IDENTITY\b|EXPRESSION\b)\w", re.I | re.S),
     "drops a column the previous release may read"),
    (re.compile(r"\bRENAME\s+(COLUMN\b|TO\b)", re.I), "renames something the previous release refers to by name"),
    (re.compile(r"\bALTER\s+(COLUMN\s+)?\w+\s+(SET\s+DATA\s+)?TYPE\b", re.I), "changes a column type under the running version"),
    (re.compile(r"\bALTER\s+(COLUMN\s+)?\w+\s+SET\s+NOT\s+NULL\b", re.I), "makes a column NOT NULL that the previous release may leave null"),
    (re.compile(r"^\s*TRUNCATE\b", re.I), "deletes data"),
    (re.compile(r"^\s*DELETE\s+FROM\b", re.I), "deletes data"),
]


def strip_comments_and_bodies(sql: str) -> str:
    """Removes -- comments, /* */ comments, and $tag$ ... $tag$ bodies (replaced by a placeholder), keeping newlines."""
    out = []
    i = 0
    n = len(sql)
    while i < n:
        if sql.startswith("--", i):
            end = sql.find("\n", i)
            i = n if end < 0 else end
        elif sql.startswith("/*", i):
            end = sql.find("*/", i + 2)
            end = n if end < 0 else end + 2
            out.append("\n" * sql.count("\n", i, end))
            i = end
        elif sql[i] == "$" and (m := re.match(r"\$[A-Za-z_]*\$", sql[i:])):
            tag = m.group(0)
            end = sql.find(tag, i + len(tag))
            end = n if end < 0 else end + len(tag)
            out.append(" 'body' " + "\n" * sql.count("\n", i, end))
            i = end
        elif sql[i] == "'":
            end = i + 1
            while end < n:
                end = sql.find("'", end)
                if end < 0:
                    end = n
                    break
                if end + 1 < n and sql[end + 1] == "'":
                    end += 2
                    continue
                break
            end = min(end + 1, n)
            out.append(" 'text' " + "\n" * sql.count("\n", i, end))
            i = end
        else:
            out.append(sql[i])
            i += 1
    return "".join(out)


def statements(sql: str):
    """Yields (line_number, statement) for each top-level statement."""
    line = 1
    start_line = 1
    buffer = []
    for ch in sql:
        if not "".join(buffer).strip():
            start_line = line
        buffer.append(ch)
        if ch == "\n":
            line += 1
        if ch == ";":
            text = "".join(buffer).strip()
            if text.strip(";").strip():
                yield start_line, text
            buffer = []
    tail = "".join(buffer).strip()
    if tail:
        yield start_line, tail


def split_top_level(text: str, separator: str = ","):
    parts, depth, current = [], 0, []
    for ch in text:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        if ch == separator and depth == 0:
            parts.append("".join(current))
            current = []
        else:
            current.append(ch)
    parts.append("".join(current))
    return parts


ADD_COLUMN = re.compile(r"^\s*ADD\s+(COLUMN\s+)?(IF\s+NOT\s+EXISTS\s+)?(?!CONSTRAINT\b)(?P<name>\"?\w+\"?)\s+(?P<definition>.*)$", re.I | re.S)


def not_null_without_default(statement: str):
    """Column names added NOT NULL with neither a DEFAULT nor a generated value."""
    m = re.match(r"\s*ALTER\s+TABLE\s+(IF\s+EXISTS\s+)?(ONLY\s+)?[\w.\"]+\s+(?P<actions>.*)$", statement.rstrip(";"), re.I | re.S)
    if not m:
        return []
    found = []
    for action in split_top_level(m.group("actions")):
        a = ADD_COLUMN.match(action)
        if not a:
            continue
        definition = a.group("definition")
        if re.search(r"\bNOT\s+NULL\b", definition, re.I) and not re.search(r"\bDEFAULT\b|\bGENERATED\b|\bPRIMARY\s+KEY\b|\bAS\s+IDENTITY\b", definition, re.I):
            found.append(a.group("name"))
    return found


def check_file(path: str, allowances: dict | None = None):
    with open(path, encoding="utf-8") as f:
        raw = f.read()
    if allowances is None:
        allowances = reviewed_allowances()
    allowed = bool(ALLOWANCE.search(raw)) or os.path.basename(path) in allowances
    findings = []
    for line, statement in statements(strip_comments_and_bodies(raw)):
        for pattern, why in RULES:
            if pattern.search(statement):
                findings.append((line, why))
                break
        for column in not_null_without_default(statement):
            findings.append((line, f"adds NOT NULL column {column} without a DEFAULT: the previous release's inserts would fail"))
    return allowed, findings


def main(paths):
    failed = False
    for path in paths:
        allowed, findings = check_file(path)
        for line, why in findings:
            level = "notice" if allowed else "error"
            print(f"::{level} file={path},line={line}::{path}:{line}: {why}" + (" (allowed: -- allow-breaking or migration_safety_allowances.txt)" if allowed else ""))
            if not allowed:
                failed = True
    if failed:
        print("A new migration would break the release still running during the rollout (or a rollback). "
              "Split it: add now, remove in a later migration, or add '-- allow-breaking: <reason>' to the file.", file=sys.stderr)
        return 1
    print(f"Checked {len(paths)} migration(s): rollout-safe.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
