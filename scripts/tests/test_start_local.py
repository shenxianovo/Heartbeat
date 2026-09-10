from pathlib import Path
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[2]


class StartupReadinessTests(unittest.TestCase):
    def test_current_startup_regressions(self):
        # start-local.sh/ps1 now forward to the shared Node entrypoint. Run its
        # behavioral tests instead of mocking the retired shell/curl implementation.
        result = subprocess.run(["node", "--test", str(ROOT / "scripts/start-local.test.mjs")],
                                cwd=ROOT, text=True, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
