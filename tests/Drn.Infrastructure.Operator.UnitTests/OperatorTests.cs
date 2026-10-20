using System.Text.Json;
using Drn.Infrastructure.Operator.Recovery;
using Drn.Pulumi;
using Xunit;
using static Drn.Infrastructure.Operator.UnitTests.Support.OperatorTestData;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed class OperatorTests
{
    private const string ConfigFile = "config.json";

    [Theory]
    [DataInlineUnit("durable")]
    [DataInlineUnit("cluster")]
    [DataInlineUnit("bootstrap")]
    public void ScopedProvisioningCommandsAcceptOnlyTheirOwnOptions(string name)
    {
        Assert.Equal(new OperatorCommand(name, ConfigFile, false), OperatorCommand.Parse([name, ConfigFile]));
        Assert.True(OperatorCommand.Parse([name, ConfigFile, "--yes"]).Yes);
        Assert.Throws<ArgumentException>(() => OperatorCommand.Parse([name, ConfigFile, "--confirm", "staging"]));
        Assert.Throws<ArgumentException>(() => OperatorCommand.Parse([name, ConfigFile, "--allow-additional-storage"]));
    }

    [Fact]
    public void DesiredWorkersArePositiveAndRecoveryOwnsRestoredGenerations()
    {
        var config = Configuration();
        (config with { Infrastructure = config.Infrastructure with { Workers = 2 } }).Validate();
        Assert.Throws<ArgumentException>(() => (config with { Infrastructure = config.Infrastructure with { Workers = 0 } }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { Infrastructure = config.Infrastructure with
        { StorageGenerations = [new(), new() { Id = new string('a', 32), BackupIds = StorageClaims.All.ToDictionary(c => c.Key, c => $"ocid1.volumebackup.oc1..{c.Key}") }] } }).Validate());
    }

    [Theory]
    [DataInlineUnit("purge")]
    [DataInlineUnit("destroy")]
    [DataInlineUnit("production")]
    [DataInlineUnit("; echo unsafe")]
    public void UnsupportedCommandsAreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => OperatorCommand.Parse([name, ConfigFile]));

    [Fact]
    public void DestructiveTransitionsRequireSpecificConfirmations()
    {
        Assert.Throws<ArgumentException>(() => OperatorCommand.Parse(["down", ConfigFile, "--yes"]));
        Assert.Throws<ArgumentException>(() => OperatorCommand.Parse(["down", ConfigFile, "--confirm", "production"]));
        Assert.True(OperatorCommand.Parse(["down", ConfigFile, "--confirm", "staging", "--yes"]).Yes);
        Assert.Throws<ArgumentException>(() => OperatorCommand.Parse(["release", ConfigFile, "--yes"]));
        Assert.Equal("release", OperatorCommand.Parse(["release", ConfigFile, "--confirm", "staging"]).Name);
        const string id = "11111111111111111111111111111111";
        Assert.Throws<ArgumentException>(() => OperatorCommand.Parse(["restore", ConfigFile, "--confirm", "staging", "--backup", id]));
        Assert.Equal(id, OperatorCommand.Parse(["restore", ConfigFile, "--confirm", "staging", "--backup", id, "--allow-additional-storage"]).Backup);
        Assert.Throws<ArgumentException>(() => OperatorCommand.Parse(["unlock", ConfigFile, "--confirm", "staging", "--owner", id]));
        Assert.Equal(Guid.ParseExact(id, "N"), OperatorCommand.Parse(["unlock", ConfigFile, "--confirm", "staging", "--owner", id, "--previous-process-stopped"]).Owner);
    }

    [Theory]
    [DataInlineUnit("--yes", "--yes")]
    [DataInlineUnit("--unknown", "value")]
    [DataInlineUnit("--confirm", "staging")]
    [DataInlineUnit("--backup", "../other")]
    public void OptionsCannotLeakIntoAnotherCommand(string first, string second) =>
        Assert.Throws<ArgumentException>(() => OperatorCommand.Parse(["up", ConfigFile, first, second]));

    [Theory]
    [DataInlineUnit("https://user:password@github.com/example/gitops.git")]
    [DataInlineUnit("file:///tmp/gitops")]
    [DataInlineUnit("https://example.com/gitops")]
    [DataInlineUnit("https://github.com/example/gitops.git?token=secret")]
    public void RepositoryCannotIntroduceCredentialsOrLocalProtocols(string repository)
    {
        var config = Configuration();
        Assert.Throws<ArgumentException>(() => (config with { Infrastructure = config.Infrastructure with { GitOpsRepository = repository } }).Validate());
    }

    [Fact]
    public void FourStackIdentitiesAndPrivateRecoverySettingsAreExplicit()
    {
        var config = Configuration();
        config.Validate();
        Assert.Equal("example/drn-pulumi/staging-recovery", config.Stack("recovery"));
        Assert.Throws<ArgumentException>(() => config.Stack("production"));
        Assert.Throws<ArgumentException>(() => (config with { RecoveryKeyPath = "relative.key" }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { BackupRetentionCount = 1 }).Validate());
        Assert.Throws<ArgumentException>(() => (config with { MaximumBackupAgeHours = 0 }).Validate());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OperatorConfiguration>("{\"organizaton\":\"typo\"}", OperatorConfiguration.JsonOptions));
    }

    [Theory]
    [DataInlineUnit("refs/tags/2026.13.1")]
    [DataInlineUnit("refs/tags/2026.01.1")]
    [DataInlineUnit("refs/tags/2026.10.0")]
    [DataInlineUnit("refs/tags/2026.10.1-preview000")]
    public void InvalidReleaseRefsFailBeforeAnyTool(string tag)
    {
        var config = Configuration();
        Assert.Throws<ArgumentException>(() => (config with { Infrastructure = config.Infrastructure with { GitOpsTag = tag } }).Validate());
    }

    [Theory]
    [DataInlineUnit]
    public async Task SetupOnlyAppliesTheRecoveryBucket(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.Operator.ExecuteAsync(new OperatorCommand("setup", "unused", false), TestContext.Current.CancellationToken);
        Assert.Equal(4, fixture.Runner.Calls.Count(call => call.Arguments.Contains("select")));
        var apply = Assert.Single(fixture.Runner.Calls, call => call.Arguments.Contains("up"));
        Assert.Contains("example/drn-pulumi/staging-recovery", apply.Arguments);
        Assert.All(fixture.Runner.Calls, call => Assert.True(call.Executable == "pulumi" || call.Arguments.SequenceEqual(["--version"])));
    }

    [Theory]
    [DataInlineUnit]
    public async Task UnreviewedCliCannotUseTheConditionalLockContract(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        fixture.Runner.CliVersion = "0.0.0";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operator.ExecuteAsync(new OperatorCommand("up", "unused", true), TestContext.Current.CancellationToken));
        Assert.Single(fixture.Runner.Calls);
        Assert.Empty(fixture.Runner.Objects);
    }

    [Theory]
    [DataInlineUnit("up")]
    [DataInlineUnit("durable")]
    [DataInlineUnit("cluster")]
    [DataInlineUnit("bootstrap")]
    [DataInlineUnit("pause")]
    [DataInlineUnit("down")]
    [DataInlineUnit("restore")]
    public async Task MissingRecoveryStateCannotReachCloudOrKubernetesMutation(DrnTestContextUnit context, string operation)
    {
        var fixture = new Fixture(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operator.ExecuteAsync(new OperatorCommand(operation, "unused", true), TestContext.Current.CancellationToken));
        Assert.All(fixture.Runner.Calls, call => Assert.True(
            call.Executable == "oci" && (call.Arguments[0] == "os" || call.Arguments.SequenceEqual(["--version"])) ||
            call.Executable == "pulumi" && call.Arguments.Contains("output") && call.Arguments.Contains("example/drn-pulumi/staging-recovery")));
        Assert.DoesNotContain(OperatorTestData.LockObject, fixture.Runner.Objects.Keys);
        Assert.DoesNotContain(OperatorTestData.StateObject, fixture.Runner.Objects.Keys);
    }

    [Fact]
    public async Task FailedSharedOwnershipCheckPreventsEveryToolAction()
    {
        var raw = new MemoryRunner();
        var coordinated = new CoordinatedRunner(raw, _ => throw new InvalidOperationException("Ownership lost"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinated.RunAsync("pulumi", ["up"], new Dictionary<string, string>(), true, TestContext.Current.CancellationToken));
        Assert.Empty(raw.Calls);
    }

    [Theory]
    [DataInlineUnit]
    public async Task InitializationRequiresIndependentKeyBackupBeforeGeneratingCredentials(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Operator.ExecuteAsync(new OperatorCommand("initialize", "unused", false), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Runner.Objects.Keys, key => key.Contains("credentials/", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Executable is "git" or "kubectl" || call.Arguments.Contains("up"));
    }

    [Theory]
    [DataInlineUnit]
    public async Task InvalidConfigurationCannotInvokeTools(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await Assert.ThrowsAsync<ArgumentException>(() => new InfrastructureOperator(fixture.Directory,
            Configuration() with { Organization = "../other-account" }, fixture.Runner)
            .ExecuteAsync(new OperatorCommand("up", "unused", true), TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Runner.Calls);
    }

    private sealed class Fixture
    {
        public string Directory { get; }
        public MemoryRunner Runner { get; } = new();
        public InfrastructureOperator Operator { get; }
        public Fixture(DrnTestContextUnit context)
        {
            Directory = CreateWorkspace(context);
            var path = Path.Combine(Directory, "key");
            RecoveryStore.GenerateKey(path);
            Operator = new InfrastructureOperator(Directory, Configuration() with { RecoveryKeyPath = path }, Runner);
        }
    }

    private sealed record RecordedCommand(string Executable, string[] Arguments);
    private sealed class MemoryRunner : ICommandRunner
    {
        private readonly ObjectStoreRunner store = new();
        public List<RecordedCommand> Calls { get; } = [];
        public Dictionary<string, RemoteObject> Objects => store.Objects;
        public string CliVersion { get; set; } = InfrastructureOperator.OciCliVersion;
        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, bool displayOutput, CancellationToken cancellationToken, string? standardInput = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new RecordedCommand(executable, arguments.ToArray()));
            if (executable == "oci" && arguments.SequenceEqual(["--version"])) return Task.FromResult(CliVersion);
            if (executable == CommandArguments.Pulumi)
            {
                CommandArguments.Prefix(arguments, CommandArguments.PulumiWorkingDirectory);
                _ = CommandArguments.Required(arguments, CommandArguments.PulumiWorkingDirectory);
                var command = arguments.Skip(2).ToArray();
                if (command.Take(2).SequenceEqual(["stack", "select"]))
                {
                    command.Should().HaveCount(4);
                    command[2].Should().StartWith("example/drn-pulumi/staging-");
                    command[3].Should().Be("--create");
                    return Task.FromResult("");
                }
                var stack = CommandArguments.Required(arguments, CommandArguments.Stack);
                if (command.Take(2).SequenceEqual(["stack", "output"]) && stack == "example/drn-pulumi/staging-recovery")
                    return Task.FromResult("{\"bucketName\":\"drn-recovery\",\"namespace\":\"namespace\",\"region\":\"eu-frankfurt-1\"}");
                if (command.Take(2).SequenceEqual(["config", "set"]) || command[0] == "up")
                    return Task.FromResult("{}");
                throw new Xunit.Sdk.XunitException($"Unexpected Pulumi command: {string.Join(' ', arguments)}");
            }
            return store.RunAsync(executable, arguments, environment, displayOutput, cancellationToken, standardInput);
        }
    }
}
