# Credential recovery TODO

Status: local tests passed. Live recovery drills are pending. Covers step 4 of the [ordered backlog](../pulumi-infrastructure-assessment.md#ordered-implementation-backlog-and-acceptance). [CredentialCheckpoint.cs](../../src/Drn.Infrastructure.Operator/Recovery/CredentialCheckpoint.cs) defines the saved identities and validation. Keep secret values private.

## Design and implementation

- [x] Block root sync in the Pulumi bootstrap project and require existing credential/marker inputs in the OCI GitOps profile.
- [x] Inventory twelve stable Secrets and two initialization markers, including application seeds, PostgreSQL identity/CA, MongoDB SCRAM/agent/keyfile, Graylog, shared trust and Sealed Secrets.
- [x] Implement authenticated AES-GCM checkpoints in a private versioned OCI bucket with an independent 32-byte key and private file permissions.
- [x] Record initialization, immutable credential generation/hash, lifecycle phase, storage identity and backup references outside the cluster.
- [x] Supply supported credentials before operators start; regenerate only derived leaves and connection objects through their owners.
- [x] Require conditional writes and authenticated readback before dependent hooks, operators or workloads receive credentials.
- [x] Restore matching stable objects and markers before reconciliation; refuse conflicting or incomplete live identity.
- [x] Open root sync only through policy checks and prepare/data/workloads activation.
- [x] Preserve credential generations referenced by backups; exclude transient service-account tokens from durable archives.
- [ ] Exercise deliberate trust/key rotation with a new checkpoint and matching backup before permitting consumer changes. Automatic root/key rotation is disabled in the OCI profile; renewal planning remains an operator responsibility.

## Acceptance

- [x] Author tests for credential relationships, immutable identity, certificate/key correspondence, tampering and live-object drift.
- [x] Run credential unit tests locally; see the [verification record](deployment-acceptance.md#local-verification).
- [ ] Interrupt live deployment before/after credential readback and consumer startup.
- [ ] Demonstrate recovery without the old cluster or its sole decryption path. Verify credential identity against retained data.
- [ ] Verify missing Secrets or keys cannot trigger regeneration against initialized data. Record sanitized results for [deployment acceptance](deployment-acceptance.md).
