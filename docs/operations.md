# Operations and recovery

[Project overview](../README.md) | [Setup](setup.md) | [Deployment](deployment.md) | [Acceptance checklist](todos/deployment-acceptance.md)

These procedures cover running, paused and removed staging deployments.

[Access](#connect-to-the-cluster) | [Validation](#validate-the-deployment) | [Recovery](#operate-and-recover) | [Troubleshooting](#troubleshooting)

## Connect to the cluster

After a cluster exists, refresh its dedicated kubeconfig from this repository's root. The operator reads the cluster OCID from the selected Pulumi stack and uses OCI CLI token authentication. It does not merge environments into your default kubeconfig. [OKE cluster access](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengdownloadkubeconfigfile.htm)

For the [DRN-STAGING session profile](setup.md#interactive-authentication-and-discovery), keep these settings in the operator terminal so the kubeconfig's OCI token command uses the same identity:

```sh
dotnet run --project src/Drn.Infrastructure.Operator -- kubeconfig .local/staging.json
export OCI_CLI_PROFILE=DRN-STAGING
export OCI_CLI_AUTH=security_token
export KUBECONFIG="$PWD/.local/staging.kubeconfig"
kubectl config current-context
kubectl get nodes -o wide
kubectl get storageclass
```

Use your configured `ociProfile` if it differs from `DRN-STAGING`. Check that the context is staging before every operation. Keep the OCI CLI on `PATH` and renew the session when required. Do not share kubeconfig or credentials with other users.

Once Argo CD and logging are healthy, run each tunnel in a separate terminal:

```sh
kubectl -n argocd port-forward --address localhost svc/argocd-server 8080:443
kubectl -n graylog port-forward --address localhost svc/drn-graylog 9000:9000
```

Use [Argo CD](https://localhost:8080) and [Graylog](http://localhost:9000) with their current application credentials. Tunnels provide network access, not application authentication. See the [GitOps local access guide](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/docs/local-access.md) for certificate handling, Linkerd Viz, Grafana and database clients. Keep database hostname verification enabled.

## Validate the deployment

Use these read-only diagnostics after provisioning and GitOps sync:

```sh
kubectl get nodes -o wide
kubectl get pods -A
kubectl get applications -n argocd
kubectl get pv
kubectl get pvc -A
kubectl get services -A
kubectl get events -A --sort-by=.metadata.creationTimestamp
```

The operator's `check` verifies backups, credentials, storage bindings, policy and database readiness. Check Argo CD Application sync status and Sample/Nexus requests separately. Repository pushes trigger CI only. Use lifecycle commands to change infrastructure or promote a GitOps release.

Complete the [deployment acceptance checklist](todos/deployment-acceptance.md) and GitOps [readiness checks](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/docs/platform-operations.md#readiness-checks). Record workload size, observation duration and actual allocatable resources. The default four A1 cores and 24 GB RAM have not passed live capacity checks.

## Operate and recover

`backup` takes a complete checkpoint and restarts the deployment. `pause` takes a checkpoint and leaves workers stopped. `down` also removes disposable infrastructure. Use `resume` to restart from recorded state. See the [command table](deployment.md#operator-commands) for prerequisites.

Complete checkpoints contain PostgreSQL and MongoDB exports, an OpenSearch snapshot and an offline OCI backup of all seven volumes. The encrypted record binds them to credentials, initialization markers, release, database versions and source volumes. Daily OCI snapshots provide a separate crash-consistent fallback. They do not replace complete checkpoints.

Health checks, release selection, restore and backup pruning verify cloud backup member IDs against saved records. A mismatch stops the operation.

Backup and teardown stop writers, export databases, stop workers and verify volume detachment before completing the cloud backup. This causes downtime with any worker count. Agree on [recovery objectives](todos/cost-recovery-objectives.md) before accepting the schedule.

Recover with the original encrypted credentials and initialization markers. Never reinitialize retained data to replace a lost checkpoint. See the [recovery design](pulumi-infrastructure-assessment.md#storage-recovery).

Use `down` to remove disposable infrastructure while preserving Pulumi protection and Kubernetes `Retain` for data volumes. Direct `pulumi destroy` bypasses these lifecycle checks. [Pulumi deletion protection](https://www.pulumi.com/docs/iac/operations/stack-management/protecting-against-undesired-changes/)

Schedule security updates, certificate/credential renewal and maintenance from the start. Test control-plane/node upgrades and planned or unexpected worker replacement. One worker means downtime. Validate scale-to-zero and boot-volume cleanup before relying on pause/resume for cost savings. Oracle may reclaim idle Always Free compute, so recovery must not depend on permanent worker availability. [Idle resource rules](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm)

### Change the GitOps release

Pause or down using the current configuration. Review the new release for compatibility with the retained database data, then update `gitOpsTag` and `gitOpsCommit` in the private configuration:

```sh
dotnet run --project src/Drn.Infrastructure.Operator -- release .local/staging.json --confirm staging
dotnet run --project src/Drn.Infrastructure.Operator -- resume .local/staging.json
```

`release` verifies the target checkout and the active generation's newest complete checkpoint. It changes the selected release without changing credentials or existing backup records. Resume creates a checkpoint if the selected release has none on that generation. Running health checks require a fresh checkpoint for the selected release.

After a failed startup, stop the previous process and follow the unlock procedure below. In the `starting` phase, `release` freezes reconciliation and stops existing writers before selecting a compatible release. Pending backups, restores and teardown block release changes. Selecting an older release does not roll back database contents.

For older database contents, use `status` to select a retained checkpoint. While paused or down, select its reviewed GitOps tag/SHA with `release`, then run `restore`. Restore restarts the deployment automatically. Selecting a release does not exempt its historical checkpoints from retention.

### Restore a complete checkpoint

Use `status` to select a checkpoint ID. First `pause` or `down`. If its GitOps commit differs from the selected release, select the checkpoint's original reviewed tag/SHA with `release` before restoring. Then:

```sh
dotnet run --project src/Drn.Infrastructure.Operator -- restore .local/staging.json \
  --backup '<checkpoint-id>' --confirm staging --allow-additional-storage
```

Restore creates seven new disks at the source generation's recorded sizes and tiers. It recreates the cluster, restores identities and PostgreSQL metadata, and verifies database recovery before starting writers. It then takes a new checkpoint and leaves the deployment running.

Source disks remain protected. Each restore adds 350 GB with default sizes. At most three generations are supported, with no automatic purge. Review storage retirement before reaching that limit.

Native recovery requires the checkpoint's pinned database/search versions and a Data Node clone that can start from its offline files. If those files cannot start the engine, use a separate emergency recovery procedure. Keep the recovery key and Pulumi account accessible without the original workstation.

### Change worker configuration

Pause or down first. Update worker count, shape, OCPUs, RAM, boot size, pod density or Fault Domains in private configuration. Preview, then resume. Workers and data stay in one AD. Pause uses deployed compute settings even if configuration has changed. Resume validates and applies the new settings. A CPU architecture change also needs compatible GitOps images.

Storage sizes and tiers are fixed at initialization. Restore inherits the source profile. Schema 1 records remain readable, but the next state write uses schema 2, which older operators reject. Update all runners and retain matching operator source for recovery. Do not downgrade by editing state. Boot filesystem growth and retained-data migration need separate procedures.

### Interrupted operations

`status` reports the phase, checkpoints and lock owner. The lock does not expire. Failed operations retain it if resources may have changed. Read-only failures release it.

Stop the previous local process or runner job, inspect its cloud/Pulumi activity, then use the exact owner ID:

```sh
dotnet run --project src/Drn.Infrastructure.Operator -- unlock .local/staging.json \
  --owner '<operation-id>' --confirm staging --previous-process-stopped
dotnet run --project src/Drn.Infrastructure.Operator -- resume .local/staging.json
```

Never unlock a running process. An OCI request already in flight can continue after a forced takeover. Resume resolves pending backups and partial teardown before reattaching data. Missing or mismatched identities stop recovery. Inspect incomplete exports and orphan backups before manual cleanup.

### Scheduled operations

[The operations workflow](../.github/workflows/operations.yml) runs complete backups daily and checks health every six hours, with default-branch restriction and non-cancelling concurrency. Provision a trusted Linux self-hosted runner labelled `drn-staging` outside the managed cluster. Configure the protected GitHub `staging` environment, its branch policy and appropriate reviewers. Scheduled jobs also need timely environment approval if reviewers are required.

Install the reviewed OCI CLI, Pulumi and kubectl. Set `DRN_INFRA_CONFIGURATION` to an absolute private JSON path outside the checkout. Use `ociAuth: InstancePrincipal` with scoped IAM, Pulumi credentials, the recovery key and an approved API source address.

Restrict the runner group to this workflow on the protected default branch. A runner label alone does not restrict access. Use the local CLI if the runner cannot be isolated. Keep pull-request source and Argo CD credentials separate from its cloud authority. Configure failure notifications and independent runner availability monitoring.

Scheduled `backup` leaves paused/down deployments offline. Their checkpoints do not expire by age. Running deployments use `maximumBackupAgeHours`, which defaults to 26.

Retention defaults to seven complete checkpoints and preserves any referenced by restored generations. Native artifacts are pruned after a newer cloud checkpoint is verified. The separate OCI fallback policy retains seven days. Verify both schedules and failure notifications during acceptance.

## Troubleshooting

| Symptom | Check first / safe next step |
|---|---|
| Operator cannot find `Pulumi.yaml` | Run `dotnet run --project src/Drn.Infrastructure.Operator -- ...` from this repository's root. |
| Pulumi login or stack access fails | Run `pulumi whoami --verbose`. Confirm the hosted backend, account/organization access and the JSON `organization`. Run `setup` for a new deployment, using its intended account. |
| Configuration validation fails | Replace example placeholders, use separate compartments and review costs before setting `allowBillableStorage: true`. |
| Initialization or scoped provisioning reports an incomplete checkpoint | Inspect the selected stack's resources and pending operations. Resolve the interrupted Pulumi operation before retrying. Do not erase checkpoint fields or reinitialize retained infrastructure. |
| OCI unauthorized or expired token | Validate the chosen profile, session, region and IAM. Check Pulumi uses `SecurityToken`, not its default `ApiKey`. |
| kubectl unauthorized / forbidden | Check kubeconfig token profile/auth and session expiry, then cloud permissions and Kubernetes RBAC separately. |
| API timeout | Check endpoint selection, private connectivity, approved client CIDRs, routes and security rules. |
| A1 out of capacity / quota exceeded | Inspect regional node-pool options and remaining limits. Stop; do not substitute a paid shape or region. |
| Pods Pending | Inspect scheduling events, allocatable capacity, taints, CSI attachments, `drn-retained` class and exact PV/PVC mapping. A missing reserved volume must not trigger dynamic fallback. |
| `ImagePullBackOff` / exec format error | Check digest availability, manifests for the selected CPU architecture, registry limits and outbound DNS/HTTPS. Do not remove digest pins. |
| Graylog/Data Node crash or OOM | Check memory, kernel requirements, filesystem ownership, disk space and database TLS/authentication. |
| Root sync denied after bootstrap | Expected after the scoped bootstrap command. Use `up` or `resume` for guarded activation. If combined startup failed, inspect its policy/credential gate and recover the interrupted operation. Do not manually bypass the deny window. |
| Root sync fails during activation | Confirm the stage, immutable revision, full sync, hooks and child health. Keep writers stopped until data recovery succeeds. |
| Infrastructure management refused | Inspect `status`, active lock, phase, backup identity and generation. Use the matching command; never clear evidence to force teardown. |
| Stale backup or workflow failure | Inspect the external runner and latest complete checkpoint. Resolve failures before assuming the recovery objective is met. |
| Recovery asks to create new credentials | Stop. Restore the external initialization record, Secrets and markers before starting credential Jobs. |
| Unexpected bill | Inspect enhanced cluster features, load balancers, surplus boot/data disks, backups, egress and overlapping replacement workers. |

Use pod descriptions and targeted logs to diagnose failures, but redact credentials and account details before sharing output. Avoid deleting PVCs, regenerating credentials or disabling TLS as troubleshooting shortcuts.
