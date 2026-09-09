import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class RefreshDatabaseTests(unittest.TestCase):
    def run_refresh(self, fail_restore=False, migration="20260829100458_AskingWindowIdentity"):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            for name in ["compose.yml", ".env"]:
                (folder / name).touch()
            docker = folder / "docker"
            docker.write_text('''#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
with open(os.environ["COMMAND_LOG"], "a") as log:
    log.write(json.dumps(args) + "\\n")
command = " ".join(args)
if "ps" in args and "--services" in args:
    print("db\\nbackend\\nfrontend\\nheadless")
elif "SELECT 1" in command:
    print("1")
elif "__EFMigrationsHistory" in command:
    print(os.environ["SNAPSHOT_MIGRATION"])
elif "pg_restore" in args and os.environ["FAIL_RESTORE"] == "1":
    sys.exit(1)
''')
            docker.chmod(0o755)
            for name, content in {
                "ssh": "#!/bin/sh\nprintf PGDMPfixture\n",
                "curl": "#!/bin/sh\nprintf 200\n",
                "sleep": "#!/bin/sh\nexit 0\n",
            }.items():
                path = folder / name
                path.write_text(content)
                path.chmod(0o755)
            log_path = folder / "commands.jsonl"
            env = dict(os.environ, PATH=str(folder) + os.pathsep + os.environ["PATH"],
                       COMMAND_LOG=str(log_path), FAIL_RESTORE=str(int(fail_restore)),
                       SNAPSHOT_MIGRATION=migration, TMPDIR=str(folder))
            result = subprocess.run(
                ["bash", str(ROOT / "scripts/refresh-local-data.sh"),
                 "--ssh-destination", "fixture.invalid", "--remote-dir", "/fixture",
                 "--compose-file", str(folder / "compose.yml"),
                 "--env-file", str(folder / ".env"), "--force"],
                env=env, text=True, capture_output=True, timeout=15)
            commands = [json.loads(line) for line in log_path.read_text().splitlines()]
            return result, commands

    def assert_only_database_started(self, commands):
        starts = [args[args.index("up") + 1:] for args in commands if "up" in args]
        self.assertTrue(starts)
        for args in starts:
            self.assertEqual([arg for arg in args if not arg.startswith("-")], ["db"])
            self.assertNotIn("--build", args)

    def test_refresh_preserves_snapshot_without_starting_applications(self):
        result, commands = self.run_refresh()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assert_only_database_started(commands)
        self.assertTrue(any("pg_restore" in args for args in commands))
        stop = next(args for args in commands if "stop" in args)
        for service in ["backend", "frontend", "headless"]:
            self.assertIn(service, stop)
        self.assertIn("20260829100458_AskingWindowIdentity", result.stdout)
        self.assertIn("No application migrations were applied", result.stdout)

    def test_failed_restore_keeps_previous_database_and_applications_stopped(self):
        result, commands = self.run_refresh(fail_restore=True)
        self.assertNotEqual(result.returncode, 0)
        self.assert_only_database_started(commands)
        self.assertFalse(any("ALTER DATABASE heartbeat RENAME" in " ".join(args) for args in commands))
        self.assertIn("Previous local database restored", result.stderr)

    def test_snapshot_newer_than_checkout_can_be_inspected_without_starting_app(self):
        result, commands = self.run_refresh(migration="20990101000000_FutureSchema")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assert_only_database_started(commands)
        self.assertIn("20990101000000_FutureSchema", result.stdout)


if __name__ == "__main__":
    unittest.main()
