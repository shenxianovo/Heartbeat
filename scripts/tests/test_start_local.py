import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class StartupReadinessTests(unittest.TestCase):
    def run_start(self, state="running 0 false 0", analytics="200"):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            for name in ["compose.yml", ".env"]:
                (folder / name).touch()
            commands = {
                "docker": '''#!/bin/sh
case "$*" in
  *inspect*) echo "$FIXTURE_STATE" ;;
  *"ps --all --quiet backend"*) echo fixture-backend ;;
  *"ps --all --quiet headless"*) echo fixture-headless ;;
esac
''',
                "curl": '''#!/bin/sh
case "$*" in
  *collectors*) echo 401 ;;
  *) echo "$FIXTURE_ANALYTICS" ;;
esac
''',
                # Bound the old script's wait so the failure-path regression stays fast.
                "sleep": "#!/bin/sh\nexit 99\n",
            }
            for name, content in commands.items():
                path = folder / name
                path.write_text(content)
                path.chmod(0o755)
            env = dict(os.environ, PATH=str(folder) + os.pathsep + os.environ["PATH"],
                       FIXTURE_STATE=state, FIXTURE_ANALYTICS=analytics)
            return subprocess.run(["bash", str(ROOT / "scripts/start-local.sh"),
                                   "--compose-file", str(folder / "compose.yml"),
                                   "--env-file", str(folder / ".env")], env=env, text=True,
                                  capture_output=True, timeout=10)

    def test_hub_unauthorized_counts_as_ready(self):
        result = self.run_start()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Local stack ready", result.stdout)

    def test_backend_crash_is_reported_before_waiting(self):
        result = self.run_start("exited 137 true 0", "502")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("backend failed", result.stderr)
        self.assertIn("OOM=true", result.stderr)

    def test_restart_loop_is_not_reported_as_ready(self):
        result = self.run_start("restarting 1 false 3", "200")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("backend failed", result.stderr)


if __name__ == "__main__":
    unittest.main()
