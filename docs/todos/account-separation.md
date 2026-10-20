# Account separation TODO

Status: not started. Part of the [ordered infrastructure backlog](../pulumi-infrastructure-assessment.md#ordered-implementation-backlog-and-acceptance).

Use separate human and runner identities within the existing OCI tenancy. Configure human access before deployment and runner access before scheduled operations. Keep credentials and account identifiers private.

## Human access

- [ ] Reserve the tenancy administrator for billing, IAM setup and emergency recovery. Enable MFA and securely retain account recovery material independently of the cluster.
- [ ] Establish separate cluster and durable-resource compartments. Create a deployment group and user outside the Administrators group, with documented permissions for the required operations and compartments.
- [ ] Use browser-based OCI sessions through the local `DRN-STAGING` profile. Configure Pulumi with `SecurityToken` authentication explicitly. Keep session tokens and private keys outside the repository and restrict local file access to the operator.

## Recovery executor

- [ ] Authorize the OKE CSI controller to access the retained volumes in the durable compartment. Verify read/attach operations independently from the operator's provisioning permissions. The code does not create IAM policies.
- [ ] Use an external recovery runner that survives cluster deletion. Grant only the permissions required by the [recovery procedure](../pulumi-infrastructure-assessment.md#storage-recovery). Use an instance principal on OCI and document credential renewal and revocation.

## Acceptance

- [ ] Verify that the deployment identity can perform the intended lifecycle operations without tenancy-wide administrator access.
- [ ] Verify CSI attachment recovery while the workstation is offline and that the external lifecycle executor cannot administer billing or IAM.
- [ ] Record allowed and denied access checks without exposing credentials.
