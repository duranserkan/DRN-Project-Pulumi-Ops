# GitOps integration TODO

Status: OCI integration implemented. Release publication and live checks are pending. Covers step 7 of the [ordered backlog](../pulumi-infrastructure-assessment.md#ordered-implementation-backlog-and-acceptance). Workload changes belong to the GitOps repository.

GitOps owns manifest/chart validation and application behavior. Pulumi Ops tests bootstrap transformations, stack isolation and lifecycle guards. Verify live revision, sync and health during recovery acceptance.

- [ ] Complete [release prerequisites](release-prerequisites.md) and prepare changes in a pinned GitOps release.
- [x] Propagate `drn-retained` to all seven application, operator and chart-generated claims through the staging-only `oci-retained` profile.
- [x] Set Traefik to ClusterIP for that profile, preserving existing behavior for other profiles and production's disabled state.
- [x] Disable fresh application/Graylog credential generation in the OCI profile and require restored Secrets and markers.
- [x] Restore the saved credential generation before operators/hooks and provide `prepare`, `data` and `workloads` stages so application writers start last.
- [x] Define the pinned Argo CD installation, source-owned health checks, restricted projects, sync deny window and SHA-pinned staging root from the selected snapshot.
- [x] Request full root sync through the lifecycle operator, preserving hooks and the resolved commit; wait for the requested operation, revision and health before advancing.
- [x] Install pinned Calico policy-only enforcement on OCI VCN-native networking and gate activation on CSI/system readiness, DNS and an allowed/denied/allowed traffic probe.
- [x] Persist `vm.max_map_count >= 262144` through node cloud-init while preserving Oracle's required OKE initialization.
- [ ] Verify workload-specific namespace isolation, Linkerd proxy traffic, DNS, webhooks and control-plane paths in the deployed release.
- [ ] Verify authenticated tunnel access and absence of an unintended cloud load balancer.
- [ ] Verify non-root workloads, database TLS and required host settings on the actual worker.
- [ ] Verify restored credentials precede operators/hooks and delayed-binding claims complete with their consumers.
- [ ] Pass credential interruption tests and record allowed/denied network checks. Hand off the integrated release to [deployment acceptance](deployment-acceptance.md).
