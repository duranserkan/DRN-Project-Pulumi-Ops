# Storage recovery TODO

Status: storage lifecycle implemented. Live recovery checks are pending. Covers step 6 of the [ordered backlog](../pulumi-infrastructure-assessment.md#ordered-implementation-backlog-and-acceptance). Follow the [storage recovery requirements](../pulumi-infrastructure-assessment.md#storage-recovery).

## Initialization and bindings

- [x] Create seven separate protected OCI volumes with configurable sizes and performance tiers, defaulting to 50 GB Balanced, with explicit cost acknowledgement.
- [x] Prepare static CSI PVs with `Retain`, unique volume handles and reservations for all seven namespace/claim pairs.
- [x] Use the explicit `drn-retained` non-provisioning StorageClass and propagate it through the OCI GitOps profile, preventing dynamic fallback.
- [x] Group each protected storage generation and assign the daily crash-consistent fallback policy only to the active group.
- [x] Persist encrypted credential and deployment identities in a separate protected versioned bucket before starting consumers.
- [ ] Verify actual device capacity, filesystem ownership, CSI encryption/attachment behavior and cross-compartment authorization.
- [ ] Verify delayed binding with consumers and exact volume-to-claim identity before workload acceptance.
- [x] Save PostgreSQL recovery metadata and verify exact volume/claim reservations before reactivation; never accept an unrelated surviving claim.
- [ ] Rehearse new-cluster claim identity and PostgreSQL metadata restoration against retained volumes.

## CSI and lifecycle recovery

- [ ] Verify the managed CSI driver recovers attachments after planned and unexpected worker replacement with the workstation offline.
- [ ] Verify old-writer fencing and confirmed detach before replacement use. Do not force attachment while the previous writer may still run.
- [x] Coordinate credentials, writer shutdown, native exports, zero workers, confirmed detach and complete group backups under the [shared lifecycle lock](pulumi-lifecycle.md).
- [x] Restore into seven new disks at the source sizes and tiers. Preserve source volumes and require additional-storage consent. Limit storage to three generations.
- [ ] Rehearse interrupted operations and owner-confirmed unlock from a second machine, using permissions from [account separation](account-separation.md).
- [ ] Stop on unverifiable ownership or volume identity. Confirm replacement respects the reviewed compute and boot-storage budget.

## Acceptance

- [ ] Include a changed node name in replacement testing.
- [ ] Test interrupted attachment, attempted duplicate ownership, wrong volumes and missing mounts. Verify there is no reformatting or duplicate writer.
- [ ] Test pause/resume, down/recreate, independent clone restore and OCI upgrades before relying on retained-data lifecycle. Record downtime, restored data/credential identity and allowance impact for [deployment acceptance](deployment-acceptance.md).
