# Deployment acceptance TODO

Status: full Release verification, CI and live acceptance pending. Covers steps 9 and 10 of the [ordered backlog](../pulumi-infrastructure-assessment.md#ordered-implementation-backlog-and-acceptance). Use the agreed [workload and recovery objectives](cost-recovery-objectives.md).

## Local verification

Recorded results on macOS Arm64:

| Date | Build | Tests |
|---|---|---|
| 19 October 2026 | Release solution: zero errors, 71 analyzer warnings. Restore with NuGet auditing passed | Pulumi 84/84 and operator 143/143 passed, with no skips |
| 20 October 2026 | Operator Debug project and source dependencies: zero warnings or errors | Operator 308/308 passed, with no skips. Two runs took 13.454 and 14.404 seconds |

These runs predate the current warnings-as-errors policy. The operator tests required host permissions for macOS cryptography and temporary files. Full Release verification, CI coverage, security scans and live acceptance remain pending:

```sh
dotnet restore Drn.Pulumi.slnx --configfile NuGet.Config -p:Configuration=Release
dotnet build Drn.Pulumi.slnx --no-restore --configuration Release --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true
dotnet run --project tests/Drn.Pulumi.UnitTests --no-build --configuration Release
dotnet run --project tests/Drn.Infrastructure.Operator.UnitTests --no-build --configuration Release
```

Lifecycle tests run the operator and encrypted state store with simulated cloud, Kubernetes and database responses. They do not verify live OCI APIs, CSI attachments, database recovery, workload compatibility or capacity.

## Prerequisites

- [x] Run the earlier baseline Release build and .NET tests. Evidence is recorded above.
- [x] Build and run current operator configuration, scoped provisioning and checkpoint regression tests in Debug.
- [ ] Build the current full Release solution and run the current Pulumi suite.
- [ ] Run the first shared CI pipeline, including coverage and security scans.
- [ ] Confirm [preflight](oci-preflight.md), [release prerequisites](release-prerequisites.md), [credential recovery](credential-recovery.md) and [GitOps integration](gitops-integration.md) pass their gates.
- [ ] Confirm [storage recovery](storage-recovery.md) and [independent backup restore](backup-restore.md) pass their tests. Reference their evidence rather than duplicating their checklists.

## Full-stack capacity

- [ ] Verify native execution for the selected CPU architecture across workloads and bootstrap dependencies.
- [ ] Measure actual worker allocatable resources, cold-start peaks, steady-state load, database maintenance and backup activity over the agreed observation period.
- [ ] Account for operators, proxies, policy agents, monitoring, system pods, pod slots and boot-disk image-cache pressure.
- [ ] Verify the private native pod subnet, VNIC allocation and configured pod density on the actual shape. Defaults are A1 with 93 pods per node. A configured limit does not prove usable capacity.
- [ ] Verify required headroom and absence of OOM, eviction and disk pressure under the agreed workload. Apply the cost policy if capacity is insufficient.
- [ ] Verify Sample, Nexus, database TLS, logging ingestion/search and monitoring work without manual setup.

## Lifecycle

- [ ] Verify key generation, independent key retrieval, four-stack setup and encrypted initialization readback before provisioning workloads.
- [ ] Verify initialization from untouched Pulumi Cloud stacks and refusal of interrupted stack creation, including pending bootstrap operations before any resource is registered.
- [ ] Verify full `up`, staged sync, policy probes and first complete checkpoint, including its temporary worker shutdown and automatic restart.
- [ ] Verify a repeated start on a running deployment is refused and `check` succeeds without credential rotation.
- [ ] Write identifiable application/database data and verify pause/resume preserves data and credentials.
- [ ] Verify release A to B, interrupted-start correction, and rollback using A's retained checkpoint. Confirm credentials remain stable and each running release obtains its own baseline checkpoint.
- [ ] Verify planned and unexpected worker replacement and OCI upgrades preserve identity and data, using the storage test evidence.
- [ ] Run down and recreate the cluster. Verify data, credentials, bindings, CNI/policy enforcement and application readiness.
- [ ] Restore a selected complete checkpoint into new volumes. Prove source volumes remain unchanged and native database exports recover the expected data. Record the source profile's additional capacity, 350 GB with defaults, and cleanup decision.
- [ ] Validate a multi-worker deployment, per-node CSI/host checks, cross-node policy enforcement, pause after edited compute settings, and custom-size/tier restore. Check root filesystem capacity when increasing boot disk size.
- [ ] Interrupt lifecycle phases and verify [CLI](pulumi-lifecycle.md) retries resume from verified records or stop safely.
- [ ] Verify missing keys or Secrets and wrong disks cannot create empty replacement databases.
- [ ] Verify scheduled backup/readiness jobs use the external protected runner, report stale checkpoints and never start a deliberately paused or removed deployment.
- [ ] Inventory resources and costs after each scenario. Confirm no unintended disks, load balancers or charges remain.
- [ ] Record the tested release, dates, outcomes, recovery durations and remaining blockers. Mark the parent roadmap complete only when all required gates pass.
