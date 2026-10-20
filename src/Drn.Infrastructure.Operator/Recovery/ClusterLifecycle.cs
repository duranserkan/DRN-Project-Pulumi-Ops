using System.Text.Json;
using System.Text.Json.Nodes;
using Drn.Pulumi;

namespace Drn.Infrastructure.Operator.Recovery;

/// <summary>Kubernetes recovery steps; the caller owns and checkpoints the cross-host operation.</summary>
public sealed class ClusterLifecycle(ICommandRunner runner, IReadOnlyDictionary<string, string> environment,
    string gitOpsCommit, Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delayAsync ?? Task.Delay;

    public async Task RestoreCredentialsAsync(CredentialCheckpoint checkpoint, CancellationToken token)
    {
        checkpoint.Validate(DateTimeOffset.UtcNow);
        var existingObjects = new List<JsonObject?>();
        foreach (var item in checkpoint.Objects)
            existingObjects.Add(await GetOptionalAsync(Text(item, "kind"), Text(item["metadata"], "name"),
                Text(item["metadata"], "namespace"), token));
        // Validate every survivor before creating any missing identity or namespace.
        checkpoint.ValidateLive(existingObjects.Select((item, index) => item ?? checkpoint.Objects[index]).ToArray(), DateTimeOffset.UtcNow);
        foreach (var ns in checkpoint.Objects.Select(o => Text(o["metadata"], "namespace")).Distinct(StringComparer.Ordinal))
            await ApplyAsync(new JsonObject { ["apiVersion"] = "v1", ["kind"] = "Namespace",
                ["metadata"] = new JsonObject { ["name"] = ns } }, token);
        for (var index = 0; index < checkpoint.Objects.Length; index++)
        {
            if (existingObjects[index] is null)
                await RunAsync(["create", "-f", "-"], token, checkpoint.Objects[index].ToJsonString());
        }
        await VerifyCredentialsAsync(checkpoint, token);
    }

    public async Task VerifyCredentialsAsync(CredentialCheckpoint checkpoint, CancellationToken token)
    {
        var objects = new List<JsonObject>();
        foreach (var item in checkpoint.Objects)
            objects.Add(await GetAsync(Text(item, "kind"), Text(item["metadata"], "name"),
                Text(item["metadata"], "namespace"), token));
        checkpoint.ValidateLive(objects.ToArray(), DateTimeOffset.UtcNow);
    }

    public async Task FreezeAsync(CancellationToken token)
    {
        await WaitAsync(async () => !Items(await RunAsync(["get", "applications", "-n", "argocd", "-o", "json"], token))
            .Any(HasActiveOperation), "Argo CD has an active sync; wait for it to finish before retrying.", token);
        if (await GetOptionalAsync("statefulset", "argocd-application-controller", "argocd", token) is null)
        {
            if (Items(await RunAsync(["get", "applications", "-n", "argocd", "-o", "json"], token)).Length != 0)
                throw new InvalidOperationException("Argo CD Applications remain but their controller is absent. Finish the recorded removal before resuming.");
            return;
        }
        await ScaleAsync("argocd", "statefulset", "argocd-application-controller", 0, token);
        var apps = Items(await RunAsync(["get", "applications", "-n", "argocd", "-o", "json"], token));
        if (apps.Any(HasActiveOperation))
            throw new InvalidOperationException("An Argo CD sync began while freezing. Inspect it before resuming; the controller remains stopped.");
        foreach (var app in apps)
            await PatchAsync("application", Text(app["metadata"], "name"), "argocd",
                new { spec = new { syncPolicy = new { automated = (object?)null } } }, token);
    }

    public async Task ActivateAsync(string stage, CancellationToken token)
    {
        if (stage is not ("prepare" or "data" or "workloads")) throw new ArgumentException("Unknown activation stage.");
        var root = await GetAsync("application", "drn-project", "argocd", token);
        if (Text(root["spec"]?["source"], "targetRevision") != gitOpsCommit ||
            Text(root["spec"], "project") != "drn-project-bootstrap")
            throw new InvalidOperationException("The root Application does not match the recorded release and project.");
        var parameters = root["spec"]?["source"]?["helm"]?["parameters"]?.DeepClone().AsArray()
            ?? throw new InvalidOperationException("The root Application is missing its release parameters.");
        if (parameters.Count(item => item?["name"]?.GetValue<string>() == "activationStage") != 1 ||
            parameters.Count(item => item?["name"]?.GetValue<string>() == "infrastructureProfile" && item?["value"]?.GetValue<string>() == "oci-retained") != 1)
            throw new InvalidOperationException("The root Application is not configured for staged OCI activation.");
        var target = parameters.Single(item => item?["name"]?.GetValue<string>() == "activationStage")!;
        target["value"] = stage;
        await FreezeAsync(token);
        if (stage != "workloads")
            foreach (var name in new[] { "sample", "nexus" })
                if (await GetOptionalAsync("deployment", name, "drn-project", token) is not null)
                    await ScaleAsync("drn-project", "deployment", name, 0, token);
        await PatchAsync("application", "drn-project", "argocd", new
        {
            spec = new { source = new { helm = new { parameters } }, syncPolicy = new { automated = (object?)null } }
        }, token);
        await PatchAsync("appproject", "drn-project-bootstrap", "argocd",
            new { spec = new { syncWindows = Array.Empty<object>() } }, token);
        await ScaleAsync("argocd", "statefulset", "argocd-application-controller", 1, token);
        var operationId = Guid.NewGuid().ToString("N");
        // A full sync reruns credential hooks and waits on the release's child-Application health contract.
        await PatchAsync("application", "drn-project", "argocd", new
        {
            operation = new { initiatedBy = new { username = "drn-infra" }, info = new[] { new { name = "drn-operation", value = operationId } }, sync = new
            { revision = gitOpsCommit, prune = false, syncOptions = new[] { "ApplyOutOfSyncOnly=false" } } }
        }, token);
        var dataWriterSyncs = new HashSet<string>(StringComparer.Ordinal);
        await WaitAsync(async () =>
        {
            var current = await GetAsync("application", "drn-project", "argocd", token);
            var operation = current["status"]?["operationState"];
            if (operation?["operation"]?["info"] is not JsonArray info ||
                !info.Any(item => item?["name"]?.GetValue<string>() == "drn-operation" && item?["value"]?.GetValue<string>() == operationId))
                return false;
            var phase = operation?["phase"]?.GetValue<string>();
            if (phase is "Failed" or "Error") throw new InvalidOperationException($"GitOps {stage} sync failed. Inspect Argo CD; recovery remains locked.");
            if (stage == "data" && !await SyncStoppedWritersAsync(operationId, dataWriterSyncs, token)) return false;
            return current["operation"] is null && phase == "Succeeded" &&
                operation?["syncResult"]?["revision"]?.GetValue<string>() == gitOpsCommit &&
                operation?["operation"]?["sync"]?["revision"]?.GetValue<string>() == gitOpsCommit &&
                current["status"]?["health"]?["status"]?.GetValue<string>() == "Healthy" &&
                current["status"]?["sync"]?["status"]?.GetValue<string>() == "Synced";
        }, $"GitOps {stage} did not become healthy within 30 minutes.", token);
    }

    private async Task<bool> SyncStoppedWritersAsync(string operationId, HashSet<string> submitted, CancellationToken token)
    {
        var complete = true;
        foreach (var name in new[] { "sample", "nexus" })
        {
            var app = await GetOptionalAsync("application", name, "argocd", token);
            // Wait until the workload branch reaches this child's wave and supplies its zero-replica source.
            if (app is null || Text(app["spec"]?["source"], "targetRevision") != gitOpsCommit ||
                app["spec"]?["source"]?["kustomize"]?["patches"] is not JsonArray patches ||
                !patches.Any(patch => Text(patch?["target"], "kind") == "Deployment" && Text(patch?["target"], "name") == name &&
                    Text(patch, "patch").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .SequenceEqual(["- op: add", "path: /spec/replicas", "value: 0"])))
            {
                complete = false;
                continue;
            }
            var childOperation = operationId + "-" + name;
            if (!submitted.Contains(name))
            {
                if (!HasActiveOperation(app))
                {
                    // Manual scaling can make a new source render already Synced. Run a full sync so the
                    // existing child health gate records this source instead of its former writer stage.
                    await PatchAsync("application", name, "argocd", new
                    {
                        operation = new { initiatedBy = new { username = "drn-infra" }, info = new[] { new { name = "drn-operation", value = childOperation } },
                            sync = new { revision = gitOpsCommit, prune = false, syncOptions = new[] { "ApplyOutOfSyncOnly=false" } } }
                    }, token);
                    submitted.Add(name);
                }
                complete = false;
                continue;
            }
            var operation = app["status"]?["operationState"];
            var matches = operation?["operation"]?["info"] is JsonArray info &&
                info.Any(item => Text(item, "name") == "drn-operation" && Text(item, "value") == childOperation);
            if (matches && Text(operation, "phase") is "Failed" or "Error")
                throw new InvalidOperationException("A stopped-writer full sync failed. Inspect Argo CD before resuming.");
            if (!matches || app["operation"] is not null || Text(operation, "phase") != "Succeeded" ||
                Text(operation?["syncResult"], "revision") != gitOpsCommit ||
                Text(app["status"]?["sync"], "status") != "Synced" || Text(app["status"]?["health"], "status") != "Healthy")
                complete = false;
        }
        return complete;
    }

    public async Task StopWritersAsync(CancellationToken token)
    {
        foreach (var name in new[] { "sample", "nexus" })
            await ScaleAsync("drn-project", "deployment", name, 0, token);
        await ScaleAsync("graylog", "statefulset", "graylog", 0, token);
    }

    // An interrupted startup may have created only some writers. Missing resources are allowed
    // here, but API errors and failures to stop an existing writer must still stop recovery.
    public async Task StopExistingWritersAsync(CancellationToken token)
    {
        foreach (var name in new[] { "sample", "nexus" })
            if (await GetOptionalAsync("deployment", name, "drn-project", token) is not null)
                await ScaleAsync("drn-project", "deployment", name, 0, token);
        if (await GetOptionalAsync("statefulset", "graylog", "graylog", token) is not null)
            await ScaleAsync("graylog", "statefulset", "graylog", 0, token);
    }

    public async Task StopDataAsync(CancellationToken token)
    {
        await HibernatePostgresAsync(token);
        await ScaleMongoOperatorAsync(0, token);
        await ScaleAsync("graylog", "statefulset", "mongodb", 0, token);
        await ScaleAsync("graylog", "statefulset", "graylog-datanode", 0, token);
        await WaitAsync(async () => !Items(await RunAsync(["get", "pods", "-A", "-o", "json"], token))
            .Any(pod => pod["spec"]?["volumes"]?.AsArray().Any(volume => volume?["persistentVolumeClaim"] is not null) == true &&
                pod["status"]?["phase"]?.GetValue<string>() is not ("Succeeded" or "Failed")),
            "Some PVC consumers remain running; refusing to snapshot attached data.", token);
    }

    public async Task StartDataAsync(CancellationToken token)
    {
        if (await GetOptionalAsync("cluster.postgresql.cnpg.io", "postgresql", "drn-project", token) is not null)
            await PatchAsync("cluster.postgresql.cnpg.io", "postgresql", "drn-project", new
            {
                metadata = new { annotations = new Dictionary<string, string?>
                { ["cnpg.io/hibernation"] = null, ["cnpg.io/reconciliationLoop"] = null } }
            }, token);
        await ScaleMongoOperatorAsync(1, token);
    }

    /// <summary>Resume database engines for an interrupted native restore, after the caller freezes Argo CD.</summary>
    public async Task StartDatabasesAsync(CancellationToken token)
    {
        await StopWritersAsync(token);
        await ScaleAsync("graylog", "statefulset", "graylog-datanode", 0, token);
        // A disconnected exec can leave pg_restore/mongorestore running remotely. Remove both
        // database pods before retrying native writes against the selected cloned generation.
        await HibernatePostgresAsync(token);
        await ScaleMongoOperatorAsync(0, token);
        await ScaleAsync("graylog", "statefulset", "mongodb", 0, token);
        await StartDataAsync(token);
        await ScaleAsync("graylog", "statefulset", "mongodb", 1, token);
        await RunAsync(["rollout", "status", "statefulset/mongodb", "-n", "graylog", "--timeout=1200s"], token);
        await RunAsync(["wait", "cluster.postgresql.cnpg.io/postgresql", "-n", "drn-project", "--for=condition=Ready", "--timeout=600s"], token);
    }

    private async Task HibernatePostgresAsync(CancellationToken token)
    {
        await RunAsync(["annotate", "cluster.postgresql.cnpg.io", "postgresql", "-n", "drn-project",
            "cnpg.io/hibernation=on", "--overwrite"], token);
        await RunAsync(["wait", "cluster.postgresql.cnpg.io/postgresql", "-n", "drn-project",
            "--for=condition=cnpg.io/hibernation", "--timeout=600s"], token);
        await WaitAsync(async () => await GetOptionalAsync("pod", "postgresql-1", "drn-project", token) is null,
            "The PostgreSQL primary pod remains; database shutdown is incomplete.", token);
    }

    public async Task<PostgresRecovery> CapturePostgresAsync(CancellationToken token)
    {
        var cluster = await GetAsync("cluster.postgresql.cnpg.io", "postgresql", "drn-project", token);
        var claim = await GetAsync("pvc", "postgresql-1", "drn-project", token);
        ValidatePostgres(cluster, claim);
        var status = new JsonObject();
        foreach (var key in new[] { "currentPrimary", "targetPrimary", "latestGeneratedNode" })
            status[key] = cluster["status"]![key]!.DeepClone();
        return new PostgresRecovery(Clean(cluster), Clean(claim), status);
    }

    public async Task<bool> HasPostgresAsync(CancellationToken token) =>
        await GetOptionalAsync("crd", "clusters.postgresql.cnpg.io", null, token) is not null &&
        await GetOptionalAsync("cluster.postgresql.cnpg.io", "postgresql", "drn-project", token) is not null;

    public async Task RestorePostgresAsync(PostgresRecovery saved, CancellationToken token)
    {
        var recorded = saved.Cluster.DeepClone().AsObject();
        recorded["status"] = saved.Status.DeepClone();
        ValidatePostgres(recorded, saved.Claim, requireOwner: false);
        var existing = await GetOptionalAsync("cluster.postgresql.cnpg.io", "postgresql", "drn-project", token);
        if (existing is not null && Text(existing["metadata"]?["annotations"], "cnpg.io/reconciliationLoop") != "disabled")
        {
            // An existing live/hibernated primary is authoritative; never rewrite its status or claim.
            ValidatePostgres(existing, await GetAsync("pvc", "postgresql-1", "drn-project", token));
            return;
        }
        if (existing is null)
        {
            var cluster = saved.Cluster.DeepClone().AsObject();
            if (cluster["metadata"]!["annotations"] is not JsonObject)
                cluster["metadata"]!["annotations"] = new JsonObject();
            var annotations = cluster["metadata"]!["annotations"]!.AsObject();
            annotations["cnpg.io/reconciliationLoop"] = "disabled";
            annotations.Remove("cnpg.io/hibernation");
            await RunAsync(["create", "-f", "-"], token, cluster.ToJsonString());
        }
        else if (!JsonNode.DeepEquals(existing["spec"], saved.Cluster["spec"]))
            throw new InvalidOperationException("The paused PostgreSQL resource differs from the saved recovery spec.");
        var created = await GetAsync("cluster.postgresql.cnpg.io", "postgresql", "drn-project", token);
        if (string.IsNullOrEmpty(Text(created["metadata"], "uid")) ||
            Text(created["metadata"]?["annotations"], "cnpg.io/reconciliationLoop") != "disabled")
            throw new InvalidOperationException("PostgreSQL must remain paused while restoring its volume identity.");
        var claim = saved.Claim.DeepClone().AsObject();
        claim["metadata"]!["ownerReferences"] = new JsonArray(new JsonObject
        {
            ["apiVersion"] = "postgresql.cnpg.io/v1", ["kind"] = "Cluster", ["name"] = "postgresql",
            ["uid"] = Text(created["metadata"], "uid"), ["controller"] = true, ["blockOwnerDeletion"] = true
        });
        var existingClaim = await GetOptionalAsync("pvc", "postgresql-1", "drn-project", token);
        if (existingClaim is null)
            await RunAsync(["create", "-f", "-"], token, claim.ToJsonString());
        else
        {
            var resumed = created.DeepClone().AsObject();
            resumed["status"] = saved.Status.DeepClone();
            ValidatePostgres(resumed, existingClaim);
        }
        created["status"] = saved.Status.DeepClone();
        await RunAsync(["replace", "--raw", "/apis/postgresql.cnpg.io/v1/namespaces/drn-project/clusters/postgresql/status", "-f", "-"],
            token, created.ToJsonString());
    }

    public async Task VerifyBindingsAsync(IReadOnlyDictionary<string, string> volumes, CancellationToken token)
    {
        VolumeBackups.ValidateClaims(volumes);
        foreach (var claim in StorageClaims.All)
        {
            var pv = await GetAsync("pv", $"drn-staging-{claim.Key}", null, token);
            ValidateVolume(pv, claim, volumes[claim.Key]);
        }
    }

    /// <summary>Called only after a complete backup, frozen controllers and confirmed cloud detachment.</summary>
    public async Task RemoveClaimsAsync(IReadOnlyDictionary<string, string> volumes, CancellationToken token, bool allowPartial = false)
    {
        VolumeBackups.ValidateClaims(volumes);
        var approved = StorageClaims.All.ToDictionary(claim => claim.Namespace + "/" + claim.ClaimName, StringComparer.Ordinal);
        var claims = Items(await RunAsync(["get", "pvc", "-A", "-o", "json"], token));
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in claims)
        {
            var identity = Text(item["metadata"], "namespace") + "/" + Text(item["metadata"], "name");
            if (!present.Add(identity) || !approved.TryGetValue(identity, out var expected) ||
                Text(item["spec"], "storageClassName") != StorageClaims.StorageClass ||
                Text(item["spec"], "volumeName") != "drn-staging-" + expected.Key ||
                string.IsNullOrEmpty(Text(item["metadata"], "uid")))
                throw new InvalidOperationException("Claim removal requires only the recorded seven retained-storage identities.");
        }
        if (!allowPartial && claims.Length != approved.Count)
            throw new InvalidOperationException("The complete seven-claim inventory is required before beginning cluster removal.");
        foreach (var expected in StorageClaims.All)
        {
            var pv = await GetOptionalAsync("pv", "drn-staging-" + expected.Key, null, token);
            if (pv is null)
            {
                if (!allowPartial || present.Contains(expected.Namespace + "/" + expected.ClaimName))
                    throw new InvalidOperationException("A retained claim has lost its recorded PersistentVolume.");
                continue; // A recorded removal can resume after partially completed bootstrap destruction.
            }
            ValidateVolume(pv, expected, volumes[expected.Key]);
            var claim = claims.SingleOrDefault(item => Text(item["metadata"], "namespace") == expected.Namespace && Text(item["metadata"], "name") == expected.ClaimName);
            if (claim is not null && Text(pv["spec"]?["claimRef"], "uid") != Text(claim["metadata"], "uid"))
                throw new InvalidOperationException("The retained volume is bound to a different claim UID.");
        }
        var consumers = Items(await RunAsync(["get", "pods", "-A", "-o", "json"], token))
            .Where(pod => pod["spec"]?["volumes"]?.AsArray().Any(volume => volume?["persistentVolumeClaim"] is not null) == true).ToArray();
        foreach (var pod in consumers)
            if (Text(pod["status"], "phase") is not ("Succeeded" or "Failed") ||
                pod["spec"]!["volumes"]!.AsArray().Where(volume => volume?["persistentVolumeClaim"] is not null)
                    .Any(volume => !approved.ContainsKey(Text(pod["metadata"], "namespace") + "/" + Text(volume!["persistentVolumeClaim"], "claimName"))))
                throw new InvalidOperationException("An active or unknown PVC consumer prevents claim removal.");
        // Kubernetes PVC protection also considers completed scheduled Pods to be consumers.
        foreach (var pod in consumers)
        {
            await DeleteIdentityAsync("pods", pod, token);
            await WaitAsync(async () => await GetOptionalAsync("pod", Text(pod["metadata"], "name"), Text(pod["metadata"], "namespace"), token) is null,
                "A completed PVC consumer has not been removed; claim removal remains blocked.", token);
        }
        foreach (var claim in claims)
            await DeleteIdentityAsync("persistentvolumeclaims", claim, token);
        await WaitAsync(async () => Items(await RunAsync(["get", "pvc", "-A", "-o", "json"], token)).Length == 0,
            "PersistentVolumeClaims remain; bootstrap destruction is blocked.", token);
    }

    private static void ValidateVolume(JsonObject pv, StorageClaim claim, string volume)
    {
        if (Text(pv["spec"]?["csi"], "volumeHandle") != volume ||
            Text(pv["spec"]?["csi"], "driver") != "blockvolume.csi.oraclecloud.com" ||
            Text(pv["spec"], "storageClassName") != StorageClaims.StorageClass ||
            Text(pv["spec"], "persistentVolumeReclaimPolicy") != "Retain" ||
            Text(pv["spec"], "volumeMode") != "Filesystem" ||
            Text(pv["spec"]?["claimRef"], "name") != claim.ClaimName ||
            Text(pv["spec"]?["claimRef"], "namespace") != claim.Namespace)
            throw new InvalidOperationException("Live PV identities do not match the selected storage generation.");
    }

    private Task<string> DeleteIdentityAsync(string resource, JsonObject item, CancellationToken token)
    {
        var uid = Text(item["metadata"], "uid");
        if (string.IsNullOrEmpty(uid)) throw new InvalidOperationException("Deletion requires the verified Kubernetes resource UID.");
        var ns = Uri.EscapeDataString(Text(item["metadata"], "namespace"));
        var name = Uri.EscapeDataString(Text(item["metadata"], "name"));
        return RunAsync(["delete", "--raw", $"/api/v1/namespaces/{ns}/{resource}/{name}", "-f", "-"], token,
            new JsonObject { ["apiVersion"] = "v1", ["kind"] = "DeleteOptions", ["preconditions"] = new JsonObject { ["uid"] = uid } }.ToJsonString());
    }

    public async Task WaitDataAsync(CancellationToken token)
    {
        foreach (var name in new[] { "mongodb", "graylog-datanode", "graylog" })
            await RunAsync(["rollout", "status", $"statefulset/{name}", "-n", "graylog", "--timeout=1200s"], token);
        await RunAsync(["wait", "cluster.postgresql.cnpg.io/postgresql", "-n", "drn-project", "--for=condition=Ready", "--timeout=600s"], token);
    }

    public Task StartGraylogAsync(CancellationToken token) => ScaleAsync("graylog", "statefulset", "graylog", 1, token);

    public async Task ScaleAsync(string ns, string kind, string name, int replicas, CancellationToken token)
    {
        await RunAsync(["scale", $"{kind}/{name}", "-n", ns, $"--replicas={replicas}"], token);
        await WaitAsync(async () =>
        {
            var current = await GetAsync(kind, name, ns, token);
            var observed = current["status"]?["observedGeneration"]?.GetValue<long>() ?? 0;
            var generation = current["metadata"]?["generation"]?.GetValue<long>() ?? 1;
            var actual = current["status"]?["replicas"]?.GetValue<int>() ?? 0;
            var ready = current["status"]?["readyReplicas"]?.GetValue<int>() ?? 0;
            if (observed < generation || actual != replicas || ready != replicas) return false;
            if (replicas != 0) return true;
            // Controllers may report zero while old pods still terminate. Wait for their removal.
            var selector = current["spec"]?["selector"]?.AsObject()
                ?? throw new InvalidOperationException("The controller has no pod selector.");
            return !Items(await RunAsync(["get", "pods", "-n", ns, "-o", "json"], token))
                .Any(pod => MatchesSelector(pod["metadata"]?["labels"]?.AsObject() ?? new JsonObject(), selector));
        }, $"{kind}/{name} did not reach {replicas} replicas.", token);
    }

    private async Task ScaleMongoOperatorAsync(int replicas, CancellationToken token)
    {
        var deployments = Items(await RunAsync(["get", "deployments", "-n", "graylog", "-l",
            "app.kubernetes.io/name=mongodb-kubernetes-operator", "-o", "json"], token));
        if (deployments.Length != 1) throw new InvalidOperationException("Expected exactly one MongoDB operator deployment.");
        await ScaleAsync("graylog", "deployment", Text(deployments[0]["metadata"], "name"), replicas, token);
    }

    public static void ValidatePostgres(JsonObject cluster, JsonObject claim, bool requireOwner = true)
    {
        if (Text(cluster, "apiVersion") != "postgresql.cnpg.io/v1" || Text(cluster, "kind") != "Cluster" ||
            Text(cluster["metadata"], "name") != "postgresql" || Text(cluster["metadata"], "namespace") != "drn-project" ||
            cluster["metadata"]?["deletionTimestamp"] is not null || claim["metadata"]?["deletionTimestamp"] is not null ||
            cluster["spec"]?["instances"]?.GetValue<int>() != 1 ||
            Text(cluster["spec"]?["storage"], "storageClass") != StorageClaims.StorageClass ||
            Text(cluster["spec"]?["bootstrap"]?["initdb"]?["secret"], "name") != "postgresql-app" ||
            Text(cluster["spec"]?["certificates"], "serverCASecret") != "postgresql-ca" ||
            Text(cluster["spec"]?["certificates"], "clientCASecret") != "postgresql-ca" ||
            Text(cluster["status"], "currentPrimary") != "postgresql-1" || Text(cluster["status"], "targetPrimary") != "postgresql-1" ||
            cluster["status"]?["latestGeneratedNode"]?.GetValue<int>() != 1 ||
            Text(claim, "apiVersion") != "v1" || Text(claim, "kind") != "PersistentVolumeClaim" ||
            Text(claim["metadata"], "name") != "postgresql-1" || Text(claim["metadata"], "namespace") != "drn-project" ||
            Text(claim["metadata"]?["labels"], "cnpg.io/cluster") != "postgresql" ||
            Text(claim["metadata"]?["annotations"], "cnpg.io/nodeSerial") != "1" ||
            Text(claim["metadata"]?["annotations"], "cnpg.io/pvcStatus") != "ready" ||
            Text(claim["metadata"]?["labels"], "cnpg.io/instanceName") != "postgresql-1" ||
            Text(claim["metadata"]?["labels"], "cnpg.io/pvcRole") != "PG_DATA" ||
            Text(claim["spec"], "storageClassName") != StorageClaims.StorageClass ||
            Text(claim["spec"], "volumeName") != "drn-staging-postgresql")
            throw new InvalidOperationException("PostgreSQL identity is outside the supported single-primary recovery profile.");
        if (requireOwner && (string.IsNullOrEmpty(Text(cluster["metadata"], "uid")) ||
            claim["metadata"]?["ownerReferences"] is not JsonArray { Count: 1 } owners ||
            Text(owners[0], "apiVersion") != "postgresql.cnpg.io/v1" || Text(owners[0], "kind") != "Cluster" ||
            Text(owners[0], "name") != "postgresql" || Text(owners[0], "uid") != Text(cluster["metadata"], "uid")))
            throw new InvalidOperationException("PostgreSQL claim ownership does not match the current Cluster identity.");
    }

    private static JsonObject Clean(JsonObject source)
    {
        var clean = source.DeepClone().AsObject();
        clean.Remove("status");
        var metadata = clean["metadata"]!.AsObject();
        foreach (var field in new[] { "uid", "resourceVersion", "generation", "creationTimestamp", "managedFields", "ownerReferences", "finalizers" })
            metadata.Remove(field);
        if (metadata["annotations"] is JsonObject annotations)
            foreach (var key in annotations.Select(pair => pair.Key).Where(key => key.StartsWith("kubectl.", StringComparison.Ordinal) ||
                key.StartsWith("pv.kubernetes.io/", StringComparison.Ordinal) || key.StartsWith("volume.kubernetes.io/", StringComparison.Ordinal)).ToArray())
                annotations.Remove(key);
        return clean;
    }

    private Task<string> PatchAsync(string kind, string name, string ns, object patch, CancellationToken token) =>
        RunAsync(["patch", kind, name, "-n", ns, "--type=merge", "--patch-file=/dev/stdin"], token, JsonSerializer.Serialize(patch));
    private Task<string> ApplyAsync(JsonObject value, CancellationToken token) =>
        RunAsync(["apply", "--server-side", "--field-manager=drn-recovery", "-f", "-"], token, value.ToJsonString());
    private async Task<JsonObject> GetAsync(string kind, string name, string? ns, CancellationToken token) =>
        await GetOptionalAsync(kind, name, ns, token) ?? throw new InvalidOperationException($"Required {kind}/{name} is absent.");
    private async Task<JsonObject?> GetOptionalAsync(string kind, string name, string? ns, CancellationToken token)
    {
        var result = await RunAsync(["get", kind, name, .. (ns is null ? Array.Empty<string>() : new[] { "-n", ns }), "--ignore-not-found", "-o", "json"], token);
        return string.IsNullOrWhiteSpace(result) ? null : JsonNode.Parse(result)!.AsObject();
    }
    private Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken token, string? input = null) =>
        runner.RunAsync("kubectl", [args[0] is "wait" or "rollout" ? "--request-timeout=30m" : "--request-timeout=30s", .. args], environment, false, token, input);
    private static JsonObject[] Items(string json) => JsonNode.Parse(json)!["items"]!.AsArray().Select(item => item!.AsObject()).ToArray();
    private static string Text(JsonNode? parent, string key) => parent?[key]?.GetValue<string>() ?? "";
    private static bool HasActiveOperation(JsonObject app) => app["operation"] is not null ||
        Text(app["status"]?["operationState"], "phase") is "Running" or "Terminating";

    private static bool MatchesSelector(JsonObject labels, JsonObject selector)
    {
        if (selector["matchLabels"] is JsonObject labelMatches && labelMatches.Any(pair => Text(labels, pair.Key) != pair.Value?.GetValue<string>())) return false;
        if (selector["matchExpressions"] is not JsonArray expressions) return true;
        foreach (var expression in expressions)
        {
            var key = Text(expression, "key");
            var present = labels.ContainsKey(key);
            var values = expression?["values"]?.AsArray().Select(value => value!.GetValue<string>()).ToArray() ?? [];
            var matches = Text(expression, "operator") switch
            {
                "In" => present && values.Contains(Text(labels, key), StringComparer.Ordinal),
                "NotIn" => !present || !values.Contains(Text(labels, key), StringComparer.Ordinal),
                "Exists" => present,
                "DoesNotExist" => !present,
                _ => throw new InvalidOperationException("The controller has an unsupported pod selector.")
            };
            if (!matches) return false;
        }
        return true;
    }
    private async Task WaitAsync(Func<Task<bool>> condition, string failure, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new InvalidOperationException(failure);
            await delay(TimeSpan.FromSeconds(5), token);
        }
    }
}
