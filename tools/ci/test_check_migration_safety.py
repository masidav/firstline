import os
import tempfile
import unittest

import check_migration_safety as check


def run(sql: str):
    with tempfile.NamedTemporaryFile("w", suffix=".sql", delete=False, encoding="utf-8") as f:
        f.write(sql)
    try:
        return check.check_file(f.name)
    finally:
        os.unlink(f.name)


class SafetyCheckTests(unittest.TestCase):
    def test_additive_migration_passes(self):
        allowed, findings = run(
            """
            -- adds a column, a table, and a trigger
            ALTER TABLE users ADD COLUMN nickname text, ADD COLUMN flags integer NOT NULL DEFAULT 0;
            CREATE TABLE things (id uuid PRIMARY KEY, name text NOT NULL);
            ALTER TABLE things ADD COLUMN n integer GENERATED ALWAYS AS IDENTITY;
            ALTER TABLE things DROP CONSTRAINT things_pkey;
            ALTER TABLE things ALTER COLUMN name DROP NOT NULL;
            CREATE FUNCTION f() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN DELETE FROM things WHERE id = NEW.id; DROP TABLE nothing; RETURN NEW; END $$;
            UPDATE scoring_rules SET description = 'DROP TABLE in a string is fine';
            """)
        self.assertFalse(allowed)
        self.assertEqual(findings, [])

    def test_drop_column_flagged(self):
        _, findings = run("ALTER TABLE users DROP COLUMN avatar_url;")
        self.assertEqual(len(findings), 1)
        self.assertIn("drops a column", findings[0][1])

    def test_drop_without_column_keyword_flagged(self):
        _, findings = run("ALTER TABLE users\n  ADD COLUMN a text,\n  DROP avatar_url;")
        self.assertEqual([why for _, why in findings], ["drops a column the previous release may read"])

    def test_not_null_without_default_flagged(self):
        _, findings = run("ALTER TABLE users ADD COLUMN region text NOT NULL, ADD COLUMN ok text NOT NULL DEFAULT 'x';")
        self.assertEqual(len(findings), 1)
        self.assertIn("region", findings[0][1])

    def test_rename_type_change_and_set_not_null_flagged(self):
        _, findings = run(
            """
            ALTER TABLE users RENAME COLUMN handle TO username;
            ALTER TABLE users ALTER COLUMN created_at TYPE timestamp;
            ALTER TABLE users ALTER COLUMN display_name SET NOT NULL;
            DROP TABLE lists;
            TRUNCATE jobs;
            DELETE FROM cities WHERE id = 'x';
            """)
        self.assertEqual(len(findings), 6)
        self.assertEqual(findings[0][0], 2)

    def test_allowance_comment_turns_errors_into_notices(self):
        allowed, findings = run(
            "-- allow-breaking: avatar_url unused since the 2026-10-20 release\nALTER TABLE users DROP COLUMN avatar_url;")
        self.assertTrue(allowed)
        self.assertEqual(len(findings), 1)

    def test_main_exit_codes(self):
        with tempfile.TemporaryDirectory() as d:
            good = os.path.join(d, "a.sql")
            bad = os.path.join(d, "b.sql")
            with open(good, "w", encoding="utf-8") as f:
                f.write("CREATE TABLE t (id int);")
            with open(bad, "w", encoding="utf-8") as f:
                f.write("DROP TABLE t;")
            self.assertEqual(check.main([good]), 0)
            self.assertEqual(check.main([good, bad]), 1)

    def test_existing_migrations_are_checked_without_crashing(self):
        root = os.path.join(os.path.dirname(__file__), "..", "..", "database", "migrations")
        for name in sorted(os.listdir(root)):
            if name.endswith(".sql"):
                check.check_file(os.path.join(root, name))


if __name__ == "__main__":
    unittest.main()
