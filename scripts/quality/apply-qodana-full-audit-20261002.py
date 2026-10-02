#!/usr/bin/env python3
"""Execute 41 enumerated corrections from the full 2,260-alert source audit.

The existing batch-2 applier is reused after its Git blob identity is verified.
This file contains approvals, not a classifier: new/unlisted alerts never qualify.
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
from pathlib import Path
import sys

SOURCE = '32d62663e9b0b7f944ca216be5b4a41cee603a34'
ANALYSIS = '9b78b24467a5a2dbc1ffd882bc4e049ff3f6aa78'
SUBJECT = 'ops: apply full Qodana audit 20261002 (28 dismissals, 13 reopenings)'
BASE_BLOB = '58228c37a72845d6c7945bf9b919880abb7a4709'
APP = 'src/Coglatas.Application/'
WEB = 'src/Coglatas.Web/'
HASHES = {
 APP+'Announcements/AnnouncementAttachmentService.cs': 'b07ae2e6413198ea771284981e99715805c7f8cd4d14438908321fe676c8f937',
 APP+'Common/Tenancy/TenancyOptions.cs': 'd4beae002602188a8162d21360256e7b1459495fe8ef5c3546f33834b10d1d7f',
 APP+'DependencyInjection.cs': 'abc55985730e40ad0ee676b44d059171976407d570e008b6af8bcba2f42530a9',
 APP+'Files/FileDtos.cs': '07edd6f572a4afac877e2fe50e10f3aa2318891283cb61366b29ee129bab40bc',
 APP+'Files/FileService.cs': '2882020cf79665499b638eaff4690166e1ad916d3572d8d27bac4a54fd3fef44',
 APP+'Projects/TaskCommandDtos.cs': '88ffa7231929764c33af8ca560278e5a43f265c5ab1018b6d990100d1574104d',
 APP+'Projects/TaskCommandService.cs': '502d53df12ed4cbb50715f4434774fa0badc2eb327e715ea7fc41a6239fb4b74',
 APP+'Projects/TaskSubresourceDtos.cs': '3ce411c16035a193e4aef84ef0bc2c1ef0f44e8500bea7aa8b4a997048fb126d',
 APP+'TenantAdministration/TenantAdministrationDtos.cs': '592e80a4063318750ddb996da3df93c0cea2198c7e75f15b7acc79683733218d',
 APP+'TenantExports/TenantExportDtos.cs': 'c8659d9905c8629d4a38d1f07482d2ad8be6183f8582f6036103150ca6305536',
 APP+'TenantExports/TenantExportService.cs': '0244bdada30b795a7a230d330359347e636eacf29c8410cf3fef27823449df39',
 WEB+'Audit/AuditPackageExportWorker.cs': 'b85fc38ba1c804fd3696027aab6dd5bbaa2a28d64127a2f71e4389879f1832af',
 WEB+'Controllers/FilesController.cs': 'b978a583f055e19b38da0b1fb528fd70587f8f7b679b9848cd382137f44c6565',
 WEB+'Controllers/ProjectsController.cs': '08859ac8455622e4779f897a0b74788743f9878cd6fb0b58d4cc81875a82230c',
 WEB+'Controllers/TenantExportController.cs': 'cc4c59e510516bdec07ff1141f4f5fe50935729faa067820b497aa38636b80b6',
 WEB+'Controllers/TenantsController.cs': '91e20cdaa0f70d8d79dd632f248d6151e929925c3faad3694e0d5f0a2f712ef2',
 WEB+'Extensions/DependencyInjection.cs': 'fa6acc5ac5b23be492cfd82a29fd5257a1124aa171e937b4412a737a390984ae',
 WEB+'Notifications/AnnouncementPublisherWorker.cs': 'ff28e8b744da0d671d53c23c8458118d4a799665a38602dbc4c49e13ad92efcd',
 WEB+'Notifications/TaskDeadlineDigestWorker.cs': '577a6d666e339ad18de42514e75d236e1e5716e360c3740d2e3cfa85bc6c14e9',
 WEB+'Program.cs': '846953e47a38a075b94c2eb867e2701b99383c7ea387caa7777dae2276802a65',
 WEB+'Realtime/AppHub.cs': '07c28fc58451ea72e5d4e2728de0260a47991f2d556ad89cad88581be32b2500',
 WEB+'Realtime/OutboxDispatcher.cs': 'f38882f139fa197f218edd84a604c483bd3d55cad5460333af03a99bf0a2549d',
 WEB+'Realtime/RealtimeOptions.cs': 'ccdd916f04caab70441595cd4719e566d0ca14afbc9e8332cabef9ef23e06acf',
 WEB+'Services/HttpTenantResolver.cs': '48cfd6948465440ef801f580a8e71094d347e6f838930b8926f95b6ff20395b3',
}

# Each row fixes alert ID, property, declaration line and a concrete runtime getter.
OPTIONS = [
 (APP+'Common/Tenancy/TenancyOptions.cs', 'Coglatas.Application.Common.Tenancy.TenancyOptions', WEB+'Extensions/DependencyInjection.cs:30', [
  (142, 'DevelopmentTenantHeaderName', 21, WEB+'Services/HttpTenantResolver.cs:68'),
  (143, 'TenantCookieName', 23, WEB+'Controllers/TenantsController.cs:41')]),
 (WEB+'Audit/AuditPackageExportWorker.cs', 'Coglatas.Web.Audit.AuditPackageExportWorkerOptions', WEB+'Extensions/DependencyInjection.cs:32', [
  (144, 'PollSeconds', 8, WEB+'Audit/AuditPackageExportWorker.cs:23'),
  (145, 'TenantBatchSize', 9, WEB+'Audit/AuditPackageExportWorker.cs:65'),
  (146, 'JobBatchSize', 10, WEB+'Audit/AuditPackageExportWorker.cs:82'),
  (147, 'StaleProcessingMinutes', 11, WEB+'Audit/AuditPackageExportWorker.cs:52')]),
 (WEB+'Notifications/AnnouncementPublisherWorker.cs', 'Coglatas.Web.Notifications.AnnouncementPublisherWorkerOptions', WEB+'Program.cs:72', [
  (148, 'PollSeconds', 9, WEB+'Notifications/AnnouncementPublisherWorker.cs:50'),
  (149, 'TenantPageSize', 10, WEB+'Notifications/AnnouncementPublisherWorker.cs:57'),
  (150, 'ClaimBatchSize', 11, WEB+'Notifications/AnnouncementPublisherWorker.cs:94'),
  (151, 'ClaimTimeoutSeconds', 12, WEB+'Notifications/AnnouncementPublisherWorker.cs:95'),
  (152, 'RetrySeconds', 13, WEB+'Notifications/AnnouncementPublisherWorker.cs:116')]),
 (WEB+'Notifications/TaskDeadlineDigestWorker.cs', 'Coglatas.Web.Notifications.TaskDeadlineDigestWorkerOptions', WEB+'Program.cs:71', [
  (153, 'PollSeconds', 9, WEB+'Notifications/TaskDeadlineDigestWorker.cs:53'),
  (154, 'CandidatePageSize', 13, WEB+'Notifications/TaskDeadlineDigestWorker.cs:20'),
  (155, 'RetrySeconds', 15, WEB+'Notifications/TaskDeadlineDigestWorker.cs:22')]),
 (WEB+'Realtime/RealtimeOptions.cs', 'Coglatas.Web.Realtime.RealtimeOptions', WEB+'Program.cs:70', [
  (156, 'SubscriptionLimitPerConnection', 5, WEB+'Realtime/AppHub.cs:90'),
  (157, 'SubscriptionAttemptsPerMinute', 6, WEB+'Realtime/AppHub.cs:52'),
  (158, 'DispatcherPollSeconds', 8, WEB+'Realtime/OutboxDispatcher.cs:39'),
  (159, 'ProcessingLockSeconds', 9, WEB+'Program.cs:342'),
  (160, 'InitialRetrySeconds', 10, WEB+'Realtime/OutboxDispatcher.cs:189'),
  (161, 'MaximumAutomaticAttempts', 11, WEB+'Realtime/OutboxDispatcher.cs:90'),
  (162, 'MaximumRetryMinutes', 12, WEB+'Realtime/OutboxDispatcher.cs:190')]),
]

REOPEN = [
 (APP+'Announcements/AnnouncementAttachmentService.cs', 'Coglatas.Application.Announcements.AnnouncementAttachmentResponse',
  [(780,'AttachmentId',14),(781,'ContentType',18),(782,'FileName',17),(783,'FileObjectId',15),(784,'SizeBytes',19),(785,'WorkspaceId',16)],
  'No production caller or HTTP/JSON consumer was found beyond this service and its DI registration. The previous Response-suffix dismissal was not justified; reopen for usage review, not as a confirmed runtime defect.',
  [APP+'Announcements/AnnouncementAttachmentService.cs:13-32',APP+'Announcements/AnnouncementAttachmentService.cs:104-114',APP+'DependencyInjection.cs:90']),
 (APP+'Files/FileDtos.cs', 'Coglatas.Application.Files.FileDownloadResponse', [(1098,'SizeBytes',25)],
  'This is a raw-file stream wrapper, not a serialized response DTO. Controllers read Content, ContentType and FileName but no SizeBytes read or serialization was found. Withdraw the Response-suffix rationale.',
  [APP+'Files/FileDtos.cs:25',APP+'Files/FileService.cs:357',WEB+'Controllers/FilesController.cs:76-94',WEB+'Controllers/FilesController.cs:138-150']),
 (APP+'TenantAdministration/TenantAdministrationDtos.cs', 'Coglatas.Application.TenantAdministration.TenantFeatureResponse', [(1776,'IsEnabled',56),(1777,'Key',56)],
  'The type only appears at its declaration. The real TenantFeaturesResponse uses strings, not TenantFeatureResponse. There is no demonstrated serialized consumer of this unused record.',
  [APP+'TenantAdministration/TenantAdministrationDtos.cs:56-58']),
 (APP+'TenantExports/TenantExportDtos.cs', 'Coglatas.Application.TenantExports.TenantExportFileResponse', [(1805,'CompletedAt',15),(1806,'CreatedAt',14),(1807,'Status',10),(1808,'TenantId',9)],
  'Export returns a ZIP stream, using ExportJobId as a header and Content/ContentType/FileName for File(...), not JSON of the wrapper metadata. The separately serialized TenantExportJobResponse does not establish usage of these properties.',
  [APP+'TenantExports/TenantExportDtos.cs:7-25',APP+'TenantExports/TenantExportService.cs:102-113',WEB+'Controllers/TenantExportController.cs:19-35']),
]


def build_manifest() -> dict:
    manifest = dict(schema_version=1, repository='NYGsatoshi/Coglatas', tool='QDNETC',
        category='qodana-dotnet-community', reviewed_source_sha=SOURCE, analysis_sha=ANALYSIS,
        apply_commit_subject=SUBJECT, source_sha256=HASHES, groups=[], individual=[], reopen_for_review=[],
        evidence_catalog={}, audit_scope={'previous_blanket_dismissals':1048,'previous_open':1212,
            'retained_serialization_dispositions':1035,'old_open_kept_for_review':1184,
            'source_bridge':'32d6266 changes only layout/CI/docs, with no C# changes from 9b78b244; protected file hashes are identical.'})
    for path, owner, binding, rows in OPTIONS:
        for number, member, line, read in rows:
            reason = f'{owner}.{member} is set by registered configuration binding and consumed at runtime. Removing its public setter would prevent configuration overrides of its initializer.'
            manifest['individual'].append(dict(number=number, rule='AutoPropertyCanBeMadeGetOnly.Global',
                path=path, line=line, symbol=owner+'.'+member, message='Auto-property can be made get-only',
                reason=reason, evidence=[f'{path}:{line}',binding,read]))
    for number, member, line in [(459,'CandidatePageSize',13),(460,'RetrySeconds',15)]:
        path=WEB+'Notifications/TaskDeadlineDigestWorker.cs'
        manifest['individual'].append(dict(number=number, rule='MemberCanBePrivate.Global', path=path,
            line=line, symbol='Coglatas.Web.Notifications.TaskDeadlineDigestWorkerOptions.'+member,
            message=f"Property '{member}' can be made private",
            reason=f'{member} must remain public for the registered TaskDeadlineDigest options binder; default binding does not bind private properties.',
            evidence=[f'{path}:{line}',WEB+'Program.cs:71',path+':17-22']))
    manifest['evidence_catalog']['task-summary'] = dict(
        reason='TaskCommandService constructs a non-null TaskSubresourceSummary inside CanonicalTaskResponse.Subresources; the task controller returns Ok(result.Value), so JSON serialization reads the nested public property.',
        evidence=[APP+'Projects/TaskCommandService.cs:1297-1298',APP+'Projects/TaskCommandDtos.cs:247-252',
                  WEB+'Controllers/ProjectsController.cs:99-100',WEB+'Controllers/ProjectsController.cs:374-376'])
    manifest['groups'].append(dict(rule='NotAccessedPositionalProperty.Global', path=APP+'Projects/TaskSubresourceDtos.cs',
        type='Coglatas.Application.Projects.TaskSubresourceSummary', evidence_key='task-summary',
        alerts=[(1651,'ChecklistCompletedCount',101),(1652,'ChecklistTotalCount',101),(1653,'CommentCount',101),(1654,'LabelCount',101),(1655,'SubtaskCount',101)]))
    for path, owner, rows, reason, evidence in REOPEN:
        for number, member, line in rows:
            symbol=owner+'.'+member
            manifest['reopen_for_review'].append(dict(number=number,rule='NotAccessedPositionalProperty.Global',
                path=path,line=line,symbol=symbol,
                message=f"Positional property '{symbol}' is never accessed (except in implicit Equals/ToString implementations)",
                reason=reason,evidence=evidence))
    return manifest


def load_engine():
    path=Path(__file__).with_name('apply-reviewed-qodana-alerts.py')
    data=path.read_bytes()
    actual=hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
    if actual != BASE_BLOB:
        raise ValueError('The existing reviewed applier changed; review it before continuing')
    spec=importlib.util.spec_from_file_location('reviewed_qodana_engine',path)
    module=importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> None:
    if len(sys.argv) != 3:
        raise ValueError('Usage: apply-qodana-full-audit-20261002.py SOURCE_ROOT EVIDENCE_DIRECTORY')
    source,out=map(Path,sys.argv[1:])
    out.mkdir(parents=True,exist_ok=True)
    manifest=build_manifest()
    engine=load_engine()
    decisions=engine.expand(manifest)
    if len(decisions)!=41 or sum(d['target']=='dismissed' for d in decisions)!=28:
        raise ValueError('Unexpected approval count')
    # The original engine checks rule/ref/category/revision/message/location,
    # all evidence hashes, previous bot/comment identity for reopens, and verifies
    # every PATCH with a separate GET. Add a main-ref guard before EVERY PATCH.
    allowed={d['number']:d['target'] for d in decisions}
    original_api=engine.Api
    class AuditedApi(original_api):
        def request(self,path,body=None):
            if body is not None:
                number=int(path.rsplit('/',1)[-1])
                if path!='/code-scanning/alerts/'+str(number) or allowed.get(number)!=body.get('state'):
                    raise ValueError('Unlisted alert write')
                if super().request('/git/ref/heads/main')['object']['sha']!=SOURCE:
                    raise ValueError('Main moved during apply; stop before the next write')
                body=dict(body)
                if 'dismissed_comment' in body:
                    body['dismissed_comment']=body['dismissed_comment'].replace(
                        'qodana-reviewed-alerts.json','review-manifest.json (run artifact)')[:280]
            return super().request(path,body)
    engine.Api=AuditedApi
    engine.NEW_PREFIX='Qodana individual review batch 3: '
    manifest_path=out/'approved-full-audit-manifest.json'
    manifest_path.write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    sys.argv=[str(Path(__file__)),str(manifest_path),str(source),str(out)]
    engine.main()


if __name__=='__main__':
    main()
