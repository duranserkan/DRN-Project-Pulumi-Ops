using System.Text.Json;
using Drn.Infrastructure.Operator.Recovery;
using Drn.Pulumi;
using Xunit;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed class VolumeBackupTests
{
    [Fact]
    public void CompleteDistinctClaimMappingIsAccepted() => VolumeBackups.ValidateClaims(VolumeIds());

    [Theory]
    [DataInlineUnit(ClaimMappingFault.Missing)]
    [DataInlineUnit(ClaimMappingFault.DuplicateIdentity)]
    [DataInlineUnit(ClaimMappingFault.UnknownClaim)]
    [DataInlineUnit(ClaimMappingFault.NullIdentity)]
    [DataInlineUnit(ClaimMappingFault.EmptyIdentity)]
    [DataInlineUnit(ClaimMappingFault.WhitespaceIdentity)]
    public void InvalidClaimMappingCannotBecomeRecoveryAuthority(ClaimMappingFault fault)
    {
        var values = VolumeIds();
        switch (fault)
        {
            case ClaimMappingFault.Missing: values.Remove("sample"); break;
            case ClaimMappingFault.DuplicateIdentity: values["sample"] = values["nexus"]; break;
            case ClaimMappingFault.UnknownClaim: values.Remove("sample"); values.Add("foreign", "foreign-volume"); break;
            case ClaimMappingFault.NullIdentity: values["sample"] = null!; break;
            case ClaimMappingFault.EmptyIdentity: values["sample"] = ""; break;
            case ClaimMappingFault.WhitespaceIdentity: values["sample"] = " "; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        Assert.Throws<InvalidOperationException>(() => VolumeBackups.ValidateClaims(values));
    }

    public enum ClaimMappingFault { Missing, DuplicateIdentity, UnknownClaim, NullIdentity, EmptyIdentity, WhitespaceIdentity }

    [Fact]
    public async Task BackupMembersUseTheirSourceGenerationsSizes()
    {
        var runner = new BackupRunner { SampleSize = 100 };
        var profile = new StorageGeneration { Volumes = new() { ["sample"] = new VolumeSettings { SizeInGbs = 100 } } };
        await new VolumeBackups(runner, new Dictionary<string, string>(), "durable", profile)
            .DescribeAsync("checkpoint", runner.Volumes, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VolumeBackups(runner, new Dictionary<string, string>(), "durable")
            .DescribeAsync("checkpoint", runner.Volumes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MemberBackupsAreMappedBySourceIdentityRatherThanArrayOrder()
    {
        var runner = new BackupRunner();
        var backup = await new VolumeBackups(runner, new Dictionary<string, string>(), "durable")
            .DescribeAsync("checkpoint", runner.Volumes, TestContext.Current.CancellationToken);
        Assert.All(StorageClaims.All, claim => Assert.Equal($"backup-{claim.Key}", backup.VolumeBackupIds[claim.Key]));
    }

    [Fact]
    public async Task ForeignVolumeMemberMakesEntireCheckpointUnusable()
    {
        var runner = new BackupRunner { ForeignSource = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VolumeBackups(runner, new Dictionary<string, string>(), "durable")
                .DescribeAsync("checkpoint", runner.Volumes, TestContext.Current.CancellationToken));
    }

    [Theory]
    [DataInlineUnit(false)]
    [DataInlineUnit(true)]
    public async Task RetentionRequiresAProtectedCheckpointAndCannotDeleteIt(bool includeCurrent)
    {
        var runner = new BackupRunner();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VolumeBackups(runner, new Dictionary<string, string>(), "durable")
                .DeleteAsync("checkpoint", includeCurrent ? new HashSet<string> { "checkpoint" } : new HashSet<string>(), TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RetentionDeletesOnlyTheVerifiedObsoleteCheckpoint()
    {
        var runner = new BackupRunner();
        await new VolumeBackups(runner, new Dictionary<string, string>(), "durable")
            .DeleteAsync("checkpoint", new HashSet<string> { "current-checkpoint" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, runner.Calls.Count);
        CommandArguments.Prefix(runner.Calls[0], "bv", "volume-group-backup", "get");
        runner.Calls[1].Should().Equal("bv", "volume-group-backup", "delete", CommandArguments.OracleCloudVolumeGroupBackupId, "checkpoint",
            "--force", CommandArguments.OracleCloudOutput, CommandArguments.OracleCloudJsonOutput, CommandArguments.OracleCloudNoRetry);
    }

    [Theory]
    [DataInlineUnit(RetentionFault.Identity)]
    [DataInlineUnit(RetentionFault.Compartment)]
    [DataInlineUnit(RetentionFault.AutomaticBackup)]
    [DataInlineUnit(RetentionFault.Project)]
    [DataInlineUnit(RetentionFault.Environment)]
    [DataInlineUnit(RetentionFault.Operation)]
    public async Task RetentionRejectsEachIndependentOwnershipMismatchBeforeDeletion(RetentionFault fault)
    {
        var runner = new BackupRunner { ConfigureBackup = data =>
        {
            var tags = (Dictionary<string, string>)data["freeform-tags"];
            switch (fault)
            {
                case RetentionFault.Identity: data["id"] = "another-checkpoint"; break;
                case RetentionFault.Compartment: data["compartment-id"] = "another-compartment"; break;
                case RetentionFault.AutomaticBackup: data["source-type"] = "SCHEDULED"; break;
                case RetentionFault.Project: tags["project"] = "another-project"; break;
                case RetentionFault.Environment: tags["environment"] = "another-environment"; break;
                case RetentionFault.Operation: tags["operation"] = "invalid-operation"; break;
                default: throw new ArgumentOutOfRangeException(nameof(fault));
            }
        } };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VolumeBackups(runner, new Dictionary<string, string>(), "durable")
                .DeleteAsync("checkpoint", new HashSet<string> { "current-checkpoint" }, TestContext.Current.CancellationToken));

        Assert.Contains("explicit manual checkpoint backups", error.Message);
        Assert.Single(runner.Calls);
        Assert.DoesNotContain(runner.Calls, call => call.Contains("delete"));
    }

    public enum RetentionFault { Identity, Compartment, AutomaticBackup, Project, Environment, Operation }

    [Fact]
    public async Task FailedPendingCheckpointStopsImmediatelyAfterCreate()
    {
        var runner = new BackupRunner();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VolumeBackups(runner, new Dictionary<string, string>(), "durable").CreateAsync("group", runner.Volumes, Guid.NewGuid(), (_, _) => throw new InvalidOperationException("Checkpoint write failed."), TestContext.Current.CancellationToken));
        Assert.Equal("Checkpoint write failed.", error.Message);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains("create", runner.Calls[1]);
    }

    [Fact]
    public async Task InterruptedCreateIsRecoveredByRecordedOperationWithoutCreatingAnotherBackup()
    {
        var runner = new BackupRunner { MatchingBackups = 1 };
        var result = await new VolumeBackups(runner, new Dictionary<string, string>(), "durable")
            .FindAsync(runner.OperationId, runner.Volumes, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("checkpoint", result.BackupId);
        Assert.Contains("--all", runner.Calls[0]);
        Assert.DoesNotContain(runner.Calls, call => call.Contains("create", StringComparer.Ordinal));
    }

    [Fact]
    public async Task EmptySuccessfulLookupIsTheOnlyEvidenceThatNoMatchingBackupExists()
    {
        var runner = new BackupRunner();
        var backups = new VolumeBackups(runner, new Dictionary<string, string>(), "durable");
        Assert.Null(await backups.FindAsync(runner.OperationId, runner.Volumes, TestContext.Current.CancellationToken));
        var failed = new BackupRunner { FailList = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VolumeBackups(failed, new Dictionary<string, string>(), "durable")
                .FindAsync(failed.OperationId, failed.Volumes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DuplicateOperationBackupsAreAmbiguousAndCannotBeSelected()
    {
        var runner = new BackupRunner { MatchingBackups = 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VolumeBackups(runner, new Dictionary<string, string>(), "durable")
                .FindAsync(runner.OperationId, runner.Volumes, TestContext.Current.CancellationToken));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task PendingBackupCannotBeDescribedAsCompleteOrBypassedOnCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new BackupRunner { CancelPendingRead = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VolumeBackups(runner, new Dictionary<string, string>(), "durable")
                .DescribeAsync("checkpoint", runner.Volumes, cancellation.Token));
        Assert.Single(runner.Calls);
    }

    private static Dictionary<string, string> VolumeIds() => StorageClaims.All.ToDictionary(claim => claim.Key, claim => $"volume-{claim.Key}");

    private sealed class BackupRunner : ICommandRunner
    {
        public Dictionary<string, string> Volumes { get; } = VolumeIds();
        public List<string[]> Calls { get; } = [];
        public bool ForeignSource { get; init; }
        public int SampleSize { get; init; } = 50;
        public Guid OperationId { get; } = Guid.NewGuid();
        public int MatchingBackups { get; init; }
        public bool FailList { get; init; }
        public Action<Dictionary<string, object>>? ConfigureBackup { get; init; }
        public CancellationTokenSource? CancelPendingRead { get; init; }
        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, bool displayOutput, CancellationToken cancellationToken,
            string? standardInput = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(CommandArguments.OracleCloudCli, executable);
            CommandArguments.Prefix(arguments, "bv");
            Assert.False(displayOutput);
            Assert.Equal(CommandArguments.OracleCloudJsonOutput, CommandArguments.Required(arguments, CommandArguments.OracleCloudOutput));
            Assert.Contains(CommandArguments.OracleCloudNoRetry, arguments);
            Calls.Add(arguments.ToArray());
            if (arguments[2] == "delete")
            {
                CommandArguments.Prefix(arguments, "bv", "volume-group-backup", "delete");
                CommandArguments.Required(arguments, CommandArguments.OracleCloudVolumeGroupBackupId).Should().Be("checkpoint");
                arguments.Should().Contain("--force");
                return Task.FromResult("");
            }
            if (arguments[2] == "list")
            {
                CommandArguments.Prefix(arguments, "bv", "volume-group-backup", "list");
                if (FailList) throw new InvalidOperationException("Authentication unavailable.");
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    data = Enumerable.Range(0, MatchingBackups).Select(_ => new Dictionary<string, object>
                    {
                        ["id"] = "checkpoint", ["compartment-id"] = "durable", ["source-type"] = "MANUAL",
                        ["freeform-tags"] = new Dictionary<string, string>
                        { ["project"] = "DRN-Project", ["environment"] = "staging", ["operation"] = OperationId.ToString("N") }
                    }).ToArray()
                }));
            }
            Dictionary<string, object> data;
            if (arguments[1] == "volume-group" && arguments[2] == "get")
                data = new() { ["id"] = "group", ["lifecycle-state"] = "AVAILABLE", ["compartment-id"] = "durable", ["volume-ids"] = Volumes.Values.ToArray() };
            else if (arguments[1] == "volume-group-backup" && arguments[2] == "create") data = new() { ["id"] = "checkpoint" };
            else if (arguments[1] == "volume-group-backup" && arguments[2] == "get")
            {
                data = new()
                {
                    ["id"] = "checkpoint", ["lifecycle-state"] = CancelPendingRead is null ? "AVAILABLE" : "CREATING", ["compartment-id"] = "durable",
                    ["volume-group-id"] = "group", ["time-created"] = OperatorTestData.Timestamp,
                    ["source-type"] = "MANUAL", ["freeform-tags"] = new Dictionary<string, string>
                    { ["project"] = "DRN-Project", ["environment"] = "staging", ["operation"] = OperationId.ToString("N") },
                    ["volume-backup-ids"] = StorageClaims.All.Reverse().Select(claim => $"backup-{claim.Key}").ToArray()
                };
                ConfigureBackup?.Invoke(data);
                CancelPendingRead?.Cancel();
            }
            else if (arguments[1] == "backup" && arguments[2] == "get")
            {
                var backupId = CommandArguments.Required(arguments, CommandArguments.OracleCloudVolumeBackupId);
                backupId.Should().StartWith("backup-");
                var claim = backupId["backup-".Length..];
                data = new()
                {
                    ["id"] = backupId, ["lifecycle-state"] = "AVAILABLE", ["compartment-id"] = "durable", ["size-in-gbs"] = claim == "sample" ? SampleSize : 50,
                    ["volume-id"] = ForeignSource ? "foreign-volume" : Volumes[claim]
                };
            }
            else throw new Xunit.Sdk.XunitException($"Unexpected OCI backup command: {string.Join(' ', arguments)}");
            return Task.FromResult(JsonSerializer.Serialize(new { data }));
        }
    }
}
