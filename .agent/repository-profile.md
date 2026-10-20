# Repository profile

## Scope and source map

DRN-Project Pulumi Ops owns staging OCI infrastructure and the Argo CD bootstrap. Platform charts, workloads and credential Jobs belong to the sibling DRN-Project-Argo-CD-Gitops repository. Application source and image publishing belong to DRN-Project.

| Path | Responsibility |
|---|---|
| [Drn.Pulumi.slnx](../Drn.Pulumi.slnx) | Normal .NET source and test projects |
| [src/Drn.Pulumi](../src/Drn.Pulumi) | One Pulumi project with staging-recovery, staging-durable, staging-cluster and staging-bootstrap roles |
| [src/Drn.Infrastructure.Operator](../src/Drn.Infrastructure.Operator) | Operator configuration, argument parsing, explicit stack orchestration and process execution |
| [configuration/staging.example.json](../configuration/staging.example.json) | Placeholder-only public configuration example |
| [tests/Drn.Pulumi.UnitTests](../tests/Drn.Pulumi.UnitTests) | Isolated configuration, resource-mock and bootstrap transformation tests |
| [tests/Drn.Infrastructure.Operator.UnitTests](../tests/Drn.Infrastructure.Operator.UnitTests) | Isolated operator validation, command sequencing and failure/guard tests |
| [README.md](../README.md) | Badges, setup/management commands and top-level roadmap |
| [docs](../docs) | Setup, deployment, operations, cost, architecture and detailed TODOs |

## Agent skill applicability

| Area | Discovered convention | Skill routing |
|---|---|---|
| Type and architecture | .NET console operator and Pulumi infrastructure executables with two test projects | Portable basic and repository overview skills. |
| Testing | xUnit v3 with Microsoft.Testing.Platform, DRN unit data attributes, Pulumi mocks and fake command runners | Load `test-unit` and `drn-testing` for test work. |
| DRN Framework | Both test projects reference DRN.Framework.Testing. Production projects do not depend on DRN Framework. | Use unit contexts and attributes in isolated tests. Hosting and container facilities are outside this test boundary. |
| Frontend | No frontend package root, Razor application or DRN buildwww assets | No frontend skills are retained. |
| Custom extensions | No custom skill prefixes or repository-specific workflow routes | Use the portable workflow routes in `AGENTS.md`. |

Retain skills for the implemented console, infrastructure and isolated-test boundaries. Add other skills only when repository scope requires them. Restore, build, app, test, benchmark and load-test commands require explicit user authorization. CI command examples do not grant that authorization.

## Implementation conventions

The operator exposes `durable`, `cluster` and `bootstrap` for staged initial provisioning. Each configures/applies only its own stack while retaining shared recovery locking and prerequisite reads. They require initialized state without pending recovery and leave that phase unchanged. Durable and cluster refuse changes after any bootstrap resource exists. Bootstrap requires recorded durable identities and a cluster with the configured workers, installs its existing resources and checks policy readiness without workload activation. `up` and `resume` remain combined provisioning/activation/baseline-backup workflows and own subsequent lifecycle transitions. `bootstrap` is no longer a full-startup alias.

Initialization and the pre-bootstrap barrier share one empty-checkpoint predicate. Absent/null deployment or resource fields represent empty Pulumi state. Nonempty pending operations, malformed fields and non-root resources block initialization. The same bootstrap checkpoint conditions block the scoped durable and cluster commands. Initialization also checks stack outputs independently. The lifecycle fake defaults to the omitted-field export shape so normal flows exercise first-run behavior.

Infrastructure reconciliation is explicit through the operator. Repository pushes run CI only. Argo CD reconciles the selected workload release after staged activation. Running `check` covers recovery, credentials, storage, policy/system and database readiness; Argo Application status and application request acceptance remain separate diagnostics in the operations guide.

Only staging is implemented. OKE Basic defaults to one A1 worker with 4 OCPUs and 24 GB RAM, a 64 GB boot disk and seven protected 50 GB Balanced durable volumes. Configure standard Flex A1/E4/E5/E6/Standard3 shape, OCPUs, RAM, boot size, worker count and pod density. Workers and retained volumes share one explicit AD. Fault Domains default to automatic distribution with optional overrides. The unchanged /24 pod subnet allows at most two workers at 93 pods each or seven at 31 pods each. Live preflight checks shape/image limits, boot minimum and VNIC capacity. Configuration must explicitly acknowledge billable storage. Custom host LVM, alternate storage backends, production, multiple ADs and automatic storage growth are outside the implemented profile.

Operator configuration describes only the initial storage generation, with optional per-claim sizeInGbs and performance settings under storageGenerations[0].volumes. Missing entries default to 50 GB Balanced. Supported tiers are lower-cost, balanced and higher-performance. Initialization records profiles in encrypted recovery state, and later configuration edits cannot resize or retier retained disks. Restore copies the selected source generation's profile. Legacy schema 1/default profiles remain readable. Successful state writes use schema 2 so older operators reject the new state. Pause reads deployed worker inputs from the Pulumi checkpoint to avoid applying edited desired compute settings during shutdown. Policy preflight validates every worker and probes cross-node connectivity when multiple workers exist.

The durable stack retains cloud-volume ownership. Bootstrap owns static native CSI PVs with Retain and exact namespace/claim reservations through the non-provisioning drn-retained StorageClass. The OCI GitOps profile supplies that class to all seven claims and sets Traefik to ClusterIP. IAM and CSI cross-compartment permissions remain administrator prerequisites.

The operator uses OCI SecurityToken authentication with an explicit profile, or InstancePrincipal on a trusted external runner. It generates private stack configuration, GitOps snapshots and a dedicated kubeconfig. Real identifiers belong in ignored .local/staging.json and generated Pulumi.staging-*.yaml files. Package pins, authentication material and private deployment values have different owners; do not copy credentials into examples.

The GitOps tag and SHA must identify a clean reviewed snapshot with OCI recovery integration. Bootstrap imports source-owned Argo CD values/projects/root and initially gates sync. The operator restores authenticated immutable credential checkpoints, verifies live Calico policy behavior and advances prepare/data/workloads stages. Startup permits the deliberately frozen application controller, then activation waits for it to run before sync; normal health checks require it running. Native VCN networking and the reviewed OKE 1.36/Oracle Linux 8/Calico 3.32 profile are explicit; do not substitute Flannel.

The explicit release command verifies the target checkout and current storage checkpoint before changing the selected tag/SHA. It accepts paused/down or interrupted starting with no pending backup/restore, freezing any surviving writers in the latter case. Credentials retain their original creation provenance and historical backups retain their own release. Baseline and running freshness checks select the current storage generation and release; native restore requires the selected checkpoint's original release. Release transitions must be compatible with retained data; a database rollback needs its matching checkpoint.

Recovery owns an encrypted conditional state document and non-expiring OCI object lock. Failed resource operations keep the lock; explicit unlock requires a stopped previous process. Backups combine native PostgreSQL/MongoDB/OpenSearch exports with detached seven-volume checkpoints. Restore creates another protected generation, retaining source disks and bound credential identity. PostgreSQL operator metadata must be restored before reattaching its disk. Pure validation and failed read-only checks do not strand mutation locks.

## Tooling and tests

The [SDK pin](../global.json), [NuGet source](../NuGet.Config) and project files own .NET and package settings. Declare build and audit settings in each `.csproj`. Set `TreatWarningsAsErrors=true` and document any exception by diagnostic ID in the affected project. [Bootstrap.cs](../src/Drn.Pulumi/Bootstrap.cs) owns the Argo CD chart pin. Production projects are .NET 10 executables. Tests use xUnit v3, Microsoft.Testing.Platform and DRN.Framework.Testing. No sibling filesystem project reference is required.

Keep Argo CD tests scoped to this repository's bootstrap contract, local transformations and lifecycle guards. The GitOps repository owns source-manifest quality, Helm rendering, workload specifications and application behavior. Do not duplicate that validation here. Root/project identity, destination, immutable release, sync gating and non-cascading deletion remain local safety contracts. StorageBindingTests owns retained-volume capacity and claim identity coverage. Keep credential, policy, operation/revision/health acceptance and recovery tests here. Synthetic lifecycle manifests model transport responses and do not establish upstream manifest correctness.

Use `Fact` for simple tests, `DataInlineUnit` for straightforward rows and `DataMemberUnit` or `DataSelfUnit` for complex cases. Request `DrnTestContextUnit` as the first parameter only when its capabilities are used. Temporary workspaces use `context.GetTempPath()`. Stateful transport fakes remain local to their scenarios or composed from small shared helpers. Oracle-specific constants use an `OracleCloud` prefix on the constant or its owning type. Keep CLI fields distinct from Pulumi input fields, and keep expected external contracts independent of production constants.

Use class-owned enums for finite test scenario selectors. Keep actual command, JSON and invalid-input values as strings. Use `const string` for repeated CLI options and fixture asset names, scoped to the owning class or existing support helper. Retain booleans for binary conditions. Use `[Flags]` only when independent options are intentionally combined. Avoid a catch-all constants class, enum-to-string protocol mappings and shared mutable asset data.

Both unit-test projects select xUnit `ParallelMode.All` in their `AssemblyInfo.cs`, allowing independent methods and theory rows to run concurrently. Keep mutable settings, JSON, fake backends and workspaces local to each test. DRN contexts own unique temporary directories per theory context. `ResourceTests` belongs to the nonparallel `PulumiDeploymentCollection`, which serializes its tests and prevents overlap with all other tests in its assembly. Every new `ResourceFixture` consumer must join that collection because its callbacks are process-wide. Retain xUnit's default conservative scheduler and processor-count concurrency limit.

Create expensive credentials only in tests that use them. Policy retry tests inject a per-test delay recorder and verify attempt counts, delay durations and cancellation without sleeping. Production `PolicyPreflight` defaults to `Task.Delay` with unchanged retry bounds and deadlines. Pass `TestContext.Current.CancellationToken` wherever supported, and link scenario cancellation tokens to it. Lifecycle fixture execution requires an explicit token. Pulumi `Deployment.TestAsync` has no cancellation-token overload and must finish before shared callback cleanup.

Setup-only credential consumers use `OperatorTestData.Credentials()`: one lazy production-generated serialized checkpoint, fresh mutable JSON and a distinct generation ID per consumer. Later-transition tests seed initialized state through the real encrypted store. Keep dedicated real initialization/startup coverage, state-factory parity checks and credential-copy isolation coverage. Never cache mutable checkpoints or skip production validation. Use the runner's CTRF report for per-test timing before optimizing.

`OperatorLifecycleTests` keeps private setup and command transport in `.Fixture.cs` and `.Api.cs` partial companion files. `LifecycleStateTests` shares the initialized-state factory and exposes independent authority failures as named theory rows. Cancellation tests wait for delay entry or early operation completion, and cloud boundary pairs use separate runners.

Cluster polling accepts an optional delay delegate through `InfrastructureOperator` and `ClusterLifecycle`. Production retains `Task.Delay`, five-second intervals and the 30-minute deadline. Lifecycle tests record delays and immediately poll again. Keep readiness, child-sync submission/recheck and pending-delay cancellation assertions. Do not make fake resources ready before the required command or alter production retry limits to speed up tests.

The shared command and object-store fakes run validation for every simulated request. Keep their success paths lightweight, build detailed diagnostics only on failure and retain strict duplicate/missing-option, value, prefix and transport checks. `CommandArgumentsTests` protects those rules. This measured optimization is scoped to the hot helpers, not a requirement to replace assertions elsewhere.

Credential validation imports certificates and keys separately, then verifies a signature to prove private-key possession and certificate matching. Do not reattach keys during each validation: macOS certificate/key pairing enters the shared keychain and was observed stalling concurrent tests. Preserve key sizes, EC encoding, CA/validity checks and redacted errors.

[Shared CI](../.github/workflows/ci.yml) owns restore, audit, build and test commands, plus coverage reporting. Tests use Pulumi mocks and fake command runners. Their results do not establish OCI permissions, CSI behavior, capacity or recovery readiness.

Develop, master and pull-request workflows call shared CI. It runs .NET validation, Trivy and CodeQL for C# and GitHub Actions with a gatekeeper and no cloud credentials. C# CodeQL analysis builds the solution explicitly. Pull requests also require dependency review at high severity and receive no repository secrets. Develop/master pushes and scheduled branch checks run SonarCloud around the build and both coverage-producing test runs, then wait for its quality gate. [.config/dotnet-tools.json](../.config/dotnet-tools.json) owns the scanner pin. Branch callers explicitly pass the `SONAR_TOKEN` repository secret. `SONAR_PROJECT_KEY` and `SONAR_ORGANIZATION` repository variables identify the project. Missing configuration fails branch CI. [README CI setup](../README.md#continuous-integration) owns external SonarCloud and GitHub protection prerequisites.

The separate protected operations workflow uses an external self-hosted runner, shared lifecycle lock and private configuration for scheduled backups/checks. Never run pull-request source on that runner. [Dependabot](../.github/dependabot.yml) targets develop for Actions and NuGet updates. Immutable action pins remain in the workflow files.

## Documentation and release notes

### Documentation Modules

| Module | README | Supporting docs | Release notes |
|---|---|---|---|
| Repository root | [README.md](../README.md) | [docs/](../docs/), including acceptance checklists | Not required. No published package or release-note module. |

Preserve the README's project, artifact and documentation badges. Keep runnable setup and management examples aligned with the operator, and keep top-level TODOs separate from detailed acceptance checklists. Historical cloud and compact-storage comparisons in the assessment do not describe the current native CSI baseline.

Update the owning guides and TODOs when operator behavior, storage ownership, costs or activation boundaries change. Checked source tasks do not establish runtime acceptance.
