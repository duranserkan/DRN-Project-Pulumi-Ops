# Learning resources

[Project overview](../README.md) | [Setup](setup.md) | [Architecture assessment](pulumi-infrastructure-assessment.md)

Check these resources against the selected release.

| Resource | How to use it here |
|---|---|
| [Awesome Oracle Cloud Free Tier](https://github.com/neitsab/awesome-oracle-cloud-free-tier) | Discover account setup, budget, networking and Kubernetes material. Recheck older examples against current allowance and OKE access rules. |
| [Awesome Pulumi](https://github.com/pulumiverse/awesome-pulumi) | Discover official learning paths, state/secrets guidance, providers and automation examples. Adapt concepts to C# and OCI rather than copying another cloud's design. |
| [Pulumi OCI registry](https://www.pulumi.com/registry/packages/oci/) | Inspect C# resource APIs and authentication configuration. Exact package versions live in the Pulumi project; runtime compatibility remains to be verified. |
| [OKE documentation](https://docs.oracle.com/en-us/iaas/Content/ContEng/home.htm) | Verify prerequisites, networking, supported nodes, access and maintenance procedures. |
| [OKE Calico setup](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengsettingupcalico.htm) | Supported Kubernetes/Calico matrix and policy-only settings for OCI VCN-native networking. This mode is not supported alongside Flannel. |
| [OCI VCN-native pod networking](https://docs.oracle.com/en-us/iaas/Content/ContEng/Concepts/contengpodnetworking_topic-OCI_CNI_plugin.htm) | Pod subnet, VNIC capacity and private routing requirements. |
| [OKE cloud-init](https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengusingcustomcloudinitscripts.htm) | Preserve Oracle's initialization script when setting the persistent host memory-map limit. |
| [Linkerd NetworkPolicy guidance](https://linkerd.io/docs/common-errors/network-policy/) | Permit proxy and control-plane paths while verifying namespace isolation. |
| [OCI CLI 3.95.0 Object Storage source](https://github.com/oracle/oci-cli/blob/v3.95.0/services/object_storage/src/oci_cli_object_storage/objectstorage_cli_extended.py) | The reviewed `--no-overwrite` path sends `If-None-Match: *` for an absent object, providing the conditional create used by the shared recovery lock. Existing-object success without an ETag is rejected by the operator. |
| [GitOps deployment README](https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops#bootstrap-a-cluster) | Use the source-owned workload bootstrap after the [OCI prerequisites](setup.md) are satisfied. |

Version pins live in [global.json](../global.json), the [Pulumi project](../src/Drn.Pulumi/Drn.Pulumi.csproj) and [shared CI](../.github/workflows/ci.yml). CI action SHAs were checked against upstream tags on 18 October 2026.

CI additions verified on 19 October 2026:

- [Dependency Review v5.0.0](https://github.com/actions/dependency-review-action/tree/v5.0.0) resolves to `a1d282b36b6f3519aa1f3fc636f609c47dddb294` in the official repository.
- [SonarScanner NuGet metadata](https://api.nuget.org/v3/catalog0/data/2026.10.15.15.17.03/dotnet-sonarscanner.12.0.0.json) identifies the manifest-pinned package and its SHA-512 provenance hash: `3de/QlFpKg+CbPeWQmVOTnMh/7A0KIw44zrwIFVyPBtRevdNAi+CODye25mzdkxK1upJ0Qrzypua855bwPFwgg==`.
- [SonarCloud .NET coverage](https://docs.sonarsource.com/sonarqube-cloud/analyzing-source-code/test-coverage/dotnet-test-coverage) documents the begin/build/test/end order and Visual Studio XML coverage import. Both repository test projects produce that XML format.
- [CodeQL compiled-language options](https://docs.github.com/en/code-security/reference/code-scanning/codeql/build-options-for-compiled-languages) documents C# analysis with an explicit build, including generated source.

The Pulumi project directly pins the OpenTelemetry exporter to raise Pulumi's older transitive dependency floor above the affected versions in [GHSA-g94r-2vxg-569j](https://github.com/open-telemetry/opentelemetry-dotnet/security/advisories/GHSA-g94r-2vxg-569j) and [GHSA-4625-4j76-fww9](https://github.com/open-telemetry/opentelemetry-dotnet/security/advisories/GHSA-4625-4j76-fww9). The selected package's canonical NuGet source commit matches the upstream release tag. Remove this override when Pulumi's dependency floor includes both fixes. Vulnerability auditing remains enabled and enforced.

[PolicyEnforcement.cs](../src/Drn.Pulumi/PolicyEnforcement.cs) pins and transforms the [upstream Calico manifest](https://github.com/projectcalico/calico/blob/eb1cf57823a1dd8d25c48b82fd023ea9e3e17996/manifests/calico-policy-only.yaml). [PolicyPreflight.cs](../src/Drn.Infrastructure.Operator/PolicyPreflight.cs) pins the BusyBox probe image. Registry checks covered digest provenance and Arm64 manifests, not image execution. See the [verification record](todos/deployment-acceptance.md#local-verification) for test results and pending checks.
