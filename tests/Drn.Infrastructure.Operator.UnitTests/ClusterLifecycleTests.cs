using System.Text;
using System.Text.Json.Nodes;
using Drn.Infrastructure.Operator.Recovery;
using Drn.Pulumi;
using Xunit;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed class ClusterLifecycleTests
{
    private const string Commit = OperatorTestData.Commit;

    [Fact]
    public async Task InvalidCheckpointIsRejectedBeforeAnyKubernetesRequest()
    {
        var credentials = OperatorTestData.Credentials();
        var api = new Api();
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Lifecycle.RestoreCredentialsAsync(credentials with { SchemaVersion = 0 }, TestContext.Current.CancellationToken));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task ConflictingSurvivorPreventsEveryCredentialWrite()
    {
        var credentials = OperatorTestData.Credentials();
        var api = new Api();
        var changed = credentials.Objects[0].DeepClone().AsObject();
        changed["data"]!["AllowedHosts"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("different-host"));
        api.Store(changed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Lifecycle.RestoreCredentialsAsync(credentials, TestContext.Current.CancellationToken));

        Assert.All(api.Calls, call => Assert.Equal("get", call.Arguments[0]));
    }

    [Fact]
    public async Task CredentialCreationUsesStdinAndVerifiesAllObjectsAfterCreation()
    {
        var credentials = OperatorTestData.Credentials();
        var api = new Api();
        await api.Lifecycle.RestoreCredentialsAsync(credentials, TestContext.Current.CancellationToken);
        Assert.Equal(14, api.Calls.Count(call => call.Arguments[0] == "create"));
        Assert.All(api.Calls, call => Assert.DoesNotContain("GRAYLOG_PASSWORD_SECRET", string.Join(' ', call.Arguments)));
        Assert.All(api.Calls.Where(call => call.Arguments[0] == "create"), call => Assert.NotNull(call.Input));
        Assert.Equal(14, api.Calls.TakeLast(14).Count(call => call.Arguments[0] == "get"));
    }

    [Fact]
    public async Task FreezeStopsControllerPodsBeforeDisablingApplicationAutomation()
    {
        var api = new Api();
        await api.Lifecycle.FreezeAsync(TestContext.Current.CancellationToken);
        var stop = api.Calls.FindIndex(call => call.Arguments.Contains("statefulset/argocd-application-controller") && call.Arguments.Contains("--replicas=0"));
        var pods = api.Calls.FindIndex(call => call.Arguments.Take(2).SequenceEqual(["get", "pods"]));
        var patch = api.Calls.FindIndex(call => call.Arguments[0] == "patch");
        stop.Should().BeGreaterThanOrEqualTo(0, "the controller must be stopped");
        pods.Should().BeGreaterThan(stop, "controller pods must be observed after scale-down");
        patch.Should().BeGreaterThan(pods, "automation must be disabled only after pods disappear");
    }

    [Fact]
    public async Task SyncThatAppearsWhileFreezingLeavesControllerStoppedAndFails()
    {
        var api = new Api { SyncAppearsAfterFreeze = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Lifecycle.FreezeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, api.ControllerReplicas);
        Assert.DoesNotContain(api.Calls, call => call.Arguments[0] == "patch");
    }

    [Fact]
    public async Task DataStageStopsExistingWritersBeforeRestartingArgo()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var api = new Api { CancelAfterChildSync = cancellation };
        // The root reports its old Healthy state immediately; acceptance must still wait for both child full syncs.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.Lifecycle.ActivateAsync("data", cancellation.Token));
        var restart = api.Calls.FindIndex(call => call.Arguments.Contains("statefulset/argocd-application-controller") && call.Arguments.Contains("--replicas=1"));
        foreach (var name in new[] { "sample", "nexus" })
        {
            var stop = api.Calls.FindIndex(call => call.Arguments.Contains("deployment/" + name) && call.Arguments.Contains("--replicas=0"));
            Assert.True(stop >= 0 && stop < restart);
        }
        Assert.NotNull(api.SubmittedOperation);
        Assert.Equal("drn-operation", api.SubmittedOperation!["info"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("data", api.Root["spec"]!["source"]!["helm"]!["parameters"]!.AsArray()
            .Single(item => item!["name"]!.GetValue<string>() == "activationStage")!["value"]!.GetValue<string>());
        Assert.Equal(2, api.ChildSyncs.Count);
        Assert.All(api.ChildSyncs.Values, operation => Assert.Equal(Commit, operation["sync"]!["revision"]!.GetValue<string>()));
    }

    [Fact]
    public async Task DataStageNeverSyncsAWriterWithItsOldOneReplicaSource()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var api = new Api { WriterSourceReplicas = 1, CancelAfterWriterRead = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.Lifecycle.ActivateAsync("data", cancellation.Token));
        Assert.Empty(api.ChildSyncs);
    }

    [Fact]
    public async Task DataStageRechecksBothChildSyncsAfterThePollDelay()
    {
        var api = new Api();
        var delays = new List<TimeSpan>();
        var lifecycle = new ClusterLifecycle(api, new Dictionary<string, string>(), Commit, (delay, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(2, api.ChildSyncs.Count);
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await lifecycle.ActivateAsync("data", TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(delays));
        foreach (var name in new[] { "sample", "nexus" })
        {
            Assert.Equal(2, api.Calls.Count(call => call.Arguments.Take(3).SequenceEqual(["get", "application", name])));
            Assert.Single(api.Calls, call => call.Arguments.Take(3).SequenceEqual(["patch", "application", name]));
        }
    }

    [Fact]
    public async Task CancellationDuringClusterPollDelayStopsFurtherReads()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var api = new Api();
        var pendingDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new ClusterLifecycle(api, new Dictionary<string, string>(), Commit, (delay, token) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(5), delay);
            Assert.Equal(cancellation.Token, token);
            delayEntered.SetResult();
            return pendingDelay.Task.WaitAsync(token);
        });
        var activation = lifecycle.ActivateAsync("data", cancellation.Token);
        await Task.WhenAny(delayEntered.Task, activation).WaitAsync(TestContext.Current.CancellationToken);
        if (activation.IsCompleted) await activation;
        Assert.True(delayEntered.Task.IsCompletedSuccessfully);
        var wasWaiting = !activation.IsCompleted;
        var callsBeforeCancellation = api.Calls.Count;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);

        Assert.True(wasWaiting);
        Assert.Equal(2, api.ChildSyncs.Count);
        Assert.Equal(callsBeforeCancellation, api.Calls.Count);
    }

    [Fact]
    public async Task StaleSuccessfulOperationCannotSatisfyANewStage()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var api = new Api { StaleOperation = true, CancelAfterOperationRead = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.Lifecycle.ActivateAsync("data", cancellation.Token));
        Assert.NotNull(api.SubmittedOperation);
    }

    [Fact]
    public async Task ZeroReplicaStatusDoesNotAcceptATerminatingWriterPod()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var api = new Api { TerminatingWriter = true, CancelAfterPodRead = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.Lifecycle.ScaleAsync("drn-project", "deployment", "sample", 0, cancellation.Token));
    }

    [Fact]
    public async Task PostgresRestorePausesBeforeCreatingItsClaimAndRestoresTheNewOwnerBeforeStatus()
    {
        var saved = SavedPostgres();
        var original = saved.Cluster.ToJsonString();
        var api = new Api();

        await api.Lifecycle.RestorePostgresAsync(saved, TestContext.Current.CancellationToken);

        var writes = api.Calls.Where(call => call.Arguments[0] is "create" or "replace").ToArray();
        Assert.Equal(3, writes.Length);
        var cluster = JsonNode.Parse(writes[0].Input!)!;
        Assert.Equal("disabled", cluster["metadata"]!["annotations"]!["cnpg.io/reconciliationLoop"]!.GetValue<string>());
        Assert.Null(cluster["metadata"]!["annotations"]!["cnpg.io/hibernation"]);
        var claim = JsonNode.Parse(writes[1].Input!)!;
        Assert.Equal("new-cluster-uid", claim["metadata"]!["ownerReferences"]![0]!["uid"]!.GetValue<string>());
        Assert.Equal("replace", writes[2].Arguments[0]);
        Assert.Contains("/apis/postgresql.cnpg.io/v1/namespaces/drn-project/clusters/postgresql/status", writes[2].Arguments);
        Assert.Equal(original, saved.Cluster.ToJsonString());
    }

    [Fact]
    public async Task InterruptedPostgresRestoreCanContinueFromItsPausedCluster()
    {
        var saved = SavedPostgres();
        var api = new Api();
        var existing = saved.Cluster.DeepClone().AsObject();
        existing["metadata"]!["uid"] = "new-cluster-uid";
        existing["metadata"]!["annotations"]!["cnpg.io/reconciliationLoop"] = "disabled";
        api.Store(existing);

        await api.Lifecycle.RestorePostgresAsync(saved, TestContext.Current.CancellationToken);

        Assert.Single(api.Calls, call => call.Arguments[0] == "create");
        Assert.Equal(KubernetesKinds.PersistentVolumeClaim, JsonNode.Parse(api.Calls.Single(call => call.Arguments[0] == "create").Input!)!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task ForeignPostgresOwnerFailsWithoutRewritingTheLivePrimary()
    {
        var saved = SavedPostgres();
        var api = new Api();
        var cluster = saved.Cluster.DeepClone().AsObject();
        cluster["metadata"]!["uid"] = "current-owner";
        cluster["status"] = saved.Status.DeepClone();
        var claim = saved.Claim.DeepClone().AsObject();
        claim["metadata"]!["ownerReferences"] = new JsonArray(new JsonObject
        {
            ["apiVersion"] = "postgresql.cnpg.io/v1", ["kind"] = "Cluster", ["name"] = "postgresql", ["uid"] = "old-owner"
        });
        api.Store(cluster);
        api.Store(claim);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Lifecycle.RestorePostgresAsync(saved, TestContext.Current.CancellationToken));

        Assert.All(api.Calls, call => Assert.Equal("get", call.Arguments[0]));
    }

    [Fact]
    public async Task UnsupportedPostgresMetadataIsRejectedBeforeCreatingResources()
    {
        var saved = SavedPostgres();
        saved.Claim["metadata"]!["annotations"]!["cnpg.io/nodeSerial"] = "2";
        var api = new Api();
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Lifecycle.RestorePostgresAsync(saved, TestContext.Current.CancellationToken));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task MissingPostgresCrdDoesNotQueryItsUnavailableResource()
    {
        var api = new Api();
        Assert.False(await api.Lifecycle.HasPostgresAsync(TestContext.Current.CancellationToken));
        Assert.Single(api.Calls);
        Assert.Equal("crd", api.Calls[0].Arguments[1]);
    }

    [Fact]
    public async Task NativeRepairStopsEveryWriterBeforeResumingOnlyDatabases()
    {
        var api = new Api();
        api.Store(SavedPostgres().Cluster);

        await api.Lifecycle.StartDatabasesAsync(TestContext.Current.CancellationToken);

        var start = api.Calls.FindIndex(call => call.Arguments[0] == "patch" && call.Arguments[1] == "cluster.postgresql.cnpg.io");
        foreach (var writer in new[] { "deployment/sample", "deployment/nexus", "statefulset/graylog", "statefulset/graylog-datanode" })
        {
            var stop = api.Calls.FindIndex(call => call.Arguments.Contains(writer) && call.Arguments.Contains("--replicas=0"));
            Assert.True(stop >= 0 && stop < start);
            Assert.DoesNotContain(api.Calls, call => call.Arguments.Contains(writer) && call.Arguments.Contains("--replicas=1"));
        }
        Assert.Contains(api.Calls, call => call.Arguments.Take(3).SequenceEqual(["rollout", "status", "statefulset/mongodb"]));
        Assert.Contains(api.Calls, call => call.Arguments[0] == "wait" && call.Arguments.Contains("--for=condition=Ready"));
    }

    [Fact]
    public async Task NativeRepairRemovesBothDatabasePodsBeforeRestartingEngines()
    {
        var api = new Api();
        api.Store(SavedPostgres().Cluster);

        await api.Lifecycle.StartDatabasesAsync(TestContext.Current.CancellationToken);

        var hibernate = api.Calls.FindIndex(call => call.Arguments[0] == "annotate" && call.Arguments.Contains("cnpg.io/hibernation=on"));
        var hibernated = api.Calls.FindIndex(call => call.Arguments[0] == "wait" && call.Arguments.Contains("--for=condition=cnpg.io/hibernation"));
        var absentPrimary = api.Calls.FindIndex(call => call.Arguments.Take(3).SequenceEqual(["get", "pod", "postgresql-1"]));
        var stopOperator = api.Calls.FindIndex(call => call.Arguments.Contains("deployment/mongodb-kubernetes-operator") && call.Arguments.Contains("--replicas=0"));
        var stopMongo = api.Calls.FindIndex(call => call.Arguments.Contains("statefulset/mongodb") && call.Arguments.Contains("--replicas=0"));
        stopMongo.Should().BeGreaterThanOrEqualTo(0, "MongoDB must be scaled down before observing pod termination");
        var absentMongo = api.Calls.FindIndex(stopMongo, call => call.Arguments.Take(2).SequenceEqual(["get", "pods"]));
        var resumePostgres = api.Calls.FindIndex(call => call.Arguments[0] == "patch" && call.Arguments[1] == "cluster.postgresql.cnpg.io");
        var startOperator = api.Calls.FindIndex(call => call.Arguments.Contains("deployment/mongodb-kubernetes-operator") && call.Arguments.Contains("--replicas=1"));
        var startMongo = api.Calls.FindIndex(call => call.Arguments.Contains("statefulset/mongodb") && call.Arguments.Contains("--replicas=1"));
        hibernate.Should().BeGreaterThanOrEqualTo(0, "PostgreSQL hibernation must be requested");
        hibernated.Should().BeGreaterThan(hibernate, "PostgreSQL must report hibernation after it is requested");
        absentPrimary.Should().BeGreaterThan(hibernated, "the PostgreSQL primary must disappear after hibernation");
        stopOperator.Should().BeGreaterThan(absentPrimary, "the PostgreSQL primary must disappear before stopping the MongoDB operator");
        stopMongo.Should().BeGreaterThan(stopOperator, "the MongoDB operator must stop before its database");
        absentMongo.Should().BeGreaterThan(stopMongo, "MongoDB pods must be observed after scale-down");
        resumePostgres.Should().BeGreaterThan(absentMongo, "both database pods must disappear before PostgreSQL resumes");
        startOperator.Should().BeGreaterThan(resumePostgres, "the MongoDB operator must restart after PostgreSQL resumes");
        startMongo.Should().BeGreaterThan(startOperator, "the MongoDB operator must restart before its database");
    }

    [Fact]
    public async Task ClaimRemovalRequiresACompleteInventoryUnlessResumingRecordedRemoval()
    {
        var api = new Api();
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Lifecycle.RemoveClaimsAsync(Volumes(), TestContext.Current.CancellationToken));
        Assert.All(api.Calls, call => Assert.Equal("get", call.Arguments[0]));
        await api.Lifecycle.RemoveClaimsAsync(Volumes(), TestContext.Current.CancellationToken, allowPartial: true);
        Assert.DoesNotContain(api.Calls, call => call.Arguments[0] == "delete");
    }

    [Fact]
    public async Task UnknownClaimBlocksRemovalOfEveryApprovedClaim()
    {
        var api = new Api();
        api.StoreRetainedStorage();
        var extra = RetainedClaim(StorageClaims.All[0]);
        extra["metadata"]!["name"] = "unrecorded-data";
        api.Store(extra);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Lifecycle.RemoveClaimsAsync(Volumes(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(api.Calls, call => call.Arguments[0] == "delete");
    }

    [Fact]
    public async Task ForeignBindingUidBlocksRemovalOfEveryClaim()
    {
        var api = new Api();
        api.StoreRetainedStorage();
        var volume = RetainedVolume(StorageClaims.All[0]);
        volume["spec"]!["claimRef"]!["uid"] = "different-claim";
        api.Store(volume);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Lifecycle.RemoveClaimsAsync(Volumes(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(api.Calls, call => call.Arguments[0] == "delete");
    }

    [Theory]
    [DataInlineUnit("Running", "sample-data")]
    [DataInlineUnit("Pending", "sample-data")]
    [DataInlineUnit("Succeeded", "unrecorded-data")]
    public async Task ActiveOrUnknownConsumersBlockEveryDeletion(string phase, string claim)
    {
        var api = new Api();
        api.StoreRetainedStorage();
        api.Store(Consumer(phase, claim));

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Lifecycle.RemoveClaimsAsync(Volumes(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(api.Calls, call => call.Arguments[0] == "delete");
    }

    [Fact]
    public async Task CompletedConsumersAreRemovedBeforeClaimsWithUidPreconditions()
    {
        var api = new Api();
        api.StoreRetainedStorage();
        api.Store(Consumer("Succeeded", "sample-data"));

        await api.Lifecycle.RemoveClaimsAsync(Volumes(), TestContext.Current.CancellationToken);

        var deletions = api.Calls.Where(call => call.Arguments[0] == "delete").ToArray();
        Assert.Equal(8, deletions.Length);
        Assert.Equal("/api/v1/namespaces/drn-project/pods/completed-backup", deletions[0].Arguments[2]);
        Assert.All(deletions.Skip(1), call => Assert.Contains("/persistentvolumeclaims/", call.Arguments[2]));
        Assert.All(deletions, call => Assert.False(string.IsNullOrEmpty(JsonNode.Parse(call.Input!)!["preconditions"]!["uid"]!.GetValue<string>())));
    }

    [Fact]
    public async Task PartialRemovalStillRejectsAClaimWhoseVolumeIsMissing()
    {
        var api = new Api();
        api.Store(RetainedClaim(StorageClaims.All[0]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Lifecycle.RemoveClaimsAsync(Volumes(), TestContext.Current.CancellationToken, allowPartial: true));
        Assert.DoesNotContain(api.Calls, call => call.Arguments[0] == "delete");
    }

    private static Dictionary<string, string> Volumes() => StorageClaims.All.ToDictionary(claim => claim.Key, claim => "volume-" + claim.Key);

    private static JsonObject RetainedClaim(StorageClaim claim) => new()
    {
        ["apiVersion"] = "v1", ["kind"] = KubernetesKinds.PersistentVolumeClaim,
        ["metadata"] = new JsonObject { ["namespace"] = claim.Namespace, ["name"] = claim.ClaimName, ["uid"] = "uid-" + claim.Key },
        ["spec"] = new JsonObject { ["volumeName"] = "drn-staging-" + claim.Key, ["storageClassName"] = StorageClaims.StorageClass }
    };

    private static JsonObject RetainedVolume(StorageClaim claim) => new()
    {
        ["apiVersion"] = "v1", ["kind"] = KubernetesKinds.PersistentVolume, ["metadata"] = new JsonObject { ["name"] = "drn-staging-" + claim.Key },
        ["spec"] = new JsonObject
        {
            ["storageClassName"] = StorageClaims.StorageClass, ["volumeMode"] = "Filesystem", ["persistentVolumeReclaimPolicy"] = "Retain",
            ["csi"] = new JsonObject { ["driver"] = "blockvolume.csi.oraclecloud.com", ["volumeHandle"] = "volume-" + claim.Key },
            ["claimRef"] = new JsonObject { ["namespace"] = claim.Namespace, ["name"] = claim.ClaimName, ["uid"] = "uid-" + claim.Key }
        }
    };

    private static JsonObject Consumer(string phase, string claim) => new()
    {
        ["apiVersion"] = "v1", ["kind"] = KubernetesKinds.Pod,
        ["metadata"] = new JsonObject { ["namespace"] = "drn-project", ["name"] = "completed-backup", ["uid"] = "consumer-uid" },
        ["status"] = new JsonObject { ["phase"] = phase },
        ["spec"] = new JsonObject { ["volumes"] = new JsonArray(new JsonObject { ["persistentVolumeClaim"] = new JsonObject { ["claimName"] = claim } }) }
    };

    private static PostgresRecovery SavedPostgres() => new(
        JsonNode.Parse("""
        {"apiVersion":"postgresql.cnpg.io/v1","kind":"Cluster","metadata":{"name":"postgresql","namespace":"drn-project","annotations":{"cnpg.io/hibernation":"on"}},"spec":{"instances":1,"storage":{"storageClass":"drn-retained"},"bootstrap":{"initdb":{"secret":{"name":"postgresql-app"}}},"certificates":{"serverCASecret":"postgresql-ca","clientCASecret":"postgresql-ca"}}}
        """)!.AsObject(),
        JsonNode.Parse("""
        {"apiVersion":"v1","kind":"PersistentVolumeClaim","metadata":{"name":"postgresql-1","namespace":"drn-project","labels":{"cnpg.io/cluster":"postgresql","cnpg.io/instanceName":"postgresql-1","cnpg.io/pvcRole":"PG_DATA"},"annotations":{"cnpg.io/nodeSerial":"1","cnpg.io/pvcStatus":"ready"}},"spec":{"volumeName":"drn-staging-postgresql","storageClassName":"drn-retained"}}
        """)!.AsObject(),
        new JsonObject { ["currentPrimary"] = "postgresql-1", ["targetPrimary"] = "postgresql-1", ["latestGeneratedNode"] = 1 });

    private sealed record Call(string[] Arguments, string? Input);

    private sealed class Api : ICommandRunner
    {
        public List<Call> Calls { get; } = [];
        private readonly Dictionary<string, JsonObject> _objects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _replicas = new(StringComparer.Ordinal);
        public int ControllerReplicas { get; private set; } = 1;
        public bool SyncAppearsAfterFreeze { get; init; }
        public bool StaleOperation { get; init; }
        public bool TerminatingWriter { get; init; }
        public int WriterSourceReplicas { get; init; }
        public CancellationTokenSource? CancelAfterOperationRead { get; init; }
        public CancellationTokenSource? CancelAfterPodRead { get; init; }
        public CancellationTokenSource? CancelAfterChildSync { get; init; }
        public CancellationTokenSource? CancelAfterWriterRead { get; init; }
        public Dictionary<string, JsonObject> ChildSyncs { get; } = new(StringComparer.Ordinal);
        public JsonObject? SubmittedOperation { get; private set; }
        public JsonObject Root { get; } = JsonNode.Parse("""
            {"apiVersion":"argoproj.io/v1alpha1","kind":"Application","metadata":{"name":"drn-project","namespace":"argocd"},"spec":{"project":"drn-project-bootstrap","source":{"targetRevision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","helm":{"parameters":[{"name":"infrastructureProfile","value":"oci-retained"},{"name":"activationStage","value":"prepare"}]}}}}
            """)!.AsObject();
        public ClusterLifecycle Lifecycle => new(this, new Dictionary<string, string>(), Commit);

        public void Store(JsonObject item) => _objects[Key(item["kind"]!.GetValue<string>(), item["metadata"]!["name"]!.GetValue<string>(), item["metadata"]!["namespace"]?.GetValue<string>() ?? "")] = item;

        public void StoreRetainedStorage()
        {
            foreach (var claim in StorageClaims.All)
            {
                Store(RetainedClaim(claim));
                Store(RetainedVolume(claim));
            }
        }

        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
            bool displayOutput, CancellationToken cancellationToken, string? standardInput = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("kubectl", executable);
            Assert.False(displayOutput);
            arguments[0].Should().BeOneOf("--request-timeout=30s", "--request-timeout=30m");
            var args = arguments.Skip(1).ToArray();
            Calls.Add(new Call(args, standardInput));
            var result = "";
            if (args[0] == "get")
            {
                var ns = args.Contains("-n") ? CommandArguments.Required(args, "-n") : "";
                if (args[1] == "applications")
                {
                    var root = Root.DeepClone().AsObject();
                    if (SyncAppearsAfterFreeze && ControllerReplicas == 0) root["operation"] = new JsonObject();
                    result = List(root);
                }
                else if (args[1] == "pods")
                {
                    result = TerminatingWriter && ns == "drn-project"
                        ? "{\"items\":[{\"metadata\":{\"labels\":{\"app\":\"sample\"},\"deletionTimestamp\":\"2026-10-19T00:00:00Z\"},\"status\":{\"phase\":\"Running\"}}]}"
                        : List(_objects.Values.Where(item => item["kind"]!.GetValue<string>() == KubernetesKinds.Pod &&
                            (ns.Length == 0 || item["metadata"]!["namespace"]!.GetValue<string>() == ns)).ToArray());
                    CancelAfterPodRead?.Cancel();
                }
                else if (args[1] == "pvc" && args.Contains("-A"))
                    result = List(_objects.Values.Where(item => item["kind"]!.GetValue<string>() == KubernetesKinds.PersistentVolumeClaim).ToArray());
                else if (args[1] == "deployments" && ns == "graylog")
                    result = List(new JsonObject { ["metadata"] = new JsonObject { ["name"] = "mongodb-kubernetes-operator" } });
                else if (args[1] == "application" && args[2] == "drn-project")
                {
                    var root = Root.DeepClone().AsObject();
                    if (SubmittedOperation is not null)
                    {
                        var operation = SubmittedOperation.DeepClone().AsObject();
                        if (StaleOperation) operation["info"] = new JsonArray(new JsonObject { ["name"] = "drn-operation", ["value"] = "old-operation" });
                        root["status"] = new JsonObject
                        {
                            ["health"] = new JsonObject { ["status"] = "Healthy" }, ["sync"] = new JsonObject { ["status"] = "Synced" },
                            ["operationState"] = new JsonObject { ["phase"] = "Succeeded", ["operation"] = operation, ["syncResult"] = new JsonObject { ["revision"] = Commit } }
                        };
                        CancelAfterOperationRead?.Cancel();
                    }
                    result = root.ToJsonString();
                }
                else if (args[1] == "application" && args[2] is "sample" or "nexus")
                {
                    var child = new JsonObject
                    {
                        ["spec"] = new JsonObject { ["source"] = new JsonObject
                        {
                            ["targetRevision"] = Commit,
                            ["kustomize"] = new JsonObject { ["patches"] = new JsonArray(new JsonObject
                            {
                                ["target"] = new JsonObject { ["kind"] = "Deployment", ["name"] = args[2] },
                                ["patch"] = $"- op: add\n  path: /spec/replicas\n  value: {WriterSourceReplicas}"
                            }) }
                        } }
                    };
                    if (ChildSyncs.TryGetValue(args[2], out var operation))
                        child["status"] = new JsonObject
                        {
                            ["health"] = new JsonObject { ["status"] = "Healthy" },
                            ["sync"] = new JsonObject { ["status"] = "Synced" },
                            ["operationState"] = new JsonObject
                            {
                                ["phase"] = "Succeeded", ["operation"] = operation.DeepClone(),
                                ["syncResult"] = new JsonObject { ["revision"] = Commit }
                            }
                        };
                    result = child.ToJsonString();
                    CancelAfterWriterRead?.Cancel();
                }
                else if (_objects.TryGetValue(Key(args[1], args[2], ns), out var value)) result = value.ToJsonString();
                else if (args[1] == "statefulset" && args[2] == "argocd-application-controller") result = Controller(args[2], ControllerReplicas);
                else if (args[1] == "deployment" && args[2] is "sample" or "nexus") result = Controller(args[2], 0);
                else if (args[1] is "deployment" or "statefulset") result = Controller(args[2], _replicas.GetValueOrDefault(args[1] + "/" + args[2]));
                else
                {
                    args[1].ToLowerInvariant().Should().BeOneOf("secret", "configmap", "cluster.postgresql.cnpg.io", "pvc", "pv", "pod", "crd");
                    args.Should().Contain(CommandArguments.KubectlIgnoreNotFound, "only an optional read can report a missing resource");
                }
            }
            else if (args[0] == "scale" && args[1] == "statefulset/argocd-application-controller")
                ControllerReplicas = args.Contains("--replicas=0") ? 0 : 1;
            else if (args[0] == "scale")
                _replicas[args[1]] = args.Contains("--replicas=0") ? 0 : 1;
            else if (args[0] == "patch" && args[1] == "application" && args[2] == "drn-project")
            {
                var patch = JsonNode.Parse(standardInput!)!;
                if (patch["spec"]?["source"]?["helm"]?["parameters"] is JsonArray parameters)
                    Root["spec"]!["source"]!["helm"]!["parameters"] = parameters.DeepClone();
                if (patch["operation"] is JsonObject operation) SubmittedOperation = operation.DeepClone().AsObject();
            }
            else if (args[0] == "patch" && args[1] == "application" && args[2] is "sample" or "nexus")
            {
                if (JsonNode.Parse(standardInput!)!["operation"] is JsonObject operation)
                {
                    ChildSyncs[args[2]] = operation.DeepClone().AsObject();
                    if (ChildSyncs.Count == 2) CancelAfterChildSync?.Cancel();
                }
            }
            else if (args[0] == "apply")
            {
                CommandArguments.Prefix(args, "apply", "--server-side", "--field-manager=drn-recovery", "-f", "-");
                var item = JsonNode.Parse(standardInput!)!.AsObject();
                Assert.Equal("Namespace", item["kind"]!.GetValue<string>());
                Store(item);
            }
            else if (args[0] == "create")
            {
                CommandArguments.Prefix(args, "create", "-f", "-");
                var item = JsonNode.Parse(standardInput!)!.AsObject();
                if (item["kind"]!.GetValue<string>() == "Cluster") item["metadata"]!["uid"] = "new-cluster-uid";
                Store(item);
            }
            else if (args[0] == "delete")
            {
                Assert.Equal("--raw", args[1]);
                var path = args[2].Split('/');
                var key = Key(path[5], path[6], path[4]);
                Assert.Equal(_objects[key]["metadata"]!["uid"]!.GetValue<string>(), JsonNode.Parse(standardInput!)!["preconditions"]!["uid"]!.GetValue<string>());
                Assert.True(_objects.Remove(key));
            }
            else if (args[0] is "wait" or "rollout" or "annotate")
            {
                args.Length.Should().BeGreaterThan(2, "the lifecycle command requires a target");
            }
            else if (args[0] == "patch" && args[1] is "cluster.postgresql.cnpg.io" or "appproject")
            {
                Assert.NotNull(standardInput);
                _ = JsonNode.Parse(standardInput)!.AsObject();
            }
            else if (args[0] == "replace")
            {
                CommandArguments.Prefix(args, "replace", "--raw", "/apis/postgresql.cnpg.io/v1/namespaces/drn-project/clusters/postgresql/status", "-f", "-");
                Assert.NotNull(standardInput);
            }
            else throw new Xunit.Sdk.XunitException($"Unexpected kubectl command: {string.Join(' ', args)}");
            return Task.FromResult(result);
        }

        private static string Key(string kind, string name, string ns) => (kind.ToLowerInvariant() switch
        { "cluster.postgresql.cnpg.io" => "cluster", "pvc" or "persistentvolumeclaims" => "persistentvolumeclaim", "pv" => "persistentvolume", "pods" => "pod", var other => other }) + "/" + ns + "/" + name;
        private static string List(params JsonObject[] items) => KubernetesJson.List(items);
        private static string Controller(string name, int replicas) =>
            KubernetesJson.Controller(name, name == "argocd-application-controller" ? KubernetesKinds.StatefulSet : KubernetesKinds.Deployment, replicas).ToJsonString();
    }
}
