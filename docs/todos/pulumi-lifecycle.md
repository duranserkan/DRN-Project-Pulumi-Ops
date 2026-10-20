# Pulumi stacks and lifecycle CLI TODO

Status: lifecycle implemented. See [verification results](deployment-acceptance.md#local-verification) for remaining local and live checks. Covers step 5 of the [ordered backlog](../pulumi-infrastructure-assessment.md#ordered-implementation-backlog-and-acceptance).

## Projects and ownership

- [ ] Complete [OCI preflight](oci-preflight.md) and [release prerequisites](release-prerequisites.md) before provisioning. Complete [recovery design](credential-recovery.md) before workload activation.
- [x] Create a .NET Pulumi project with recovery, durable, cluster and bootstrap roles, explicit validation and SecurityToken/InstancePrincipal authentication.
- [ ] Verify Pulumi Cloud account access, encrypted state and independent key recovery. Keep state outside the disposable cluster.
- [x] Protect the recovery bucket and durable volume generations; preserve their Pulumi ownership through cluster deletion.
- [x] Define OKE Basic with a configurable worker pool in one AD, private native-VCN networking, Calico policy enforcement and restricted public API access. Default to one A1 worker.
- [x] Preserve deployed compute settings during pause and restore desired worker settings on resume. Keep volume profiles bound to their generations and read legacy profiles as defaults.
- [x] Bootstrap the pinned Argo CD release, restricted projects, root sync deny window and static CSI bindings without owning workloads.
- [ ] Verify routing, API access, registry egress, cloud permissions and resource ceilings in OCI.

## Lifecycle commands

- [x] Separate initial durable, cluster and bootstrap provisioning into commands that configure/apply only their own stack. Keep bootstrap sync gated and retain combined up/resume recovery workflows.
- [x] Accept optional empty Pulumi checkpoint fields and reject pending operations or malformed checkpoints before initialization or scoped durable/cluster changes.
- [x] Implement and document validation, key generation, setup, initialization, preview, activation, access, checks, backups, pause/resume, down, restore and explicit stopped-owner unlock.
- [x] Serialize commands in one checkout, select stacks explicitly, verify release tag/SHA and stop after subprocess failures.
- [x] Implement a non-expiring cross-host OCI lock, conditional state writes, ownership checks and persisted phases shared by CLI and executor.
- [x] Integrate [storage recovery](storage-recovery.md), [credential recovery](credential-recovery.md) and [GitOps integration](gitops-integration.md) in the documented order.
- [x] Gate teardown on complete backup evidence; suspend all child reconciliation, stop writers/data services and verify detachment before removal.
- [x] Handle exact claim identities, PostgreSQL metadata, partial claim deletion and non-cascading Argo roots; retain cloud disks outside bootstrap ownership.
- [x] Keep purge unavailable and require explicit staging/backup/additional-storage confirmation for independent restore.
- [x] Add guarded release selection without regenerating credentials, retain each backup's release, and require matching release metadata during restore. Keep startup readiness separate from deliberately frozen reconciliation.
- [ ] Validate scale-to-zero, boot-volume cleanup, maintenance policy and upgrade behavior. Stop on unplanned resource growth or failed recovery evidence.

## Acceptance

- [x] Author .NET unit tests and shared CI with coverage, dependency auditing, Trivy and CodeQL.
- [x] Run the earlier baseline Release build and unit tests locally. See the [verification record](deployment-acceptance.md#local-verification).
- [x] Run operator configuration, scoped command and checkpoint regression tests in Debug.
- [ ] Build the current full Release solution and run the current Pulumi suite.
- [ ] Run the first CI pipeline. Configure required checks and code-scanning merge protection.
- [ ] Verify fresh initialization, staged provisioning, repeat operations and retained-data pause/resume/down in OCI.
- [ ] Pass storage replacement and credential-recovery drills before relying on the lifecycle operationally.
- [ ] Demonstrate idempotent and interrupted command handling through [deployment acceptance](deployment-acceptance.md). Record resource and cost evidence without secrets.
