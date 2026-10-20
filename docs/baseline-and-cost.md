# Baseline and cost

[Project overview](../README.md) | [Preflight checklist](todos/oci-preflight.md) | [Architecture assessment](pulumi-infrastructure-assessment.md)

These are the staging defaults. Worker and storage settings are [configurable](setup.md#configure-workers-and-storage). Verify account eligibility, remaining allowances and capacity before provisioning. Live deployment and capacity checks are pending.

| Setting | Default staging configuration |
|---|---|
| Cloud / placement | OCI home region, verified tenancy-specific availability domain |
| Account | Paid tenancy (Pay As You Go), with verified unused A1 free allowance |
| Control plane | OKE **Basic**, explicitly selected |
| Networking | Private workers and OCI VCN-native pods, NAT and regional service gateways; public API restricted to approved clients and cluster subnets |
| Worker | One `VM.Standard.A1.Flex`, **4 OCPUs / 4 Arm cores, 24 GB RAM** |
| Policy profile | Exact OKE `v1.36` patch and Oracle Linux 8 Arm image, pinned Calico 3.32.0 policy-only enforcement, maximum 93 pods |
| Storage | **Seven 50 GB retained data volumes**, separate **64 GB disposable boot** volume, **414 GB total** |
| Recovery | Protected versioned Object Storage bucket, encrypted credentials/state, offline group checkpoints and native database exports |
| Applications | Full staging stack, including Graylog, MongoDB and Data Node |
| Access | Authenticated local tunnels, explicit Traefik `ClusterIP` Service |
| Releases | Existing public Docker Hub images pinned by digest, public NuGet dependencies |
| Production | Disabled |

**Target: the paid-tenancy A1 free allowance.** Oracle's price list gives paid tenancies 3,000 A1 OCPU-hours and 18,000 GB-hours per month, shared across A1 compute usage. This worker consumes 2,976 OCPU-hours and 17,856 GB-hours in a 31-day month. Verify the paid account's entitlement and unused allowance before deployment. The smaller Always Free-only allowance does not cover this configuration, and A2/A4 are not covered by the A1 allowance. [OCI price list](https://www.oracle.com/cloud/price-list/)

**Storage is intentionally billable.** Seven native CSI volumes use Oracle's 50 GB minimum, totaling 350 GB of data storage. Including boot storage, 414 GB exceeds the shared 200 GB boot/block allowance by 214 GB even before other tenancy volumes. Confirm home-region eligibility and remaining allowances separately. Set `allowBillableStorage: true` in private operator configuration only after approving this baseline. This acknowledgement does not check the account's bill or impose a spending cap. [OCI volume provisioning](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengcreatingpersistentvolumeclaim_topic-Provisioning_PVCs_on_BV.htm), [Always Free resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm)

Data volumes default to Balanced performance at 10 VPUs/GB. Lower Cost (0) and Higher Performance (20) are configurable before initialization. Each generation retains its storage profile.

OKE Basic has no cluster-management fee. Confirm account access and budget workers, storage and ancillary services separately. Do not treat trial credit as a sustainable allowance. [OKE cluster types](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengcomparingenhancedwithbasicclusters_topic.htm)

Budget backups, versioned recovery storage, networking, transfer and the external runner separately. A second 64 GB boot volume during replacement would raise disk allocation to 478 GB. The node pool deletes before replacement and does not force eviction after the grace period. Verify cleanup and maintenance behavior in OCI. Budget alerts do not impose a spending cap.

Each restore adds seven protected volumes at the source sizes. Defaults add **350 GB**, reaching **764 GB** with two generations and one boot disk. At most three generations are supported, totaling **1,114 GB** with default sizes and one worker. Restore requires `--allow-additional-storage`. Source volumes remain protected, with no automatic purge.

For custom settings, add all retained data volumes to `workers * bootVolumeSizeInGbs` while running. Each restore adds its source generation's capacity. Worker count, shape and storage tier affect cost. Reassess the budget before changing them.

Complete checkpoints retain 2 to 14 records (default seven) and include native exports carried in the offline group backup. A separate daily seven-day policy protects the active volume group with crash-consistent fallback backups. Include both backup streams and recovery-bucket versions in the budget. Native exports consume data-volume capacity. Every complete checkpoint temporarily stops all workers. Measure that downtime, startup demand and backup storage growth before accepting the operating schedule.

Unavailable A1 capacity or failed eligibility blocks deployment. Approve additional paid capacity only when measurements justify it. Keep logging in scope. After `pause` or `down`, retained volumes, backups and recovery storage can still incur charges. See the [cost policy](pulumi-infrastructure-assessment.md).
