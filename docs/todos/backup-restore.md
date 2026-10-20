# Backup and restore TODO

Status: implemented, with live backup and restore pending. Covers step 8 of the [ordered backlog](../pulumi-infrastructure-assessment.md#ordered-implementation-backlog-and-acceptance). Follow the [restore procedure](../operations.md#restore-a-complete-checkpoint).

## Backup configuration

- [ ] Use the agreed [RPO, RTO and cost ceiling](cost-recovery-objectives.md) to define backup frequency, retention and deletion rules.
- [x] Implement PostgreSQL custom dumps, MongoDB archives and OpenSearch native snapshots before an offline seven-volume OCI group checkpoint.
- [x] Require terminated old workers and detached disks; validate AVAILABLE group members by exact source OCID, size and compartment.
- [x] Record completion outside the cluster with credential generation, release, database versions, native artifacts, PostgreSQL metadata and volume mappings.
- [x] Reject partial sets and resolve ambiguous group creation by recorded operation identity before resume.
- [x] Implement native artifact retention after a verified cloud checkpoint and configurable complete-checkpoint retention. Preserve backups referenced by restored generations.
- [x] Add a daily seven-day OCI crash-consistent fallback policy and a separate protected external-runner backup/check workflow.
- [x] Implement independent restore to a new protected seven-disk generation with explicit extra-storage consent.
- [ ] Configure runner availability monitoring, environment approvals and failed/stale-backup notification recipients. Verify that they remain actionable while the managed cluster is unavailable.

## Independent restore acceptance

- [x] Author tests for partial/mismatched sets, ambiguous requests, freshness, ownership, retention boundaries and native restore inputs.
- [ ] Run tests and verify backup failure reporting and stale-backup detection.
- [ ] Restore a complete checkpoint into seven new volumes without access to the source volumes. Stay within the approved temporary-resource budget.
- [ ] Verify identifiable test data, matching credential identity, measured recovery time and achieved recovery point.
- [ ] Clean temporary resources and record costs and sanitized evidence before [full-stack and lifecycle acceptance](deployment-acceptance.md).

The [restore procedure](../operations.md#restore-a-complete-checkpoint) documents database version and corruption limits.
