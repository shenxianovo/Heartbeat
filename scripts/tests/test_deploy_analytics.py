import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
IMAGE = "ghcr.io/shenxianovo/heartbeat-backend@sha256:" + "a" * 64


class AnalyticsDeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.folder = Path(self.temporary.name)
        (self.folder / ".env").write_text("DB_PASSWORD=fixture\nBACKEND_IMAGE=old\n")
        self.log = self.folder / "commands"
        for name, body in {
            "docker": '''#!/bin/sh
printf '%s|%s\\n' "$BACKEND_IMAGE" "$*" >> "$COMMAND_LOG"
case "$*" in
  *"inspect heartbeat-analytics-migration"*) exit "${MIGRATION_ABSENT:-1}" ;;
  *"ps --all --quiet backend"*) echo backend-id ;;
  *".Image"*|*".RepoDigests"*) echo old-image ;;
  *".State.Status"*) echo "${BACKEND_STATE:-running 0 false}" ;;
esac
if [ -n "$FAIL_COMMAND" ]; then
  case "$*" in *"$FAIL_COMMAND"*) exit 42 ;; esac
fi
case "$*" in *pg_dump*) echo fixture-backup ;; esac
''',
            # Lock behavior itself belongs to flock; orchestration runs on macOS too.
            "flock": "#!/bin/sh\nexit 0\n",
            "sleep": "#!/bin/sh\nexit 0\n",
        }.items():
            path = self.folder / name
            path.write_text(body)
            path.chmod(0o755)

    def run_phase(self, phase, failure="", image=IMAGE, state="running 0 false", migration_absent="1"):
        env = dict(os.environ, PATH=str(self.folder) + os.pathsep + os.environ["PATH"],
                   COMMAND_LOG=str(self.log), FAIL_COMMAND=failure, BACKEND_STATE=state,
                   MIGRATION_ABSENT=migration_absent)
        return subprocess.run(["bash", str(ROOT / "scripts/deploy-analytics.sh"), phase, image],
                              cwd=self.folder, env=env, text=True, capture_output=True, timeout=10)

    def commands(self):
        return self.log.read_text() if self.log.exists() else ""

    def test_migration_stops_and_backs_up_before_running_and_deployment_uses_same_image(self):
        migration = self.run_phase("migrate")
        self.assertEqual(migration.returncode, 0, migration.stdout + migration.stderr)
        commands = self.commands()
        self.assertLess(commands.index("stop backend"), commands.index("pg_dump"))
        self.assertLess(commands.index("pg_restore"), commands.index("--migrate"))
        self.assertNotIn("up -d --no-deps", commands)
        deployment = self.run_phase("deploy")
        self.assertEqual(deployment.returncode, 0, deployment.stdout + deployment.stderr)
        commands = self.commands()
        self.assertLess(commands.index("--check-database"), commands.index("up -d --no-deps"))
        self.assertTrue(all(line.startswith(IMAGE + "|") for line in commands.splitlines()))
        self.assertIn("BACKEND_IMAGE=" + IMAGE, (self.folder / ".env").read_text())
        self.assertIn("DB_PASSWORD=fixture", (self.folder / ".env").read_text())

    def test_backup_and_migration_failures_never_start_backend(self):
        for failure in ["pg_dump", "pg_restore", "--migrate"]:
            with self.subTest(failure=failure):
                migration = self.run_phase("migrate", failure)
                self.assertNotEqual(migration.returncode, 0)
                deployment = self.run_phase("deploy")
                self.assertNotEqual(deployment.returncode, 0)
                self.assertNotIn("up -d --no-deps", self.commands())

    def test_stale_receipt_and_schema_failure_block_startup(self):
        self.assertEqual(self.run_phase("migrate").returncode, 0)
        self.assertNotEqual(self.run_phase("deploy", image=IMAGE.replace("a" * 64, "b" * 64)).returncode, 0)
        self.assertNotEqual(self.run_phase("deploy", "--check-database").returncode, 0)
        self.assertNotIn("up -d --no-deps", self.commands())

    def test_crashed_backend_fails_without_waiting_thirty_minutes(self):
        self.assertEqual(self.run_phase("migrate").returncode, 0)
        for state in ["exited 0 false", "running 1 false", "running 0 true"]:
            with self.subTest(state=state):
                result = self.run_phase("deploy", state=state)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("stop backend", self.commands())
                self.assertNotIn("BACKEND_IMAGE=" + IMAGE, (self.folder / ".env").read_text())

    def test_existing_migration_blocks_retry_before_stop_or_backup(self):
        result = self.run_phase("migrate", migration_absent="0")
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("stop backend", self.commands())
        self.assertNotIn("pg_dump", self.commands())

    def test_retention_keeps_two_successful_backups_and_all_failed_backups(self):
        backups = self.folder / "backups"
        backups.mkdir()
        for index in range(4):
            prefix = backups / f"analytics-20000101T00000{index}Z-1"
            prefix.with_suffix(".dump").write_text("backup")
            if index < 3:
                prefix.with_suffix(".success").touch()
        self.assertEqual(self.run_phase("migrate").returncode, 0)
        self.assertEqual(self.run_phase("deploy").returncode, 0)
        self.assertEqual(len(list(backups.glob("*.success"))), 2)
        self.assertTrue((backups / "analytics-20000101T000003Z-1.dump").exists())

    def test_mutable_image_is_rejected_before_any_docker_operation(self):
        result = self.run_phase("migrate", image="ghcr.io/shenxianovo/heartbeat-backend:latest")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.commands(), "")


if __name__ == "__main__":
    unittest.main()
