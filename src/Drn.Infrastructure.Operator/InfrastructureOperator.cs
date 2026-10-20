using System.Text.Json;
using Drn.Infrastructure.Operator.Recovery;
using Drn.Pulumi;

namespace Drn.Infrastructure.Operator;

/// <summary>Coordinates Pulumi, GitOps and recovery through one encrypted deployment record.</summary>
public sealed class InfrastructureOperator(string repository, OperatorConfiguration configuration, ICommandRunner runner,
    INativeDatabaseBackup? nativeBackup = null, Func<TimeSpan, CancellationToken, Task>? clusterDelayAsync = null)
{
    public const string OciCliVersion = "3.95.0";
    private string ProjectDirectory => Path.Combine(repository, "src", "Drn.Pulumi");
    private string LocalDirectory => Path.Combine(repository, ".local");
    private string Kubeconfig => Path.Combine(LocalDirectory, "staging.kubeconfig");
    private string Snapshot => Path.Combine(LocalDirectory, "gitops", configuration.Infrastructure.GitOpsCommit);
    private Dictionary<string, string> Environment => new()
    {
        ["OCI_CLI_PROFILE"] = configuration.Infrastructure.OciProfile,
        ["OCI_CLI_AUTH"] = configuration.Infrastructure.OciAuth == "InstancePrincipal" ? "instance_principal" : "security_token",
        ["OCI_CLI_REGION"] = configuration.Infrastructure.Region, ["KUBECONFIG"] = Kubeconfig
    };
    private ICommandRunner commands = runner;
    private readonly ICommandRunner rawRunner = runner;
    private RecoveryStore Store => new(rawRunner, configuration.Infrastructure.ObjectStorageNamespace,
        configuration.Infrastructure.RecoveryBucketName, configuration.RecoveryKeyPath, Environment);
    private RecoveryLock? held;
    private LifecycleState state = new();
    private string? stateETag;
    private CredentialCheckpoint credentials = new();
    private bool operationMayBeIncomplete;
    private ClusterLifecycle Kubernetes => new(commands, Environment, configuration.Infrastructure.GitOpsCommit, clusterDelayAsync);
    private CloudPreflight Cloud => new(commands, Environment, configuration.Infrastructure);
    private StorageGeneration ActiveStorage => state.StorageGenerations.Single(g => g.Id == state.ActiveStorageGeneration);
    private VolumeBackups Volumes => VolumesFor(ActiveStorage);
    private VolumeBackups VolumesFor(StorageGeneration generation) =>
        new(commands, Environment, configuration.Infrastructure.DurableCompartmentId, generation);
    private INativeDatabaseBackup Native => nativeBackup ?? new NativeDatabaseBackup(commands, Environment, AssertOwnedAsync);

    public async Task ExecuteAsync(OperatorCommand command, CancellationToken token = default)
    {
        configuration.Validate();
        if (!File.Exists(Path.Combine(ProjectDirectory, "Pulumi.yaml")))
            throw new ArgumentException("Run this command from the Pulumi Ops repository root.");
        if (command.Name == "validate")
        {
            Console.WriteLine("Configuration is valid. Live account, image, policy and recovery checks run during operations.");
            return;
        }
        PrepareLocalDirectory();
        using var localLock = new FileStream(Path.Combine(LocalDirectory, "lifecycle.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (command.Name == "keygen")
        {
            RecoveryStore.GenerateKey(configuration.RecoveryKeyPath);
            Console.WriteLine("Recovery key created without overwriting an existing file. Store an independent copy before initialize.");
            return;
        }
        if ((await rawRunner.RunAsync("oci", ["--version"], Environment, false, token)).Trim() != OciCliVersion)
            throw new InvalidOperationException($"Install the reviewed OCI CLI {OciCliVersion}; conditional object writes and image discovery depend on its contract.");
        if (command.Name == "setup")
        {
            foreach (var role in new[] { "recovery", "durable", "cluster", "bootstrap" })
                await PulumiAsync(["stack", "select", configuration.Stack(role), "--create"], true, token);
            await ConfigureAsync("recovery", configuration.Infrastructure.Workers, token);
            await StackAsync("up", "recovery", command.Yes, token);
            Console.WriteLine("Four Pulumi stacks selected; the protected recovery bucket is ready. Initialize credentials before up.");
            return;
        }
        if (command.Name == "status")
        {
            var current = await Store.ReadAsync<LifecycleState>("state", token);
            var active = await Store.ReadLockAsync(token);
            Console.WriteLine(current is null ? "Not initialized." :
                $"Phase: {current.Value.Phase}; generation: {current.Value.ActiveStorageGeneration}; complete backups: {current.Value.Backups.Length}.");
            if (current is not null)
            {
                foreach (var backup in current.Value.Backups.OrderByDescending(b => b.Volumes.CreatedAt))
                    Console.WriteLine($"Backup {backup.Id}: {backup.Volumes.CreatedAt:O}, generation {backup.StorageGeneration}, release {backup.GitOpsCommit}.");
                if (current.Value.PendingNative is not null)
                    Console.WriteLine($"Pending checkpoint {current.Value.OperationId:N}: {current.Value.PendingVolumeBackupId ?? "cloud result unresolved"}.");
            }
            if (active is not null) Console.WriteLine($"Operation {active.OwnerId:N}: {active.Operation}, since {active.CreatedAt:O}. Stop that process before explicit unlock.");
            return;
        }
        if (command.Name == "unlock")
        {
            var existing = await Store.ReadLockAsync(token) ?? throw new InvalidOperationException("No operation lock exists.");
            if (command.Owner != existing.OwnerId) throw new InvalidOperationException("The owner confirmation does not match the current operation.");
            await Store.ReleaseAsync(existing, token);
            Console.WriteLine("Stopped operation unlocked. Resume converges the recorded generation; inspect pending backups before cleanup.");
            return;
        }
        var recovery = await OutputsAsync("recovery", token);
        if (!recovery.TryGetProperty("bucketName", out var bucket) || bucket.GetString() != configuration.Infrastructure.RecoveryBucketName ||
            !recovery.TryGetProperty("namespace", out var ns) || ns.GetString() != configuration.Infrastructure.ObjectStorageNamespace ||
            !recovery.TryGetProperty("region", out var region) || region.GetString() != configuration.Infrastructure.Region)
            throw new InvalidOperationException("The selected recovery bucket is not owned by this deployment's recovery stack. Run setup with its original configuration.");
        held = await Store.AcquireAsync(Guid.NewGuid(), command.Name, token);
        Console.WriteLine($"Operation {held.OwnerId:N} owns the shared staging lock.");
        commands = new CoordinatedRunner(rawRunner, AssertOwnedAsync);
        var complete = false;
        try
        {
            if (command.Name == "initialize") await InitializeAsync(token);
            else
            {
                await LoadAsync(token, allowReleaseMismatch: command.Name == "release");
                switch (command.Name)
                {
                    case "preview":
                        await Cloud.RunAsync(token);
                        foreach (var role in new[] { "durable", "cluster" })
                        {
                            await ConfigureAsync(role, state.Phase is "paused" or "offline" ? 0 : configuration.Infrastructure.Workers, token);
                            await StackAsync("preview", role, false, token);
                        }
                        break;
                    case "durable": case "cluster": case "bootstrap":
                        await ProvisionStackAsync(command.Name, command.Yes, token);
                        break;
                    case "up": case "resume":
                        if (state.Phase == "running")
                            throw new InvalidOperationException("The deployment is running. Use check, backup, pause or down before changing infrastructure.");
                        if (state.Phase == "removing") await RemoveClusterAsync(command.Yes, token);
                        await ResolvePendingBackupAsync(token);
                        await StartAsync(command.Yes, token);
                        if (!state.Backups.Any(b => b.StorageGeneration == state.ActiveStorageGeneration && b.GitOpsCommit == state.GitOpsCommit))
                        {
                            await BackupAndPauseAsync(command.Yes, token);
                            await StartAsync(command.Yes, token);
                        }
                        break;
                    case "backup":
                        // A scheduled run must never start a deliberately paused or removed deployment.
                        if (state.Phase is "paused" or "down") Console.WriteLine("Deployment is intentionally offline; retaining its complete checkpoint.");
                        else { RequireRunning(); await BackupAndPauseAsync(command.Yes, token); await StartAsync(command.Yes, token); }
                        break;
                    case "pause":
                        if (state.Phase != "paused") { RequireRunning(); await BackupAndPauseAsync(command.Yes, token); }
                        break;
                    case "down":
                        if (state.Phase != "down")
                        {
                            if (state.Phase is not ("paused" or "removing")) { RequireRunning(); await BackupAndPauseAsync(command.Yes, token); }
                            await RemoveClusterAsync(command.Yes, token);
                        }
                        break;
                    case "restore": await RestoreAsync(command, token); break;
                    case "release": await SelectReleaseAsync(token); break;
                    case "kubeconfig": await WriteKubeconfigAsync(token); break;
                    case "check": await CheckAsync(token); break;
                    default: throw new ArgumentException("Unsupported operation.");
                }
            }
            complete = true;
        }
        finally
        {
            if (complete || !operationMayBeIncomplete) await Store.ReleaseAsync(held, CancellationToken.None);
            else Console.Error.WriteLine($"Operation {held.OwnerId:N} remains locked. Inspect status, stop the previous process, then unlock and resume; no automatic takeover occurs.");
        }
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        if (!configuration.RecoveryKeyBackedUp)
            throw new InvalidOperationException("Copy the recovery key to independent secure storage, then set recoveryKeyBackedUp=true.");
        if (await Store.ReadAsync<LifecycleState>("state", token) is not null)
            throw new InvalidOperationException("This deployment already has recovery state. Never regenerate credentials for retained data.");
        foreach (var role in new[] { "durable", "cluster", "bootstrap" })
        {
            using var exported = JsonDocument.Parse(await PulumiAsync(["stack", "export", "--stack", configuration.Stack(role)], false, token));
            if (!IsEmptyStack(exported.RootElement) ||
                (await OutputsAsync(role, token)).EnumerateObject().Any())
                throw new InvalidOperationException("Initialize requires new, empty stacks without pending operations. Existing infrastructure needs its original recovery state and key.");
        }
        await FetchSnapshotAsync(token);
        credentials = CredentialCheckpoint.Create(configuration.Infrastructure.GitOpsCommit, DateTimeOffset.UtcNow);
        operationMayBeIncomplete = true;
        await AssertOwnedAsync(token);
        await Store.WriteAsync($"credentials/{credentials.Generation}", credentials, null, token);
        state = new LifecycleState
        {
            Organization = configuration.Organization, Region = configuration.Infrastructure.Region,
            AvailabilityDomain = configuration.Infrastructure.AvailabilityDomain,
            ClusterCompartmentId = configuration.Infrastructure.ClusterCompartmentId,
            DurableCompartmentId = configuration.Infrastructure.DurableCompartmentId,
            GitOpsCommit = configuration.Infrastructure.GitOpsCommit, GitOpsTag = configuration.Infrastructure.GitOpsTag,
            CredentialGeneration = credentials.Generation, CredentialHash = credentials.StableIdentityHash(), OperationId = held!.OwnerId,
            StorageGenerations = configuration.Infrastructure.StorageGenerations
        };
        await SaveAsync(token);
        Console.WriteLine("Encrypted credentials and deployment identity were read back successfully before any workload starts.");
    }

    private async Task LoadAsync(CancellationToken token, bool allowReleaseMismatch = false)
    {
        var stored = await Store.ReadAsync<LifecycleState>("state", token)
            ?? throw new InvalidOperationException("Recovery state is absent. Initialize only a new deployment; otherwise restore the original state.");
        state = stored.Value;
        stateETag = stored.ETag;
        credentials = (await Store.ReadAsync<CredentialCheckpoint>($"credentials/{state.CredentialGeneration}", token))?.Value
            ?? throw new InvalidOperationException("The recorded credential generation is missing. No cloud or workload mutation is permitted.");
        state.Validate(configuration, credentials, allowReleaseMismatch);
    }

    private async Task SaveAsync(CancellationToken token)
    {
        state.Validate(configuration, credentials);
        // Older operators must reject state that can carry configurable retained-volume profiles.
        state = state with { SchemaVersion = 2 };
        await AssertOwnedAsync(token);
        operationMayBeIncomplete = true;
        stateETag = await Store.WriteAsync("state", state, stateETag, token);
    }

    private async Task CheckAsync(CancellationToken token)
    {
        if (state.Phase == "running") state.RequireFreshBackup(DateTimeOffset.UtcNow, configuration.MaximumBackupAgeHours);
        var latest = state.Backups.Where(b => b.StorageGeneration == state.ActiveStorageGeneration &&
            (state.Phase != "running" || b.GitOpsCommit == state.GitOpsCommit)).MaxBy(b => b.Volumes.CreatedAt)
            ?? throw new InvalidOperationException("The active generation has no complete backup.");
        await VerifyBackupAsync(latest, token);
        if (state.Phase == "running")
        {
            await WriteKubeconfigAsync(token);
            await Kubernetes.VerifyCredentialsAsync(credentials, token);
            await Kubernetes.VerifyBindingsAsync(state.VolumeIds, token);
            await new PolicyPreflight(commands, Environment, Kubeconfig, configuration.Infrastructure.Workers).RunAsync(token);
            await Kubernetes.WaitDataAsync(token);
        }
        else if (state.Phase is not ("paused" or "down"))
            throw new InvalidOperationException("A lifecycle operation is incomplete. Resume it before accepting recovery health.");
        Console.WriteLine("Recorded recovery and applicable live checks passed.");
    }

    private async Task ProvisionStackAsync(string role, bool yes, CancellationToken token)
    {
        if (state.Phase != "initialized" || state.PendingNative is not null || state.PendingVolumeBackupId is not null ||
            state.RestoreBackupId is not null || state.NativeRestoreStarted)
            throw new InvalidOperationException("Scoped provisioning requires initialized state without pending recovery. Use up or resume for lifecycle transitions.");

        if (role is "durable" or "cluster")
        {
            // A failed bootstrap can leave resources without stack outputs. Inspect its checkpoint.
            using var bootstrap = JsonDocument.Parse(await PulumiAsync(
                ["stack", "export", "--stack", configuration.Stack("bootstrap")], false, token));
            if (!IsEmptyStack(bootstrap.RootElement))
                throw new InvalidOperationException("Bootstrap resources already exist or its checkpoint is incomplete. Inspect pending operations, then use up or resume to coordinate infrastructure and workloads.");
        }

        switch (role)
        {
            case "durable":
                operationMayBeIncomplete = true;
                await ApplyDurableAsync(yes, token);
                break;
            case "cluster":
                var cluster = await OutputsAsync("cluster", token);
                if (cluster.TryGetProperty("clusterId", out _)) RequireNativeNetwork(cluster);
                await Cloud.RunAsync(token);
                operationMayBeIncomplete = true;
                await ApplyClusterAsync(configuration.Infrastructure.Workers, yes, token);
                await WriteKubeconfigAsync(token);
                break;
            case "bootstrap":
                await RequireBootstrapPrerequisitesAsync(token);
                await FetchSnapshotAsync(token);
                await WriteKubeconfigAsync(token);
                operationMayBeIncomplete = true;
                await ApplyBootstrapAsync(yes, token);
                break;
            default: throw new ArgumentException("Unsupported provisioning stack.");
        }
        Console.WriteLine($"The {role} stack is provisioned. Workload activation and the baseline backup require up or resume.");
    }

    private async Task ApplyDurableAsync(bool yes, CancellationToken token)
    {
        await ConfigureAsync("durable", configuration.Infrastructure.Workers, token);
        await StackAsync("up", "durable", yes, token);
        var durable = await OutputsAsync("durable", token);
        var actual = durable.GetProperty("volumeIds").Deserialize<Dictionary<string, string>>()!;
        VolumeBackups.ValidateClaims(actual);
        if (durable.GetProperty("storageGeneration").GetString() != state.ActiveStorageGeneration ||
            state.VolumeIds.Count > 0 && !MappingsEqual(state.VolumeIds, actual))
            throw new InvalidOperationException("Durable stack output differs from the recorded generation. Refusing to bind workloads.");
        state = state with { VolumeIds = actual, VolumeGroupId = durable.GetProperty("volumeGroupId").GetString()! };
        await SaveAsync(token);
    }

    private async Task RequireBootstrapPrerequisitesAsync(CancellationToken token)
    {
        var durable = await OutputsAsync("durable", token);
        if (state.VolumeIds.Count != StorageClaims.All.Count || string.IsNullOrWhiteSpace(state.VolumeGroupId) ||
            !durable.TryGetProperty("volumeIds", out var ids) ||
            !durable.TryGetProperty("volumeGroupId", out var group) || group.GetString() != state.VolumeGroupId ||
            !durable.TryGetProperty("storageGeneration", out var generation) || generation.GetString() != state.ActiveStorageGeneration ||
            ids.Deserialize<Dictionary<string, string>>() is not { } actual || !MappingsEqual(state.VolumeIds, actual))
            throw new InvalidOperationException("Bootstrap requires the recorded durable storage. Run durable before bootstrap.");
        var cluster = await OutputsAsync("cluster", token);
        if (!cluster.TryGetProperty("clusterId", out var id) || id.GetString() is not { } clusterId ||
            !clusterId.StartsWith("ocid1.cluster.", StringComparison.Ordinal) ||
            !cluster.TryGetProperty("workers", out var workers) || workers.GetInt32() != configuration.Infrastructure.Workers)
            throw new InvalidOperationException("Bootstrap requires a provisioned cluster with the configured workers. Run cluster before bootstrap.");
        RequireNativeNetwork(cluster);
    }

    private async Task ApplyBootstrapAsync(bool yes, CancellationToken token)
    {
        await ConfigureAsync("bootstrap", configuration.Infrastructure.Workers, token);
        await StackAsync("up", "bootstrap", yes, token);
        await new PolicyPreflight(commands, Environment, Kubeconfig, configuration.Infrastructure.Workers)
            .RunAsync(token, allowFrozenApplicationController: true);
    }

    private async Task StartAsync(bool yes, CancellationToken token)
    {
        await FetchSnapshotAsync(token);
        await Cloud.RunAsync(token);
        var existing = await OutputsAsync("cluster", token);
        var newCluster = !existing.TryGetProperty("clusterId", out _);
        operationMayBeIncomplete = true;
        if (!newCluster)
        {
            RequireNativeNetwork(existing);
            await WriteKubeconfigAsync(token);
            var crd = await commands.RunAsync("kubectl", ["get", "crd", "applications.argoproj.io", "--ignore-not-found", "-o", "name"], Environment, false, token);
            if (!string.IsNullOrWhiteSpace(crd)) await Kubernetes.FreezeAsync(token);
            await Kubernetes.StopExistingWritersAsync(token);
        }
        state = state with { Phase = state.RestoreBackupId is null ? "starting" : "restoring" };
        await SaveAsync(token);
        await ApplyDurableAsync(yes, token);
        await ApplyClusterAsync(configuration.Infrastructure.Workers, yes, token);
        await WriteKubeconfigAsync(token);
        await ApplyBootstrapAsync(yes, token);
        await Kubernetes.RestoreCredentialsAsync(credentials, token);
        await Kubernetes.VerifyBindingsAsync(state.VolumeIds, token);
        if (newCluster || !await Kubernetes.HasPostgresAsync(token))
        {
            await Kubernetes.ActivateAsync("prepare", token);
        }
        if (state.Postgres is not null) await Kubernetes.RestorePostgresAsync(state.Postgres, token);
        await Kubernetes.StartDataAsync(token);
        if (state.NativeRestoreStarted)
        {
            // Repair partially replaced databases before waiting for their Graylog consumers.
            await Kubernetes.FreezeAsync(token);
            await Kubernetes.StartDatabasesAsync(token);
            await RestoreNativeAsync(token);
        }
        await Kubernetes.ActivateAsync("data", token);
        await Kubernetes.WaitDataAsync(token);
        if (state.RestoreBackupId is not null)
        {
            state = state with { NativeRestoreStarted = true };
            await SaveAsync(token);
            await Kubernetes.FreezeAsync(token);
            await Kubernetes.StopWritersAsync(token);
            await RestoreNativeAsync(token);
            await Kubernetes.StartGraylogAsync(token);
        }
        state = state with { Postgres = await Kubernetes.CapturePostgresAsync(token) };
        await SaveAsync(token); // Persist reattachment metadata before starting application writers.
        await PruneNativeBackupsAsync(token);
        await RetainBackupsAsync(token);
        await Kubernetes.VerifyCredentialsAsync(credentials, token);
        await Kubernetes.ActivateAsync("workloads", token);
        state = state with { Phase = "running" };
        await SaveAsync(token);
        Console.WriteLine("Staging workloads are healthy with the recorded credentials, storage and GitOps release.");
    }

    private async Task RestoreNativeAsync(CancellationToken token)
    {
        var backup = state.Backups.Single(b => b.Id == state.RestoreBackupId);
        if (state.VolumeIds.Values.Intersect(backup.SourceVolumes.Values, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("Restore cannot target any original source volume.");
        await Kubernetes.VerifyBindingsAsync(state.VolumeIds, token);
        await Native.RestoreAsync(backup.Native, state.ActiveStorageGeneration,
            ct => Kubernetes.ScaleAsync("graylog", "statefulset", "graylog-datanode", 0, ct),
            ct => Kubernetes.ScaleAsync("graylog", "statefulset", "graylog-datanode", 1, ct), token);
        state = state with { RestoreBackupId = null, NativeRestoreStarted = false };
        await SaveAsync(token);
    }

    private async Task BackupAndPauseAsync(bool yes, CancellationToken token)
    {
        await WriteKubeconfigAsync(token);
        await Kubernetes.VerifyCredentialsAsync(credentials, token);
        await Kubernetes.VerifyBindingsAsync(state.VolumeIds, token);
        var cluster = await OutputsAsync("cluster", token);
        RequireNativeNetwork(cluster);
        var deployedCompute = await ReadDeployedComputeAsync(token);
        var instances = await Cloud.ReadWorkerInstancesAsync(cluster.GetProperty("nodePoolId").GetString()!, token);
        state = state with { Phase = "quiescing", OperationId = held!.OwnerId,
            Postgres = await Kubernetes.CapturePostgresAsync(token), PendingNative = null, PendingVolumeBackupId = null };
        await SaveAsync(token);
        await Kubernetes.FreezeAsync(token);
        var preparation = await Native.PrepareAsync(state.OperationId, token);
        await Kubernetes.StopWritersAsync(token);
        var native = await Native.TakeAsync(preparation,
            ct => Kubernetes.ScaleAsync("graylog", "statefulset", "graylog-datanode", 0, ct), token);
        state = state with { PendingNative = native };
        await SaveAsync(token);
        await Kubernetes.StopDataAsync(token);
        await ApplyClusterAsync(0, yes, token, deployedCompute);
        await Cloud.RequireDetachedAsync(state.VolumeIds.Values.ToArray(), instances, token);
        state = state with { Phase = "offline" };
        await SaveAsync(token);
        var backup = await Volumes.CreateAsync(state.VolumeGroupId, state.VolumeIds, state.OperationId, async (id, cancellation) =>
        {
            state = state with { PendingVolumeBackupId = id };
            await SaveAsync(cancellation);
        }, token);
        var record = new BackupRecord(state.OperationId.ToString("N"), state.CredentialGeneration, state.GitOpsCommit,
            state.ActiveStorageGeneration, new Dictionary<string, string>(state.VolumeIds), backup, native, state.Postgres!);
        state = state with { Phase = "paused", Backups = [.. state.Backups, record], PendingNative = null, PendingVolumeBackupId = null };
        await SaveAsync(token);
        Console.WriteLine($"Complete checkpoint {record.Id} verified. Workers are stopped and all data volumes detached.");
    }

    private async Task ResolvePendingBackupAsync(CancellationToken token)
    {
        if (state.PendingNative is null) return;
        if (state.Phase is "offline" or "paused")
        {
            var resolved = await Volumes.FindAsync(state.OperationId, state.VolumeIds, token);
            if (resolved is not null)
            {
                if (state.PendingVolumeBackupId is not null && resolved.BackupId != state.PendingVolumeBackupId)
                    throw new InvalidOperationException("The pending checkpoint resolves to another cloud backup identity.");
                var record = new BackupRecord(state.OperationId.ToString("N"), state.CredentialGeneration, state.GitOpsCommit,
                    state.ActiveStorageGeneration, new Dictionary<string, string>(state.VolumeIds), resolved, state.PendingNative, state.Postgres!);
                state = state with { Backups = [.. state.Backups, record] };
            }
            else if (state.PendingVolumeBackupId is not null)
                throw new InvalidOperationException("The recorded pending backup disappeared. Investigate before resuming writers.");
        }
        // An export interrupted before its offline cloud checkpoint is not a complete backup.
        state = state with { PendingNative = null, PendingVolumeBackupId = null };
        await SaveAsync(token);
    }

    private async Task PruneNativeBackupsAsync(CancellationToken token)
    {
        var latest = state.Backups.Where(b => b.StorageGeneration == state.ActiveStorageGeneration && b.GitOpsCommit == state.GitOpsCommit)
            .MaxBy(b => b.Volumes.CreatedAt);
        if (latest is null) return;
        var obsolete = state.Backups.Where(b => b.Id != latest.Id && b.Volumes.CreatedAt <= latest.Volumes.CreatedAt)
            .Select(b => b.Native).ToArray();
        if (obsolete.Length == 0) return;
        await VerifyBackupAsync(latest, token);
        await Native.PruneAsync(latest.Native, obsolete, token);
    }

    private async Task RetainBackupsAsync(CancellationToken token)
    {
        var ordered = state.Backups.OrderByDescending(b => b.Volumes.CreatedAt).ToArray();
        var referencedMembers = state.StorageGenerations.Where(g => g.BackupIds is not null)
            .SelectMany(g => g.BackupIds!.Values).ToHashSet(StringComparer.Ordinal);
        var retained = ordered.Take(configuration.BackupRetentionCount).Concat(ordered.Where(b =>
            b.Volumes.VolumeBackupIds.Values.Any(referencedMembers.Contains) || b.Id == state.RestoreBackupId)).ToArray();
        var protectedIds = retained.Select(b => b.Volumes.BackupId).ToHashSet(StringComparer.Ordinal);
        foreach (var record in ordered.Where(b => !protectedIds.Contains(b.Volumes.BackupId)))
        {
            // An interrupted delete leaves an extra cloud backup, never a dangling recovery authority.
            state = state with { Backups = state.Backups.Where(b => b.Id != record.Id).ToArray() };
            await SaveAsync(token);
            await Volumes.DeleteAsync(record.Volumes.BackupId, protectedIds, token);
        }
    }

    private async Task RemoveClusterAsync(bool yes, CancellationToken token)
    {
        if (!state.Backups.Any(b => b.StorageGeneration == state.ActiveStorageGeneration))
            throw new InvalidOperationException("Removing the cluster requires a complete backup of its active generation.");
        await Cloud.RequireDetachedAsync(state.VolumeIds.Values.ToArray(), Array.Empty<string>(), token);
        var retry = state.Phase == "removing";
        state = state with { Phase = "removing" };
        await SaveAsync(token);
        var outputs = await OutputsAsync("cluster", token);
        if (outputs.TryGetProperty("clusterId", out _))
        {
            await WriteKubeconfigAsync(token);
            await Kubernetes.RemoveClaimsAsync(state.VolumeIds, token, allowPartial: retry);
        }
        await StackAsync("destroy", "bootstrap", yes, token);
        await StackAsync("destroy", "cluster", yes, token);
        state = state with { Phase = "down" };
        await SaveAsync(token);
    }

    private async Task RestoreAsync(OperatorCommand command, CancellationToken token)
    {
        if (!command.AllowAdditionalStorage || command.Backup is null)
            throw new ArgumentException("Restore requires a selected complete backup and explicit additional-storage consent.");
        if (state.Phase is not ("paused" or "down"))
            throw new InvalidOperationException("Pause or down before restore. The source generation must be offline and remain protected.");
        if (state.StorageGenerations.Length >= 3)
            throw new InvalidOperationException("Three protected generations already exist. Review and retire obsolete cloud storage before another restore.");
        var source = state.Backups.SingleOrDefault(b => b.Id == command.Backup)
            ?? throw new InvalidOperationException("The selected complete checkpoint does not exist.");
        if (source.GitOpsCommit != state.GitOpsCommit)
            throw new InvalidOperationException("Select the checkpoint's GitOps release with release before restoring it. Native recovery requires the original release.");
        var verified = await VerifyBackupAsync(source, token);
        var sourceProfile = state.StorageGenerations.Single(g => g.Id == source.StorageGeneration);
        Console.WriteLine($"Restore adds {sourceProfile.TotalSizeInGbs} GB of protected data disks and retains all source volumes.");
        if (state.Phase != "down") await RemoveClusterAsync(command.Yes, token);
        var generation = new StorageGeneration
        {
            Id = Guid.NewGuid().ToString("N"), BackupIds = verified.VolumeBackupIds,
            Volumes = StorageClaims.All.ToDictionary(c => c.Key, c => sourceProfile.Volume(c.Key))
        };
        state = state with { Phase = "restoring", ActiveStorageGeneration = generation.Id,
            StorageGenerations = [.. state.StorageGenerations, generation], VolumeIds = [], VolumeGroupId = "",
            Postgres = source.Postgres, RestoreBackupId = source.Id };
        await SaveAsync(token);
        await StartAsync(command.Yes, token);
        await BackupAndPauseAsync(command.Yes, token);
        await StartAsync(command.Yes, token);
    }

    private async Task SelectReleaseAsync(CancellationToken token)
    {
        if (state.Phase is not ("paused" or "down" or "starting") || state.PendingNative is not null ||
            state.PendingVolumeBackupId is not null || state.RestoreBackupId is not null || state.NativeRestoreStarted)
            throw new InvalidOperationException("Release selection requires paused/down infrastructure or an interrupted startup without a pending backup or restore.");
        var checkpoint = state.Backups.Where(b => b.StorageGeneration == state.ActiveStorageGeneration)
            .MaxBy(b => b.Volumes.CreatedAt)
            ?? throw new InvalidOperationException("Release selection requires a complete checkpoint of the current storage generation.");
        if (!MappingsEqual(checkpoint.SourceVolumes, state.VolumeIds))
            throw new InvalidOperationException("The release checkpoint does not match the current volume identities.");
        await FetchSnapshotAsync(token);
        await VerifyBackupAsync(checkpoint, token);
        if (state.Phase == "starting")
        {
            var cluster = await OutputsAsync("cluster", token);
            if (cluster.TryGetProperty("clusterId", out _))
            {
                RequireNativeNetwork(cluster);
                operationMayBeIncomplete = true;
                await WriteKubeconfigAsync(token);
                var crd = await commands.RunAsync("kubectl", ["get", "crd", "applications.argoproj.io", "--ignore-not-found", "-o", "name"], Environment, false, token);
                if (!string.IsNullOrWhiteSpace(crd)) await Kubernetes.FreezeAsync(token);
                await Kubernetes.StopExistingWritersAsync(token);
            }
        }
        state = state with { GitOpsCommit = configuration.Infrastructure.GitOpsCommit, GitOpsTag = configuration.Infrastructure.GitOpsTag };
        await SaveAsync(token);
        Console.WriteLine("Reviewed release selected; credentials, volumes and historical checkpoints are unchanged. Resume only a release compatible with the retained data, or restore its matching checkpoint while offline.");
    }

    private async Task<VolumeBackup> VerifyBackupAsync(BackupRecord checkpoint, CancellationToken token)
    {
        var sourceProfile = state.StorageGenerations.Single(g => g.Id == checkpoint.StorageGeneration);
        var verified = await VolumesFor(sourceProfile).DescribeAsync(checkpoint.Volumes.BackupId, checkpoint.SourceVolumes, token);
        if (!MappingsEqual(verified.VolumeBackupIds, checkpoint.Volumes.VolumeBackupIds))
            throw new InvalidOperationException("The checkpoint member identities changed.");
        return verified;
    }

    private void RequireRunning()
    {
        if (state.Phase != "running") throw new InvalidOperationException("This operation requires a healthy running deployment. Resume the recorded state first.");
    }
    private static void RequireNativeNetwork(JsonElement outputs)
    {
        if (!outputs.TryGetProperty("podNetwork", out var network) || network.GetString() != "OCI_VCN_IP_NATIVE")
            throw new InvalidOperationException("This cluster is outside the native-VCN policy profile. Do not replace a live pod network in place.");
    }
    private static bool MappingsEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private Task AssertOwnedAsync(CancellationToken token) => Store.AssertOwnedAsync(held
        ?? throw new InvalidOperationException("No shared operation lock is held."), token);
    private async Task ApplyClusterAsync(int workers, bool yes, CancellationToken token, InfrastructureSettings? compute = null)
    {
        compute ??= workers == 0 ? await ReadDeployedComputeAsync(token) : configuration.Infrastructure;
        await ConfigureAsync("cluster", workers, token, compute);
        await StackAsync("up", "cluster", yes, token);
    }
    private async Task<InfrastructureSettings> ReadDeployedComputeAsync(CancellationToken token)
    {
        // Preview can overwrite stack configuration. The checkpoint retains the deployed inputs.
        using var exported = JsonDocument.Parse(await PulumiAsync(["stack", "export", "--stack", configuration.Stack("cluster")], false, token));
        var pool = exported.RootElement.GetProperty("deployment").GetProperty("resources").EnumerateArray()
            .Single(r => r.GetProperty("type").GetString() == "oci:ContainerEngine/nodePool:NodePool").GetProperty("inputs");
        var shape = pool.GetProperty("nodeShapeConfig");
        var source = pool.GetProperty("nodeSourceDetails");
        var nodeConfig = pool.GetProperty("nodeConfigDetails");
        var placement = nodeConfig.GetProperty("placementConfigs").EnumerateArray().Single();
        if (placement.GetProperty("availabilityDomain").GetString() != configuration.Infrastructure.AvailabilityDomain ||
            pool.GetProperty("compartmentId").GetString() != configuration.Infrastructure.ClusterCompartmentId)
            throw new InvalidOperationException("Deployed workers do not share the recorded storage availability domain.");
        var ocpus = shape.GetProperty("ocpus").GetDouble();
        if (!double.IsFinite(ocpus) || ocpus < 1 || ocpus > int.MaxValue || ocpus != Math.Truncate(ocpus))
            throw new InvalidOperationException("Deployed worker OCPUs are outside the supported integer allocation profile.");
        var deployed = configuration.Infrastructure with
        {
            Workers = 0,
            NodeShape = pool.GetProperty("nodeShape").GetString()!,
            NodeOcpus = (int)ocpus,
            NodeMemoryInGbs = shape.GetProperty("memoryInGbs").GetDouble(),
            NodeImageId = source.GetProperty("imageId").GetString()!,
            BootVolumeSizeInGbs = int.Parse(source.GetProperty("bootVolumeSizeInGbs").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            KubernetesVersion = pool.GetProperty("kubernetesVersion").GetString()!,
            MaxPodsPerNode = nodeConfig.GetProperty("nodePoolPodNetworkOptionDetails").GetProperty("maxPodsPerNode").GetInt32(),
            FaultDomains = placement.TryGetProperty("faultDomains", out var domains) && domains.ValueKind == JsonValueKind.Array
                ? domains.EnumerateArray().Select(fd => fd.GetString()!).ToArray() : []
        };
        deployed.Validate("cluster");
        return deployed;
    }
    private static bool IsEmptyStack(JsonElement checkpoint)
    {
        if (checkpoint.ValueKind != JsonValueKind.Object) return false;
        // Pulumi omits deployment/resources for stacks that have never registered resources.
        if (!checkpoint.TryGetProperty("deployment", out var deployment) || deployment.ValueKind == JsonValueKind.Null)
            return true;
        if (deployment.ValueKind != JsonValueKind.Object) return false;
        // An interrupted create may exist only in pending_operations, before resource registration.
        if (deployment.TryGetProperty("pending_operations", out var pending) && pending.ValueKind != JsonValueKind.Null &&
            (pending.ValueKind != JsonValueKind.Array || pending.GetArrayLength() != 0))
            return false;
        if (!deployment.TryGetProperty("resources", out var resources) || resources.ValueKind == JsonValueKind.Null)
            return true;
        return resources.ValueKind == JsonValueKind.Array && resources.EnumerateArray().All(resource =>
            resource.ValueKind == JsonValueKind.Object && resource.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String && type.GetString() == "pulumi:pulumi:Stack");
    }

    private async Task ConfigureAsync(string role, int workers, CancellationToken token, InfrastructureSettings? compute = null)
    {
        var settings = (compute ?? configuration.Infrastructure) with
        {
            Workers = workers, DurableStack = configuration.Stack("durable"), ClusterStack = configuration.Stack("cluster"),
            GitOpsDirectory = Snapshot, KubeconfigPath = Kubeconfig,
            StorageGenerations = state.StorageGenerations, ActiveStorageGeneration = state.ActiveStorageGeneration
        };
        await PulumiAsync(["config", "set", "drn-pulumi:settings", JsonSerializer.Serialize(settings, OperatorConfiguration.JsonOptions),
            "--stack", configuration.Stack(role)], false, token);
    }
    private async Task<JsonElement> OutputsAsync(string role, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await PulumiAsync(["stack", "output", "--json", "--stack", configuration.Stack(role)], false, token));
        return document.RootElement.Clone();
    }
    private async Task WriteKubeconfigAsync(CancellationToken token)
    {
        var outputs = await OutputsAsync("cluster", token);
        var cluster = outputs.TryGetProperty("clusterId", out var id) ? id.GetString() : null;
        if (cluster is null || !cluster.StartsWith("ocid1.cluster.", StringComparison.Ordinal))
            throw new InvalidOperationException("Cluster output is missing. Apply the cluster stack before requesting access.");
        var temporary = Path.Combine(LocalDirectory, $"kubeconfig-{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (new FileStream(temporary, options)) { }
            await commands.RunAsync("oci", ["ce", "cluster", "create-kubeconfig", "--cluster-id", cluster,
                "--region", configuration.Infrastructure.Region, "--file", temporary,
                "--token-version", "2.0.0", "--kube-endpoint", "PUBLIC_ENDPOINT"], Environment, false, token);
            if (new FileInfo(temporary).Length == 0) throw new InvalidOperationException("OCI returned an empty kubeconfig.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, Kubeconfig, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
    private async Task FetchSnapshotAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Snapshot)!);
        var hooks = Path.Combine(LocalDirectory, "empty-git-hooks");
        Directory.CreateDirectory(hooks);
        if (!Directory.Exists(Snapshot))
            await commands.RunAsync("git", ["clone", "--no-checkout", "--filter=blob:none", "--", configuration.Infrastructure.GitOpsRepository, Snapshot], Environment, false, token);
        if ((await GitAsync(["remote", "get-url", "origin"], token)).Trim() != configuration.Infrastructure.GitOpsRepository)
            throw new InvalidOperationException("The cached GitOps origin differs from the selected repository.");
        await GitAsync(["fetch", "--depth", "1", "origin", configuration.Infrastructure.GitOpsTag], token);
        if ((await GitAsync(["rev-parse", "FETCH_HEAD^{commit}"], token)).Trim() != configuration.Infrastructure.GitOpsCommit)
            throw new InvalidOperationException("The GitOps tag does not resolve to the configured commit. Review the release before retrying.");
        await GitAsync(["-c", $"core.hooksPath={hooks}", "checkout", "--detach", configuration.Infrastructure.GitOpsCommit], token);
        if (!string.IsNullOrWhiteSpace(await GitAsync(["status", "--porcelain", "--untracked-files=all"], token)))
            throw new InvalidOperationException("The cached GitOps snapshot has local changes. Preserve them and remove the cache before retrying.");
        InfrastructureSettings.ValidateGitOpsSnapshot(Snapshot);
    }
    private Task<string> GitAsync(string[] args, CancellationToken token) =>
        commands.RunAsync("git", ["-C", Snapshot, .. args], Environment, false, token);
    private Task<string> StackAsync(string operation, string role, bool yes, CancellationToken token) =>
        PulumiAsync([operation, "--stack", configuration.Stack(role), "--diff", .. (yes ? new[] { "--yes" } : Array.Empty<string>())], true, token);
    private Task<string> PulumiAsync(IReadOnlyList<string> args, bool display, CancellationToken token) =>
        commands.RunAsync("pulumi", ["--cwd", ProjectDirectory, .. args], Environment, display, token);
    private void PrepareLocalDirectory()
    {
        if (new DirectoryInfo(LocalDirectory).LinkTarget is not null)
            throw new InvalidOperationException(".local must be a private directory, not a symbolic link.");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(LocalDirectory);
        else
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(LocalDirectory, mode);
            File.SetUnixFileMode(LocalDirectory, mode);
        }
    }
}
