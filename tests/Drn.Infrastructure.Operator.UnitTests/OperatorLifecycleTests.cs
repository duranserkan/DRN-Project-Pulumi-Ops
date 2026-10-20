using System.Text.Json;
using Drn.Pulumi;
using Xunit;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed partial class OperatorLifecycleTests
{
    [Theory]
    [DataInlineUnit("{}")]
    [DataInlineUnit("""{"deployment":null}""")]
    [DataInlineUnit("""{"deployment":{}}""")]
    [DataInlineUnit("""{"deployment":{"resources":null,"pending_operations":null}}""")]
    [DataInlineUnit("""{"deployment":{"resources":[],"pending_operations":[]}}""")]
    [DataInlineUnit("""{"deployment":{"resources":[{"type":"pulumi:pulumi:Stack"}]}}""")]
    public async Task NewStacksCanInitializeAndProvisionWithOptionalCheckpointFields(
        DrnTestContextUnit context, string checkpoint)
    {
        var fixture = new Fixture(context);
        foreach (var role in new[] { "durable", "cluster", "bootstrap" })
            fixture.Api.StackExports[role] = checkpoint;

        await fixture.ExecuteAsync("initialize", token: TestContext.Current.CancellationToken);
        await fixture.AssertInitializedSetupMatchesAsync();
        await fixture.ExecuteAsync("durable", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("cluster", token: TestContext.Current.CancellationToken);

        var state = await fixture.StateAsync();
        Assert.Equal("initialized", state.Phase);
        Assert.Equal(7, state.VolumeIds.Count);
        Assert.Equal(1, fixture.Api.Workers);
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit("""{"deployment":{"pending_operations":[{"type":"creating"}]}}""")]
    [DataInlineUnit("""{"deployment":{"resources":[{"type":"oci:Core/volume:Volume"}]}}""")]
    [DataInlineUnit("[]")]
    [DataInlineUnit("""{"deployment":[]}""")]
    [DataInlineUnit("""{"deployment":{"resources":{}}}""")]
    [DataInlineUnit("""{"deployment":{"resources":[{}]}}""")]
    [DataInlineUnit("""{"deployment":{"resources":[{"type":7}]}}""")]
    [DataInlineUnit("""{"deployment":{"pending_operations":{}}}""")]
    public async Task InitializationRejectsOccupiedOrUncertainCheckpointsBeforeWritingCredentials(
        DrnTestContextUnit context, string checkpoint)
    {
        var fixture = new Fixture(context);
        fixture.Api.StackExports["durable"] = checkpoint;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("initialize", token: TestContext.Current.CancellationToken));

        Assert.Contains("new, empty stacks", error.Message);
        Assert.Empty(fixture.Api.Objects);
        AssertNoStackMutation(fixture.Api);
        Assert.DoesNotContain(fixture.Api.Calls, call => call.Executable is "git" or "kubectl");
    }

    [Theory]
    [DataInlineUnit("durable")]
    [DataInlineUnit("cluster")]
    public async Task PendingBootstrapCreationBlocksScopedProvisioningWithoutStrandingTheLock(
        DrnTestContextUnit context, string role)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        var stateETag = fixture.Api.Objects[OperatorTestData.StateObject].ETag;
        fixture.Api.StackExports["bootstrap"] = """{"deployment":{"pending_operations":[{"type":"creating"}]}}""";
        fixture.Api.Calls.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync(role, token: TestContext.Current.CancellationToken));

        AssertNoStackMutation(fixture.Api);
        Assert.Equal(stateETag, fixture.Api.Objects[OperatorTestData.StateObject].ETag);
        Assert.DoesNotContain(fixture.Api.Calls, call => call.Executable is "git" or "kubectl");
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit("durable")]
    [DataInlineUnit("cluster")]
    [DataInlineUnit("bootstrap")]
    public async Task EmptyCheckpointDoesNotOverrideExistingStackOutputs(DrnTestContextUnit context, string role)
    {
        var fixture = new Fixture(context);
        fixture.Api.ConfigureOutputs = (stack, output) =>
        {
            if (stack == role) output["retainedIdentity"] = "existing";
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("initialize", token: TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Api.Objects);
        AssertNoStackMutation(fixture.Api);
    }

    [Theory]
    [DataInlineUnit("up")]
    [DataInlineUnit("resume")]
    public async Task ScopedProvisioningIsRepeatableAndCombinedStartupStillActivatesAndBacksUp(
        DrnTestContextUnit context, string start)
    {
        var fixture = new Fixture(context);
        await fixture.ExecuteAsync("initialize", token: TestContext.Current.CancellationToken);
        var credentials = (await fixture.StateAsync()).CredentialHash;
        foreach (var role in new[] { "cluster", "durable", "bootstrap" })
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                fixture.Api.Calls.Clear();
                await fixture.ExecuteAsync(role, token: TestContext.Current.CancellationToken, yes: attempt == 1);
                AssertOnlyProvisionedStack(fixture.Api, role);
                var apply = Assert.Single(fixture.Api.Calls, call => call.Executable == "pulumi" && call.Arguments[2] == "up");
                Assert.Equal(attempt == 1, apply.Arguments.Contains("--yes"));
                var state = await fixture.StateAsync();
                Assert.Equal("initialized", state.Phase);
                Assert.Equal(credentials, state.CredentialHash);
                Assert.Empty(state.Backups);
                Assert.Empty(fixture.Api.Activations);
                Assert.Null(state.Postgres);
                Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
                Assert.DoesNotContain(fixture.Api.Calls, call => call.Executable == "kubectl" &&
                    call.Arguments[0] is "scale" or "annotate" or "patch" or "replace");
                if (role != "bootstrap")
                    Assert.DoesNotContain(fixture.Api.Calls, call => call.Executable is "git" or "kubectl");
                if (role == "cluster") Assert.Empty(state.VolumeIds);
                else Assert.Equal(7, state.VolumeIds.Count);
            }
        }
        Assert.Equal(2, fixture.Api.PolicyProbesCreated);
        fixture.Api.Calls.Clear();

        await fixture.ExecuteAsync(start, token: TestContext.Current.CancellationToken);

        var running = await fixture.StateAsync();
        Assert.Equal("running", running.Phase);
        Assert.Single(running.Backups);
        Assert.Equal(credentials, running.CredentialHash);
        Assert.Contains("workloads", fixture.Api.Activations);
        Assert.Equal(new[] { "example/drn-pulumi/staging-durable", "example/drn-pulumi/staging-cluster",
            "example/drn-pulumi/staging-bootstrap" }, fixture.Api.Calls
            .Where(call => call.Executable == "pulumi" && call.Arguments[2] == "up")
            .Take(3).Select(call => CommandArguments.Required(call.Arguments, "--stack")));
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit("none", "durable storage")]
    [DataInlineUnit("cluster", "durable storage")]
    [DataInlineUnit("durable", "provisioned cluster")]
    public async Task BootstrapRejectsMissingPrerequisitesWithoutApplyingAnyStack(
        DrnTestContextUnit context, string provisioned, string message)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        if (provisioned != "none") await fixture.ExecuteAsync(provisioned, token: TestContext.Current.CancellationToken);
        var stateETag = fixture.Api.Objects[OperatorTestData.StateObject].ETag;
        fixture.Api.Calls.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("bootstrap", token: TestContext.Current.CancellationToken));

        Assert.Contains(message, error.Message);
        AssertNoStackMutation(fixture.Api);
        Assert.Equal(stateETag, fixture.Api.Objects[OperatorTestData.StateObject].ETag);
        Assert.DoesNotContain(fixture.Api.Calls, call => call.Executable is "git" or "kubectl");
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit("durable", "volumeIds", "recorded durable storage")]
    [DataInlineUnit("durable", "volumeGroupId", "recorded durable storage")]
    [DataInlineUnit("durable", "storageGeneration", "recorded durable storage")]
    [DataInlineUnit("cluster", "clusterId", "provisioned cluster")]
    [DataInlineUnit("cluster", "workers", "configured workers")]
    [DataInlineUnit("cluster", "podNetwork", "native-VCN")]
    public async Task BootstrapRejectsPrerequisiteDriftBeforeMutation(
        DrnTestContextUnit context, string role, string field, string message)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("durable", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("cluster", token: TestContext.Current.CancellationToken);
        fixture.Api.ConfigureOutputs = (stack, output) =>
        {
            if (stack != role) return;
            if (field == "volumeIds") output[field]!["sample"] = "ocid1.volume.oc1..foreign";
            else if (field == "workers") output[field] = 0;
            else output[field] = "unexpected";
        };
        var stateETag = fixture.Api.Objects[OperatorTestData.StateObject].ETag;
        fixture.Api.Calls.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("bootstrap", token: TestContext.Current.CancellationToken));

        Assert.Contains(message, error.Message);
        AssertNoStackMutation(fixture.Api);
        Assert.Equal(stateETag, fixture.Api.Objects[OperatorTestData.StateObject].ETag);
        Assert.Empty(fixture.Api.Activations);
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit("durable")]
    [DataInlineUnit("cluster")]
    public async Task InfrastructureProvisioningRejectsEvenPartialBootstrapResources(DrnTestContextUnit context, string role)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        fixture.Api.PartialBootstrap = true;
        fixture.Api.Calls.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync(role, token: TestContext.Current.CancellationToken));

        Assert.Contains("Bootstrap resources already exist", error.Message);
        AssertNoStackMutation(fixture.Api);
        Assert.DoesNotContain(fixture.Api.Calls, call => call.Executable is "git" or "kubectl");
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit("durable")]
    [DataInlineUnit("cluster")]
    [DataInlineUnit("bootstrap")]
    public async Task ScopedCommandsRejectLifecyclePhasesAndPendingRecovery(DrnTestContextUnit context, string role)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        var initialized = await fixture.StateAsync();
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        var running = await fixture.StateAsync();
        foreach (var phase in new[] { "starting", "running", "quiescing", "offline", "paused", "removing", "down", "restoring" })
        {
            await fixture.SaveStateAsync(running with { Phase = phase });
            await RejectAsync();
        }
        await fixture.SaveStateAsync(initialized with { PendingVolumeBackupId = "ocid1.volumegroupbackup.oc1..pending" });
        await RejectAsync();

        async Task RejectAsync()
        {
            var stateETag = fixture.Api.Objects[OperatorTestData.StateObject].ETag;
            var activations = fixture.Api.Activations.ToArray();
            fixture.Api.Calls.Clear();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync(role, token: TestContext.Current.CancellationToken));
            Assert.Contains("Scoped provisioning requires initialized state without pending recovery", error.Message);
            AssertNoStackMutation(fixture.Api);
            Assert.Equal(stateETag, fixture.Api.Objects[OperatorTestData.StateObject].ETag);
            Assert.Equal(activations, fixture.Api.Activations);
            Assert.DoesNotContain(fixture.Api.Calls, call => call.Executable is "git" or "kubectl");
            Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
        }
    }

    [Theory]
    [DataInlineUnit("durable")]
    [DataInlineUnit("cluster")]
    [DataInlineUnit("bootstrap")]
    public async Task FailedScopedApplyRetainsLockAndNeverContinuesToAnotherStack(DrnTestContextUnit context, string role)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        if (role == "bootstrap")
        {
            await fixture.ExecuteAsync("durable", token: TestContext.Current.CancellationToken);
            await fixture.ExecuteAsync("cluster", token: TestContext.Current.CancellationToken);
        }
        fixture.Api.FailApply = role;
        fixture.Api.Calls.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync(role, token: TestContext.Current.CancellationToken));

        Assert.Equal("Simulated stack apply failure.", error.Message);
        AssertOnlyProvisionedStack(fixture.Api, role);
        Assert.Empty(fixture.Api.Activations);
        Assert.True(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
        Assert.Equal("initialized", (await fixture.StateAsync()).Phase);
    }

    private static void AssertOnlyProvisionedStack(Api api, string role)
    {
        var mutations = api.Calls.Where(call => call.Executable == "pulumi" &&
            (call.Arguments[2] != "stack" || call.Arguments[3] is not ("output" or "export"))).ToArray();
        Assert.Equal(new[] { "config", "up" }, mutations.Select(call => call.Arguments[2]));
        Assert.All(mutations, call => Assert.Equal($"example/drn-pulumi/staging-{role}",
            CommandArguments.Required(call.Arguments, "--stack")));
    }

    private static void AssertNoStackMutation(Api api) =>
        Assert.DoesNotContain(api.Calls, call => call.Executable == "pulumi" &&
            (call.Arguments[2] != "stack" || call.Arguments[3] is not ("output" or "export")));

    [Theory]
    [DataInlineUnit]
    public async Task FailedBootstrapPolicyCheckKeepsActivationClosedAndAllowsBootstrapRetry(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("durable", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("cluster", token: TestContext.Current.CancellationToken);
        fixture.Api.FailNextPolicyProbe = true;
        fixture.Api.Calls.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("bootstrap", token: TestContext.Current.CancellationToken));

        AssertOnlyProvisionedStack(fixture.Api, "bootstrap");
        Assert.True(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
        Assert.Empty(fixture.Api.Activations);
        await fixture.UnlockAsync();
        foreach (var role in new[] { "durable", "cluster" })
        {
            fixture.Api.Calls.Clear();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync(role, token: TestContext.Current.CancellationToken));
            Assert.Contains("Bootstrap resources already exist", error.Message);
            AssertNoStackMutation(fixture.Api);
            Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
        }
        fixture.Api.Calls.Clear();
        await fixture.ExecuteAsync("bootstrap", token: TestContext.Current.CancellationToken);
        AssertOnlyProvisionedStack(fixture.Api, "bootstrap");
        Assert.Empty(fixture.Api.Activations);
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit]
    public async Task LegacyStateRemainsReadableAndUpgradesOnTheNextLifecycleWrite(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        await fixture.SaveStateAsync((await fixture.StateAsync()) with { SchemaVersion = 1 });
        await fixture.ExecuteAsync("check", token: TestContext.Current.CancellationToken);
        Assert.Equal(1, (await fixture.StateAsync()).SchemaVersion);
        await fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken);
        var paused = await fixture.StateAsync();
        Assert.Equal(2, paused.SchemaVersion);
        Assert.Equal("paused", paused.Phase);
        Assert.True(paused.StorageGenerations[0].HasSameVolumes(new StorageGeneration()));
    }

    [Theory]
    [DataInlineUnit]
    public async Task ConfiguredWorkersResumeWhilePausePreservesDeployedComputeAfterPreview(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        fixture.Configuration = fixture.Configuration with { Infrastructure = fixture.Configuration.Infrastructure with
        { Workers = 3, MaxPodsPerNode = 31, NodeOcpus = 2, NodeMemoryInGbs = 16 } };
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        Assert.Equal(3, fixture.Api.Workers);
        fixture.Configuration = fixture.Configuration with { Infrastructure = fixture.Configuration.Infrastructure with
        { Workers = 2, MaxPodsPerNode = 93, NodeOcpus = 8, NodeMemoryInGbs = 48, BootVolumeSizeInGbs = 100 } };
        await fixture.ExecuteAsync("preview", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Api.Workers);
        Assert.Equal(2, fixture.Api.DeployedCompute!.NodeOcpus);
        Assert.Equal(16, fixture.Api.DeployedCompute.NodeMemoryInGbs);
        Assert.Equal(64, fixture.Api.DeployedCompute.BootVolumeSizeInGbs);
        await fixture.ExecuteAsync("resume", token: TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Api.Workers);
        Assert.Equal(8, fixture.Api.DeployedCompute.NodeOcpus);
        Assert.Equal(48, fixture.Api.DeployedCompute.NodeMemoryInGbs);
        Assert.Equal(100, fixture.Api.DeployedCompute.BootVolumeSizeInGbs);
    }

    [Theory]
    [DataInlineUnit(DeployedInputFault.MissingPool, typeof(InvalidOperationException))]
    [DataInlineUnit(DeployedInputFault.DuplicatePool, typeof(InvalidOperationException))]
    [DataInlineUnit(DeployedInputFault.MissingShape, typeof(KeyNotFoundException))]
    [DataInlineUnit(DeployedInputFault.MissingImage, typeof(KeyNotFoundException))]
    [DataInlineUnit(DeployedInputFault.WrongCpuType, typeof(InvalidOperationException))]
    [DataInlineUnit(DeployedInputFault.FractionalCpus, typeof(InvalidOperationException))]
    [DataInlineUnit(DeployedInputFault.ZeroCpus, typeof(InvalidOperationException))]
    [DataInlineUnit(DeployedInputFault.InvalidBootSize, typeof(FormatException))]
    [DataInlineUnit(DeployedInputFault.ForeignAd, typeof(InvalidOperationException))]
    [DataInlineUnit(DeployedInputFault.ForeignCompartment, typeof(InvalidOperationException))]
    public async Task InvalidDeployedInputsRejectPauseBeforeShutdownOrStateMutation(DrnTestContextUnit context, DeployedInputFault change, Type exceptionType)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        var original = await fixture.StateAsync();
        var stateETag = fixture.Api.Objects[OperatorTestData.StateObject].ETag;
        var replicas = fixture.Api.Replicas.OrderBy(pair => pair.Key).ToArray();
        var deployed = fixture.Api.DeployedCompute;
        fixture.Api.Calls.Clear();
        fixture.Api.ConfigureClusterExport = export =>
        {
            var resources = export["deployment"]!["resources"]!.AsArray();
            var inputs = resources[0]!["inputs"]!.AsObject();
            switch (change)
            {
                case DeployedInputFault.MissingPool: resources.Clear(); break;
                case DeployedInputFault.DuplicatePool: resources.Add(resources[0]!.DeepClone()); break;
                case DeployedInputFault.MissingShape: inputs.Remove("nodeShapeConfig"); break;
                case DeployedInputFault.MissingImage: inputs["nodeSourceDetails"]!.AsObject().Remove("imageId"); break;
                case DeployedInputFault.WrongCpuType: inputs["nodeShapeConfig"]!["ocpus"] = "four"; break;
                case DeployedInputFault.FractionalCpus: inputs["nodeShapeConfig"]!["ocpus"] = 1.5; break;
                case DeployedInputFault.ZeroCpus: inputs["nodeShapeConfig"]!["ocpus"] = 0; break;
                case DeployedInputFault.InvalidBootSize: inputs["nodeSourceDetails"]!["bootVolumeSizeInGbs"] = "invalid"; break;
                case DeployedInputFault.ForeignAd: inputs["nodeConfigDetails"]!["placementConfigs"]![0]!["availabilityDomain"] = "another-ad"; break;
                case DeployedInputFault.ForeignCompartment: inputs["compartmentId"] = "another-compartment"; break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
        };

        await Assert.ThrowsAsync(exceptionType, () => fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken));

        Assert.Equal(stateETag, fixture.Api.Objects[OperatorTestData.StateObject].ETag);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await fixture.StateAsync()));
        Assert.Equal(replicas, fixture.Api.Replicas.OrderBy(pair => pair.Key).ToArray());
        Assert.Equal(deployed, fixture.Api.DeployedCompute);
        Assert.Equal(1, fixture.Api.Workers);
        Assert.Contains(fixture.Api.Calls, call => call.Executable == "pulumi" && call.Arguments.Contains("export"));
        Assert.DoesNotContain(fixture.Api.Calls, call => call.Executable == "kubectl" &&
            call.Arguments.Any(arg => arg is "scale" or "annotate" or "patch" or "create" or "apply" or "replace" or "delete"));
        Assert.DoesNotContain(fixture.Api.Calls, call => call.Executable == "pulumi" &&
            call.Arguments.Any(arg => arg is "config" or "up" or "destroy"));
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit(false)]
    [DataInlineUnit(true)]
    public async Task LaterWorkerProbeFailureCleansUpAndBlocksActivation(DrnTestContextUnit context, bool cancel)
    {
        var fixture = new Fixture(context);
        fixture.Configuration = fixture.Configuration with { Infrastructure = fixture.Configuration.Infrastructure with
        { Workers = 3, MaxPodsPerNode = 31 } };
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken);
        var original = await fixture.StateAsync();
        var activations = fixture.Api.Activations.ToArray();
        fixture.Api.Calls.Clear();
        fixture.Api.ProbeNamespaces.Clear();
        fixture.Api.CompletedPolicyWorkers.Clear();
        fixture.Api.DeletedProbeNamespaces.Clear();
        fixture.Api.FailPolicyOnWorker = "worker-1";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (cancel) fixture.Api.CancelPolicyProbe = cancellation;

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ExecuteAsync("resume", token: cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("resume", token: cancellation.Token));
            Assert.Equal("Injected policy failure on worker-1.", error.Message);
        }

        Assert.Equal(new[] { "worker-0", "worker-1" }, fixture.Api.ProbeNamespaces.Select(probe => probe.Worker));
        Assert.Equal(new[] { "worker-0" }, fixture.Api.CompletedPolicyWorkers);
        Assert.Equal(fixture.Api.ProbeNamespaces.Select(probe => probe.Namespace), fixture.Api.DeletedProbeNamespaces);
        Assert.Equal(activations, fixture.Api.Activations.ToArray());
        Assert.Equal(0, fixture.Api.ControllerReplicas);
        Assert.Equal(0, fixture.Api.Replicas["deployment/sample"]);
        Assert.Equal(0, fixture.Api.Replicas["deployment/nexus"]);
        var interrupted = await fixture.StateAsync();
        Assert.Equal("starting", interrupted.Phase);
        Assert.Equal(original.CredentialHash, interrupted.CredentialHash);
        Assert.Equal(original.Backups.Length, interrupted.Backups.Length);
        Assert.True(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
        Assert.Equal(cancel, cancellation.IsCancellationRequested);
    }

    [Theory]
    [DataInlineUnit]
    public async Task NonDefaultStorageSurvivesBackupAndRestoreWithoutChangingSourceGeneration(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        fixture.Configuration = fixture.Configuration with { Infrastructure = fixture.Configuration.Infrastructure with
        { StorageGenerations = [new() { Volumes = new() { ["postgresql"] = new VolumeSettings
        { SizeInGbs = 100, Performance = "higher-performance" } } }] } };
        await fixture.ExecuteAsync("initialize", token: TestContext.Current.CancellationToken);
        await fixture.AssertInitializedSetupMatchesAsync();
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken);
        var original = await fixture.StateAsync();
        Assert.Equal(2, original.SchemaVersion);
        await fixture.ExecuteAsync("restore", token: TestContext.Current.CancellationToken, backup: original.Backups.Last().Id);
        var restored = await fixture.StateAsync();
        Assert.Equal(2, restored.StorageGenerations.Length);
        Assert.All(restored.StorageGenerations, g => Assert.Equal(100, g.Volume("postgresql").SizeInGbs));
        Assert.All(restored.StorageGenerations, g => Assert.Equal("higher-performance", g.Volume("postgresql").Performance));
        Assert.Empty(original.VolumeIds.Values.Intersect(restored.VolumeIds.Values));
        await fixture.ExecuteAsync("check", token: TestContext.Current.CancellationToken);
    }

    [Theory]
    [DataInlineUnit]
    public async Task FirstUpCompletesItsMandatoryCheckpointAndResumesWithNoOpHelmUpdate(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.ExecuteAsync("initialize", token: TestContext.Current.CancellationToken);
        var initialized = await fixture.StateAsync();
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        var running = await fixture.StateAsync();
        Assert.Equal("running", running.Phase);
        Assert.Single(running.Backups);
        Assert.Equal(initialized.CredentialHash, running.CredentialHash);
        Assert.Equal(1, fixture.Api.ControllerReplicas);
        Assert.Contains(0, fixture.Api.ControllerReplicasAtBootstrap);
        Assert.Equal(new[] { "prepare", "data", "workloads", "data", "workloads" }, fixture.Api.Activations);
        Assert.Equal(2, fixture.Api.PolicyProbesCreated);
        Assert.Equal(2, fixture.PollDelays.Count);
        Assert.All(fixture.PollDelays, delay => Assert.Equal(TimeSpan.FromSeconds(5), delay));
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit("backup")]
    [DataInlineUnit("pause")]
    [DataInlineUnit("down")]
    public async Task CompleteCheckpointAndResumePreserveCredentialsAndOriginalVolumes(DrnTestContextUnit context, string operation)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        var original = await fixture.StateAsync();
        await fixture.ExecuteAsync(operation, token: TestContext.Current.CancellationToken);
        var checkpoint = await fixture.StateAsync();
        Assert.Equal(operation == "backup" ? "running" : operation == "pause" ? "paused" : "down", checkpoint.Phase);
        Assert.Equal(2, checkpoint.Backups.Length);
        if (operation != "backup")
        {
            Assert.Equal(0, fixture.Api.Workers);
            await fixture.ExecuteAsync("resume", token: TestContext.Current.CancellationToken);
        }
        await fixture.ExecuteAsync("check", token: TestContext.Current.CancellationToken);
        var resumed = await fixture.StateAsync();
        Assert.Equal("running", resumed.Phase);
        Assert.Equal(original.CredentialGeneration, resumed.CredentialGeneration);
        Assert.Equal(original.CredentialHash, resumed.CredentialHash);
        Assert.Equal(original.VolumeIds.OrderBy(p => p.Key), resumed.VolumeIds.OrderBy(p => p.Key));
        Assert.Equal(1, fixture.Api.ControllerReplicas);
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit]
    public async Task ReleaseAdvancePreservesOldCheckpointAndRollbackRestoresItsOwnReleaseToNewVolumes(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken);
        var releaseA = fixture.Configuration;
        var original = await fixture.StateAsync();
        var source = original.Backups.Last();
        fixture.Configuration = releaseA with { Infrastructure = releaseA.Infrastructure with
        { GitOpsCommit = new string('b', 40), GitOpsTag = "refs/tags/2026.10.1-preview002" } };
        await fixture.ExecuteAsync("release", token: TestContext.Current.CancellationToken);
        var selected = await fixture.StateAsync();
        Assert.Equal("paused", selected.Phase);
        Assert.Equal(0, fixture.Api.Workers);
        Assert.Equal(fixture.Configuration.Infrastructure.GitOpsCommit, selected.GitOpsCommit);
        await fixture.ExecuteAsync("resume", token: TestContext.Current.CancellationToken);
        var releaseB = await fixture.StateAsync();
        Assert.Equal("running", releaseB.Phase);
        Assert.Equal(original.CredentialGeneration, releaseB.CredentialGeneration);
        Assert.Equal(original.CredentialHash, releaseB.CredentialHash);
        Assert.Contains(releaseB.Backups, b => b.Id == source.Id && b.GitOpsCommit == original.GitOpsCommit);
        Assert.Contains(releaseB.Backups, b => b.GitOpsCommit == releaseB.GitOpsCommit);

        await fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("restore", token: TestContext.Current.CancellationToken, backup: source.Id));
        Assert.Empty(fixture.Api.RestoredCheckpoints);
        Assert.Equal("paused", (await fixture.StateAsync()).Phase);
        fixture.Configuration = releaseA;
        await fixture.ExecuteAsync("release", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("restore", token: TestContext.Current.CancellationToken, backup: source.Id);
        await fixture.ExecuteAsync("check", token: TestContext.Current.CancellationToken);
        var restored = await fixture.StateAsync();
        Assert.Equal("running", restored.Phase);
        Assert.Equal(original.GitOpsCommit, restored.GitOpsCommit);
        Assert.Equal(original.CredentialHash, restored.CredentialHash);
        Assert.NotEqual(original.ActiveStorageGeneration, restored.ActiveStorageGeneration);
        Assert.Empty(original.VolumeIds.Values.Intersect(restored.VolumeIds.Values));
        Assert.Contains(restored.StorageGenerations, g => g.Id == original.ActiveStorageGeneration);
        Assert.Equal(new[] { source.Id }, fixture.Api.RestoredCheckpoints);
        Assert.Contains(restored.Backups, b => b.StorageGeneration == restored.ActiveStorageGeneration);
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit]
    public async Task ReleaseSelectionRejectsRunningDeploymentAndUnverifiedSnapshotWithoutChangingAuthority(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        var original = await fixture.StateAsync();
        fixture.Configuration = fixture.Configuration with { Infrastructure = fixture.Configuration.Infrastructure with
        { GitOpsCommit = new string('b', 40), GitOpsTag = "refs/tags/2026.10.1-preview002" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("release", token: TestContext.Current.CancellationToken));
        Assert.Equal(original.GitOpsCommit, (await fixture.StateAsync()).GitOpsCommit);
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
        fixture.Configuration = fixture.Configuration with { Infrastructure = fixture.Configuration.Infrastructure with
        { GitOpsCommit = original.GitOpsCommit, GitOpsTag = original.GitOpsTag } };
        await fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken);
        fixture.Configuration = fixture.Configuration with { Infrastructure = fixture.Configuration.Infrastructure with
        { GitOpsCommit = new string('b', 40), GitOpsTag = "refs/tags/2026.10.1-preview002" } };
        fixture.Api.ResolvedCommit = new string('c', 40);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("release", token: TestContext.Current.CancellationToken));
        var unchanged = await fixture.StateAsync();
        Assert.Equal("paused", unchanged.Phase);
        Assert.Equal(original.GitOpsCommit, unchanged.GitOpsCommit);
        Assert.Equal(original.CredentialHash, unchanged.CredentialHash);
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit]
    public async Task FailedReleaseStartupRetainsLockAndCanSelectCompatibleCorrectionAfterExplicitUnlock(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken);
        var original = await fixture.StateAsync();
        fixture.Configuration = fixture.Configuration with { Infrastructure = fixture.Configuration.Infrastructure with
        { GitOpsCommit = new string('b', 40), GitOpsTag = "refs/tags/2026.10.1-preview002" } };
        await fixture.ExecuteAsync("release", token: TestContext.Current.CancellationToken);
        fixture.Api.FailNextPolicyProbe = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("resume", token: TestContext.Current.CancellationToken));
        Assert.Equal("starting", (await fixture.StateAsync()).Phase);
        Assert.Equal(0, fixture.Api.ControllerReplicas);
        Assert.True(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
        await fixture.UnlockAsync();
        fixture.Configuration = fixture.Configuration with { Infrastructure = fixture.Configuration.Infrastructure with
        { GitOpsCommit = new string('c', 40), GitOpsTag = "refs/tags/2026.10.1-preview003" } };
        await fixture.ExecuteAsync("release", token: TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Api.ControllerReplicas);
        Assert.Equal(0, fixture.Api.Replicas["deployment/sample"]);
        Assert.Equal(0, fixture.Api.Replicas["deployment/nexus"]);
        await fixture.ExecuteAsync("resume", token: TestContext.Current.CancellationToken);
        var corrected = await fixture.StateAsync();
        Assert.Equal("running", corrected.Phase);
        Assert.Equal(new string('c', 40), corrected.GitOpsCommit);
        Assert.Equal(original.CredentialHash, corrected.CredentialHash);
        Assert.Contains(corrected.Backups, b => b.Id == original.Backups.Last().Id);
        Assert.Contains(corrected.Backups, b => b.GitOpsCommit == corrected.GitOpsCommit);
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit]
    public async Task PendingRecoveryAndChangedCloudMembersPreventReleaseSelection(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken);
        var original = await fixture.StateAsync();
        var checkpoint = original.Backups.Last();
        fixture.Configuration = fixture.Configuration with { Infrastructure = fixture.Configuration.Infrastructure with
        { GitOpsCommit = new string('b', 40), GitOpsTag = "refs/tags/2026.10.1-preview002" } };
        foreach (var pending in new[]
        {
            original with { OperationId = checkpoint.Native.OperationId, PendingNative = checkpoint.Native, PendingVolumeBackupId = checkpoint.Volumes.BackupId },
            original with { RestoreBackupId = checkpoint.Id, NativeRestoreStarted = true }
        })
        {
            // Model persisted interruption markers without bypassing the operator's state validation.
            await fixture.SaveStateAsync(pending);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("release", token: TestContext.Current.CancellationToken));
            Assert.Contains("without a pending backup or restore", error.Message);
            Assert.Equal(original.GitOpsCommit, (await fixture.StateAsync()).GitOpsCommit);
            Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
        }
        await fixture.SaveStateAsync(original);
        fixture.Api.ReplaceBackupMembers = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("release", token: TestContext.Current.CancellationToken));
        Assert.Equal(original.GitOpsCommit, (await fixture.StateAsync()).GitOpsCommit);
        Assert.Equal(original.CredentialHash, (await fixture.StateAsync()).CredentialHash);
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit("running")]
    [DataInlineUnit("paused")]
    [DataInlineUnit("down")]
    public async Task HealthCheckRejectsChangedCheckpointMembersWithoutStrandingTheLock(DrnTestContextUnit context, string phase)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        if (phase != "running") await fixture.ExecuteAsync(phase == "paused" ? "pause" : "down", token: TestContext.Current.CancellationToken);
        var original = await fixture.StateAsync();
        fixture.Api.ReplaceBackupMembers = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("check", token: TestContext.Current.CancellationToken));

        Assert.Contains("checkpoint member identities changed", error.Message);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await fixture.StateAsync()));
        Assert.False(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    [Theory]
    [DataInlineUnit]
    public async Task ChangedCheckpointMembersPreventPruningNativeBackupsDuringResume(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.SeedInitializedAsync(TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("up", token: TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("pause", token: TestContext.Current.CancellationToken);
        fixture.Api.ReplaceBackupMembers = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync("resume", token: TestContext.Current.CancellationToken));

        Assert.Contains("checkpoint member identities changed", error.Message);
        Assert.Equal(0, fixture.Api.NativePruneCount);
        Assert.Equal("starting", (await fixture.StateAsync()).Phase);
        Assert.True(fixture.Api.Objects.ContainsKey(OperatorTestData.LockObject));
    }

    public enum DeployedInputFault
    {
        MissingPool, DuplicatePool, MissingShape, MissingImage, WrongCpuType, FractionalCpus,
        ZeroCpus, InvalidBootSize, ForeignAd, ForeignCompartment
    }
}
