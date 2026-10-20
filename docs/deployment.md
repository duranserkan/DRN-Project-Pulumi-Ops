# Deploy and bootstrap staging

[Project overview](../README.md) | [Setup](setup.md) | [Operations](operations.md) | [Implementation checklist](todos/pulumi-lifecycle.md)

Complete [preflight](todos/oci-preflight.md) before creating resources. Live OCI acceptance is pending. Record [deployment acceptance](todos/deployment-acceptance.md) before relying on the environment.

## Provision infrastructure

Follow the [README command sequence](../README.md#set-up-staging) from the repository root with a completed `.local/staging.json`. Select a reviewed GitOps tag and record its commit SHA. The release must contain the `oci-retained` profile and `prepare`, `data` and `workloads` activation stages.

For a new deployment, generate and independently back up the recovery key, then set `recoveryKeyBackedUp: true`. `setup` selects four stacks and creates the recovery bucket. `initialize` requires empty infrastructure stacks and saves credentials before workloads start. Recover existing deployments with their original key and state.

The optional `durable`, `cluster` and `bootstrap` commands each apply one stack under the shared recovery lock. `durable` records volume identities. `cluster` checks cloud prerequisites, provisions the cluster and writes kubeconfig. Either may run first.

After `initialize`, the optional staged path is:

```sh
dotnet run --project src/Drn.Infrastructure.Operator -- durable .local/staging.json
dotnet run --project src/Drn.Infrastructure.Operator -- cluster .local/staging.json
dotnet run --project src/Drn.Infrastructure.Operator -- bootstrap .local/staging.json
dotnet run --project src/Drn.Infrastructure.Operator -- up .local/staging.json
```

`bootstrap` requires matching durable outputs and the configured workers. It verifies the GitOps snapshot, applies bootstrap resources and checks policy readiness. Workload sync remains disabled until `up` or `resume`.

These commands require `initialized` state with no pending recovery and leave that phase unchanged. `durable` and `cluster` refuse changes after bootstrap resources or pending operations exist. Malformed checkpoints also block provisioning. Retry `bootstrap` while still initialized. Follow [interruption recovery](operations.md#interrupted-operations) after a failed mutation.

`bootstrap` no longer performs full startup. Use `up` or `resume` to reconcile all three stacks, activate workloads and take the first backup.

`up` performs these steps:

1. Acquire the recovery lock, decrypt saved state and verify the GitOps tag, commit and clean checkout.
2. Check live OCI compatibility and freeze any existing GitOps reconciliation.
3. Apply durable volumes, OKE Basic networking and the worker pool. Write `.local/staging.kubeconfig`.
4. Apply static CSI bindings, Calico, Argo CD and the staging root with sync disabled.
5. Verify every worker, CSI registration, DNS, host settings and NetworkPolicy behavior, including cross-node traffic. Restore and compare credentials and volume identities.
6. Start the Argo CD application controller. Run preparation and data stages, restore PostgreSQL metadata when needed, then activate workloads after data readiness checks.
7. If this generation has no complete checkpoint for the selected release, take one and restart. This stops all workers temporarily.

The root pins the resolved commit and starts with `infrastructureProfile=oci-retained` and `activationStage=prepare`. Sync is blocked until the operator completes readiness checks. It then requests each full sync and verifies its operation, revision and health. Pulumi owns bootstrap resources. Argo CD owns platform and workload resources. Do not bypass the sync gate or regenerate credentials for retained databases.

Pulumi prompts before each apply unless `--yes` is supplied. Failures stop subsequent commands. Operations that may have changed resources retain the recovery lock for inspection.

## Recovery and management

Follow [operations](operations.md) for backup, pause, release changes, restore and interruption recovery. The shared lock has no automatic expiry. Stop the previous process before unlocking it. Direct cloud, Pulumi and Kubernetes commands bypass this coordination.

Complete the [acceptance checklist](todos/deployment-acceptance.md), including recovery drills, network isolation, workload capacity and measured downtime.

## Operator commands

Use `dotnet run --project src/Drn.Infrastructure.Operator -- <command> .local/staging.json` from this repository's root.

| Command | Current behavior |
|---|---|
| `validate` | Check configuration locally. It does not query OCI entitlement, identity or capacity |
| `keygen` | Create a private 32-byte recovery key; never replace an existing key |
| `setup` | Select four Pulumi Cloud stacks and apply the recovery bucket only |
| `initialize` | Require a new deployment, then persist and read back its encrypted credential generation and identity |
| `preview` | Validate live compute compatibility, then preview durable and cluster resources |
| `durable` | Configure/apply only durable storage and record volume identities; require initialized state and no bootstrap resources or pending operations |
| `cluster` | Configure/apply only the cluster and write kubeconfig; require initialized state and no bootstrap resources or pending operations |
| `bootstrap` | Apply bootstrap resources and check policy readiness. Requires initialized state, durable storage and workers. Leaves workload sync disabled |
| `up`, `resume` | Start the recorded generation through the same guarded provisioning and activation path; require the deployment not already running |
| `status` | Show saved phase, active generation, complete checkpoint IDs and current lock owner |
| `check` | Verify backup identities. When running, also check freshness, credentials, bindings, policy and data health |
| `kubeconfig` | Refresh the private kubeconfig for the cluster stack |
| `backup` | Take a complete checkpoint and restart; leave deliberately paused or removed deployments offline |
| `pause` | Take a complete checkpoint and leave zero workers |
| `down --confirm staging` | Checkpoint if needed, then remove bootstrap and cluster resources while retaining recovery and data |
| `restore --backup <id> --allow-additional-storage --confirm staging` | Restore a checkpoint into seven new volumes after pause or down. Take a new checkpoint and leave the deployment running |
| `release --confirm staging` | Select a reviewed compatible release while paused/down or recovering an interrupted startup; preserve credentials and checkpoints, then explicitly resume |
| `unlock --owner <id> --previous-process-stopped --confirm staging` | Explicitly release the matching lock after the previous operator has stopped |

Use `release` to change the selected GitOps revision. Restore requires the checkpoint's original commit. Never edit saved credentials, backup revisions or volume mappings to bypass checks. There is no automatic purge of protected volumes or recovery keys.
