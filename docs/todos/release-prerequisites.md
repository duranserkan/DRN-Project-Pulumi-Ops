# Release prerequisites TODO

Status: version pins and snapshot validation implemented. Release publication and compatibility checks are pending. Covers step 3 of the [ordered backlog](../pulumi-infrastructure-assessment.md#ordered-implementation-backlog-and-acceptance).

- [x] Pin the .NET SDK, Pulumi/OCI/Kubernetes packages, Argo CD chart and CI actions in their owning files.
- [x] Pin the Calico manifest commit and Calico/BusyBox multi-architecture image digests; verify canonical registry provenance and Arm64 manifest presence without executing images.
- [x] Fetch a public GitOps tag, compare its resolved SHA and use its clean detached snapshot for installation inputs.
- [x] Select OCI CLI 3.95.0 and review its conditional Object Storage create/update behavior used by recovery coordination.
- [ ] Execute compatibility checks for OCI CLI 3.95.0 and record compatible Pulumi and kubectl CLI versions.
- [ ] Publish an immutable GitOps release containing the OCI recovery profile and all three activation stages after its CI passes, then select its tag and commit in private configuration.
- [ ] Inventory every workload, operator, init container, hook, chart default and downloaded executable for native Arm support. Verify image digest provenance and retain the required image references for recovery.
- [ ] Verify access to the public Docker Hub images and NuGet dependencies. Document pull authentication only if it is required.
- [ ] Resolve GB/GiB differences and record actual device bytes and worker allocatable memory during provisioning.
- [ ] Verify the CLI tools, OKE v1.36 patch, matching Oracle Linux 8 image and Calico 3.32.0 together. Block deployment on unavailable versions or unresolved dependencies.
- [ ] Record the tested release inputs and verification evidence without copying upstream manifests into a second maintained source. Hand off to [GitOps integration](gitops-integration.md).
