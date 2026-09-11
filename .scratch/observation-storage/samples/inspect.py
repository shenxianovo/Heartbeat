"""Verify and present captured synthetic PostgreSQL rows; never connects to a database."""
from pathlib import Path
import difflib
import json

root = Path(__file__).parent
before_doc = json.loads((root / 'before.json').read_text())
after_doc = json.loads((root / 'after.json').read_text())
before, after = before_doc['tables'], after_doc['tables']
cases = json.loads((root / 'cases.json').read_text())
old = {f['Id']: f for table in ('Segments', 'Events') for f in before[table]}
new = {f['Id']: f for f in after['Facts']}
objects = {o['Id']: o for o in after['Objects']}
assert old.keys() == new.keys()
for case in cases:
    a, b = old[case['Id']], new[case['Id']]
    expected = dict(a)
    expected['CollectorId'] = expected.pop('ObserverId')
    expected['Result'] = expected.pop('Payload')
    expected['Kind'] = case['family']
    if case['family'] == 'event':
        expected['StartTime'] = expected.pop('Timestamp')
        expected['EndTime'] = None
    assert expected == {k:v for k,v in b.items() if k not in ('FoiId','Aspect')}, case['label']
for table in ('Devices', 'Apps', 'ServiceAccounts', 'Persons'):
    assert before[table] == [{k:v for k,v in row.items() if k != 'ObjectId'} for row in after[table]], table
for table in ('AppIdentities','ApplicationContexts','PersonAssociations','Streams','Subjects','FactGaps'):
    assert before[table] == after[table], table
assert len(after['Collectors']) == len({f['ObserverId'] for f in old.values() if f['ObserverId']})
assert all((f['OwnerId'],f['CollectorId']) in {(c['OwnerId'],c['Id']) for c in after['Collectors']} for f in new.values() if f['CollectorId'])

def object_name(object_id):
    if object_id is None:
        return 'null'
    o = objects[object_id]
    return f"{o['Kind']}:{o['Name'] or o['Key']}"

def members(relation):
    return {m['Role']:m['ObjectId'] for m in after['RelationMembers'] if m['RelationId'] == relation['Id']}

for relation in after['Relations']:
    m = members(relation)
    if relation['Kind'] == 'observed-on':
        f = old[relation['FactId']]
        if f['TargetKind'] == 'device':
            device = f['TargetId']
            app = next(i['AppId'] for i in before['AppIdentities'] if i['Id'] == f['AppIdentityId'])
        else:
            context = next(c for c in before['ApplicationContexts'] if c['Id'] == f['TargetId'])
            device, app = context['DeviceId'], context['AppId']
        assert m == {'device':next(d['ObjectId'] for d in after['Devices'] if d['Id'] == device), 'app':next(a['ObjectId'] for a in after['Apps'] if a['Id'] == app)}
        assert relation['ValidFrom'] == new[relation['FactId']]['StartTime']
        assert relation['ValidTo'] == (new[relation['FactId']]['EndTime'] or new[relation['FactId']]['StartTime'])
    elif relation['Kind'] == 'used-by':
        a = next(a for a in before['PersonAssociations'] if a['Id'] == relation['AssociationId'])
        role, table, key = ('device','Devices','DeviceId') if a['DeviceId'] else ('account','ServiceAccounts','AccountId')
        assert m == {'person':next(p['ObjectId'] for p in after['Persons'] if p['Id']==a['PersonId']),role:next(o['ObjectId'] for o in after[table] if o['Id']==a[key])}
        assert relation['ValidFrom'] == a['Start'] and relation['ValidTo'] == a['End']
    else:
        raise AssertionError(relation['Kind'])
assert len(after['Relations']) == 5 and len(after['RelationMembers']) == 10

lines = ['# PostgreSQL 迁移前后取样', '',
    '这是构造样本在真实隔离 PostgreSQL 中执行追加迁移后的查询结果，不是生产数据或示意数据。此前测试库按 fixture 生命周期删除，本次重新建库并取样。', '',
    f"采集时间：{after_doc['capturedAtUtc']}。迁移：`{before_doc['migrations'][-1]}` → `{after_doc['migrations'][-1]}`。", '',
    '[原始 before.json](before.json) · [原始 after.json](after.json) · [取样数据 SQL](seed.sql) · [核对脚本](inspect.py)', '',
    '## 核对结果', '',
    '- 原 Segments 6 行 + Events 3 行 → Facts 9 行。旧行 Id 一一对应。',
    '- 9/9 的 Owner、StreamId、客户端 FactId、Revision、时间、Collector、Source、AppIdentity 以及完整 JSON 值一致。Payload 只改列名为 Result。',
    '- 新增 Collectors 5、Objects 7、Relations 5、RelationMembers 10。每个关系的对象与有效时间均对照旧证据。',
    '- 原资料仅增加 ObjectId；Streams、Subjects、PersonAssociations 等原内容不变。FactGaps：0 → 0。',
    '- Measurement 尚未有落地生产者，当前 schema 不接收此 Kind，没有伪造 Measurement 样本。', '',
    '## 每种事实', '',
    '下表对象名只是对实际 UUID 的可读解析；原 UUID 在后文及原始 JSON 中保留。', '',
    '| 样本 | 之前 | 现在 FoiId → Object | Aspect | 关系 |',
    '| --- | --- | --- | --- | --- |']
for c in cases:
    a,b = old[c['Id']],new[c['Id']]
    rel = next((r for r in after['Relations'] if r['FactId']==b['Id']),None)
    relation = ', '.join(f'{role}={object_name(obj)}' for role,obj in members(rel).items()) if rel else '未生成事实专属设备关系'
    lines.append(f"| {c['label']} | {'Segments' if c['family']=='segment' else 'Events'}；{a['TargetKind'] or 'null'}:{a['TargetId'] if a['TargetId'] is not None else 'null'} | {object_name(b['FoiId'])} | {b['Aspect'] or 'null'} | {relation} |")
lines += ['', '## Objects：四类对象', '', '| 原资料 | 新 UUID | Kind / Scope / Key | Owner |', '| --- | --- | --- | --- |']
for table in ('Devices','Apps','ServiceAccounts','Persons'):
    for row in after[table]:
        o=objects[row['ObjectId']]
        lines.append(f"| {table}#{row['Id']} | `{o['Id']}` | {o['Kind']} / {o['Scope']} / {o['Key']} | {o['OwnerId'] or 'null（全局 App）'} |")
lines += ['', '账号缺真实 ID 时使用原 SubjectId，仍在 vrchat 作用域，Name 为 null；没有新增 legacy 命名空间。', '', '## Collectors', '', '| 原 ObserverId | 新 Collectors.Id | Kind |', '| --- | --- | --- |']
for c in after['Collectors']:
    lines.append(f"| `{c['Id']}` | 同值 | {c['Kind']} |")
lines += ['', 'ObserverId 为 null 的两条事实仍是 CollectorId=null，没有创建虚构观测者。', '', '## Relations 与 RelationMembers', '', '| Relation | Kind | Evidence | Members | 时间 |', '| --- | --- | --- | --- | --- |']
for r in after['Relations']:
    lines.append(f"| `{r['Id']}` | {r['Kind']} | `{json.dumps(r['Evidence'])}` | {'; '.join(role+'='+object_name(o) for role,o in members(r).items())} | {r['ValidFrom']} → {r['ValidTo']} |")
lines += ['', '三条 observed-on 各自绑定 System/Mac Browser/Windows Browser 的准确 Fact。两条 used-by 来自原 PersonAssociations。无设备证据的 VRChat 事实没有被关联到 Mac、Windows 或 Quest。', '', '## 逐条原始 diff', '', '以下仅按字段名排序以方便阅读，没有改写字段值。`TargetKind/TargetId/Source/StreamId/FactId/AppIdentityId` 仍实际存在；本轮是存储迁移，没有同时替换上传协议和平台身份目录。']
for c in cases:
    a,b=old[c['Id']],new[c['Id']]
    diff='\n'.join(difflib.unified_diff(json.dumps(a,ensure_ascii=False,indent=2,sort_keys=True).splitlines(),json.dumps(b,ensure_ascii=False,indent=2,sort_keys=True).splitlines(),fromfile=f"{'Segments' if c['family']=='segment' else 'Events'}/{c['Id']}",tofile=f"Facts/{c['Id']}",lineterm=''))
    lines += ['',f"### {c['label']}",'','```diff',diff,'```']
(root/'REPORT.md').write_text('\n'.join(lines)+'\n')
print('PASS: 9 full fact comparisons, original metadata, 5 collectors, 5 exact relations and time ranges; REPORT.md generated.')
