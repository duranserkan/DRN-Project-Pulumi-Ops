using System.Text.Json;
using Drn.Pulumi;

namespace Drn.Infrastructure.Operator.Recovery;

public sealed record VolumeBackup(string BackupId, string GroupId, DateTimeOffset CreatedAt,
    Dictionary<string, string> VolumeBackupIds);

/// <summary>Creates one offline group checkpoint and verifies every member's source identity.</summary>
public sealed class VolumeBackups(ICommandRunner runner, IReadOnlyDictionary<string, string> environment,
    string durableCompartmentId, StorageGeneration? generation = null)
{
    private readonly StorageGeneration profile = generation ?? new StorageGeneration();
    public async Task<VolumeBackup> CreateAsync(string groupId, IReadOnlyDictionary<string, string> volumeIdsByClaim,
        Guid operationId, Func<string, CancellationToken, Task> recordPending, CancellationToken cancellationToken)
    {
        ValidateClaims(volumeIdsByClaim);
        profile.ValidateVolumes();
        if (operationId == Guid.Empty) throw new ArgumentException("A backup requires a recorded operation ID.");
        using (var group = await GetAsync("volume-group", "--volume-group-id", groupId, cancellationToken))
        {
            var data = group.RootElement.GetProperty("data");
            RequireAvailable(data);
            if (Required(data, "id") != groupId || data.GetProperty("compartment-id").GetString() != durableCompartmentId ||
                !data.GetProperty("volume-ids").EnumerateArray().Select(item => item.GetString()!)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(volumeIdsByClaim.Values))
                throw new InvalidOperationException("Volume group does not contain exactly the seven selected durable volumes.");
        }

        // The identifier is checkpointed before waiting. An ambiguous create never starts another phase.
        using var tags = new PrivateFile(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        { ["project"] = "DRN-Project", ["environment"] = "staging", ["operation"] = operationId.ToString("N") }));
        using var created = JsonDocument.Parse(await RunAsync(["volume-group-backup", "create", "--volume-group-id", groupId,
            "--type", "FULL", "--display-name", $"drn-staging-{operationId:N}", "--freeform-tags", $"file://{tags.Path}"], cancellationToken));
        var id = Required(created.RootElement.GetProperty("data"), "id");
        await recordPending(id, cancellationToken);
        return await DescribeAsync(id, volumeIdsByClaim, cancellationToken);
    }

    public async Task<VolumeBackup?> FindAsync(Guid operationId, IReadOnlyDictionary<string, string> volumeIdsByClaim,
        CancellationToken cancellationToken)
    {
        ValidateClaims(volumeIdsByClaim);
        if (operationId == Guid.Empty) throw new ArgumentException("Backup lookup requires the recorded operation ID.");
        using var result = JsonDocument.Parse(await RunAsync(["volume-group-backup", "list", "--compartment-id",
            durableCompartmentId, "--all"], cancellationToken));
        var matches = result.RootElement.GetProperty("data").EnumerateArray().Where(item =>
            item.TryGetProperty("freeform-tags", out var tags) && tags.ValueKind == JsonValueKind.Object &&
            Tag(tags, "project") == "DRN-Project" && Tag(tags, "environment") == "staging" &&
            Tag(tags, "operation") == operationId.ToString("N")).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || Required(matches[0], "compartment-id") != durableCompartmentId ||
            Required(matches[0], "source-type") != "MANUAL")
            throw new InvalidOperationException("Pending operation does not identify exactly one owned manual backup. Recovery remains locked.");
        return await DescribeAsync(Required(matches[0], "id"), volumeIdsByClaim, cancellationToken);
    }

    private async Task<JsonDocument> GetAvailableBackupAsync(string backupId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddHours(24);
        while (true)
        {
            var result = await GetAsync("volume-group-backup", "--volume-group-backup-id", backupId, cancellationToken);
            var data = result.RootElement.GetProperty("data");
            var state = Required(data, "lifecycle-state");
            var owned = Required(data, "id") == backupId && Required(data, "compartment-id") == durableCompartmentId;
            if (owned && state == "AVAILABLE") return result;
            result.Dispose();
            if (!owned) throw new InvalidOperationException("Backup belongs to another durable compartment or identity.");
            if (state is not ("REQUEST_RECEIVED" or "CREATING" or "COMMITTED") || DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException("Volume group backup did not become available. Its ID remains in recovery state.");
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }

    public async Task<VolumeBackup> DescribeAsync(string backupId, IReadOnlyDictionary<string, string> volumeIdsByClaim,
        CancellationToken cancellationToken)
    {
        ValidateClaims(volumeIdsByClaim);
        profile.ValidateVolumes();
        using var group = await GetAvailableBackupAsync(backupId, cancellationToken);
        var data = group.RootElement.GetProperty("data");
        RequireAvailable(data);
        if (Required(data, "compartment-id") != durableCompartmentId || Required(data, "id") != backupId)
            throw new InvalidOperationException("Backup belongs to another durable compartment or identity.");
        var backupIds = data.GetProperty("volume-backup-ids").EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (backupIds.Length != StorageClaims.All.Count || backupIds.Distinct(StringComparer.Ordinal).Count() != backupIds.Length)
            throw new InvalidOperationException("Backup must contain exactly seven distinct volume backups.");
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var memberId in backupIds)
        {
            using var member = await GetAsync("backup", "--volume-backup-id", memberId, cancellationToken);
            var volume = member.RootElement.GetProperty("data");
            RequireAvailable(volume);
            var source = Required(volume, "volume-id");
            var claim = volumeIdsByClaim.SingleOrDefault(entry => entry.Value == source).Key;
            if (claim is null || Required(volume, "id") != memberId || Required(volume, "compartment-id") != durableCompartmentId ||
                volume.GetProperty("size-in-gbs").GetInt64() != profile.Volume(claim).SizeInGbs || !mapping.TryAdd(claim, memberId))
                throw new InvalidOperationException("Backup member mapping, compartment or size does not match the selected generation.");
        }
        ValidateClaims(mapping);
        return new VolumeBackup(backupId, Required(data, "volume-group-id"),
            data.GetProperty("time-created").GetDateTimeOffset(), mapping);
    }

    public static void ValidateClaims(IReadOnlyDictionary<string, string> values)
    {
        if (!StorageClaims.All.Select(claim => claim.Key).ToHashSet(StringComparer.Ordinal).SetEquals(values.Keys) ||
            values.Values.Any(string.IsNullOrWhiteSpace) || values.Values.Distinct(StringComparer.Ordinal).Count() != StorageClaims.All.Count)
            throw new InvalidOperationException("The recovery generation requires exactly seven distinct, named volume identities.");
    }

    public async Task DeleteAsync(string backupId, IReadOnlySet<string> protectedBackupIds, CancellationToken cancellationToken)
    {
        if (protectedBackupIds.Count == 0 || protectedBackupIds.Contains(backupId))
            throw new InvalidOperationException("Retention must preserve the verified current checkpoint and every active recovery reference.");
        using var result = await GetAsync("volume-group-backup", "--volume-group-backup-id", backupId, cancellationToken);
        var data = result.RootElement.GetProperty("data");
        RequireAvailable(data);
        var tags = data.GetProperty("freeform-tags");
        if (Required(data, "id") != backupId || Required(data, "compartment-id") != durableCompartmentId ||
            Required(data, "source-type") != "MANUAL" ||
            Required(tags, "project") != "DRN-Project" || Required(tags, "environment") != "staging" ||
            !Guid.TryParseExact(Required(tags, "operation"), "N", out _))
            throw new InvalidOperationException("Retention can delete only this deployment's explicit manual checkpoint backups.");
        await RunAsync(["volume-group-backup", "delete", "--volume-group-backup-id", backupId, "--force"], cancellationToken);
    }

    private async Task<JsonDocument> GetAsync(string resource, string key, string id, CancellationToken cancellationToken) =>
        JsonDocument.Parse(await RunAsync([resource, "get", key, id], cancellationToken));

    private Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        runner.RunAsync("oci", ["bv", .. args, "--output", "json", "--no-retry"], environment, false, cancellationToken);

    private static string Required(JsonElement value, string key) => value.GetProperty(key).GetString() is { Length: > 0 } text
        ? text : throw new InvalidOperationException("OCI backup metadata is incomplete.");
    private static string? Tag(JsonElement tags, string name) =>
        tags.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static void RequireAvailable(JsonElement value)
    {
        if (Required(value, "lifecycle-state") != "AVAILABLE")
            throw new InvalidOperationException("Recovery requires an AVAILABLE volume group and every backup member.");
    }
}
