# Local setup and OCI preparation

[Project overview](../README.md) | [Deployment](deployment.md) | [Preflight checklist](todos/oci-preflight.md)

Complete account, cost and network preparation before provisioning staging. See the [verification record](todos/deployment-acceptance.md#local-verification) for tested behavior and pending checks.

[Tools](#install-local-tools) | [OCI access](#prepare-oci-access) | [Pulumi](#prepare-pulumi) | [Network and storage](#prepare-networking-and-storage)

## Install local tools

Use Linux or macOS because recovery keys require Unix file permissions. Record tool versions before deployment. [global.json](../global.json) selects the .NET SDK. The [Pulumi project](../src/Drn.Pulumi/Drn.Pulumi.csproj) pins provider packages.

| Tool | Purpose / installation |
|---|---|
| Git | Retrieve this repository and the selected GitOps release |
| .NET 10 SDK | Build the infrastructure and operator projects; follow [Pulumi .NET setup](https://www.pulumi.com/docs/iac/languages-sdks/dotnet/) |
| Pulumi CLI | Preview and apply infrastructure; [installation](https://www.pulumi.com/docs/install/) |
| OCI CLI **3.95.0** | Login, discovery, kubeconfig and conditional recovery writes; [installation](https://docs.oracle.com/en-us/iaas/Content/API/SDKDocs/cliinstall.htm). Use the reviewed version on workstations and runners |
| kubectl | Inspect Kubernetes; match the supported control-plane version |
| Helm and Argo CD CLI, optional | Inspect the GitOps bootstrap. Pulumi installs the Helm release without invoking these CLIs |
| Linkerd CLI and kubeseal, optional | GitOps platform diagnostics and sealed-secret workflows |

Use the GitOps repository's [tool and bootstrap guide](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops#bootstrap-a-cluster) for platform-specific versions. Optional context-switching tools and Headlamp are described in its [tools section](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops#tools).

Local version checks:

```sh
git --version
dotnet --info
pulumi version
oci --version
kubectl version --client
```

## Prepare OCI access

### Account, region and permissions

Before resource creation, record these in access-controlled deployment configuration:

- Tenancy OCID, confirmed paid account type, home region and remaining A1 and storage free allowances.
- Region display name, identifier, region key and exact availability-domain name returned by OCI.
- Separate cluster and durable compartment OCIDs, with deletion boundaries.
- Object Storage namespace and a distinct private recovery bucket name in the durable compartment.
- Operator identity, required IAM policies, Kubernetes authorization and approved client network ranges.
- Supported Kubernetes version, selected standard Flex shape, compatible OKE image and limits.
- Budget notifications, resource inventory and owner for billing review.

An OCID identifies an Oracle Cloud resource. Keep deployment identifiers in private configuration. Administrators must provide IAM policies for the required compartments, volume backups, conditional Object Storage writes and CSI access to durable volumes. Verify Kubernetes authorization separately. Use the [OKE prerequisites](https://docs.oracle.com/en-us/iaas/Content/ContEng/Concepts/contengprerequisites.htm) and [credential inventory](pulumi-infrastructure-assessment.md#deployment-configuration-and-credential-inventory).

### Interactive authentication and discovery

Replace every `<...>` placeholder before running these authentication and discovery commands.

```sh
oci session authenticate --region '<oci-region-id>' --profile-name DRN-STAGING
oci session validate --profile DRN-STAGING --auth security_token
oci iam region-subscription list --tenancy-id '<tenancy-ocid>' --profile DRN-STAGING --auth security_token
oci iam availability-domain list --compartment-id '<tenancy-ocid>' --region '<oci-region-id>' --profile DRN-STAGING --auth security_token
oci ce node-pool-options get --node-pool-option-id all --compartment-id '<cluster-compartment-ocid>' --region '<oci-region-id>' --profile DRN-STAGING --auth security_token
```

Confirm the home-region subscription. Select an exact OKE **v1.36 patch** and a compatible **Oracle Linux 8 image** for the chosen CPU architecture. This matches the pinned Calico 3.32.0 policy layer. Stop if the pair is unavailable. Follow Oracle's cgroups v2 requirements and verify allocation capacity. [Supported images and shapes](https://docs.oracle.com/en-us/iaas/Content/ContEng/Reference/contengimagesshapes.htm), [OKE Calico compatibility](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengsettingupcalico.htm)

Browser login creates local authentication material. Protect the profile, session token and private key. Sessions expire, so validate before deployment and refresh while valid or authenticate again. OCI CLI token calls need `--auth security_token`. Unattended replacement needs its own renewable identity outside the disposable cluster. [OCI session authentication](https://docs.oracle.com/en-us/iaas/Content/API/SDKDocs/clitoken.htm)

## Prepare Pulumi

Run commands from this repository's root. [Drn.Pulumi.slnx](../Drn.Pulumi.slnx) contains the infrastructure, operator and unit-test projects. The operator invokes Pulumi, OCI, Git and kubectl with argument lists and stops on command failures.

### Install and sign in

On macOS with Homebrew, install the CLI from Pulumi's official tap:

```sh
brew install pulumi/tap/pulumi
pulumi version
```

On Linux, follow [Pulumi's installation instructions](https://www.pulumi.com/docs/install/) and ensure `pulumi` is on `PATH`. Install and record the CLI separately from the .NET provider packages.

Create or sign in to a [Pulumi Cloud account](https://app.pulumi.com/). Select the hosted backend explicitly, then inspect the current identity:

```sh
pulumi login https://api.pulumi.com
pulumi whoami --verbose
```

Complete the CLI's browser or access-token prompt. Put your personal username or organization slug in `organization` in the private JSON. Confirm that identity can create and update stacks there. OCI authentication is separate and must also be configured. [Pulumi login](https://www.pulumi.com/docs/iac/cli/commands/pulumi_login/), [identity inspection](https://www.pulumi.com/docs/iac/cli/commands/pulumi_whoami/)

The project is defined in `src/Drn.Pulumi/Pulumi.yaml`. Run operator commands from the repository root. Use `setup` to select stacks and generate configuration. Direct `pulumi up` or `destroy` bypasses lifecycle safeguards.

The OCI provider defaults to `SecurityToken` with your `ociProfile` and `region`; the operator passes the same identity mode to OCI CLI calls. An external OCI runner can use `ociAuth: "InstancePrincipal"` with reviewed dynamic-group policies. Browser authentication does not create an unattended deployment identity. [OCI provider configuration](https://www.pulumi.com/registry/packages/oci/api-docs/provider/)

### State, secrets and stack configuration

The operator uses four fully qualified Pulumi Cloud stack names under `<organization>/drn-pulumi/`: `staging-recovery`, `staging-durable`, `staging-cluster` and `staging-bootstrap`. State tracks resource identities and must outlive the cluster. Keep the same backend and organization across workstations and the operations runner. A DIY backend is outside the current operator contract. [State and backends](https://www.pulumi.com/docs/iac/concepts/state-and-backends/)

| Store | What it owns | Recovery requirement |
|---|---|---|
| Pulumi Cloud | Stack configuration, resource identities and deployment checkpoints | Access to the original account/organization and stacks |
| OCI recovery bucket | Encrypted credential generations, lifecycle state and shared lock | Original bucket plus the independent recovery key |
| Private local configuration | Intended cloud settings and references to the two stores | A secure copy of `.local/staging.json` or runner configuration |

Deleting the checkout does not delete those cloud stores, but a fresh checkout still needs its private configuration and original key. Never run `keygen` or `initialize` as a recovery shortcut.

The recovery stack owns a protected, private, versioned Object Storage bucket. The operator encrypts credentials, lifecycle state and locks with AES-256-GCM, then reads back each write for verification. Keep the 32-byte recovery key outside the bucket with an independent secure copy. Protect Pulumi state separately. Do not expose secrets in command arguments, output or decrypted exports. [Pulumi secrets](https://www.pulumi.com/docs/iac/concepts/secrets/)

Copy [configuration/staging.example.json](../configuration/staging.example.json) to `.local/staging.json` as shown in the [README](../README.md#set-up-staging). Replace every placeholder. The example deliberately leaves billable storage disabled.

| Field | Required value |
|---|---|
| `organization` | Your Pulumi Cloud account or organization name |
| `recoveryKeyPath` | Absolute path to a new private key file; its parent directory must already exist |
| `recoveryKeyBackedUp` | Set `true` only after securely copying the generated key outside this workstation and cluster |
| `backupRetentionCount`, `maximumBackupAgeHours` | Complete checkpoint count, 2 to 14 (default 7), and running-deployment readiness age limit, 24 to 168 hours (default 26) |
| `region`, `availabilityDomain` | Validated home region and exact tenancy-specific availability-domain name |
| `clusterCompartmentId`, `durableCompartmentId` | Two different compartment OCIDs with reviewed deletion boundaries |
| `kubernetesVersion`, `nodeImageId` | Exact `v1.36.patch` and compatible Oracle Linux 8 OKE image for the selected shape architecture |
| `apiAllowedCidrs` | Approved IPv4 network ranges, normally operator `/32` addresses. `0.0.0.0/0` and ranges with host bits are rejected |
| `ociProfile`, `ociAuth` | Local profile, initially `DRN-STAGING`, and `SecurityToken`; use `InstancePrincipal` only on the authorized OCI runner |
| `objectStorageNamespace`, `recoveryBucketName` | Tenancy Object Storage namespace and a distinct lowercase bucket name, 3 to 63 letters, numbers or hyphens |
| `allowBillableStorage` | Set `true` only after approving [configured storage and ancillary costs](baseline-and-cost.md). The default is 414 GB |
| `workers` | Desired active count, default `1`. Subnet capacity limits the maximum. `pause` transitions to zero and `resume` restores the configured count |
| `faultDomains` | Default `[]` lets OKE distribute workers across FDs in the configured AD. Optional distinct `FAULT-DOMAIN-1`, `FAULT-DOMAIN-2`, `FAULT-DOMAIN-3` values |
| `nodeShape` | Default `VM.Standard.A1.Flex`. Also accepts `VM.Standard.E4.Flex`, `VM.Standard.E5.Flex`, `VM.Standard.E6.Flex` and `VM.Standard3.Flex`, subject to live compatibility |
| `nodeOcpus`, `nodeMemoryInGbs` | Per-node positive integer OCPUs and positive finite GB RAM, default `4` and `24`. Live shape and image limits also apply |
| `bootVolumeSizeInGbs` | Per-node boot disk, default `64`. Integer from `50` to `32768`, at least the selected image minimum |
| `maxPodsPerNode` | Default `93`, range `1` to `110`. Constrained by subnet addresses and shape VNIC capacity |
| `storageGenerations` | One `initial` generation in operator input. Its optional `volumes` map configures the seven existing claims before initialization |
| `gitOpsRepository` | Public HTTPS GitHub repository URL without credentials |
| `gitOpsTag`, `gitOpsCommit` | Immutable `refs/tags/YYYY.M.sequence[-previewNNN]` and its full lowercase 40-character commit SHA, containing OCI integration |

Keep operator settings at the JSON root and cloud/GitOps fields inside `infrastructure`, as shown in the example. `.local/` and `src/Drn.Pulumi/Pulumi.staging-*.yaml` are ignored by Git. Keep authentication material in the OCI profile and Pulumi credential store. Do not add passwords or tokens to the JSON or edit recorded storage generations by hand.

### Configure workers and storage

All workers and retained data volumes use the single `availabilityDomain`. An empty `faultDomains` array preserves automatic placement. CPU type follows `nodeShape`. A1 uses Arm, while E4, E5, E6 and Standard3 use x86. Choose the matching Oracle Linux 8 OKE image and verify that the selected GitOps release supports that architecture. OCI OCPUs are provider allocation units, not a universal vCPU count.

The pod subnet is `/24`. The operator budgets 32 addresses per secondary VNIC and one VNIC per 31 pods on each worker. It rejects totals above 253 addresses. This permits two workers at 93 pods each, three at 62, or seven at 31. Shape VNIC limits and workload resource requests may reduce usable capacity. [OCI native pod networking](https://docs.oracle.com/en-us/iaas/Content/ContEng/Concepts/contengpodnetworking_topic-OCI_CNI_plugin.htm)

Set `sizeInGbs` and `performance` under `storageGenerations[0].volumes.<claim>`. The claim keys are `sample`, `nexus`, `postgresql`, `mongodb-data`, `mongodb-logs`, `graylog` and `datanode`. Omitted claims use 50 GB and `balanced`. Sizes are integers from 50 to 32768 GB. Supported performance values are `lower-cost` (0 VPUs/GB), `balanced` (10) and `higher-performance` (20). Persistent volume capacity follows the durable stack's output. Storage uses OCI block volumes through native CSI. [OCI performance tiers](https://docs.oracle.com/en-us/iaas/Content/Block/Concepts/update-performance-block-bv-volume.htm)

Initialization freezes the storage profile in recovery state. Later size or tier edits are rejected. Restore uses the selected source generation's profile and preserves original disks. Use the existing offline lifecycle to change compute settings. Larger boot disks are provisioned at the requested cloud capacity. Verify root partition/filesystem capacity separately because automatic filesystem growth is outside this profile. [OKE boot volume sizing](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengaddingnodepools_topic.htm)

### Preview and deployment order

Follow the [README quick start](../README.md#quick-start). Back up the generated key before setting `recoveryKeyBackedUp: true`. Initialization requires empty stacks without pending operations and saves credentials before workloads start. Never initialize over retained infrastructure or replace a lost recovery key with a new one.

`validate` checks JSON locally. `preview` checks live compute compatibility and previews durable and cluster resources. `up` applies durable, cluster and bootstrap stacks, then activates GitOps after recovery, policy and storage checks. The first startup includes a complete backup and restart. See the [deployment sequence](deployment.md).

For separate initial provisioning steps, run `durable`, `cluster` and `bootstrap` after `initialize`. Finish with `up` or `resume` to activate workloads and take the first backup. See the [command boundaries](deployment.md#provision-infrastructure).

Check replacements, deletes, disk sizes, cluster type, worker count and unexpected load balancers. A preview shows intended resource changes, not proven capacity, cost or application readiness. Omit `--yes` to retain Pulumi's approval prompts. Run only trusted Pulumi programs. [Pulumi preview](https://www.pulumi.com/docs/iac/cli/commands/pulumi_preview/)

### External automation

Scheduled backups and checks require a dedicated Linux runner outside the managed cluster. Follow [scheduled operations](operations.md#scheduled-operations) to configure its identity, private configuration, recovery key and workflow restrictions.

## Prepare networking and storage

### Network and access boundaries

The implemented cluster uses VCN `10.42.0.0/16`, API subnet `10.42.0.0/28`, private worker subnet `10.42.1.0/24`, private native pod subnet `10.42.2.0/24` and services `10.96.0.0/16`. Check these against connected networks before provisioning. The default pod density is 93 per worker. Configured density and worker count must fit the subnet and selected shape's VNIC limits. Private subnets route outbound internet traffic through a NAT gateway and regional Oracle services through a service gateway. Security lists allow required cluster traffic and path-MTU discovery, with no public SSH or application listener. No load balancer is created. [OKE network configuration](https://docs.oracle.com/en-us/iaas/Content/ContEng/Concepts/contengnetworkconfig.htm)

The public API allows port 6443 from `apiAllowedCidrs`, workers and pods. Include the external runner's fixed egress address. Private endpoints and custom networks are not implemented.

Bootstrap installs Calico 3.32.0 policy enforcement over OCI CNI. Before activation, the operator checks CSI, system readiness, DNS, host settings and allowed/denied traffic. Cloud-init sets `vm.max_map_count >= 262144` and runs Oracle's OKE initialization script. This repository requires recreation of a Flannel cluster, not an in-place CNI change. [OKE Calico setup](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengsettingupcalico.htm), [OKE cloud-init](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengusingcustomcloudinitscripts.htm)

The GitOps `oci-retained` profile explicitly sets Traefik to **`ClusterIP`**. Select a release containing that integration. Keep demo access on authenticated local tunnels. Public exposure remains deferred until DNS, trusted TLS and ingress policy are designed and verified.

### Retained storage

The durable stack creates seven protected volumes for Sample, Nexus, PostgreSQL, MongoDB data, MongoDB logs, Graylog and Data Node. Each defaults to 50 GB Balanced storage. Native OCI CSI handles attachment and mounts. The active generation has a daily backup policy with seven-day retention. Complete operator checkpoints also contain native database exports and credential identity.

- Bootstrap creates static CSI PersistentVolumes (PVs) with `Retain` and explicit namespace/claim reservations from [StorageClaims.cs](../src/Drn.Pulumi/StorageClaims.cs).
- The `drn-retained` StorageClass uses `kubernetes.io/no-provisioner` and `WaitForFirstConsumer`. The GitOps profile supplies it to all seven claims, including operator and chart-generated claims.
- CSI volume handles identify the durable cloud volumes. Unmatched claims stay pending instead of dynamically allocating empty replacements. Delayed binding is evaluated with consumers.
- Verify CSI cross-compartment permissions, attachments, filesystem ownership and the selected availability domain before workload acceptance. Storage growth and migration are not automated.

Follow the [recovery procedures](operations.md#operate-and-recover) for shutdown, backups and restore. [Oracle's CSI guidance](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengcreatingpersistentvolumeclaim_topic-Provisioning_PVCs_on_BV.htm) covers access and topology requirements.
