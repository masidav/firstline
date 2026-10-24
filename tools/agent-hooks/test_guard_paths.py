"""Tests for guard_paths.py. Run: python3 -m unittest discover -s tools/agent-hooks"""
import json
import os
import subprocess
import sys
import tempfile
import unittest

GUARD = os.path.join(os.path.dirname(os.path.abspath(__file__)), "guard_paths.py")


def git(repo: str, *args: str) -> None:
    subprocess.run(["git", "-C", repo, *args], check=True, capture_output=True)


class GuardPathsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        # A throwaway repo whose `main` holds one applied migration.
        cls.repo = tempfile.mkdtemp()
        git(cls.repo, "init", "-q", "-b", "main")
        git(cls.repo, "config", "user.email", "test@example.test")
        git(cls.repo, "config", "user.name", "test")
        os.makedirs(os.path.join(cls.repo, "database/migrations"))
        with open(os.path.join(cls.repo, "database/migrations/0001_initial.sql"), "w") as f:
            f.write("SELECT 1;\n")
        git(cls.repo, "add", ".")
        git(cls.repo, "commit", "-q", "-m", "init")

    def run_guard(self, tool: str, **tool_input: str) -> subprocess.CompletedProcess:
        payload = {"tool_name": tool, "tool_input": tool_input, "cwd": self.repo}
        env = {**os.environ, "CLAUDE_PROJECT_DIR": self.repo}
        return subprocess.run(
            [sys.executable, GUARD], input=json.dumps(payload), capture_output=True, text=True, env=env, check=False
        )

    def assertBlocked(self, result: subprocess.CompletedProcess) -> None:
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertIn("Blocked", result.stderr)

    def assertAllowed(self, result: subprocess.CompletedProcess) -> None:
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_edit_applied_migration_is_blocked(self):
        self.assertBlocked(self.run_guard("Edit", file_path=f"{self.repo}/database/migrations/0001_initial.sql"))

    def test_relative_path_to_applied_migration_is_blocked(self):
        self.assertBlocked(self.run_guard("Write", file_path="database/migrations/0001_initial.sql"))

    def test_new_migration_is_allowed(self):
        self.assertAllowed(self.run_guard("Write", file_path=f"{self.repo}/database/migrations/0002_next.sql"))

    def test_reading_applied_migration_is_allowed(self):
        self.assertAllowed(self.run_guard("Read", file_path=f"{self.repo}/database/migrations/0001_initial.sql"))

    def test_env_files_are_blocked(self):
        for name in (".env", ".env.local", ".env.production"):
            self.assertBlocked(self.run_guard("Read", file_path=f"{self.repo}/{name}"))
            self.assertBlocked(self.run_guard("Edit", file_path=f"{self.repo}/{name}"))

    def test_env_example_is_allowed(self):
        self.assertAllowed(self.run_guard("Edit", file_path=f"{self.repo}/.env.example"))

    def test_bash_touching_env_is_blocked(self):
        self.assertBlocked(self.run_guard("Bash", command="cat .env"))
        self.assertBlocked(self.run_guard("Bash", command="set -a; . ./.env; set +a"))

    def test_bash_with_env_example_is_allowed(self):
        self.assertAllowed(self.run_guard("Bash", command="cp .env.example /tmp/x"))

    def test_bash_modifying_applied_migration_is_blocked(self):
        self.assertBlocked(self.run_guard("Bash", command="sed -i 's/1/2/' database/migrations/0001_initial.sql"))
        self.assertBlocked(self.run_guard("Bash", command="echo x >> database/migrations/0001_initial.sql"))

    def test_bash_reading_applied_migration_is_allowed(self):
        self.assertAllowed(self.run_guard("Bash", command="grep -n CREATE database/migrations/0001_initial.sql"))

    def test_unrelated_tools_and_bad_input_are_allowed(self):
        self.assertAllowed(self.run_guard("Glob", pattern="**/*.cs"))
        result = subprocess.run([sys.executable, GUARD], input="not json", capture_output=True, text=True, check=False)
        self.assertAllowed(result)


if __name__ == "__main__":
    unittest.main()
