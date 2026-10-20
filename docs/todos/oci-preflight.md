# OCI preflight TODO

Status: configuration checks implemented. Account preparation and live checks are pending. Part of the [ordered backlog](../pulumi-infrastructure-assessment.md#ordered-implementation-backlog-and-acceptance).

Complete these checks before provisioning. Follow the [cost policy](../pulumi-infrastructure-assessment.md) and keep account, access and billing evidence in private configuration.

## Account and billing

- [ ] Confirm that the subscription shows Pay As You Go. Resolve upgrade failures with Oracle before provisioning.
- [ ] Record billing consent, the billable-capacity ceiling and the person responsible for reviewing charges. Do not treat trial credits as a recurring allowance.
- [ ] Configure a monthly budget covering the cluster, durable resources and supporting services. Choose actual-spend and forecast alerts and verify recipient addresses. Record that budget alerts do not enforce a spending cap.
- [ ] Inventory existing tenancy usage and verify remaining A1 compute and boot/block storage allowances against the proposed baseline. Include backups, restore resources, state/recovery storage, networking and executor hosting in the cost review.

## Identity and placement

- [ ] Complete the human-access tasks in [account separation](account-separation.md) and record the intended deployment identity and compartment boundaries.
- [ ] Validate the local OCI session using the [access preparation instructions](../setup.md#prepare-oci-access).
- [ ] Record the tenancy home region, subscription, region identifier and exact availability-domain names in the [private configuration inventory](../pulumi-infrastructure-assessment.md#private-deployment-configuration).
- [ ] Select a compatible availability domain for all seven retained volumes and the replacement worker. Confirm home-region storage eligibility and explicitly approve the 414 GB storage baseline.
- [ ] Record approved operator/executor network access and Kubernetes authorization requirements separately from cloud IAM permissions.
- [ ] Grant the operator required volume-group backup and conditional Object Storage permissions, and independently authorize CSI access to durable volumes. Verify GitOps cannot delete cloud recovery resources.
- [ ] Provision the external protected runner with `InstancePrincipal`, restricted API access and the independently saved recovery key before enabling scheduled operations.

## Service limits and deployment options

- [x] Restrict configuration to OKE v1.36 patches, one AD, supported standard Flex shapes, explicit API CIDRs, separate compartments and approved billable storage. Preserve A1/4 OCPU/24 GB defaults.
- [x] Validate configured worker/pod counts against the /24 subnet budget and live shape/image CPU, memory, boot and VNIC constraints. Validate explicit Fault Domains.
- [ ] Exercise non-default worker counts and resource settings against OCI, including cross-node policy probes and custom volume sizes/tiers.
- [ ] Confirm OKE Basic access and an available v1.36 patch / Oracle Linux 8 image pair for the selected architecture from node-pool options, compatible with Calico 3.32.0. Record the selection.
- [ ] Verify the VCN, API, worker, native pod and service ranges do not overlap connected networks; include external runner access in approved client CIDRs.
- [ ] Check service limits, compartment quotas and existing allocations for the worker, boot/data volumes, backup/restore generations, attachments, pod VNICs, NAT and service gateways.
- [ ] Record allocation capacity as provisional until creation succeeds. An advertised shape or sufficient quota does not prove that capacity is available.
- [ ] Record blockers without automatically changing the region, shape, cluster type or worker count. Reconcile any proposed cost change with the assessment's cost policy.

## Acceptance

- [ ] Save dated evidence for account eligibility, allowances, identity, placement, limits and selected deployment options in private configuration.
- [ ] Confirm that unresolved blockers stop provisioning. Recheck expiring authentication and changing usage before deployment.
- [ ] Verify recovery bucket privacy/versioning, independent key retrieval and encrypted state readback before workload activation.
- [ ] Mark preflight complete only when its checks pass. Storage recovery, backups and full-stack acceptance remain separate deployment gates in the ordered backlog.
