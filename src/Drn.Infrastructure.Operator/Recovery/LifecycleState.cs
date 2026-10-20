using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Drn.Pulumi;

namespace Drn.Infrastructure.Operator.Recovery;

public sealed record PostgresRecovery(JsonObject Cluster, JsonObject Claim, JsonObject Status);

public sealed record BackupRecord(string Id, string CredentialGeneration, string GitOpsCommit,
    string StorageGeneration, Dictionary<string, string> SourceVolumes, VolumeBackup Volumes,
    NativeBackupManifest Native, PostgresRecovery Postgres);

/// <summary>The encrypted, conditionally written authority for a single staging deployment.</summary>
public sealed record LifecycleState
{
    public int SchemaVersion { get; init; } = 2;
    public string Organization { get; init; } = "";
    public string Region { get; init; } = "";
    public string AvailabilityDomain { get; init; } = "";
    public string ClusterCompartmentId { get; init; } = "";
    public string DurableCompartmentId { get; init; } = "";
    public string GitOpsCommit { get; init; } = "";
    public string GitOpsTag { get; init; } = "";
    public string CredentialGeneration { get; init; } = "";
    public string CredentialHash { get; init; } = "";
    public string Phase { get; init; } = "initialized";
    public Guid OperationId { get; init; }
    public string ActiveStorageGeneration { get; init; } = "initial";
    public StorageGeneration[] StorageGenerations { get; init; } = [new()];
    public Dictionary<string, string> VolumeIds { get; init; } = [];
    public string VolumeGroupId { get; init; } = "";
    public PostgresRecovery? Postgres { get; init; }
    public NativeBackupManifest? PendingNative { get; init; }
    public string? PendingVolumeBackupId { get; init; }
    public BackupRecord[] Backups { get; init; } = [];
    public string? RestoreBackupId { get; init; }
    public bool NativeRestoreStarted { get; init; }

    public void Validate(OperatorConfiguration configuration, CredentialCheckpoint checkpoint, bool allowReleaseMismatch = false)
    {
        var settings = configuration.Infrastructure;
        if (SchemaVersion is not (1 or 2) || Organization != configuration.Organization || Region != settings.Region ||
            AvailabilityDomain != settings.AvailabilityDomain || ClusterCompartmentId != settings.ClusterCompartmentId ||
            DurableCompartmentId != settings.DurableCompartmentId ||
            !Regex.IsMatch(GitOpsCommit, "^[0-9a-f]{40}$") || !InfrastructureSettings.IsGitOpsTag(GitOpsTag) ||
            (!allowReleaseMismatch && (GitOpsCommit != settings.GitOpsCommit || GitOpsTag != settings.GitOpsTag)) ||
            CredentialGeneration != checkpoint.Generation || CredentialHash != checkpoint.StableIdentityHash() ||
            Phase is not ("initialized" or "starting" or "running" or "quiescing" or "offline" or "paused" or "removing" or "down" or "restoring"))
            throw new InvalidOperationException("Recovery state, credential checkpoint and selected deployment do not match.");
        checkpoint.Validate(DateTimeOffset.UtcNow);
        (settings with { StorageGenerations = StorageGenerations, ActiveStorageGeneration = ActiveStorageGeneration }).Validate("durable");
        if (SchemaVersion == 1 && StorageGenerations.Any(g => !g.HasSameVolumes(new StorageGeneration())))
            throw new InvalidOperationException("Legacy recovery state cannot declare non-default storage profiles.");
        if (!StorageGenerations[0].HasSameVolumes(settings.StorageGenerations[0]))
            throw new InvalidOperationException("Configured storage differs from the recorded initial generation. Restore its original sizes and tiers. Resizing or changing retained storage requires a separate migration.");
        if (VolumeIds.Count > 0) VolumeBackups.ValidateClaims(VolumeIds);
        if (Phase is "running" or "quiescing" or "offline" or "paused" or "removing" or "down" &&
            (VolumeIds.Count != StorageClaims.All.Count || string.IsNullOrWhiteSpace(VolumeGroupId) || Postgres is null))
            throw new InvalidOperationException("An initialized lifecycle phase requires volume identities and PostgreSQL recovery metadata.");
        if (Backups.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != Backups.Length)
            throw new InvalidOperationException("Recovery state contains duplicate checkpoint identities.");
        foreach (var backup in Backups)
        {
            VolumeBackups.ValidateClaims(backup.SourceVolumes);
            VolumeBackups.ValidateClaims(backup.Volumes.VolumeBackupIds);
            NativeDatabaseBackup.ValidateManifest(backup.Native);
            if (backup.Id != backup.Native.OperationId.ToString("N") || !Regex.IsMatch(backup.GitOpsCommit, "^[0-9a-f]{40}$") ||
                backup.CredentialGeneration != CredentialGeneration ||
                !StorageGenerations.Any(g => g.Id == backup.StorageGeneration))
                throw new InvalidOperationException("Backup metadata is not bound to this deployment and credential generation.");
        }
        if (PendingNative is not null)
        {
            NativeDatabaseBackup.ValidateManifest(PendingNative);
            if (PendingNative.OperationId != OperationId)
                throw new InvalidOperationException("Pending native backup belongs to another operation.");
        }
        if (RestoreBackupId is not null && !Backups.Any(b => b.Id == RestoreBackupId))
            throw new InvalidOperationException("The pending restore checkpoint is missing.");
        if (NativeRestoreStarted && RestoreBackupId is null)
            throw new InvalidOperationException("An interrupted native restore requires its source checkpoint.");
    }

    public void RequireFreshBackup(DateTimeOffset now, int maximumAgeHours)
    {
        var newest = Backups.Where(b => b.StorageGeneration == ActiveStorageGeneration && b.GitOpsCommit == GitOpsCommit)
            .MaxBy(b => b.Volumes.CreatedAt);
        if (newest is null || newest.Volumes.CreatedAt > now.AddMinutes(5) ||
            now - newest.Volumes.CreatedAt > TimeSpan.FromHours(maximumAgeHours))
            throw new InvalidOperationException("No recent complete backup exists for the active generation. Run backup and inspect its result.");
    }
}

/// <summary>Checks cross-host ownership before each tool action. The store itself uses the raw runner.</summary>
public sealed class CoordinatedRunner(ICommandRunner inner, Func<CancellationToken, Task> assertOwned) : ICommandRunner
{
    public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, bool displayOutput, CancellationToken cancellationToken,
        string? standardInput = null)
    {
        await assertOwned(cancellationToken);
        return await inner.RunAsync(executable, arguments, environment, displayOutput, cancellationToken, standardInput);
    }
}
