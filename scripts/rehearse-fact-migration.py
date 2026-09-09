#!/usr/bin/env python3
"""Verify ADR-055 against a full backup in an isolated, resource-limited stack.

Requires Docker and Python 3.11+. Builds no images and never connects to the source
database. Private exports/logs stay in --output; only aggregates go to stdout.
"""
import argparse
import hashlib
import itertools
import json
import os
from pathlib import Path
import subprocess
import time
import uuid


BASELINE = "20260829100458_AskingWindowIdentity"
TARGET = "20260908141403_NativeFactCustody"
SUBJECT = """CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
    THEN split_part(d."HardwareId", ':', 3)::uuid
    ELSE md5('legacy-subject:' || d."OwnerId" || ':' || d."Id")::uuid END"""
KIND = """CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
    THEN split_part(d."HardwareId", ':', 2) ELSE 'machine' END"""
PAYLOAD = """CASE WHEN jsonb_typeof(f."Attributes") = 'object'
    AND jsonb_typeof(f."Attributes"->'identityKey') = 'string'
    AND f."Attributes"->>'identityKey' = f."IdentityKey"
    AND (jsonb_typeof(f."Attributes"->'title') IS NULL OR jsonb_typeof(f."Attributes"->'title') IN ('string','null'))
    AND f."Attributes"->>'title' IS NOT DISTINCT FROM f."Title"
    AND jsonb_typeof(f."Attributes"->'attributes') = 'object'
    THEN (f."Attributes" - 'identityKey') || jsonb_build_object('activityKey', f."IdentityKey")
    ELSE jsonb_build_object('activityKey', f."IdentityKey", 'title', f."Title", 'attributes', f."Attributes") END"""


def rows(family, migrated, after=None):
    segment = family == "segments"
    times = 'f."StartTime", f."EndTime"' if segment else 'f."Timestamp"'

    def page(table):
        # Bound the rows before joining/serializing Payload. UUID keyset paging
        # visits every Id exactly once without an OFFSET scan or one huge sort.
        predicate = f'''WHERE "Id" > '{uuid.UUID(after)}'::uuid''' if after else ''
        return f'''(SELECT * FROM "{table}" {predicate} ORDER BY "Id" LIMIT 10000)'''

    if migrated:
        table = "Segments" if segment else "Events"
        return f'''SELECT jsonb_build_array(f."Id", f."OwnerId", f."StreamId", f."FactId",
            f."Revision", f."Source", f."AppIdentityId", a."AppId", {times}, f."Payload",
            s."SubjectId", u."Kind", u."DeviceId", s."Source", s."FactKind", s."Origin")
            FROM {page(table)} f LEFT JOIN "AppIdentities" a ON a."Id"=f."AppIdentityId"
            LEFT JOIN "Streams" s ON s."OwnerId"=f."OwnerId" AND s."StreamId"=f."StreamId"
            LEFT JOIN "Subjects" u ON u."OwnerId"=s."OwnerId" AND u."SubjectId"=s."SubjectId"
            ORDER BY f."Id"'''
    table = "ActivitySegments" if segment else "InputEvents"
    source = 'f."Source"' if segment else "'system'::text"
    kind = "segment" if segment else "event"
    payload = PAYLOAD if segment else '''jsonb_build_object('eventType',
        CASE f."EventType" WHEN 1 THEN 'keyDown' WHEN 2 THEN 'mouseButton' WHEN 3 THEN 'mouseScroll' END,
        'codeSet', f."CodeSet", 'code', f."Code")'''
    apps = 'f."AppIdentityId", a."AppId"' if segment else "NULL, NULL"
    app_join = 'LEFT JOIN "AppIdentities" a ON a."Id"=f."AppIdentityId"' if segment else ""
    return f'''SELECT jsonb_build_array(f."Id", d."OwnerId",
        md5('legacy-stream:' || d."OwnerId" || ':' || d."Id" || ':' || {source} || ':{kind}')::uuid,
        f."Id", 1, {source}, {apps}, {times}, {payload}, {SUBJECT}, {KIND},
        CASE WHEN ({KIND})='machine' THEN d."Id" ELSE NULL END, {source}, '{kind}', 'legacy-import')
        FROM {page(table)} f LEFT JOIN "Devices" d ON d."Id"=f."DeviceId" {app_join} ORDER BY f."Id"'''


def aggregates(family, migrated):
    if family == "segments":
        table = "Segments" if migrated else "ActivitySegments"
        owner = 'f."OwnerId"' if migrated else 'd."OwnerId"'
        join = "" if migrated else 'JOIN "Devices" d ON d."Id"=f."DeviceId"'
        query = f'''SELECT {owner} AS owner, f."Source" AS source, a."AppId" AS app,
            date_trunc('day', f."StartTime") AS day, count(*) AS count,
            sum(extract(epoch FROM (f."EndTime"-f."StartTime"))) AS seconds
            FROM "{table}" f {join} LEFT JOIN "AppIdentities" a ON a."Id"=f."AppIdentityId"
            GROUP BY 1,2,3,4'''
    else:
        table = "Events" if migrated else "InputEvents"
        owner = 'f."OwnerId"' if migrated else 'd."OwnerId"'
        join = "" if migrated else 'JOIN "Devices" d ON d."Id"=f."DeviceId"'
        event = '''f."Payload"->>'eventType', f."Payload"->>'codeSet', (f."Payload"->>'code')::int''' if migrated else '''
            CASE f."EventType" WHEN 1 THEN 'keyDown' WHEN 2 THEN 'mouseButton' WHEN 3 THEN 'mouseScroll' END,
            f."CodeSet", f."Code"'''
        query = f'''SELECT {owner} AS owner, date_trunc('day', f."Timestamp") AS day,
            {event}, count(*) AS count FROM "{table}" f {join} GROUP BY 1,2,3,4,5'''
        # Array column names differ between layouts; use stable names for row JSON.
        query = f'SELECT * FROM ({query}) q(owner, day, event_type, code_set, code, count)'
    return f'SELECT to_jsonb(q) FROM ({query}) q ORDER BY to_jsonb(q)::text'


def compare(expected, actual):
    mismatches = count = 0
    with expected.open('rb') as left, actual.open('rb') as right:
        for a, b in itertools.zip_longest(left, right):
            count += 1
            mismatches += a != b
    return {"rows": count, "mismatches": mismatches}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--backup', type=Path, required=True)
    parser.add_argument('--image', required=True, help='Locally built candidate image')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if not args.backup.is_file():
        parser.error('An existing custom-format pg_dump is required')
    os.umask(0o077)
    args.output.mkdir(parents=True, exist_ok=False)
    prefix = 'heartbeat-fact-rehearsal-' + uuid.uuid4().hex[:10]
    database, backend = prefix + '-db', prefix + '-backend'
    report = {'passed': False, 'samples': [], 'limits': {
        'database': {'cpus': 0.75, 'memoryMiB': 512},
        'backend': {'cpus': 0.25, 'memoryMiB': 256}, 'swapMiB': 0,
        'fullRowExportBatchSize': 10000},
        'scope': 'isolated backup-to-health rehearsal; excludes real deployment and installed collectors'}
    deadline = None

    def run(*command, **kwargs):
        timeout = max(0.1, deadline - time.monotonic()) if deadline else 600
        result = subprocess.run(command, check=True, timeout=timeout,
                                stderr=subprocess.PIPE, **kwargs)
        return result.stdout

    def capture(*command):
        return run(*command, stdout=subprocess.PIPE).decode().strip()

    def sql(query):
        return capture('docker', 'exec', '-e', 'PGTZ=UTC', database, 'psql',
                       '-U', 'postgres', '-d', 'heartbeat', '-XqAt', '-v', 'ON_ERROR_STOP=1', '-c', query)

    def export(query, path):
        with path.open('xb') as out:
            run('docker', 'exec', '-e', 'PGTZ=UTC', database, 'psql', '-U', 'postgres',
                '-d', 'heartbeat', '-XqAt', '-v', 'ON_ERROR_STOP=1',
                '-c', f'COPY ({query}) TO STDOUT', stdout=out)

    def export_rows(family, migrated, path):
        cursor = None
        with path.open('xb') as out:
            while True:
                data = run('docker', 'exec', '-e', 'PGTZ=UTC', database, 'psql', '-U', 'postgres',
                           '-d', 'heartbeat', '-XqAt', '-v', 'ON_ERROR_STOP=1',
                           '-c', f'COPY ({rows(family, migrated, cursor)}) TO STDOUT', stdout=subprocess.PIPE)
                if not data:
                    break
                out.write(data)
                # COPY escapes embedded newlines. The first array member is Id;
                # parse only that UUID rather than interpreting private Payload.
                last_id = str(uuid.UUID(data.rsplit(b'\n', 2)[-2].split(b'"', 2)[1].decode()))
                if cursor is not None and last_id <= cursor:
                    raise RuntimeError('Full-row export did not advance')
                cursor = last_id
                if data.count(b'\n') < 10000:
                    break

    def storage():
        return json.loads(sql('''SELECT json_build_object('databaseBytes', pg_database_size(current_database()),
            'walBytes', (SELECT sum(size) FROM pg_ls_waldir()),
            'tempBytes', (SELECT temp_bytes FROM pg_stat_database WHERE datname=current_database()),
            'lsn', pg_current_wal_lsn()::text)'''))

    def sample():
        point = {'seconds': round(time.monotonic()-started, 2)}
        for name, label in [(database, 'database'), (backend, 'backend')]:
            state = json.loads(capture('docker', 'inspect', '--format', '{{json .State}}', name))
            if not state['Running'] or state['OOMKilled']:
                raise RuntimeError(f'{label} stopped or was OOM killed')
            memory = capture('docker', 'exec', name, 'cat', '/sys/fs/cgroup/memory.current',
                             '/sys/fs/cgroup/memory.peak').splitlines()
            point[label+'MemoryBytes'], point[label+'PeakMemoryBytes'] = map(int, memory)
            counters = dict(line.split() for line in capture('docker', 'exec', name,
                            'cat', '/sys/fs/cgroup/memory.stat').splitlines())
            point[label+'WorkingSetBytes'] = int(memory[0]) - int(counters.get('inactive_file', 0))
        point['postgresDirectoryKiB'] = int(capture('docker', 'exec', database, 'du', '-sk',
                                                   '/var/lib/postgresql').split()[0])
        report['samples'].append(point)

    def ready():
        while True:
            sample()
            try:
                run('docker', 'exec', database, 'wget', '-q', '-O', '/dev/null', '-T', '2',
                    f'http://{backend}:8080/health', stdout=subprocess.DEVNULL)
                return
            except subprocess.CalledProcessError:
                time.sleep(2)

    try:
        report['imageId'] = capture('docker', 'image', 'inspect', '--format', '{{.Id}}', args.image)
        with args.backup.open('rb') as backup:
            report['backupSha256'] = hashlib.file_digest(backup, 'sha256').hexdigest()
        capture('docker', 'network', 'create', '--internal', prefix)
        capture('docker', 'run', '--detach', '--name', database, '--network', prefix,
                '--cpus=0.75', '--memory=512m', '--memory-swap=512m',
                '-e', 'POSTGRES_HOST_AUTH_METHOD=trust', '-e', 'POSTGRES_DB=heartbeat', 'postgres:18.4-alpine')
        deadline = time.monotonic() + 60
        while True:
            try:
                capture('docker', 'exec', database, 'pg_isready', '-h', '127.0.0.1', '-U', 'postgres')
                break
            except subprocess.CalledProcessError:
                time.sleep(1)
        deadline = None
        print('Restoring full backup in isolated PostgreSQL...', flush=True)
        with args.backup.open('rb') as backup:
            run('docker', 'exec', '-i', database, 'pg_restore', '-U', 'postgres', '-d', 'heartbeat',
                '--no-owner', '--no-acl', '--exit-on-error', stdin=backup, stdout=subprocess.DEVNULL)
        if sql('SELECT max("MigrationId") FROM "__EFMigrationsHistory"') != BASELINE:
            raise RuntimeError('Backup is not at the supported pre-Fact baseline')
        sql('ANALYZE')
        report['before'] = storage()
        report['sourceCounts'] = json.loads(sql('''SELECT json_build_object(
            'segments', (SELECT count(*) FROM "ActivitySegments"), 'events', (SELECT count(*) FROM "InputEvents"))'''))
        report['tableOidsBefore'] = sql('''SELECT '"ActivitySegments"'::regclass::oid, '"InputEvents"'::regclass::oid''')
        print('Exporting every historical row and query aggregate for exact comparison...', flush=True)
        for family in ['segments', 'events']:
            export_rows(family, False, args.output / f'{family}-expected.rows')
            export(aggregates(family, False), args.output / f'{family}-aggregate-expected.rows')
        report['before'] = storage()
        started = time.monotonic()
        deadline = started + 600
        # Include a full constrained backup in the simulated write-stop budget.
        with (args.output / 'upgrade-backup.dump').open('xb') as backup:
            run('docker', 'exec', database, 'pg_dump', '-U', 'postgres', '-d', 'heartbeat', '-Fc', stdout=backup)
        with (args.output / 'upgrade-backup.dump').open('rb') as backup:
            run('docker', 'exec', '-i', database, 'pg_restore', '--list', stdin=backup, stdout=subprocess.DEVNULL)
        report['constrainedBackupSeconds'] = round(time.monotonic()-started, 3)
        capture('docker', 'run', '--detach', '--name', backend, '--network', prefix,
                '--cpus=0.25', '--memory=256m', '--memory-swap=256m',
                '-e', f'ConnectionStrings__DefaultConnection=Host={database};Database=heartbeat;Username=postgres',
                '-e', 'AuthService__Authority=https://auth.invalid', args.image)
        print('Measuring backup-to-health within 600 seconds...', flush=True)
        ready()
        report['backupToHealthySeconds'] = round(time.monotonic()-started, 3)
        deadline = None
        report['after'] = storage()
        report['migrationWalBytes'] = int(sql(f"SELECT pg_wal_lsn_diff('{report['after']['lsn']}', '{report['before']['lsn']}')"))
        report['tableOidsAfter'] = sql('''SELECT '"Segments"'::regclass::oid, '"Events"'::regclass::oid''')
        if report['tableOidsBefore'] != report['tableOidsAfter']:
            raise RuntimeError('Migration replaced physical tables instead of evolving them')
        report['migrationAppliedOnce'] = sql(f'''SELECT count(*) FROM "__EFMigrationsHistory" WHERE "MigrationId"='{TARGET}' ''') == '1'
        report['columns'] = json.loads(sql('''SELECT json_object_agg(table_name,n) FROM
            (SELECT table_name,count(*) n FROM information_schema.columns WHERE table_schema='public'
            AND table_name IN ('Segments','Events') GROUP BY 1) q'''))
        if not report['migrationAppliedOnce'] or report['columns'] != {'Segments': 10, 'Events': 9}:
            raise RuntimeError('Unexpected migration history or family layout')
        if sql('''SELECT to_regclass('public."Facts"') IS NULL''') != 't':
            raise RuntimeError('Universal Facts table remains')
        report['comparisons'] = {}
        for family in ['segments', 'events']:
            for name in [family, family+'-aggregate']:
                begin = time.monotonic()
                if name == family:
                    export_rows(family, True, args.output / f'{name}-actual.rows')
                else:
                    export(aggregates(family, True), args.output / f'{name}-actual.rows')
                result = compare(args.output / f'{name}-expected.rows', args.output / f'{name}-actual.rows')
                result['exportAndCompareSeconds'] = round(time.monotonic()-begin, 3)
                report['comparisons'][name] = result
                if result['mismatches'] or (name == family and result['rows'] != report['sourceCounts'][family]):
                    raise RuntimeError(f'{name}: historical semantic mismatch; private rows retained in output')
        print('All rows match. Checking restart and migration idempotence...', flush=True)
        restarted = time.monotonic()
        deadline = restarted + 600
        capture('docker', 'restart', backend)
        ready()
        report['restartHealthySeconds'] = round(time.monotonic()-restarted, 3)
        deadline = None
        for family in ['segments', 'events']:
            export_rows(family, True, args.output / f'{family}-restart.rows')
            result = compare(args.output / f'{family}-expected.rows', args.output / f'{family}-restart.rows')
            report['comparisons'][family+'-restart'] = result
            if result['mismatches']:
                raise RuntimeError(f'{family}: restart changed historical semantics')
        report['final'] = storage()
        sample()
        report['passed'] = True
    except Exception as error:
        # Commands and stderr can contain row identities/content. Keep them local.
        if isinstance(error, subprocess.CalledProcessError):
            report['error'] = f'Command failed with exit code {error.returncode}; see error.log'
            (args.output / 'error.log').write_bytes(error.stderr or b'')
            (args.output / 'failed-command.json').write_text(json.dumps(error.cmd)+'\n')
        elif isinstance(error, subprocess.TimeoutExpired):
            report['error'] = 'Command exceeded the remaining rehearsal time budget'
        else:
            report['error'] = type(error).__name__ + ': ' + str(error)
    finally:
        for name, label in [(backend, 'backend'), (database, 'database')]:
            logs = subprocess.run(['docker', 'logs', '--timestamps', name], capture_output=True)
            (args.output / f'{label}.log').write_bytes(logs.stdout + logs.stderr)
            state = subprocess.run(['docker', 'inspect', '--format', '{{json .State}}', name], capture_output=True)
            if state.returncode == 0:
                report[label+'FinalState'] = json.loads(state.stdout)
            subprocess.run(['docker', 'rm', '--force', '--volumes', name], capture_output=True)
        subprocess.run(['docker', 'network', 'rm', prefix], capture_output=True)
        (args.output / 'report.json').write_text(json.dumps(report, indent=2)+'\n')
    print(json.dumps({k: report[k] for k in ['passed', 'backupToHealthySeconds', 'restartHealthySeconds', 'comparisons', 'error'] if k in report}), flush=True)
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
