#!/usr/bin/env python3
"""Read each release/deploy workflow's latest attempt and latest successful run via gh."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
FIELDS = "databaseId,headSha,headBranch,status,conclusion,createdAt,updatedAt,url"
LIMITATION = (
    "Actions records describe successful release/deployment runs, not live container "
    "digests or installed Desktop/Collector versions. Deployments currently pull :latest."
)


def command(*args):
    result = subprocess.run(args, cwd=ROOT, text=True, capture_output=True, timeout=60)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or f"Command failed: {args[0]}")
    return result.stdout.strip()


def workflow_status(repo, workflow):
    base = ["gh", "run", "list", "--repo", repo, "--workflow", str(workflow["id"]),
            "--limit", "1", "--json", FIELDS]
    try:
        latest = json.loads(command(*base))
        # Query independently: a long history of failures must not hide the last success.
        success = json.loads(command(*base, "--status", "success"))
        return {"workflow": workflow["name"], "path": workflow["path"],
                "latestAttempt": latest[0] if latest else None,
                "latestSuccessfulRun": success[0] if success else None}
    except (RuntimeError, subprocess.TimeoutExpired, json.JSONDecodeError) as error:
        return {"workflow": workflow["name"], "path": workflow["path"], "error": str(error)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", help="GitHub owner/repo; defaults to this checkout's repository")
    parser.add_argument("--json", action="store_true", help="Print machine-readable evidence")
    args = parser.parse_args()
    repo = args.repo or command("gh", "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner")
    workflows = json.loads(command("gh", "workflow", "list", "--repo", repo, "--all",
                                   "--limit", "1000", "--json", "id,name,path,state"))
    # Discover names from GitHub; deployment units need no second version registry here.
    selected = [w for w in workflows if w["state"] == "active" and
                (Path(w["path"]).name.startswith(("deploy-", "release-")) or w["name"] == "CI")]
    if not selected:
        raise RuntimeError("No active release/deploy/CI workflows found")
    with ThreadPoolExecutor(max_workers=4) as executor:
        rows = list(executor.map(lambda w: workflow_status(repo, w), selected))
    report = {"repository": repo, "queriedAt": datetime.now(timezone.utc).isoformat(),
              "localHead": command("git", "rev-parse", "HEAD"),
              "localDirty": bool(command("git", "status", "--porcelain")),
              "limitation": LIMITATION, "workflows": rows}
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        print(f"Repository: {repo}\nLocal HEAD: {report['localHead']} (dirty={report['localDirty']})\n")
        print("| Workflow | Successful ref | Commit | Run created (UTC) | Latest attempt | Evidence |")
        print("| --- | --- | --- | --- | --- | --- |")
        for row in rows:
            if "error" in row:
                print(f"{row['workflow']}: {row['error']}", file=sys.stderr)
                continue
            success, latest = row["latestSuccessfulRun"], row["latestAttempt"]
            attempt = (f"{latest['conclusion'] or latest['status']} @ {latest['headSha'][:7]}"
                       if latest else "no runs")
            if success:
                print(f"| {row['workflow']} | {success['headBranch']} | {success['headSha'][:7]} | "
                      f"{success['createdAt']} | {attempt} | [run]({success['url']}) |")
            else:
                print(f"| {row['workflow']} | no successful run | — | — | {attempt} | — |")
        print(f"\n{LIMITATION}")
    return 1 if any("error" in row for row in rows) else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (RuntimeError, OSError, subprocess.TimeoutExpired, json.JSONDecodeError) as error:
        print(f"release-status: {error}", file=sys.stderr)
        sys.exit(1)
