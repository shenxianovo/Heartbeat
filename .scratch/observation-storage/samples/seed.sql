-- Synthetic inspection data. Run ONLY in an isolated DB at CompleteHistoricalTargets.
INSERT INTO "Users" ("Id","Username","LastSeenAt","IsPublic") VALUES ('sample-owner','sample-user','2026-09-11T00:00:00Z',false);
INSERT INTO "Devices" ("Id","OwnerId","HardwareId","DeviceName") VALUES
 (101,'sample-owner','sample-mac-1','Mac1'),(102,'sample-owner','sample-windows-1','Windows1');
INSERT INTO "Apps" ("Id","Key","DisplayName","IsProvisional") VALUES (201,'chrome','Chrome',false),(202,'vrchat','VRChat',false);
INSERT INTO "AppIdentities" ("Id","Key","AppId") VALUES (211,'mac:com.google.chrome',201),(212,'win:chrome',201);
INSERT INTO "ApplicationContexts" ("Id","OwnerId","DeviceId","AppId") VALUES (221,'sample-owner',101,201),(222,'sample-owner',102,201);
INSERT INTO "ServiceProducts" ("ServiceKey","AppId") VALUES ('vrchat',202);


INSERT INTO "ServiceAccounts" ("Id","OwnerId","ServiceKey","ServiceAccountId","LegacySubjectId") VALUES
 (301,'sample-owner','vrchat','usr_11111111-1111-4111-8111-111111111111',NULL),(302,'sample-owner','vrchat',NULL,'00000000-0000-0000-0000-00000000012e');
INSERT INTO "Persons" ("Id","OwnerId","Reference") VALUES (401,'sample-owner','00000000-0000-0000-0000-000000000191');
INSERT INTO "PersonAssociations" ("Id","OwnerId","PersonId","DeviceId","AccountId","Start","End") VALUES
 (501,'sample-owner',401,101,NULL,'2026-09-11T01:00:00Z','2026-09-11T02:00:00Z'),
 (502,'sample-owner',401,NULL,301,'2026-09-11T01:00:00Z','2026-09-11T02:00:00Z');


INSERT INTO "Subjects" ("OwnerId","SubjectId","Kind","DeviceId") VALUES ('sample-owner','00000000-0000-0000-0000-000000000fa1','machine',101);
INSERT INTO "Streams" ("OwnerId","StreamId","SubjectId","CollectorInstanceId","OutputId","Source","FactKind","Dimensions","Origin")
 VALUES ('sample-owner','00000000-0000-0000-0000-000000000bb9','00000000-0000-0000-0000-000000000fa1','00000000-0000-0000-0000-000000000259','sample','system','segment','{}'::jsonb,'native');

INSERT INTO "Segments" ("Id","OwnerId","StreamId","FactId","Revision","ObserverId","Source","TargetKind","TargetId","AppIdentityId","Payload","StartTime","EndTime")
 VALUES ('00000000-0000-0000-0000-0000000003e9','sample-owner','00000000-0000-0000-0000-000000000bb9','00000000-0000-0000-0000-0000000007d1',3,'00000000-0000-0000-0000-000000000259','system','device',101,211,'{"activityKey": "chrome", "title": "Heartbeat — Chrome", "appIdentityKey": "mac:com.google.chrome", "extra": {"keep": [1, 2]}}'::jsonb,'2026-09-11T01:00:00Z','2026-09-11T01:10:00Z');

INSERT INTO "Subjects" ("OwnerId","SubjectId","Kind","DeviceId") VALUES ('sample-owner','00000000-0000-0000-0000-000000000fa2','machine',101);
INSERT INTO "Streams" ("OwnerId","StreamId","SubjectId","CollectorInstanceId","OutputId","Source","FactKind","Dimensions","Origin")
 VALUES ('sample-owner','00000000-0000-0000-0000-000000000bba','00000000-0000-0000-0000-000000000fa2','00000000-0000-0000-0000-000000000259','sample','system','event','{}'::jsonb,'native');

INSERT INTO "Events" ("Id","OwnerId","StreamId","FactId","Revision","ObserverId","Source","TargetKind","TargetId","AppIdentityId","Payload","Timestamp")
 VALUES ('00000000-0000-0000-0000-0000000003ea','sample-owner','00000000-0000-0000-0000-000000000bba','00000000-0000-0000-0000-0000000007d2',3,'00000000-0000-0000-0000-000000000259','system','device',101,NULL,'{"eventType": "keyDown", "codeSet": "windows-vk-v1", "code": 65, "extra": "keep"}'::jsonb,'2026-09-11T01:05:00Z');

INSERT INTO "Subjects" ("OwnerId","SubjectId","Kind","DeviceId") VALUES ('sample-owner','00000000-0000-0000-0000-000000000fa3','machine',101);
INSERT INTO "Streams" ("OwnerId","StreamId","SubjectId","CollectorInstanceId","OutputId","Source","FactKind","Dimensions","Origin")
 VALUES ('sample-owner','00000000-0000-0000-0000-000000000bbb','00000000-0000-0000-0000-000000000fa3','00000000-0000-0000-0000-00000000025a','sample','browser','segment','{}'::jsonb,'native');

INSERT INTO "Segments" ("Id","OwnerId","StreamId","FactId","Revision","ObserverId","Source","TargetKind","TargetId","AppIdentityId","Payload","StartTime","EndTime")
 VALUES ('00000000-0000-0000-0000-0000000003eb','sample-owner','00000000-0000-0000-0000-000000000bbb','00000000-0000-0000-0000-0000000007d3',3,'00000000-0000-0000-0000-00000000025a','browser','application-context',221,211,'{"activityKey": "https://example.com/docs", "title": "Docs", "attributes": {"url": "https://example.com/docs", "windowId": 7}}'::jsonb,'2026-09-11T01:00:00Z','2026-09-11T01:10:00Z');

INSERT INTO "Subjects" ("OwnerId","SubjectId","Kind","DeviceId") VALUES ('sample-owner','00000000-0000-0000-0000-000000000fa4','machine',102);
INSERT INTO "Streams" ("OwnerId","StreamId","SubjectId","CollectorInstanceId","OutputId","Source","FactKind","Dimensions","Origin")
 VALUES ('sample-owner','00000000-0000-0000-0000-000000000bbc','00000000-0000-0000-0000-000000000fa4','00000000-0000-0000-0000-00000000025b','sample','browser','segment','{}'::jsonb,'native');

INSERT INTO "Segments" ("Id","OwnerId","StreamId","FactId","Revision","ObserverId","Source","TargetKind","TargetId","AppIdentityId","Payload","StartTime","EndTime")
 VALUES ('00000000-0000-0000-0000-0000000003ec','sample-owner','00000000-0000-0000-0000-000000000bbc','00000000-0000-0000-0000-0000000007d4',3,'00000000-0000-0000-0000-00000000025b','browser','application-context',222,212,'{"activityKey": "https://example.com/work", "title": "Work", "attributes": {"url": "https://example.com/work", "windowId": 9}}'::jsonb,'2026-09-11T01:00:00Z','2026-09-11T01:10:00Z');

INSERT INTO "Subjects" ("OwnerId","SubjectId","Kind","DeviceId") VALUES ('sample-owner','00000000-0000-0000-0000-000000000fa5','account',NULL);
INSERT INTO "Streams" ("OwnerId","StreamId","SubjectId","CollectorInstanceId","OutputId","Source","FactKind","Dimensions","Origin")
 VALUES ('sample-owner','00000000-0000-0000-0000-000000000bbd','00000000-0000-0000-0000-000000000fa5','00000000-0000-0000-0000-00000000025c','sample','vrchat.account','segment','{}'::jsonb,'native');

INSERT INTO "Segments" ("Id","OwnerId","StreamId","FactId","Revision","ObserverId","Source","TargetKind","TargetId","AppIdentityId","Payload","StartTime","EndTime")
 VALUES ('00000000-0000-0000-0000-0000000003ed','sample-owner','00000000-0000-0000-0000-000000000bbd','00000000-0000-0000-0000-0000000007d5',3,'00000000-0000-0000-0000-00000000025c','vrchat.account','account',301,NULL,'{"activityKey": "world|instance", "title": "World", "worldId": "world", "instanceId": "instance"}'::jsonb,'2026-09-11T01:00:00Z','2026-09-11T01:10:00Z');

INSERT INTO "Subjects" ("OwnerId","SubjectId","Kind","DeviceId") VALUES ('sample-owner','00000000-0000-0000-0000-000000000fa6','account',NULL);
INSERT INTO "Streams" ("OwnerId","StreamId","SubjectId","CollectorInstanceId","OutputId","Source","FactKind","Dimensions","Origin")
 VALUES ('sample-owner','00000000-0000-0000-0000-000000000bbe','00000000-0000-0000-0000-000000000fa6',NULL,'sample','vrchat.account','segment','{}'::jsonb,'native');

INSERT INTO "Segments" ("Id","OwnerId","StreamId","FactId","Revision","ObserverId","Source","TargetKind","TargetId","AppIdentityId","Payload","StartTime","EndTime")
 VALUES ('00000000-0000-0000-0000-0000000003ee','sample-owner','00000000-0000-0000-0000-000000000bbe','00000000-0000-0000-0000-0000000007d6',3,NULL,'vrchat.account','account',302,NULL,'{"activityKey": "world-old|instance-old", "worldId": "world-old", "extra": {"kept": true}}'::jsonb,'2026-09-11T01:00:00Z','2026-09-11T01:10:00Z');

INSERT INTO "Subjects" ("OwnerId","SubjectId","Kind","DeviceId") VALUES ('sample-owner','00000000-0000-0000-0000-000000000fa7','person',NULL);
INSERT INTO "Streams" ("OwnerId","StreamId","SubjectId","CollectorInstanceId","OutputId","Source","FactKind","Dimensions","Origin")
 VALUES ('sample-owner','00000000-0000-0000-0000-000000000bbf','00000000-0000-0000-0000-000000000fa7','00000000-0000-0000-0000-00000000025d','sample','journal','event','{}'::jsonb,'native');

INSERT INTO "Events" ("Id","OwnerId","StreamId","FactId","Revision","ObserverId","Source","TargetKind","TargetId","AppIdentityId","Payload","Timestamp")
 VALUES ('00000000-0000-0000-0000-0000000003ef','sample-owner','00000000-0000-0000-0000-000000000bbf','00000000-0000-0000-0000-0000000007d7',3,'00000000-0000-0000-0000-00000000025d','journal','person',401,NULL,'{"entry": "Went for a walk", "tags": ["outside"], "extra": 17}'::jsonb,'2026-09-11T01:05:00Z');

INSERT INTO "Subjects" ("OwnerId","SubjectId","Kind","DeviceId") VALUES ('sample-owner','00000000-0000-0000-0000-000000000fa8','person',NULL);
INSERT INTO "Streams" ("OwnerId","StreamId","SubjectId","CollectorInstanceId","OutputId","Source","FactKind","Dimensions","Origin")
 VALUES ('sample-owner','00000000-0000-0000-0000-000000000bc0','00000000-0000-0000-0000-000000000fa8',NULL,'sample','custom','event','{}'::jsonb,'native');

INSERT INTO "Events" ("Id","OwnerId","StreamId","FactId","Revision","ObserverId","Source","TargetKind","TargetId","AppIdentityId","Payload","Timestamp")
 VALUES ('00000000-0000-0000-0000-0000000003f0','sample-owner','00000000-0000-0000-0000-000000000bc0','00000000-0000-0000-0000-0000000007d8',3,NULL,'custom',NULL,NULL,NULL,'{"opaque": {"a": [1, true, null]}}'::jsonb,'2026-09-11T01:05:00Z');

INSERT INTO "Subjects" ("OwnerId","SubjectId","Kind","DeviceId") VALUES ('sample-owner','00000000-0000-0000-0000-000000000fa9','machine',101);
INSERT INTO "Streams" ("OwnerId","StreamId","SubjectId","CollectorInstanceId","OutputId","Source","FactKind","Dimensions","Origin")
 VALUES ('sample-owner','00000000-0000-0000-0000-000000000bc1','00000000-0000-0000-0000-000000000fa9','00000000-0000-0000-0000-000000000259','sample','system','segment','{}'::jsonb,'native');

INSERT INTO "Segments" ("Id","OwnerId","StreamId","FactId","Revision","ObserverId","Source","TargetKind","TargetId","AppIdentityId","Payload","StartTime","EndTime")
 VALUES ('00000000-0000-0000-0000-0000000003f1','sample-owner','00000000-0000-0000-0000-000000000bc1','00000000-0000-0000-0000-0000000007d9',3,'00000000-0000-0000-0000-000000000259','system','device',101,NULL,'{"activityKey": "away", "title": "Away"}'::jsonb,'2026-09-11T01:00:00Z','2026-09-11T01:10:00Z');
