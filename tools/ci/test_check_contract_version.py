import unittest

import check_contract_version as check


def spec(version: str, extra: str = "") -> str:
    return (f"openapi: 3.1.0\ninfo:\n  title: Fandom Score API\n  version: {version}\n  description: |\n"
            f"    nested:\n      version: 9.9.9\n{extra}paths: {{}}\n")


class ContractVersionTests(unittest.TestCase):
    def test_reads_the_version_under_info_only(self):
        self.assertEqual(check.contract_version(spec("1.4.0")), (1, 4, 0))
        self.assertEqual(check.contract_version(spec('"2.0.1"')), (2, 0, 1))
        with self.assertRaises(ValueError):
            check.contract_version("openapi: 3.1.0\ninfo:\n  title: x\npaths: {}\n")
        with self.assertRaises(ValueError):
            check.contract_version(spec("1.4"))

    def test_first_release_and_unchanged_file(self):
        self.assertEqual(check.check(spec("1.0.0"), None, breaking=False), (True, None))
        self.assertEqual(check.check(spec("1.0.0"), spec("1.0.0"), breaking=False), (False, None))

    def test_changed_file_needs_a_higher_version(self):
        changed, problem = check.check(spec("1.0.0", "x-note: a\n"), spec("1.0.0"), breaking=False)
        self.assertTrue(changed)
        self.assertIn("Bump it", problem)
        changed, problem = check.check(spec("0.9.0"), spec("1.0.0"), breaking=False)
        self.assertTrue(changed)
        self.assertIsNotNone(problem)
        self.assertEqual(check.check(spec("1.1.0"), spec("1.0.0"), breaking=False), (True, None))
        self.assertEqual(check.check(spec("1.0.1"), spec("1.0.0"), breaking=False), (True, None))

    def test_breaking_change_needs_a_major_bump(self):
        changed, problem = check.check(spec("1.1.0"), spec("1.0.0"), breaking=True)
        self.assertTrue(changed)
        self.assertIn("2.0.0", problem)
        self.assertEqual(check.check(spec("2.0.0"), spec("1.3.0"), breaking=True), (True, None))


if __name__ == "__main__":
    unittest.main()
