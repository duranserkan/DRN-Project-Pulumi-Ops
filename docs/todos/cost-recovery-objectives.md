# Cost and recovery objectives TODO

Status: baseline selected. Budgets and recovery objectives are pending. Covers step 1 of the [ordered backlog](../pulumi-infrastructure-assessment.md#ordered-implementation-backlog-and-acceptance).

- [x] Select seven native CSI volumes instead of custom host LVM, with a 414 GB total baseline and explicit `allowBillableStorage` acknowledgement.
- [x] Limit storage to three protected generations. Require consent for each restore, which adds 350 GB with defaults.
- [x] Define checkpoint and fallback retention in the [cost baseline](../baseline-and-cost.md).
- [ ] Record the agreed workload, observation period and required capacity headroom for the full staging stack.
- [ ] Record the recovery point objective (RPO) and recovery time objective (RTO), including acceptable single-worker downtime.
- [ ] Record the billable-capacity ceiling and how proposed changes receive a separate cost decision. Keep logging in scope.
- [ ] Budget executor hosting, state, keys, backups, restore tests and networking alongside compute and disks. Do not rely on trial credits.
- [ ] Define a bounded temporary-resource budget for independent restore testing and the cleanup evidence required afterward.
- [ ] Supply these decisions to [OCI preflight](oci-preflight.md), [backup planning](backup-restore.md) and [deployment acceptance](deployment-acceptance.md). Keep actual billing details in private configuration.
