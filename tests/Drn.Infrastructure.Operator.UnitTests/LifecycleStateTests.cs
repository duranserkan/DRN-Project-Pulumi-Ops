using System.Text.Json.Nodes;
using Drn.Infrastructure.Operator.Recovery;
using Drn.Pulumi;
using Xunit;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed class LifecycleStateTests
{
    [Fact]
    public void LegacyStorageDefaultsRemainReadableButCannotBeResizedThroughConfiguration()
    {
        var config = OperatorTestData.Configuration();
        var credentials = OperatorTestData.Credentials();
        var legacy = OperatorTestData.InitializedState(config, credentials) with { SchemaVersion = 1 };
        legacy.Validate(config, credentials);
        var explicitDefaults = config with { Infrastructure = config.Infrastructure with { StorageGenerations = [new()
        { Volumes = new() { ["sample"] = new VolumeSettings() } }] } };
        legacy.Validate(explicitDefaults, credentials);
        var changed = config with { Infrastructure = config.Infrastructure with { StorageGenerations = [new()
        { Volumes = new() { ["sample"] = new VolumeSettings { SizeInGbs = 100 } } }] } };
        Assert.Throws<InvalidOperationException>(() => legacy.Validate(changed, credentials));
        var revised = OperatorTestData.InitializedState(changed, credentials);
        revised.Validate(changed, credentials);
        Assert.Throws<InvalidOperationException>(() => (revised with { SchemaVersion = 1 }).Validate(changed, credentials));
        var differentTier = changed with { Infrastructure = changed.Infrastructure with { StorageGenerations = [new()
        { Volumes = new() { ["sample"] = new VolumeSettings { SizeInGbs = 100, Performance = "lower-cost" } } }] } };
        Assert.Throws<InvalidOperationException>(() => revised.Validate(differentTier, credentials));
    }

    [Theory]
    [DataInlineUnit(AuthorityFault.Organization)]
    [DataInlineUnit(AuthorityFault.Release)]
    [DataInlineUnit(AuthorityFault.CredentialGeneration)]
    [DataInlineUnit(AuthorityFault.CredentialHash)]
    [DataInlineUnit(AuthorityFault.IncompleteRunningState)]
    public void RecoveryAuthorityCannotMoveBetweenAccountsReleasesOrCredentialGenerations(AuthorityFault fault)
    {
        var config = OperatorTestData.Configuration();
        var credentials = OperatorTestData.Credentials();
        var state = OperatorTestData.InitializedState(config, credentials);
        state.Validate(config, credentials);
        var invalid = fault switch
        {
            AuthorityFault.Organization => state with { Organization = "another" },
            AuthorityFault.Release => state with { GitOpsCommit = new string('b', 40) },
            AuthorityFault.CredentialGeneration => state with { CredentialGeneration = Guid.NewGuid().ToString("N") },
            AuthorityFault.CredentialHash => state with { CredentialHash = new string('0', 64) },
            AuthorityFault.IncompleteRunningState => state with { Phase = "running" },
            _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, null)
        };

        Assert.Throws<InvalidOperationException>(() => invalid.Validate(config, credentials));
    }

    [Fact]
    public void FreshnessUsesTheActiveGenerationAndRejectsFutureOrStaleCheckpoints()
    {
        var now = OperatorTestData.Timestamp;
        var backup = Backup("initial", now.AddHours(-1));
        var state = new LifecycleState { GitOpsCommit = backup.GitOpsCommit, Backups = [backup] };
        state.RequireFreshBackup(now, 26);
        Assert.Throws<InvalidOperationException>(() => state.RequireFreshBackup(now.AddHours(27), 26));
        Assert.Throws<InvalidOperationException>(() => (state with { ActiveStorageGeneration = new string('a', 32) }).RequireFreshBackup(now, 26));
        Assert.Throws<InvalidOperationException>(() => (state with { Backups = [Backup("initial", now.AddHours(1))] }).RequireFreshBackup(now, 26));
        Assert.Throws<InvalidOperationException>(() => (state with { GitOpsCommit = new string('b', 40) }).RequireFreshBackup(now, 26));
    }

    [Fact]
    public void ReleaseSelectionPreservesCredentialIdentityAndHistoricalReleaseMetadata()
    {
        var original = OperatorTestData.Configuration();
        var credentials = OperatorTestData.Credentials();
        var target = original with { Infrastructure = original.Infrastructure with
        { GitOpsCommit = new string('b', 40), GitOpsTag = "refs/tags/2026.10.2-preview001" } };
        var oldState = OperatorTestData.InitializedState(original, credentials);
        Assert.Throws<InvalidOperationException>(() => oldState.Validate(target, credentials));
        oldState.Validate(target, credentials, allowReleaseMismatch: true);
        Assert.Throws<InvalidOperationException>(() => oldState.Validate(target with { Organization = "another" }, credentials, allowReleaseMismatch: true));

        var ca = credentials.Objects.Single(item => item["metadata"]?["name"]?.GetValue<string>() == "postgresql-ca");
        var backup = Backup("initial", DateTimeOffset.UtcNow) with { CredentialGeneration = credentials.Generation };
        backup = backup with { Native = backup.Native with
        { CaCertificate = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ca["data"]!["ca.crt"]!.GetValue<string>())) } };
        var selected = oldState with
        { GitOpsCommit = target.Infrastructure.GitOpsCommit, GitOpsTag = target.Infrastructure.GitOpsTag, Backups = [backup] };
        selected.Validate(target, credentials);
        Assert.Equal(original.Infrastructure.GitOpsCommit, credentials.GitOpsCommit);
        Assert.Equal(original.Infrastructure.GitOpsCommit, Assert.Single(selected.Backups).GitOpsCommit);
        Assert.Throws<InvalidOperationException>(() => (selected with
        { Backups = [backup with { GitOpsCommit = "invalid" }] }).Validate(target, credentials));
        Assert.Throws<InvalidOperationException>(() => (selected with
        { Backups = [backup with { CredentialGeneration = "another" }] }).Validate(target, credentials));
    }

    [Theory]
    [DataInlineUnit(-93600, true)]
    [DataInlineUnit(-93601, false)]
    [DataInlineUnit(300, true)]
    [DataInlineUnit(301, false)]
    public void FreshnessAcceptsExactAgeAndClockSkewBoundaries(int offsetSeconds, bool accepted)
    {
        var now = OperatorTestData.Timestamp;
        var backup = Backup("initial", now.AddSeconds(offsetSeconds));
        var state = new LifecycleState { GitOpsCommit = backup.GitOpsCommit, Backups = [backup] };
        var check = () => state.RequireFreshBackup(now, 26);
        if (accepted) check.Should().NotThrow();
        else check.Should().ThrowExactly<InvalidOperationException>().WithMessage("No recent complete backup*");
    }

    [Fact]
    public void FreshnessSelectsNewestMatchingBackupRegardlessOfArrayOrder()
    {
        var now = OperatorTestData.Timestamp;
        var current = Backup("initial", now.AddHours(-1));
        var stale = Backup("initial", now.AddHours(-27));
        var foreign = Backup(new string('b', 32), now);
        var state = new LifecycleState { GitOpsCommit = current.GitOpsCommit, Backups = [current, foreign, stale] };
        state.RequireFreshBackup(now, 26);
        (state with { Backups = [stale, foreign, current] }).RequireFreshBackup(now, 26);
        Assert.Throws<InvalidOperationException>(() => (state with { Backups = [stale, foreign] }).RequireFreshBackup(now, 26));
    }

    public enum AuthorityFault
    {
        Organization, Release, CredentialGeneration, CredentialHash, IncompleteRunningState
    }

    private static BackupRecord Backup(string generation, DateTimeOffset created)
    {
        var operation = Guid.NewGuid();
        return new BackupRecord(operation.ToString("N"), "credential-generation", new string('a', 40), generation,
            StorageClaims.All.ToDictionary(c => c.Key, c => $"ocid1.volume.oc1..{c.Key}"),
            new VolumeBackup("ocid1.volumegroupbackup.oc1..test", "ocid1.volumegroup.oc1..test", created,
                StorageClaims.All.ToDictionary(c => c.Key, c => $"ocid1.volumebackup.oc1..{c.Key}")),
            new NativeBackupManifest(operation, $"drn-{operation:N}", "3.7.0", ["graylog_0"],
                $"/var/lib/postgresql/data/drn-backups/{operation:N}.dump", $"/data/drn-backups/{operation:N}.archive.gz", "17.7", "public-ca"),
            new PostgresRecovery(new JsonObject(), new JsonObject(), new JsonObject()));
    }
}
