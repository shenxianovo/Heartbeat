#!/usr/bin/env python3
"""Rehearse the ADR-059 store via ADR-058 on an existing family backup, without connecting to its source.

Private rows, dumps and logs stay in a new --output directory. Docker/Python 3.11+
required. This is a constrained container rehearsal, not a production deployment
or a measurement of a complete 1C1G host.
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

BASELINE = '20260908141403_NativeFactCustody'
TARGET = '20260911025354_ObservationObjects'


def family_source(family, migrated):
    if not migrated:
        return '"Segments"' if family == 'segments' else '"Events"'
    kind = 'segment' if family == 'segments' else 'event'
    return f'''(SELECT f.*, f."Result" AS "Payload", f."CollectorId" AS "ObserverId",
        f."StartTime" AS "Timestamp" FROM "Facts" f WHERE f."Kind"='{kind}')'''


def row_query(family, attribution=False, migrated=False, after=None):
    predicate = f'''WHERE "Id" > '{uuid.UUID(after)}'::uuid''' if after else ''
    page = f'''(SELECT * FROM {family_source(family, migrated)} {predicate} ORDER BY "Id" LIMIT 10000) f'''
    if not attribution:
        times = 'f."StartTime", f."EndTime"' if family == 'segments' else 'f."Timestamp"'
        return f'''SELECT jsonb_build_array(f."Id", f."OwnerId", f."StreamId", f."FactId",
            f."Revision", f."Source", f."AppIdentityId", {times}, f."Payload") FROM {page} ORDER BY f."Id"'''
    if migrated:
        return f'''SELECT jsonb_build_array(f."Id", f."ObserverId", f."TargetKind",
            CASE WHEN f."TargetKind"='device' THEN f."TargetId" ELSE c."DeviceId" END,
            c."AppId", a."ServiceKey", a."ServiceAccountId", a."LegacySubjectId")
            FROM {page}
            LEFT JOIN "ApplicationContexts" c ON f."TargetKind"='application-context' AND c."OwnerId"=f."OwnerId" AND c."Id"=f."TargetId"
            LEFT JOIN "ServiceAccounts" a ON f."TargetKind"='account' AND a."OwnerId"=f."OwnerId" AND a."Id"=f."TargetId"
            ORDER BY f."Id"'''
    # Evidence from the deployed baseline, independent of the new Target row IDs.
    browser = '''f."Source"='browser' AND u."Kind"='machine' AND u."DeviceId" IS NOT NULL AND i."AppId" IS NOT NULL'''
    account = '''f."Source"='vrchat.account' AND u."Kind"='account' '''
    return f'''SELECT jsonb_build_array(f."Id",
        CASE WHEN s."Origin"='native' THEN CASE
            WHEN f."Source"='system' AND u."Kind"='machine' OR {account} THEN s."CollectorInstanceId"
            WHEN f."Source"='browser' AND u."Kind"='machine' AND s."Dimensions"->>'externalHostIdentity'
                ~ '^[0-9a-fA-F]{{8}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{12}}$'
                THEN nullif((s."Dimensions"->>'externalHostIdentity')::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
            END END,
        CASE WHEN {account} THEN 'account' WHEN {browser} THEN 'application-context'
            WHEN u."Kind"='machine' AND u."DeviceId" IS NOT NULL THEN 'device' END,
        CASE WHEN u."Kind"='machine' THEN u."DeviceId" END,
        CASE WHEN {browser} THEN i."AppId" END,
        CASE WHEN {account} THEN 'vrchat' END, NULL,
        CASE WHEN {account} THEN u."SubjectId" END)
        FROM {page} JOIN "Streams" s ON s."OwnerId"=f."OwnerId" AND s."StreamId"=f."StreamId"
        JOIN "Subjects" u ON u."OwnerId"=s."OwnerId" AND u."SubjectId"=s."SubjectId"
        LEFT JOIN "AppIdentities" i ON i."Id"=f."AppIdentityId" ORDER BY f."Id"'''


def aggregate_query(migrated):
    device = '''CASE WHEN f."TargetKind"='device' THEN f."TargetId" WHEN f."TargetKind"='application-context' THEN c."DeviceId" END''' if migrated else 'u."DeviceId"'
    joins = '''LEFT JOIN "ApplicationContexts" c ON f."TargetKind"='application-context' AND c."OwnerId"=f."OwnerId" AND c."Id"=f."TargetId"''' if migrated else '''JOIN "Streams" s ON s."OwnerId"=f."OwnerId" AND s."StreamId"=f."StreamId"
        JOIN "Subjects" u ON u."OwnerId"=s."OwnerId" AND u."SubjectId"=s."SubjectId"'''
    # System Report and input counts retain their meanings; other Sources are not attention.
    return f'''SELECT to_jsonb(q) FROM (
        SELECT 'system' AS family, f."OwnerId" AS owner, {device} AS device, i."AppId" AS app,
            date_trunc('day', f."StartTime") AS day, NULL::text AS code, count(*) AS count,
            sum(extract(epoch FROM(f."EndTime"-f."StartTime"))) AS seconds
        FROM {family_source("segments", migrated)} f {joins} LEFT JOIN "AppIdentities" i ON i."Id"=f."AppIdentityId"
        WHERE f."Source"='system' GROUP BY 1,2,3,4,5,6
        UNION ALL
        SELECT 'input', f."OwnerId", {device}, NULL::bigint, date_trunc('day', f."Timestamp"),
            jsonb_build_array(f."Payload"->'eventType', f."Payload"->'codeSet', f."Payload"->'code')::text,
            count(*), NULL::numeric FROM {family_source("events", migrated)} f {joins} WHERE f."Source"='system' GROUP BY 1,2,3,4,5,6
        ) q ORDER BY to_jsonb(q)::text'''


def compare(left, right):
    with left.open('rb') as a, right.open('rb') as b:
        count = mismatch = 0
        for x, y in itertools.zip_longest(a, b):
            count += 1
            mismatch += x != y
    if mismatch:
        raise RuntimeError(f'{right.name}: {mismatch} mismatches in {count} rows')
    return {'rows': count, 'mismatches': mismatch}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--backup', type=Path, required=True)
    parser.add_argument('--image', required=True)
    parser.add_argument('--baseline-image', required=True, help='Exact previous Analytics image for restored-baseline startup')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--timeout-seconds', type=int, default=21000, help='Outer job budget; SQL timeout remains zero')
    args = parser.parse_args()
    if not args.backup.is_file() or args.timeout_seconds <= 0:
        parser.error('An existing backup and positive job timeout are required')
    os.umask(0o077)
    args.output.mkdir(parents=True, exist_ok=False)
    prefix = 'heartbeat-observation-rehearsal-' + uuid.uuid4().hex[:10]
    database = prefix + '-db'
    containers = [database]
    begun = time.monotonic()
    deadline = begun + args.timeout_seconds
    report = {'passed': False, 'baseline': BASELINE, 'target': TARGET, 'comparisons': {}, 'commands': {},
              'scope': 'isolated full-copy rehearsal; not production or whole-host 1C1G acceptance',
              'limits': {'databaseCpu': .75, 'databaseMiB': 768, 'analyticsCpu': .25, 'analyticsMiB': 256, 'swapMiB': 0,
                         'restoreSession': 'maintenance_work_mem=32MB; max_parallel_maintenance_workers=0'},
              'peaks': {}}

    def run(*command, **kwargs):
        return subprocess.run(command, check=True, timeout=max(.1, deadline-time.monotonic()),
                              stderr=subprocess.PIPE, **kwargs)

    def capture(*command):
        return run(*command, stdout=subprocess.PIPE).stdout.decode().strip()

    def sql(query):
        return capture('docker', 'exec', '-e', 'PGTZ=UTC', database, 'psql', '-U', 'postgres', '-d', 'heartbeat', '-XqAt', '-v', 'ON_ERROR_STOP=1', '-c', query)

    def export(query, path):
        with path.open('xb') as out:
            run('docker', 'exec', '-e', 'PGTZ=UTC', database, 'psql', '-U', 'postgres', '-d', 'heartbeat', '-XqAt', '-v', 'ON_ERROR_STOP=1', '-c', f'COPY ({query}) TO STDOUT', stdout=out)

    def export_rows(family, path, attribution=False, migrated=False):
        cursor = None
        with path.open('xb') as out:
            while True:
                data = run('docker', 'exec', '-e', 'PGTZ=UTC', database, 'psql', '-U', 'postgres', '-d', 'heartbeat', '-XqAt', '-v', 'ON_ERROR_STOP=1', '-c', f'COPY ({row_query(family, attribution, migrated, cursor)}) TO STDOUT', stdout=subprocess.PIPE).stdout
                if not data:
                    break
                out.write(data)
                last = str(uuid.UUID(data.rsplit(b'\n', 2)[-2].split(b'"', 2)[1].decode()))
                if cursor is not None and last <= cursor:
                    raise RuntimeError('Row cursor failed to advance')
                cursor = last
                if data.count(b'\n') < 10000:
                    break

    def sample(name, label):
        result = subprocess.run(['docker', 'exec', name, 'cat', '/sys/fs/cgroup/memory.peak'], capture_output=True)
        if result.returncode == 0:
            report['peaks'][label+'MemoryBytes'] = max(report['peaks'].get(label+'MemoryBytes', 0), int(result.stdout))

    def start_app(label, command=(), image_id=None):
        name = prefix + '-' + label
        containers.append(name)
        capture('docker', 'run', '-d', '--name', name, '--network', prefix, '--cpus=.25', '--memory=256m', '--memory-swap=256m',
                '-e', 'ASPNETCORE_ENVIRONMENT=Production', '-e', 'AuthService__Authority=https://auth.invalid',
                '-e', f'ConnectionStrings__DefaultConnection=Host={database};Database=heartbeat;Username=postgres',
                '-e', 'DatabaseMigration__CommandTimeoutSeconds=0', image_id or report['imageId'], *command)
        return name

    def command(label, arg, success):
        start = time.monotonic()
        name = start_app(label, [arg] if arg else [])
        while True:
            state = json.loads(capture('docker', 'inspect', '--format', '{{json .State}}', name))
            if not state['Running']:
                break
            sample(name, label)
            sample(database, 'database')
            time.sleep(.5)
        report['commands'][label] = {'exitCode': state['ExitCode'], 'oomKilled': state['OOMKilled'], 'seconds': round(time.monotonic()-start, 3)}
        if state['OOMKilled'] or state['ExitCode'] != (0 if success else 1):
            raise RuntimeError(f'{label} did not exit as expected')

    def healthy(name, label):
        while True:
            state = json.loads(capture('docker', 'inspect', '--format', '{{json .State}}', name))
            if not state['Running'] or state['OOMKilled']:
                raise RuntimeError('Production startup failed')
            sample(name, label)
            sample(database, 'database')
            try:
                capture('docker', 'exec', database, 'wget', '-q', '-O', '/dev/null', '-T', '2', f'http://{name}:8080/health')
                break
            except subprocess.CalledProcessError:
                time.sleep(1)

    def storage():
        return json.loads(sql('''SELECT json_build_object('databaseBytes',pg_database_size(current_database()),
            'walBytes',(SELECT sum(size) FROM pg_ls_waldir()),'lsn',pg_current_wal_lsn()::text,
            'tempBytes',(SELECT temp_bytes FROM pg_stat_database WHERE datname=current_database()))'''))

    def restore(path):
        with path.open('rb') as source:
            run('docker', 'exec', '-i', '-e',
                'PGOPTIONS=-c maintenance_work_mem=32MB -c max_parallel_maintenance_workers=0',
                database, 'pg_restore', '-U', 'postgres', '-d', 'heartbeat',
                '--no-owner', '--no-acl', '--exit-on-error', stdin=source, stdout=subprocess.DEVNULL)

    def verify_rows(stage, attribution=False):
        for family in ['segments', 'events']:
            for metadata in ([False, True] if attribution else [False]):
                suffix = '-targets' if metadata else ''
                actual = args.output / f'{family}{suffix}-{stage}.rows'
                export_rows(family, actual, metadata, stage != 'restored')
                result = compare(args.output/f'{family}{suffix}-before.rows', actual)
                if result['rows'] != report['counts'][family]:
                    raise RuntimeError('Incomplete family export')
                report['comparisons'][f'{family}{suffix}-{stage}'] = result

    try:
        report['imageId'] = capture('docker', 'image', 'inspect', '--format', '{{.Id}}', args.image)
        report['baselineImageId'] = capture('docker', 'image', 'inspect', '--format', '{{.Id}}', args.baseline_image)
        with args.backup.open('rb') as source:
            report['backupSha256'] = hashlib.file_digest(source, 'sha256').hexdigest()
        capture('docker', 'network', 'create', '--internal', prefix)
        capture('docker', 'run', '-d', '--name', database, '--network', prefix, '--cpus=.75', '--memory=768m', '--memory-swap=768m',
                '-e', 'POSTGRES_HOST_AUTH_METHOD=trust', '-e', 'POSTGRES_DB=heartbeat', 'postgres:18.4-alpine')
        for _ in range(60):
            try:
                capture('docker', 'exec', database, 'pg_isready', '-h', '127.0.0.1', '-U', 'postgres')
                break
            except subprocess.CalledProcessError:
                time.sleep(1)
        print('Restoring the existing family backup...', flush=True)
        restore(args.backup)
        if sql('SELECT max("MigrationId") FROM "__EFMigrationsHistory"') != BASELINE:
            raise RuntimeError('Backup must be at the deployed family baseline')
        report['counts'] = json.loads(sql('''SELECT json_build_object('segments',(SELECT count(*) FROM "Segments"),'events',(SELECT count(*) FROM "Events"))'''))
        if min(report['counts'].values()) == 0:
            raise RuntimeError('Empty families do not satisfy a full-copy rehearsal')
        sql('ANALYZE')
        report['tableOidsBefore'] = sql('''SELECT '"Segments"'::regclass::oid, '"Events"'::regclass::oid''')
        print('Exporting all immutable family rows, expected attribution and query aggregates...', flush=True)
        for family in ['segments', 'events']:
            export_rows(family, args.output/f'{family}-before.rows')
            export_rows(family, args.output/f'{family}-targets-before.rows', True)
        export(aggregate_query(False), args.output/'queries-before.rows')
        stopped = time.monotonic()
        report['simulatedWriteStopUtc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
        report['before'] = storage()
        backup = args.output/'upgrade-backup.dump'
        with backup.open('xb') as out:
            run('docker', 'exec', database, 'pg_dump', '-U', 'postgres', '-d', 'heartbeat', '-Fc', '-Z1', stdout=out)
        with backup.open('rb') as source:
            run('docker', 'exec', '-i', database, 'pg_restore', '--list', stdin=source, stdout=subprocess.DEVNULL)
        report['backupSeconds'] = round(time.monotonic()-stopped, 3)
        print('Checking failed migration, rejected production startup and same-image retry...', flush=True)
        sql('''CREATE FUNCTION rehearsal_reject_update() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Injected rehearsal failure'; END $$;
            CREATE TRIGGER rehearsal_reject_update BEFORE UPDATE ON "Segments" FOR EACH ROW EXECUTE FUNCTION rehearsal_reject_update();''')
        command('injected-failure', '--migrate', False)
        if sql('SELECT max("MigrationId") FROM "__EFMigrationsHistory"') != BASELINE:
            raise RuntimeError('Failed first migration was not rolled back')
        command('rejected-check', '--check-database', False)
        command('rejected-production', None, False)
        sql('DROP TRIGGER rehearsal_reject_update ON "Segments"; DROP FUNCTION rehearsal_reject_update();')
        command('migrate', '--migrate', True)
        command('check', '--check-database', True)
        if sql('SELECT max("MigrationId") FROM "__EFMigrationsHistory"') != TARGET:
            raise RuntimeError('Unexpected target migration')
        name = start_app('production')
        healthy(name, 'production')
        report['simulatedStopToHealthySeconds'] = round(time.monotonic()-stopped, 3)
        report['after'] = storage()
        report['migrationWalBytes'] = int(sql(f"SELECT pg_wal_lsn_diff('{report['after']['lsn']}', '{report['before']['lsn']}')"))
        if report['tableOidsBefore'].split('|')[0] != sql('''SELECT '"Facts"'::regclass::oid'''):
            raise RuntimeError('Segments was not evolved in place')
        if sql("SELECT count(*) FROM pg_class WHERE relname IN ('Segments','Events') AND relkind='r'") != '0':
            raise RuntimeError('Additional physical fact stores remain')
        report['objectViolations'] = json.loads(sql('''SELECT json_build_object(
            'foi', (SELECT count(*) FROM "Facts" f LEFT JOIN LATERAL heartbeat_fact_objects(f."OwnerId",f."TargetKind",f."TargetId",f."AppIdentityId") o ON true WHERE f."FoiId" IS DISTINCT FROM o.foi),
            'relations', (SELECT count(*) FROM "Relations" r LEFT JOIN "Facts" f ON f."OwnerId"=r."OwnerId" AND f."Id"=r."FactId"
                LEFT JOIN LATERAL heartbeat_fact_objects(f."OwnerId",f."TargetKind",f."TargetId",f."AppIdentityId") o ON true
                WHERE r."Kind"='observed-on' AND (f."Id" IS NULL OR r."ValidFrom" IS DISTINCT FROM f."StartTime"
                  OR r."ValidTo" IS DISTINCT FROM coalesce(f."EndTime", f."StartTime")
                  OR (SELECT count(*) FROM "RelationMembers" m WHERE m."RelationId"=r."Id") <> 2
                  OR NOT EXISTS (SELECT 1 FROM "RelationMembers" m WHERE m."RelationId"=r."Id" AND m."Role"='device' AND m."ObjectId"=o.device)
                  OR NOT EXISTS (SELECT 1 FROM "RelationMembers" m WHERE m."RelationId"=r."Id" AND m."Role"='app' AND m."ObjectId"=o.app))),
            'missingRelations', (SELECT count(*) FROM "Facts" f CROSS JOIN LATERAL heartbeat_fact_objects(f."OwnerId",f."TargetKind",f."TargetId",f."AppIdentityId") o
                WHERE o.device IS NOT NULL AND o.app IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Relations" r WHERE r."OwnerId"=f."OwnerId" AND r."FactId"=f."Id" AND r."Kind"='observed-on')))
            '''))
        if any(report['objectViolations'].values()):
            raise RuntimeError('Object or exact-fact relation conversion is incomplete')
        print('Comparing every row and direct attribution after Production startup...', flush=True)
        verify_rows('upgraded', True)
        export(aggregate_query(True), args.output/'queries-after.rows')
        report['comparisons']['queries'] = compare(args.output/'queries-before.rows', args.output/'queries-after.rows')
        report['objectCounts'] = json.loads(sql('SELECT json_object_agg("Kind", total) FROM (SELECT "Kind", count(*) AS total FROM "Objects" GROUP BY "Kind") q'))
        capture('docker', 'stop', name)
        command('retry', '--migrate', True)
        verify_rows('retry', True)
        print('Restoring the actual pre-upgrade backup and comparing every family row...', flush=True)
        capture('docker', 'exec', database, 'dropdb', '-U', 'postgres', '--force', 'heartbeat')
        capture('docker', 'exec', database, 'createdb', '-U', 'postgres', 'heartbeat')
        restore(backup)
        if sql('SELECT max("MigrationId") FROM "__EFMigrationsHistory"') != BASELINE:
            raise RuntimeError('Restored migration history differs')
        restored = start_app('restored-production', image_id=report['baselineImageId'])
        healthy(restored, 'restoredProduction')
        if sql('SELECT max("MigrationId") FROM "__EFMigrationsHistory"') != BASELINE:
            raise RuntimeError('Previous image changed the restored schema')
        report['restoredProductionHealthy'] = True
        verify_rows('restored')
        export(aggregate_query(False), args.output/'queries-restored.rows')
        report['comparisons']['queries-restored'] = compare(args.output/'queries-before.rows', args.output/'queries-restored.rows')
        sample(database, 'database')
        report['passed'] = True
    except KeyboardInterrupt:
        report['error'] = 'Interrupted'
    except Exception as error:
        report['error'] = type(error).__name__
        (args.output/'error.log').write_text(str(error)+'\n')
        if isinstance(error, subprocess.CalledProcessError):
            (args.output/'stderr.log').write_bytes(error.stderr or b'')
    finally:
        for name in reversed(containers):
            logs = subprocess.run(['docker', 'logs', '--timestamps', name], capture_output=True)
            (args.output/f'{name.removeprefix(prefix)}.log').write_bytes(logs.stdout+logs.stderr)
            subprocess.run(['docker', 'rm', '-f', '-v', name], capture_output=True)
        subprocess.run(['docker', 'network', 'rm', prefix], capture_output=True)
        report['totalSeconds'] = round(time.monotonic()-begun, 3)
        (args.output/'report.json').write_text(json.dumps(report, indent=2)+'\n')
    print(json.dumps({k: report[k] for k in ['passed', 'counts', 'simulatedStopToHealthySeconds', 'comparisons', 'error'] if k in report}), flush=True)
    return 0 if report['passed'] else 130 if report.get('error') == 'Interrupted' else 1


if __name__ == '__main__':
    raise SystemExit(main())
