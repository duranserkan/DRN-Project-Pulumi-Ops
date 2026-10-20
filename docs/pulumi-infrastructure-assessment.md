# Pulumi infrastructure assessment for the DRN demo

19 October 2026. Architecture assessment and implementation contract. Source is implemented in part. Earlier baseline Release builds and unit tests passed. The configurable infrastructure changes have not been built or tested. No infrastructure has been provisioned.

**Current delivery scope.** First deploy the existing DRN-Project applications and the DRN-Project-Argo-CD-Gitops repository through Pulumi. Use their current Docker Hub images and public NuGet dependencies. Keep the existing digest pins and bootstrap behavior. Private projects, private package feeds, registry migration and boilerplate generation are deferred until deployment and recovery work. Private image distribution will be evaluated after deployment and recovery pass acceptance.

Use **Pulumi with C#** to deploy OKE Basic in the OCI home region. The allocation is listed below. Keep region identifiers in private configuration and verify tenancy-specific availability domains, A1 support, images and capacity during preflight. The first milestone is one-command deployment, healthy applications, retained-data teardown and cluster recreation. See [Oracle regions](https://docs.oracle.com/en-us/iaas/Content/General/Concepts/regions.htm) and [OKE prerequisites](https://docs.oracle.com/en-us/iaas/Content/ContEng/Concepts/contengprerequisites.htm).

**Current storage decision.** The implementation defaults to seven 50 GB native OCI CSI volumes and one 64 GB boot disk, totaling **414 GB**. Worker count, boot size and initial per-claim sizes and performance tiers are configurable. Recorded data-volume profiles remain fixed across normal lifecycle operations. This supersedes the shared 128 GB LVM proposal. The approved tradeoff accepts billable storage to avoid custom host formatting, mounts, fencing and recovery machinery. Private configuration must acknowledge it with `allowBillableStorage: true`. The compact-storage and other-cloud comparisons below remain historical alternatives, not supported runtime profiles.

**Cost policy.** Target the paid tenancy's verified unused A1 allowance and account for the explicit CSI storage excess. Account upgrade and billing consent are operator prerequisites. Beyond this storage baseline, additional billable capacity needs evidence of the smallest sufficient increase and its cost. Keep logging enabled. Failed eligibility or unavailable A1 capacity blocks deployment; do not substitute another region, shape or provider automatically.

The .NET stacks and operator now implement credential checkpoints, shared coordination, staged activation, policy checks, native backups and independent restore. Local unit tests cover simulated lifecycle operations. Operator account values and live permissions still require preflight. Use [OCI session authentication](https://docs.oracle.com/en-us/iaas/Content/API/SDKDocs/clitoken.htm) to inspect account type, home region, consumption, node-pool options, images matching the selected architecture, Kubernetes versions and limits. See [OKE shape and image discovery](https://docs.oracle.com/en-us/iaas/Content/ContEng/Reference/contengimagesshapes.htm).

Keep the full stack, including Graylog, MongoDB and Data Node. Validate startup, steady-state load and maintenance peaks before accepting capacity.

Record provider memory units, actual node capacity and Kubernetes allocatable bytes before comparing them with GiB requests. A single worker requires maintenance downtime and cannot provide application high availability. Define the billable-capacity ceiling before provisioning and the recovery time objective (RTO) and recovery point objective (RPO) before workload activation.

**Free allowance and paid excess, reviewed 18 October 2026**

| Resource | Implemented allocation | Free coverage when eligible | Paid exposure |
|---|---|---|---|
| OKE Basic | One cluster | No management fee | None |
| A1 worker CPU | 4 OCPUs / 4 Arm cores | Within the paid-tenancy A1 monthly allowance | None |
| A1 worker RAM | 24 GB | Within the paid-tenancy A1 monthly allowance | None |
| Retained data volumes | 7 × 50 GB | Shared 200 GB boot/block allowance | Storage exceeds the allowance |
| Separate boot volume | 64 GB | Shared allowance, 414 GB combined | At least 214 GB beyond the combined allowance |

Free coverage depends on account eligibility and unused allowances. Include ancillary services in the total bill.

- Oracle's price list gives paid tenancies 3,000 OCPU-hours and 18,000 GB-hours per month for A1, shared across A1 compute usage. Its Always Free guide lists the smaller 1,500 OCPU-hour and 9,000 GB-hour allowance for free tenancies, which does not cover this configuration continuously. Confirm paid account status, actual entitlement and other A1 consumption before deployment. Neither allowance covers A2 or A4. Sources: [OCI price list](https://www.oracle.com/cloud/price-list/), [Always Free](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm).
- The documented storage allowance is 200 GB combined boot/block storage in the home region, with five volume backups. The selected 414 GB exceeds it by 214 GB before other tenancy volumes. An overlapping replacement boot disk would raise allocation to 478 GB. Backups, restore clones and existing resources need their own inventory.
- At 730 hours, the proposed A1 worker uses 2,920 OCPU-hours and 17,520 GB-hours, within the published paid-tenancy allowances if unused. At 744 hours, it uses 2,976 OCPU-hours and 17,856 GB-hours, leaving only 24 OCPU-hours and 144 GB-hours for other A1 usage. Replacement overlap must fit that remaining allowance too.
- Preflight must itemize active compute, retained disks, backup retention, state/recovery storage, networking, public endpoints, transfer and deployment tooling. Specify OKE Basic explicitly. Reject unplanned enhanced features or resource growth. Use quotas and lifecycle checks where supported. Budget alerts are notification, not a hard spending cap.

## Deployment configuration and credential inventory

Populate the following deployment configuration during preflight. Values in angle brackets are placeholders.

### Datacenter and placement

| Field | Proposed value or required record |
|---|---|
| Cloud | Oracle Cloud Infrastructure (OCI) |
| Account | Paid tenancy (Pay As You Go), with verified unused A1 free allowance |
| Datacenter / region display name | `<region-display-name>` in private deployment configuration |
| Region identifier | `<oci-region-id>` |
| Region key / realm | `<region-key>` / `<realm-id>` |
| Availability domains | Verify the count for the configured region. Record the exact tenancy-returned name as `<availability-domain-name>`. Do not invent its tenancy-specific prefix. |
| Physical facility | No facility name or street address is selected by this proposal. Region and availability domain are the deployment placement identifiers. |
| Home region | `<tenancy-home-region>`; must match the target region for the intended regional free allowances. Verify before creation. |
| Environment | `staging`; production remains disabled |
| Cluster | OKE Basic; `<cluster-name>` and `<cluster-ocid>` recorded after creation |
| Worker | One `VM.Standard.A1.Flex`, 4 OCPUs / 4 Arm cores, 24 GB RAM |
| Kubernetes / image | `<supported-kubernetes-version>` and `<arm-oke-image-ocid>` from regional node-pool options |
| Data / boot disks | Seven 50 GB retained volumes / 64 GB disposable boot; record every volume OCID |
| Storage placement | Retained volumes and replacement worker in the same compatible availability domain |
| Application access | Authenticated tunnels and explicit ClusterIP ingress; no public application load balancer |

Region names and identifiers come from [Oracle's region list](https://docs.oracle.com/en-us/iaas/Content/General/Concepts/regions.htm). The exact availability-domain name, supported image, quota and available capacity require authenticated inspection. If the home region differs, report the free-eligibility blocker rather than silently changing region or incurring charges.

### Private deployment configuration

These identifiers are not authentication secrets, but they describe the account and deployment. The implemented input is private `.local/staging.json`, based on [the example](../configuration/staging.example.json) and [field guidance](setup.md#state-secrets-and-stack-configuration). The CLI validates input shape and stack roles. Live tenancy, entitlement and permission checks remain preflight responsibilities. Bootstrap checks that durable and cluster stack outputs match the configured region and availability domain.

| Required field | Placeholder / purpose |
|---|---|
| Tenancy name and OCID | `<tenancy-name>`, `<tenancy-ocid>` |
| Account type and allowance evidence | Confirmed paid tenancy, `<allowance-check-timestamp>` and remaining A1 compute/storage allocation |
| Identity domain and login identity | `<identity-domain>`, `<deployment-user-ocid>` where applicable |
| Cluster and durable compartments | `<cluster-compartment-ocid>`, `<durable-compartment-ocid>`; record any shared parent and deletion restrictions |
| Interactive OCI profile | `<oci-profile-name>` with provider authentication mode `SecurityToken` |
| Network | `<vcn-ocid>`, API/worker subnet IDs, security-group IDs and authorized client CIDRs |
| Pulumi backend and stacks | `<backend-location>`, `<durable-stack>`, `<cluster-stack>`, `<bootstrap-stack>` |
| Recovery store | `<encrypted-recovery-location>`, `<encryption-key-reference>`, `<recovery-generation>` |
| GitOps release | Repository URL, immutable release tag and resolved commit SHA |
| Replacement executor | `<executor-identity>`, `<trigger>`, credential-renewal method and lifecycle-lock location |
| Resource inventory | Cluster, node-pool, instance, volume and attachment OCIDs, CSI handles and exact PV/PVC mappings |

Do not place passwords, tokens, private keys, session-token contents or embedded kubeconfig credentials in these fields. A key reference identifies where authorized automation obtains a secret; it is not the secret value.

### Credentials, ownership and recovery

| Credential or recovery material | Creation / owner | Storage and lifecycle requirement |
|---|---|---|
| Oracle console login and MFA recovery | Account owner / identity administrator | Approved password manager or identity system. Never passed into Pulumi as application configuration. |
| Interactive OCI session token and associated private key | Authorized operator through browser-based OCI session login | Protected local OCI profile files. Configure the intended profile and `SecurityToken` explicitly. Renew expired sessions; do not commit or copy tokens into recovery archives. |
| Unattended executor identity | External protected runner using OCI InstancePrincipal | Restricted IAM and Pulumi backend access outside the disposable cluster. Record permissions, availability monitoring and revocation procedure before enabling its workflow. |
| Pulumi backend access | Infrastructure operator | Credential store appropriate to the chosen backend. Keep access available after cluster teardown. |
| Pulumi state encryption / recovery decryption authority | Infrastructure operator | Independently recoverable encrypted key store. Losing the cluster must not remove the only decryption key or access path. |
| Kubernetes access configuration | Infrastructure deployment process | Protected operator/executor configuration with scoped authorization. Regenerate cluster-specific access after recreation; do not archive transient service-account tokens as durable identities. |
| Argo CD administrator / automation credentials | Bootstrap process | Encrypted external store with restricted access. Record rotation and recreation behavior. Never require manual UI login for deployment. |
| Sample / Nexus settings, seeds and keys | Pulumi Ops credential checkpoint in the OCI profile | Encrypted durable checkpoints and recovery inventory; restore before credential Jobs and workloads. Preserve existing identity on repeat deployment. |
| PostgreSQL authentication and required TLS material | Database operator / designated recovery owner | Exact Secret names and required keys recorded in the private inventory. Restore consistently with retained database files before operator initialization. |
| MongoDB operator authentication, Graylog credentials and shared trust-root material | Pulumi Ops supplies stable identities; operators own derived leaves | Preserve exact credential relationships and private trust material. Restore before logging operators/hooks. Never generate replacements against retained data. |
| Initialization markers | Existing application and Graylog bootstrap Jobs | Back up both ConfigMaps with credential checkpoints. They record an initialization claim, not completion. |
| Sealed Secrets private keys, if used | Sealed Secrets controller / recovery owner | Encrypted external backup restored before sealed resources are reconciled. |
| Registry and private-feed credentials | None required for the current public-dependency milestone | Deferred. Do not introduce tokens or paid subscriptions as initial prerequisites. |

For every durable secret, record its store reference, owner, consuming service, expiry if applicable, last rotation, backup generation and restore order. Actual secret values remain in the encrypted store. Do not print them in CLI arguments, logs, diagnostics or documentation. Limit export permissions to the named recovery inventory.

Interactive authentication follows [OCI session authentication](https://docs.oracle.com/en-us/iaas/Content/API/SDKDocs/clitoken.htm). Configure the provider according to [Pulumi OCI authentication](https://www.pulumi.com/registry/packages/oci/api-docs/provider/). Browser login does not establish an unattended authentication design. Exact IAM policies and the external executor's credential mechanism remain deployment prerequisites.

Before deployment acceptance, populate the private identifiers through authorized setup, verify least-privilege access, record credential ownership and demonstrate recovery without the old cluster. Before teardown, verify that the recovery inventory and its independent decryption path are usable. Missing or inconsistent records block teardown or restoration.

Keep cloud teardown authority outside Argo CD and use short-lived deployment credentials. Measure the full stack before paying for premium IOPS or larger hardware.

## Historical cloud and compact-storage comparisons

The comparisons through the deferred LVM allocation table record earlier alternatives. Their prices and compact-storage assumptions do not describe the implemented 414 GB native CSI baseline. They are retained for future evaluation, not as current deployment instructions.

**Closest modern configuration on each cloud**

These historical comparisons use an 8-vCPU/12-GiB target. All use x86 processors, on-demand workers and persistent SSDs. Quotas, regional availability and the selected Kubernetes node image must be checked before provisioning. AWS and Azure exceed that memory target.

| Provider | Managed cluster and worker configuration | Total CPU / RAM | Application SSD allocation | Main deviation |
|---|---|---|---|---|
| GCP | Zonal GKE Standard, 1 × `n4d-custom-8-12288` | 8 vCPU / 12 GiB | 64 GiB Hyperdisk Balanced | Closest complete hardware match. |
| Oracle | OKE Basic, 1 × `VM.Standard.E6.Flex`, 4 OCPUs and 12 GB | 8 vCPU / 12 GB | 1 × 128 GB Block Volume, Balanced | Requires automated host volume subdivision and recovery. |
| AWS | EKS standard support, managed node group, 1 × `c8i.2xlarge` | 8 vCPU / 16 GiB | 64 GiB EBS gp3 | 4 GiB more RAM and a control-plane fee. C8a is a higher-cost alternative. |
| Azure | AKS Free tier, shared system pool, 2 × `Standard_F4als_v7` | 8 vCPU / 16 GiB | 64 GiB Premium SSD v2 | Two workers and 4 GiB more RAM. Regional AKS support needs confirmation. |

**GCP:** N4D uses fifth-generation AMD EPYC processors and supports custom memory from 0.5 GB per vCPU. The proposed 8/12 shape is valid. GKE documents N4D support for manually created Standard node pools. N4D requires Hyperdisk, so do not pair it with `pd-balanced` or `pd-ssd`. Its 8-vCPU size supports enough disk attachments for seven data disks and a boot disk. This is the preferred match if the selected zone has capacity. See [N4D specifications](https://docs.cloud.google.com/compute/docs/general-purpose-machines#n4d_machine_series) and [GKE feature releases](https://docs.cloud.google.com/kubernetes-engine/docs/release-notes-new-features).

**Oracle:** E6 Flex also uses fifth-generation AMD EPYC processors. Four OCPUs provide eight hardware threads, with memory selected independently. E6 is used by managed OKE node pools. It is the x86 budget fallback. A4 Arm is cheaper, subject to the checks below. All application volumes share the proposed data disk's performance budget. See [E6 shapes](https://docs.oracle.com/en-us/iaas/Content/Compute/References/computeshapes.htm) and [Oracle's OKE E6 deployment](https://blogs.oracle.com/cloud-infrastructure/scaling-1000-agents-on-oke-and-fss).

**AWS:** Prefer `c8i.2xlarge`, using Intel Xeon 6, for the lower verified US East price. C8a is a current AMD EPYC 9005 alternative. Both provide the nearest conventional memory ratio: 8 vCPU with 16 GiB. C8a exposes one physical core per vCPU, unlike the threaded vCPU accounting on N4D and E6. Equal vCPU counts are not equal CPU performance. Keep the node group fixed at one worker initially. See [C8a specifications](https://aws.amazon.com/ec2/instance-types/c8a/) and [C8i specifications](https://aws.amazon.com/ec2/instance-types/c8i/).

EKS still requires cluster subnets in at least two availability zones. The single worker and its retained gp3 volumes can stay in one of them. Provision that network automatically. One worker does not mean one cluster subnet. See [EKS networking requirements](https://docs.aws.amazon.com/eks/latest/userguide/network-reqs.html).

**Azure:** Microsoft's current system-pool guidance requires at least two nodes, each with at least four vCPUs. Two F4als v7 workers give the closest aggregate capacity. Each has 4 vCPU and 8 GiB on AMD EPYC 9005. For the demo budget, prefer the cheaper D4als v7 alternative below. This small demo would share the system pool with application pods, so it must allow their scheduling. Two nodes add OS and DaemonSet overhead, and each large pod must fit on one 8 GiB node. Confirm that the selected region and AKS node image support these v7 shapes before deployment. See [system-pool restrictions](https://learn.microsoft.com/en-us/azure/aks/use-system-pools) and [Falsv7 specifications](https://learn.microsoft.com/en-us/azure/virtual-machines/sizes/compute-optimized/falsv7-series).

**SSD choices and useful performance**

| Cloud | Data disk selection | Baseline per proposed disk | Selection rationale |
|---|---|---|---|
| GCP | Hyperdisk Balanced | 3,000 IOPS and 140 MiB/s at 6 GiB or larger | Small disks with independently configured performance. |
| AWS | EBS gp3 | 3,000 IOPS and 125 MiB/s | Sustained baseline without burst credits. |
| Azure | Premium SSD v2, `PremiumV2_LRS` | 3,000 IOPS and 125 MB/s | Flexible capacity without buying a large fixed disk tier. |
| Oracle | Block Volume Balanced, 10 VPUs/GB | At 128 GB: 7,680 IOPS and 61.44 MB/s, shared | At 64 GB: 3,840 IOPS and 30.72 MB/s. These are whole-disk limits. |

These are documented disk limits, not measured application speeds. VM-level limits and concurrent I/O can reduce the aggregate. GCP and AWS express throughput in MiB/s. Azure and Oracle use MB/s. See [Hyperdisk Balanced](https://docs.cloud.google.com/compute/docs/disks/hd-types/hyperdisk-balanced), [gp3](https://docs.aws.amazon.com/ebs/latest/userguide/general-purpose.html), [Premium SSD v2](https://learn.microsoft.com/en-us/azure/virtual-machines/disks-types#premium-ssd-v2) and [OCI performance](https://docs.oracle.com/en-us/iaas/Content/Block/Concepts/blockvolumeperformance.htm).

Explicitly set GCP data disks to 3,000 IOPS and 140 MiB/s. Its size-based defaults can provision more than the included baseline. Check the node pool's boot-disk performance defaults separately. Keep AWS and Azure at their included baseline too. Azure Premium SSD v2 requires compatible zonal placement and caching set to `None`. Place both demo workers and their disks in one compatible zone for simple reattachment. It cannot be the OS disk. See [AKS Premium SSD v2 configuration](https://learn.microsoft.com/en-us/azure/aks/use-premium-v2-disks).

Allow a separate **64 GiB SSD boot disk per worker** as an initial planning size, subject to the chosen node image's minimum. Use Hyperdisk Balanced on GCP, gp3 on AWS, and a Balanced boot volume on OCI. Azure needs a supported OS disk such as 64 GiB Premium SSD P6. P6 has a lower baseline than Premium SSD v2, but application data stays on v2. Increase boot capacity only if image-cache and ephemeral-storage measurements justify it. Local SSD or instance-store disks are unsuitable for retained application data.

**Cost and priority order**

GKE charges $0.10 per cluster-hour, with a $74.40 monthly billing-account credit applicable to zonal Standard clusters. If that credit is available, it covers this single cluster's management fee. It does not cover the worker or disks. EKS standard-support control-plane pricing is $0.10/hour, approximately $73 for 730 hours, before compute. AKS Free has no management fee, but does not solve the hardware mismatch. See [GKE pricing](https://cloud.google.com/kubernetes-engine/pricing), [EKS pricing](https://aws.amazon.com/eks/pricing/) and [AKS tiers](https://learn.microsoft.com/en-us/azure/aks/free-standard-pricing-tiers).

OKE Basic has no cluster-management charge. Its missing workload identity and node cycling matter for automation. Keep cloud access in the external deployment process instead of compensating with broad worker credentials. Enhanced OKE adds those capabilities and an SLA. See [Oracle pricing](https://www.oracle.com/cloud/price-list/) and the [Basic versus Enhanced comparison](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengcomparingenhancedwithbasicclusters_topic.htm).

**Daily and monthly price comparison, checked 18 October 2026.** Use USD, Linux on-demand rates, 24 hours per day and 730 hours per month. These are US reference prices: GCP Iowa, AWS Northern Virginia, Azure East US and OCI's published E6 rates applicable to its commercial US regions. They are not quotes for a deployment region near Turkey. Monthly disk charges are prorated over 730 hours to produce comparable daily estimates.

| Configuration | Compute / month | Cluster / month | SSDs / month | Total / day | Total / month |
|---|---:|---:|---:|---:|---:|
| OKE Basic, E6 Flex, 8 vCPU / 12 GB | $105.12 | $0.00 | $8.16 | **$3.72** | **$113.28** |
| GKE zonal, N4D custom, 8 vCPU / 12 GiB | $208.96 | $0.00 with credit | $10.24 | **$7.21** | **$219.20** |
| EKS, C8i, 8 vCPU / 16 GiB | $273.63 | $73.00 | $10.24 | **$11.73** | **$356.87** |
| AKS Free, 2 × F4als v7, total 8 vCPU / 16 GiB | $353.32 | $0.00 | $25.55 | **$12.46** | **$378.87** |
| EKS, C8a, 8 vCPU / 16 GiB | $314.69 | $73.00 | $10.24 | **$13.08** | **$397.93** |

Each estimate includes a 64 GiB SSD boot disk per worker. GCP and AWS include 64 GiB application storage plus one boot disk. Azure includes 64 GiB application storage plus two P6 boot disks. OCI now includes one 128 GB data volume plus a 64 GB boot volume. Choosing 64 GB of OCI data storage subtracts $2.72/month. Data disks use the baseline performance selected above. Azure's regional AKS/image compatibility and OCI's compact storage lifecycle still need validation.

GCP compute is `8 × $0.030566 + 12 × $0.003476 = $0.28624/hour`. Hyperdisk capacity is approximately $0.08/GiB-month. The table assumes baseline boot-disk performance too. If the 64 GiB boot disk receives Compute Engine's size-based defaults, its additional IOPS and throughput add about $5.76/month. Budget about **$225/month** until that setting is confirmed. Without the GKE management credit, add **$2.40/day or $73/month**. See [compute rates](https://cloud.google.com/products/compute/pricing/general-purpose), [disk rates](https://cloud.google.com/compute/disks-image-pricing) and [Hyperdisk defaults](https://docs.cloud.google.com/compute/docs/disks/hd-types/hyperdisk-balanced#default_values).

OCI compute is `4 × $0.03 + 12 × $0.002 = $0.144/hour`. Balanced storage is `$0.0255 + 10 × $0.0017 = $0.0425/GB-month`. OKE Enhanced adds **$2.40/day or $73/month**, bringing this E6 configuration to about **$6.12/day or $186.28/month**. See [Oracle's published rates](https://blogs.oracle.com/cloud-infrastructure/revolutionize-healthcare-data-hub-using-kafka-on-oci).

AWS's regional Linux price feed lists **$0.37484/hour for C8i** and **$0.43108/hour for C8a**. Use $0.08/GiB-month for gp3 capacity at baseline performance. C8i saves **$41.06/month** against C8a in this comparison. See the [AWS regional price feed](https://b0.p.awsstatic.com/pricing/2.0/meteredUnitMaps/ec2/USD/current/ec2-ondemand-without-sec-sel/US%20East%20%28N.%20Virginia%29/Linux/index.json), [EBS rates](https://aws.amazon.com/ebs/pricing/) and [EKS standard-support fee](https://aws.amazon.com/eks/pricing/).

Azure's retail API lists **$0.242/hour per Linux F4als v7**, **$0.00011/GiB-hour for Premium SSD v2 capacity**, and **$10.207/month per P6 LRS managed disk**. No additional v2 performance charge applies at 3,000 IOPS and 125 MB/s. Sources: [worker rate](https://prices.azure.com/api/retail/prices?%24filter=armSkuName%20eq%20%27Standard_F4als_v7%27%20and%20armRegionName%20eq%20%27eastus%27%20and%20priceType%20eq%20%27Consumption%27), [data-disk rates](https://prices.azure.com/api/retail/prices?%24filter=serviceName%20eq%20%27Storage%27%20and%20armRegionName%20eq%20%27eastus%27%20and%20contains%28productName%2C%27Premium%20SSD%20v2%27%29) and [P6 rates](https://prices.azure.com/api/retail/prices?%24filter=serviceName%20eq%20%27Storage%27%20and%20armRegionName%20eq%20%27eastus%27%20and%20meterName%20eq%20%27P6%20LRS%20Disk%27).

These are core infrastructure estimates. They exclude tax, public IPv4, NAT, load balancers, traffic, backups, recovery-secret storage, registry storage and paid deployment tooling. No trial credits, Spot rates or commitments are assumed. The recurring GKE management credit is shown explicitly. Requote in the chosen deployment region before purchase.

**After deleting the cluster and retaining only application disks:**

| Cloud | Retained data capacity | Storage / day | Storage / month |
|---|---:|---:|---:|
| GCP | 64 GiB | $0.17 | $5.12 |
| AWS | 64 GiB | $0.17 | $5.12 |
| Azure | 64 GiB | $0.17 | $5.14 |
| Oracle compact | 64 GB | $0.09 | $2.72 |
| Oracle recommended | 128 GB | $0.18 | $5.44 |

These retention figures exclude backups and recovery-secret storage. They assume boot disks, cluster and disposable networking are deleted. Scaling only the AWS worker to zero still leaves the EKS cluster fee. Retention is cheaper than an active cluster, but preserving disks alone is not sufficient for recovery.

Compare complete bills: active worker hours, control plane, boot and data disks, backups, public IPs, NAT, load balancers, egress and logging. Trial credits and commitments should not determine the design. Use on-demand capacity for the only worker. Spot interruption conflicts with predictable demos. For intermittent use, paying for fewer active hours is more useful than a long commitment.

Other alternatives have explicit boundaries. GKE Autopilot gives up the requested control over the exact worker shape and count. A VM running k3s could reduce costs, but would make the control plane your responsibility and would not meet the managed-Kubernetes requirement.

**Oracle Arm: recommended candidate, with explicit acceptance gates**

The reference `VM.Standard.A4.Flex` uses AmpereOne M. A2 is the previous AmpereOne generation and remains a deferred comparison. A1 uses older Ampere Altra hardware. Its allowance may reduce the bill, but tenancy eligibility, capacity and the full workload must be verified. Do not base a stable demo on an assumed free allocation. See [A4 specifications](https://blogs.oracle.com/cloud-infrastructure/introducing-oci-ampere-a4-standard) and [A2 specifications](https://blogs.oracle.com/cloud-infrastructure/introducing-oci-ampere-a2-arm-cloud-compute).

A4 and A2 figures below are historical paid comparisons.

CPU accounting differs by generation: **A2 and A4 provide two physical Arm cores per OCPU. A1 provides one. E6 provides two x86 threads per OCPU.** The proposed four A1 OCPUs therefore provide four Arm cores. Equal vCPU counts do not prove equal application throughput. See [Oracle's provisioning units](https://docs.oracle.com/en-us/iaas/Content/Compute/References/computeshapes.htm).

The following totals use OKE Basic, 12 GB RAM, one 128 GB Balanced data volume and one 64 GB Balanced boot volume. The same 730-hour month, 24-hour day and exclusions apply. No free allowances are deducted. These estimates assume the proposed compact-storage design passes acceptance.

| Oracle configuration | CPU allocation | Total / day | Total / month | Saving against E6 baseline / month |
|---|---|---:|---:|---:|
| E6 reference | 4 OCPUs, 8 x86 vCPUs | $3.72 | $113.28 | $0.00 |
| A4 historical comparison | 4 OCPUs, 8 Arm cores | **$2.37** | **$72.11** | **$41.17** |
| A2 cheaper Arm alternative | 4 OCPUs, 8 Arm cores | $2.19 | $66.56 | $46.72 |
| A4 reduced CPU candidate | 2 OCPUs, 4 Arm cores | $1.71 | $51.96 | $61.32 |
| A2 reduced CPU candidate | 2 OCPUs, 4 Arm cores | $1.52 | $46.12 | $67.16 |

Savings use unrounded totals. A4 compute is `4 × $0.0138 + 12 × $0.0027 = $0.0876/hour`. A2 is `4 × $0.014 + 12 × $0.002 = $0.08/hour`. Both add $8.16/month for the proposed SSDs. A4 costs only $5.55/month more than A2 at eight cores. That is a reasonable premium for the newer generation, without assuming Oracle's published benchmark gains apply to DRN. Rates are in the [Oracle global price list](https://www.oracle.com/sa/a/ocom/docs/corporate/pricing/oracle-paas-and-iaas-global-price-list.pdf).

Registry manifest inspection on 18 October 2026 confirmed `linux/arm64` entries in the exact pinned image indexes for:

- [Sample](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/services/sample/base/deployment.yaml) and [Nexus](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/services/nexus/base/deployment.yaml).
- [PostgreSQL](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/services/postgresql/base/cluster.yaml) and the [CloudNativePG operator](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/deployment/workloads/templates/operators/cloudnative-pg.yaml).
- [Graylog and Data Node](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/deployment/logging/templates/graylog/graylog.yaml).
- [MongoDB server](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/infrastructure/graylog/mongodb/mongodb-custom-resource.yaml), its [operator, agent, upgrade hook and readiness helper](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/deployment/logging/templates/operators/mongodb-operator.yaml).

These eleven images do not require rebuilding just to obtain an Arm variant. Manifest support is not runtime acceptance. Complete the same check for Argo CD, Linkerd, ingress, certificate controllers, monitoring and bootstrap/helper images. Check downloaded executables and native libraries too. Keep multi-platform index digests when one manifest must serve both architectures. Do not introduce x86 emulation on the demo worker.

Build a release-specific inventory of every image, including chart defaults, init containers, Dex, Redis, Prometheus, trust packages and SDK bootstraps. Record index and Arm manifest digests. The sibling repository documents Kubernetes 1.36.4 as its baseline while explicitly leaving runtime compatibility unverified. For OKE 1.35 and later, select supported OL8 images with cgroups v2 enabled. Verify the exact image/Kubernetes pair through node-pool options. See [GitOps dependency evidence](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/docs/platform-operations.md#dependency-review-2026-10-17) and [OKE image requirements](https://docs.oracle.com/en-us/iaas/Content/ContEng/Reference/contengimagesshapes.htm).

Verify the selected Flex shape and a compatible OKE image for the selected tenancy, AD, architecture and Kubernetes version. Automated preflight checks node-pool options, shape resource and VNIC limits, image size and shape constraints, and explicit fault domains. A1 remains the default. See [OKE image and shape requirements](https://docs.oracle.com/en-us/iaas/Content/ContEng/Reference/contengimagesshapes.htm).

Accept Arm only after the full stack starts, remains within memory limits and passes the retained-data recovery scenario. A later Arm-to-x86 change needs a replacement node pool and a database-supported migration or restore procedure. Do not assume raw database files are portable between architectures. See [OKE Arm node guidance](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengrunningarmnodes.htm).

**Cheaper demo configurations compared with the current candidates**

Reduce CPU before reducing RAM. Four CPU cores or vCPUs are candidates for a light demo, not proven functional minimums. The full pod request total, startup jobs and workload concurrency still need acceptance. Keep all seven logical application volumes and the full logging stack for this comparison. Oracle uses the revised 128 GB shared data disk.

| Provider | Lower-cost configuration | Total / day | Total / month | Monthly saving | Tradeoff |
|---|---|---:|---:|---:|---|
| Oracle x86 | E6 Flex, 2 OCPUs, 4 vCPU / 12 GB | $2.28 | $69.48 | $43.80, 38.7% | Half the original CPU allocation. A4 offers eight Arm cores for $2.63/month more. |
| GCP | N4D custom, 4 vCPU / 12 GiB | $4.27 | $129.94 | $89.25, 40.7% | Half the CPU allocation and lower aggregate disk bandwidth. |
| AWS | `m8i.xlarge`, 4 vCPU / 16 GiB | $7.82 | $237.77 | $119.11, 33.4% | Half the CPU allocation. Still above the exact RAM target. |
| Azure | 2 × `Standard_D4als_v7`, total 8 vCPU / 16 GiB | $8.57 | $260.61 | $118.26, 31.2% | Same advertised CPU/RAM totals on a cheaper general-purpose family. Still two nodes. |

The AWS alternative uses the current M8i family at $0.211680/hour in the same [regional price feed](https://b0.p.awsstatic.com/pricing/2.0/meteredUnitMaps/ec2/USD/current/ec2-ondemand-without-sec-sel/US%20East%20%28N.%20Virginia%29/Linux/index.json). The EKS fee and SSD allocation remain unchanged. GCP uses the same custom CPU/memory rates and management credit as the reference configuration. Its potential $5.76 boot-performance supplement still applies.

Azure Dalsv7 also uses AMD EPYC 9005. Its Linux East US rate is $0.161/hour per D4als v7, versus $0.242 for F4als v7. Retain the same Premium SSD v2 data disks and two P6 boot disks. The D4als v7 supports 12 remote disks and up to 273 MB/s of baseline Premium SSD v2 throughput per node. Prefer it for this demo, subject to regional AKS support and an NVMe-compatible node image. Falsv7 provides one physical core per vCPU, so equal CPU counts must not be presented as equal compute performance. See [Dalsv7 specifications](https://learn.microsoft.com/en-us/azure/virtual-machines/sizes/general-purpose/dalsv7-series) and the [D4als v7 retail rate](https://prices.azure.com/api/retail/prices?%24filter=armSkuName%20eq%20%27Standard_D4als_v7%27%20and%20armRegionName%20eq%20%27eastus%27%20and%20priceType%20eq%20%27Consumption%27).

The four-vCPU N4D limit is eight Hyperdisk attachments. Seven application disks plus the boot disk consume all eight. Its aggregate Balanced throughput limit is 240 MiB/s, versus 480 MiB/s at eight vCPUs. Verify the custom shape and CSI attachment capacity during preflight. Two-vCPU N4D permits only four attached disks and cannot host the current full storage topology. See [N4D disk limits](https://docs.cloud.google.com/compute/docs/general-purpose-machines#n4d_machine_series) and [Hyperdisk performance limits](https://docs.cloud.google.com/compute/docs/disks/hyperdisk-perf-limits).

**Do not choose 8 GiB for the full stack.** The logging services alone request 5.5 GiB before their agent/operator overhead. GKE reserves part of installed memory for the node, leaving little space for the remaining services and startup peaks. At four vCPUs, GCP 8 GiB costs about $119.79/month, saving only $10.15 against 12 GiB. AWS C8i at 4 vCPU / 8 GiB costs about $220.06/month, saving $17.71 against M8i 4/16. Those are lean-profile candidates only, with the entire logging group explicitly disabled and functionality revalidated. Storage is held constant in those estimates.

**Fewer active hours can save more than smaller hardware.** As a planning scenario, 120 worker-hours per month with all proposed SSDs retained for the full month would cost approximately $18.67 for A4 8/12, $15.36 for A4 4/12, $25.44 for E6 8/12 or $44.59 for GKE 8/12 with the management credit. These are monthly infrastructure estimates, before the same exclusions. They assume automation actually removes billable worker compute outside demo hours. Stopping application pods alone saves no VM charges. Budget boot storage conservatively even when worker deletion removes it.

### Deferred compact-storage profile

The earlier proposal compared 128 GB and 64 GB shared application disks with a separate 64 GB boot disk. The base GitOps claims request 32 GiB in total. The proposed 128 GB allocation allowed more room for logs, maintenance and growth, but required a custom recovery system. Neither shared-disk profile is implemented. Source: [storage and database operations](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/docs/platform-operations.md#application-persistent-storage).

| Historical A4 cost comparison, before allowances | Compact (deferred) | Former recommendation (deferred) |
|---|---:|---:|
| Persistent data disk | 64 GB | 128 GB |
| Separate boot disk | 64 GB | 64 GB |
| Data storage / month | $2.72 | $5.44 |
| Compute + OKE Basic + both disks / day | $2.28 | $2.37 |
| Compute + OKE Basic + both disks / month | **$69.39** | **$72.11** |
| Data-only cost after teardown / month | **$2.72** | **$5.44** |

The extra 64 GB costs $2.72/month and doubles the disk's capacity-based Balanced performance allowance. Both choices retain the full stack. Set log retention, journal/index limits and free-space alerts. Keep backups outside this disk. The prices use the same rates and exclusions as the other tables.

**Deferred design:** one durable OCI Block Volume would be divided into Linux logical volumes with separate filesystems and static Kubernetes `local` PersistentVolumes. This preserves application boundaries while avoiding seven cloud disks. Kubernetes does not manage the underlying cloud attachment or host mounts, so the design needs its own initialization and recovery executor. See [Kubernetes local volumes](https://kubernetes.io/docs/concepts/storage/volumes/#local).

For the seven fixed claims, start with static logical volumes. An additional storage operator is not necessary for this design. Do not use a shared writable application directory or duplicate the same CSI volume handle across independent PersistentVolumes. Separate filesystems enforce capacity limits, so one application's full volume cannot consume another's allocation. All seven still share one disk's I/O and failure domain.

The existing GitOps claims use the default StorageClass unless configured otherwise. Before deployment, provide an explicit compatible local class using `kubernetes.io/no-provisioner` and `WaitForFirstConsumer`, and propagate it to application, operator and chart-generated claims. Record exact namespace/claim names, including operator-generated names. Pre-reserve each PV for its intended claim, with capacity matching the mounted filesystem and verified non-root ownership. On recreation, handle old claim UIDs and node affinity deliberately. Never leave a recovery claim eligible for dynamic OCI provisioning. Prepare mounts and PV reservations before workload sync, then wait for binding together with consumers. Waiting for all delayed-binding PVCs before creating consumers can deadlock deployment. See [local volume binding](https://kubernetes.io/docs/concepts/storage/volumes/#local) and [GitOps storage prerequisites](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/docs/platform-operations.md#application-persistent-storage).

Proposed initial logical allocations are below. Leave space unallocated for metadata and later expansion. Verify device capacity in bytes during provisioning. These are new-install settings, not changes to existing PVCs.

| Application | Current claim | Compact allocation | Recommended allocation |
|---|---:|---:|---:|
| Sample | 1 GiB | 2 GiB | 4 GiB |
| Nexus | 1 GiB | 2 GiB | 4 GiB |
| PostgreSQL | 8 GiB | 8 GiB | 16 GiB |
| MongoDB data | 4 GiB | 4 GiB | 8 GiB |
| MongoDB logs | 2 GiB | 2 GiB | 4 GiB |
| Graylog | 8 GiB | 8 GiB | 16 GiB |
| Data Node | 8 GiB | 24 GiB | 48 GiB |
| Total logical allocation | 32 GiB | **50 GiB** | **100 GiB** |

## Storage recovery

The current durable stack creates seven volumes with generation-owned size and performance settings, defaulting to 50 GB Balanced per claim. Bootstrap verifies per-claim size outputs and gives each volume a static CSI PV with `Retain` and the exact namespace/claim reservation in [StorageClaims.cs](../src/Drn.Pulumi/StorageClaims.cs). The `drn-retained` class has no dynamic provisioner. The OCI GitOps profile propagates it to all seven claims, so missing reservations cannot silently allocate empty replacement disks. [OKE volume provisioning](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengcreatingpersistentvolumeclaim_topic-Provisioning_PVCs_on_BV.htm)

OCI CSI manages attachment and mounts. Verify its controller identity can access the separate durable compartment. Verify the selected image supports the required attachment mode, filesystem ownership and encryption in transit. Keep the cluster and all retained volumes in the same compatible availability domain. Do not force attachment while an old writer might still run.

Before enabling a retained-data lifecycle:

1. Record volume OCIDs, CSI handles, exact claim identities and the matching credential/recovery generation outside the cluster.
2. Verify planned and unexpected worker replacement, old-writer fencing and attachment recovery while the workstation is offline.
3. Exercise implemented shared coordination, resumable phases, credential checkpoints and verified backups across operator machines.
4. Rehearse cluster recreation with deliberate old-claim-UID handling, restored credentials and no dynamic fallback. Test interrupted attachment, wrong volumes and attempted duplicate writers.

The CLI now quiesces workloads, checkpoints credentials and PostgreSQL metadata, validates detached volumes and takes complete group backups before pause/down. Bootstrap's deny window opens only after credential readback and live policy checks. Full retained-data lifecycle acceptance is still pending.

No automatic volume expansion or storage migration is implemented. Plan increases, filesystem growth and claim changes together, with a separate cost decision and tested recovery. OCI volumes cannot be shrunk. See [volume resizing](https://docs.oracle.com/en-us/iaas/Content/Block/Tasks/resizingavolume.htm).

The selected **24 GB is worker memory, not pod-allocatable memory**. Reserve memory for the OS and Kubernetes components, then derive workload budgets from the actual OKE node's allocatable bytes. The repository documents **5.5 GiB of requests for MongoDB, Graylog and Data Node alone**, excluding MongoDB agent/operator overhead. Startup peaks, Argo reconciliation and database maintenance must fit too. The full-stack 24 GB configuration remains unvalidated. See [Graylog capacity](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/docs/platform-operations.md#capacity-and-upgrades).

Measure OKE allocatable memory directly. Inventory CPU, memory, pod slots and ephemeral storage for the entire release, including injected proxies, monitoring, operators, system pods and concurrent SDK bootstrap Jobs. Measure OKE cold-start peaks, steady-state usage, database maintenance and image-cache pressure on the 64 GB boot disk. Define workload volume, observation duration and required headroom before accepting a candidate. If it fails, budget the necessary OCI capacity and record the configuration change rather than removing capabilities silently.

## Repository and resource ownership

Keep infrastructure in this repository. The existing [README roadmap](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/README.md#gitops-roadmap) already anticipates this separation. Its benefit is independent cloud permissions and lifecycle changes. Repository separation must be backed by branch protection and separate deployment identities.

| Owner | Resources |
|---|---|
| Pulumi recovery and durable stacks | Private versioned recovery bucket, encrypted external state, protected volume generations/groups and scheduled fallback backups. |
| Pulumi cluster stack and operator CLI | VCN, security lists, managed cluster, worker pool, explicit stack orchestration and dedicated kubeconfig. IAM creation remains an administrator prerequisite. |
| Pulumi bootstrap stack | Argo CD installation, restricted projects, root sync deny window, environment root and static native CSI PV bindings. |
| Argo CD | Existing platform charts, operators, workload manifests and staging credential bootstrap Jobs. |

Keep durable resources in a cloud project/account or resource group that cluster teardown cannot delete. Keep Pulumi state outside Kubernetes and outside the disposable stack. Pulumi Cloud is the simpler initial backend. A versioned object-storage backend is a valid alternative with separate bootstrap and key recovery.

The operator accepts a public GitHub repository URL, immutable release tag and resolved commit SHA. It verifies and fetches that snapshot before cloud mutations and uses its Argo CD values and bootstrap manifests. It does not require a manually prepared sibling checkout. Argo CD continues propagating the selected SHA to its children after future activation.

The small .NET operator uses direct argument lists to coordinate the existing Pulumi, OCI, Git and kubectl CLIs. This keeps normal .NET project/test boundaries without introducing an additional automation framework. The runtime prerequisites are documented in [setup](setup.md).

The .NET SDK, provider packages, Calico manifest/images and Argo CD chart are pinned in their owning source files. Record validated CLI versions during [release verification](todos/release-prerequisites.md). Interactive OCI authentication selects `SecurityToken`; the external runner can use `InstancePrincipal`. Protect recovery keys and backend access independently of cluster deletion. See [Pulumi OCI authentication](https://www.pulumi.com/registry/packages/oci/api-docs/provider/).

## Operator and activation boundaries

The [command table](deployment.md#operator-commands) covers setup, initialization, preview, staged activation, access, checks, backup, pause/resume, down and independent restore. The [README](../README.md#set-up-staging) supplies runnable commands. `up` restores credentials before consumers, verifies policy enforcement and performs the initial complete backup. Purge is not implemented.

`pause` and `down` require the recorded healthy generation and verified recovery authority. They suspend child reconciliation, export native data, shut down services and verify detachment before a complete group checkpoint. `down` removes only the recorded claims before destroying bootstrap and cluster stacks. Confirm actual scale-to-zero, boot-volume cleanup and charges in OCI before relying on cost savings. Historical GKE comparisons are not evidence for this lifecycle.

The activation implementation checks the exact OCI image/network profile, restores credentials before use and proves allowed/denied pod traffic before a full staged root sync. It waits for child Applications and data readiness before application writers. Interrupted operations reuse verified generations and remain explicitly locked until the old executor is stopped. Manual gate removal or credential regeneration is not an alternative recovery path.

Cloud-account creation, billing consent, the initial trusted login and future domain verification remain separate prerequisites.

**Keeping data through cluster deletion**

Use three independent protections: Pulumi `protect` on durable resources, Kubernetes `Retain` reclaim policy, and external backups. Protect the data's parent container and encryption keys as well. Pulumi protection only governs Pulumi operations. Kubernetes and direct cloud deletion need their own controls.

Do not rely on `retainOnDelete` for routine teardown. It keeps the cloud resource but removes it from Pulumi state, creating an adoption problem on the next deployment. Keep the durable stack managed and destroy only the disposable stacks. See [Pulumi deletion protection](https://www.pulumi.com/docs/iac/operations/stack-management/protecting-against-undesired-changes/).

Create disks in the durable stack and bind native CSI PersistentVolumes to the expected namespace and claim names. Record volume ID, availability domain, capacity, claim identity and recovery generation. Recreate the cluster in the compatible availability domain. Argo CD and operators retain ownership of their claims. Never let an unmatched recovery claim silently provision an empty replacement.

The recovery set must include application seeds and keys, PostgreSQL credentials and required certificate material, MongoDB authentication material, Graylog credentials, and both bootstrap initialization markers when logging is enabled. Preserve Sealed Secrets private keys if sealed resources are used. Record the exact GitOps revision and database versions alongside the data. Retain the application image digests required for recovery in Docker Hub. If authenticated pulls are configured, restore current pull credentials from their durable secret store rather than an expired cluster snapshot.

The OCI profile treats bootstrap Jobs as verifiers of supplied identities. The encrypted external initialization record is authoritative; missing recovery material stops deployment before generation. Restore existing identities instead of using a credential-generation override. See [initialization markers](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/docs/staging-configuration.md#initialization-marker) and [Graylog recovery](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops/blob/master/docs/platform-operations.md#graylog-credentials-and-recovery).

Use the [credential inventory](#credentials-ownership-and-recovery) to record exact object namespace/name/key mappings, cloud-volume and CSI identities, claim mappings and release/database versions. Regenerate transient service-account tokens through Kubernetes. Record initialization states such as uninitialized, initializing and complete with a recovery generation and verified backup references. Both existing markers record a claim, not successful completion. Graylog has no regeneration override. An interrupted first initialization may require restoration even before a deployment has ever been healthy.

Prefer generating and saving credentials before deployment, then supplying them to operators that support existing Secrets. Keep recovery-store authority with the external executor. Use this procedure for initial credentials and rotations:

1. Save the initialization or rotation record outside the cluster before generating credentials.
2. Write the credentials to encrypted recovery storage, verify read-back and decryption, and save that verification outside the cluster before dependent operators, hooks or workloads use them.
3. Reuse the verified credential generation on retries. If recovery is incomplete, stop until it is verified or a documented safe restart is possible. Never regenerate credentials against initialized data.

Where an operator must generate credentials, verify that it can pause before using them so they can be saved first. Do not wait for database readiness to save credentials the database needs to start. An operator that supports neither supplied credentials nor this pause blocks deployment.

Test interruptions before and after saving credentials, recording verification and starting dependent workloads. Losing the cluster after workloads start must preserve the recoverable credential identity, including after rotation.

Before teardown, suspend reconciliation, stop new writes, use supported database shutdown or backup procedures and verify snapshots/backups completed. Export selected recovery objects without stale UIDs, resource versions or broken owner references. Wait for disk detachment. Remove disposable load balancers and networking while the cluster controllers can still clean up their cloud resources. Destroy bootstrap resources before the cluster, without cascading into retained data. Abort on incomplete recovery evidence.

Suspend child auto-sync and operator reconciliation as needed, not merely the manually synced root. Specify namespace, Application-finalizer and CR/PVC deletion handling so bootstrap teardown cannot cascade through durable state. Use one lifecycle lock and resumable phases for up, pause, replacement and down. Before deleting a source disk during purge, name the environment, disk and recovery generation explicitly. Backup restore must be exercised separately from disk reattachment, with enough quota and a bounded cost plan for temporary restore resources.

### Backups

Retention is not backup. The operator exports PostgreSQL and MongoDB and creates a native OpenSearch snapshot before stopping services, terminating the worker and verifying detachment. A complete seven-volume group checkpoint then preserves those artifacts and application files outside the original disks. OCI encrypts volume backups; authenticated encrypted recovery metadata binds their identities to the credential generation and pinned release.

The external operations workflow schedules complete checkpoints and checks; a native OCI daily incremental policy provides an additional seven-day crash-consistent fallback. The lifecycle executor accepts only complete records for teardown/restore. Configure runner availability and failure notifications independently of the managed cluster. Backups incur downtime; a configured schedule is not a measured recovery objective.

Each completed backup set must record its data references, matching credential/key generations, initialization state, GitOps revision and database versions. Do not mark partial sets as recoverable. Set frequency, retention and deletion rules from the agreed RPO, RTO and cost ceiling. Keep referenced credentials and keys for as long as their backups exist.

Before acceptance, restore a completed set to a new seven-disk generation with the original unavailable. The command requires explicit additional-storage consent and keeps the source protected, with at most three declared generations. Verify test data, credential identity, recovery time and costs. Native recovery requires compatible versions and a clone that can start from its known-good offline files; arbitrary engine corruption and cross-cloud migration need separate emergency procedures.

### Security and deployment prerequisites

Use authenticated Kubernetes tunnels for Argo CD, databases and demo applications. The GitOps OCI profile explicitly sets Traefik to `ClusterIP`; its application listener still serves plaintext HTTP inside the tunnel. Verify there is no public application load balancer. Add automated DNS and trusted TLS before enabling public access.

Restrict Kubernetes API access to authorized identities and networks. The implemented profile uses private worker/pod subnets, native VCN pod networking, NAT/service routes and pinned Calico policy-only enforcement. Oracle's supported combination is checked before deployment; Flannel is not substituted. Use a dedicated least-privilege node identity and protect metadata credentials from application pods. Public API addressing does not authorize public application or SSH access.

Select and install a compatible NetworkPolicy enforcement provider before workloads. Do not assume selecting OCI pod networking enforces the existing policies. Verify denied cross-namespace traffic and permitted DNS, control-plane and application paths. Include policy-agent overhead and Linkerd compatibility in capacity acceptance. Source: [OKE networking guidance](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengbestpractices_topic-Networking-best-practices.htm).

Preflight checks must also cover the managed provider's supported Kubernetes versions, disk attachment limits, node pod capacity and Linkerd networking compatibility. The repository's tested Kubernetes version is not proof that every cloud offers it. If full logging is enabled, configure and verify `vm.max_map_count >= 262144` through supported node configuration. Preserve image digest pins, verified database TLS and the existing non-root workloads.

Configure security updates and a maintenance window from the start. Worker replacement interrupts this single-node environment. Set the upgrade policy explicitly so temporary surge nodes and their cost are understood.

## Ordered implementation backlog and acceptance

Multiple workers within the existing subnet budget are configurable in one AD, with automatic fault-domain placement by default. Multiple ADs, alternative storage backends, live volume resizing, other providers, shared-disk LVM, private distribution and public DNS/TLS remain deferred. The linked checklists distinguish implemented source from unexecuted acceptance work.

| Order | Work | Acceptance criteria |
|---|---|---|
| 1 | [Cost and recovery objectives](todos/cost-recovery-objectives.md) | Paid tenancy, A1/4 OCPUs/24 GB, seven 50 GB data volumes and 64 GB boot are explicit. Acknowledge billable CSI storage and verify remaining compute allowances. Record the cost ceiling, workload target, RPO and RTO. No trial-dependent pricing. |
| 2 | Authorized OCI preflight | Complete the [OCI preflight checklist](todos/oci-preflight.md), including human access separation. Confirm paid account status, allowances, home region, existing usage, IAM/compartment, region subscription, OKE versions, shape/image options and quotas. Allocation capacity remains provisional until creation. |
| 3 | [Release prerequisites](todos/release-prerequisites.md) | Pin tool/provider versions and GitOps tag/SHA. Inventory every image architecture and bootstrap dependency. Resolve exact memory units. |
| 4 | [Credential recovery](todos/credential-recovery.md) | Exercise the implemented encrypted inventory, independent key recovery, shared lifecycle lock and supplied-credential contract. |
| 5 | [Pulumi stacks and lifecycle CLI](todos/pulumi-lifecycle.md) | Verify the implemented Basic cluster, protected volumes, explicit stacks and guarded commands. Complete durable recovery state/key resources before retained-data lifecycle. Network and resource ceilings match the cost plan. |
| 6 | [CSI storage and recovery coordination](todos/storage-recovery.md) | All seven claims bind to their intended cloud volumes. Complete CSI and executor access from the [account separation checklist](todos/account-separation.md). Pass the [storage recovery tests](#storage-recovery) before enabling retained-data lifecycle. |
| 7 | [GitOps integration](todos/gitops-integration.md) | Verify CSI/ClusterIP integration, live Calico enforcement and credential interruption recovery through the guarded activation path. Keep production disabled. |
| 8 | [Backups and independent restore](todos/backup-restore.md) | Configure the [backup procedure](#backups) and pass failure-reporting and independent-restore tests within the agreed RPO, RTO and budget. |
| 9 | [Full-stack acceptance](todos/deployment-acceptance.md#full-stack-capacity) | Native Arm execution, cold-start and steady-state headroom, no OOM/eviction or disk pressure under the agreed load, including backup activity. Applications, database TLS, logging ingestion/search and monitoring work without manual setup. |
| 10 | [Lifecycle acceptance](todos/deployment-acceptance.md#lifecycle) | Fresh one-command deployment, repeat up without credential rotation, pause/resume, OCI upgrade/replacement and down/recreate preserve identifiable test data and credential identity. Missing keys or Secrets, wrong disks and interrupted phases stop safely. No empty replacement databases, unintended disks, load balancers or charges remain. |

Evidence includes repository inspection, official documentation, public pricing, recorded registry-manifest checks and the [local build and unit tests](todos/deployment-acceptance.md#local-verification). Chart renders, live workload tests and cloud operations remain pending. Verify tenancy eligibility, IAM, quotas, remaining allowances and billing through authenticated inspection; validate capacity and recovery through deployment tests.
