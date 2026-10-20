using System.Text.Json;
using Drn.Pulumi;
using Xunit;
using static Drn.Pulumi.UnitTests.Support.PulumiTestData;

namespace Drn.Pulumi.UnitTests;

public class SettingsTests
{
    [Fact]
    public void CamelCaseConfigurationRoundTripsWithDefaultPulumiSerializer()
    {
        var json = JsonSerializer.Serialize(ValidSettings);
        Assert.Contains("\"allowBillableStorage\":true", json);
        Assert.Contains("\"apiAllowedCidrs\"", json);
        var settings = JsonSerializer.Deserialize<InfrastructureSettings>(json)!;
        settings.Validate("cluster");
        Assert.Equal(ValidSettings.Region, settings.Region);
        Assert.Equal(ValidSettings.ApiAllowedCidrs, settings.ApiAllowedCidrs);
    }

    [Theory]
    [DataInlineUnit((string?)null)]
    [DataInlineUnit("")]
    [DataInlineUnit("0.0.0.0/0")]
    [DataInlineUnit("203.0.113.10/24")]
    [DataInlineUnit("203.0.113.10")]
    [DataInlineUnit("203.0.113.10/33")]
    [DataInlineUnit("::/0")]
    [DataInlineUnit("127.1/32")]
    [DataInlineUnit("0x7f000001/32")]
    public void RejectsUnrestrictedOrAmbiguousApiNetworks(string? cidr)
    {
        var settings = ValidSettings with { ApiAllowedCidrs = [cidr!] };
        Assert.Throws<ArgumentException>(() => settings.Validate("cluster"));
    }

    [Theory]
    [DataInlineUnit("203.0.113.10/32")]
    [DataInlineUnit("203.0.113.0/24")]
    public void AcceptsExplicitNetworkAndSingleHostCidrs(string cidr) =>
        (ValidSettings with { ApiAllowedCidrs = [cidr] }).Validate("cluster");

    [Fact]
    public void RejectsBillableStorageWithoutAcknowledgement() =>
        Assert.Throws<ArgumentException>(() => (ValidSettings with { AllowBillableStorage = false }).Validate("durable"));

    [Fact]
    public void RejectsSharedDeletionBoundary() =>
        Assert.Throws<ArgumentException>(() => (ValidSettings with
        {
            DurableCompartmentId = ValidSettings.ClusterCompartmentId
        }).Validate("durable"));

    [Theory]
    [DataInlineUnit(-1)]
    [DataInlineUnit(3)]
    public void RejectsUnapprovedWorkerCounts(int workers) =>
        Assert.Throws<ArgumentException>(() => (ValidSettings with { Workers = workers }).Validate("cluster"));

    [Theory]
    [DataInlineUnit(2, 93)]
    [DataInlineUnit(7, 31)]
    [DataInlineUnit(3, 62)]
    public void WorkerCountFitsTheExistingPodSubnet(int workers, int pods) =>
        (ValidSettings with { Workers = workers, MaxPodsPerNode = pods }).Validate("cluster");

    [Theory]
    [DataInlineUnit(8, 31)]
    [DataInlineUnit(4, 62)]
    [DataInlineUnit(1, 0)]
    [DataInlineUnit(1, 111)]
    public void RejectsInvalidPodDensityAndExhaustedSubnet(int workers, int pods) =>
        Assert.Throws<ArgumentException>(() => (ValidSettings with { Workers = workers, MaxPodsPerNode = pods }).Validate("cluster"));

    [Theory]
    [DataMemberUnit(nameof(InvalidComputeSettings))]
    public void RejectsInvalidComputeAndPlacementSettings(string scenario, InfrastructureSettings settings)
    {
        var validate = () => settings.Validate("cluster");
        validate.Should().ThrowExactly<ArgumentException>(scenario);
    }

    public static IEnumerable<object[]> InvalidComputeSettings =>
    [
        ["zero CPUs", ValidSettings with { NodeOcpus = 0 }],
        ["NaN memory", ValidSettings with { NodeMemoryInGbs = double.NaN }],
        ["infinite memory", ValidSettings with { NodeMemoryInGbs = double.PositiveInfinity }],
        ["negative memory", ValidSettings with { NodeMemoryInGbs = -1 }],
        ["unsupported shape", ValidSettings with { NodeShape = "VM.GPU.Flex" }],
        ["undersized boot disk", ValidSettings with { BootVolumeSizeInGbs = 49 }],
        ["repeated fault domain", ValidSettings with { FaultDomains = ["FAULT-DOMAIN-1", "FAULT-DOMAIN-1"] }],
        ["unknown fault domain", ValidSettings with { FaultDomains = ["FAULT-DOMAIN-4"] }]
    ];

    [Theory]
    [DataInlineUnit("lower-cost", 0)]
    [DataInlineUnit("balanced", 10)]
    [DataInlineUnit("higher-performance", 20)]
    public void VolumeProfilesRoundTripWithLegacyDefaults(string performance, int vpus)
    {
        var settings = ValidSettings with { StorageGenerations = [new()
        { Volumes = new() { ["postgresql"] = new VolumeSettings { SizeInGbs = 100, Performance = performance } } }] };
        var roundTrip = JsonSerializer.Deserialize<InfrastructureSettings>(JsonSerializer.Serialize(settings))!;
        roundTrip.Validate("durable");
        Assert.Equal(100, roundTrip.ActiveStorage.Volume("postgresql").SizeInGbs);
        Assert.Equal(vpus, roundTrip.ActiveStorage.Volume("postgresql").VpusPerGb);
        Assert.Equal(new VolumeSettings(), roundTrip.ActiveStorage.Volume("sample"));
        Assert.Equal(400L, roundTrip.ActiveStorage.TotalSizeInGbs);
    }

    [Theory]
    [DataInlineUnit("unknown", 50, "balanced")]
    [DataInlineUnit("sample", 49, "balanced")]
    [DataInlineUnit("sample", 32769, "balanced")]
    [DataInlineUnit("sample", 50, "nfs")]
    [DataInlineUnit("sample", 50, "ultra-high-performance")]
    public void RejectsUnsupportedStorage(string key, int size, string performance) =>
        Assert.Throws<ArgumentException>(() => (ValidSettings with { StorageGenerations = [new()
        { Volumes = new() { [key] = new VolumeSettings { SizeInGbs = size, Performance = performance } } }] }).Validate("durable"));

    [Theory]
    [DataInlineUnit("v1.35.1")]
    [DataInlineUnit("v1.37.0")]
    [DataInlineUnit("v1.36")]
    [DataInlineUnit("1.36.1")]
    [DataInlineUnit("v1.36.1-preview")]
    public void RejectsVersionsOutsideTheSupportedPolicyProfile(string version) =>
        Assert.Throws<ArgumentException>(() => (ValidSettings with { KubernetesVersion = version }).Validate("cluster"));

    [Theory]
    [DataInlineUnit("SecurityToken")]
    [DataInlineUnit("InstancePrincipal")]
    public void SupportsOnlyTheApprovedOperatorAuthenticationModes(string auth) =>
        (ValidSettings with { OciAuth = auth }).Validate("recovery");

    [Fact]
    public void RejectsUnrecognizedAuthenticationMode() =>
        Assert.Throws<ArgumentException>(() => (ValidSettings with { OciAuth = "ApiKey" }).Validate("recovery"));

    [Theory]
    [DataInlineUnit("")]
    [DataInlineUnit(" example")]
    [DataInlineUnit("example\nnamespace")]
    public void RequiresAnExplicitObjectStorageNamespace(string value) =>
        Assert.Throws<ArgumentException>(() => (ValidSettings with { ObjectStorageNamespace = value }).Validate("recovery"));

    [Theory]
    [DataInlineUnit("")]
    [DataInlineUnit("ab")]
    [DataInlineUnit("Recovery-Bucket")]
    [DataInlineUnit("drn/recovery")]
    public void RejectsInvalidRecoveryBucketNames(string value) =>
        Assert.Throws<ArgumentException>(() => (ValidSettings with { RecoveryBucketName = value }).Validate("recovery"));

    [Fact]
    public void RecoveryAndDurableRolesDoNotRequireAClusterImageOrApiNetwork()
    {
        var settings = ValidSettings with { NodeImageId = "", KubernetesVersion = "", ApiAllowedCidrs = [] };
        settings.Validate("recovery");
        settings.Validate("durable");
    }

    [Fact]
    public void AcceptsAnExplicitCompleteRestoreGeneration()
    {
        var restored = RestoredGeneration();
        (ValidSettings with
        {
            StorageGenerations = [new(), restored], ActiveStorageGeneration = restored.Id
        }).Validate("durable");
    }

    [Theory]
    [DataInlineUnit(BackupMappingFault.Missing)]
    [DataInlineUnit(BackupMappingFault.Duplicate)]
    [DataInlineUnit(BackupMappingFault.WrongKind)]
    [DataInlineUnit(BackupMappingFault.Null)]
    public void RejectsIncompleteOrAmbiguousBackupMappings(BackupMappingFault fault)
    {
        var restored = RestoredGeneration();
        var first = StorageClaims.All[0].Key;
        var second = StorageClaims.All[1].Key;
        switch (fault)
        {
            case BackupMappingFault.Missing: restored.BackupIds!.Remove(first); break;
            case BackupMappingFault.Duplicate: restored.BackupIds![first] = restored.BackupIds[second]; break;
            case BackupMappingFault.WrongKind: restored.BackupIds![first] = "ocid1.volume.oc1..source"; break;
            case BackupMappingFault.Null: restored.BackupIds![first] = null!; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }
        Assert.Throws<ArgumentException>(() => (ValidSettings with
        {
            StorageGenerations = [new(), restored], ActiveStorageGeneration = restored.Id
        }).Validate("durable"));
    }

    [Theory]
    [DataInlineUnit(GenerationHistoryFault.MissingInitial)]
    [DataInlineUnit(GenerationHistoryFault.DuplicateGeneration)]
    [DataInlineUnit(GenerationHistoryFault.MissingActive)]
    [DataInlineUnit(GenerationHistoryFault.TooMany)]
    [DataInlineUnit(GenerationHistoryFault.InvalidId)]
    [DataInlineUnit(GenerationHistoryFault.InitialWithBackups)]
    public void RejectsInvalidGenerationHistory(GenerationHistoryFault fault)
    {
        var restored = RestoredGeneration();
        var settings = fault switch
        {
            GenerationHistoryFault.MissingInitial => ValidSettings with { StorageGenerations = [restored], ActiveStorageGeneration = restored.Id },
            GenerationHistoryFault.DuplicateGeneration => ValidSettings with { StorageGenerations = [new(), restored, restored] },
            GenerationHistoryFault.MissingActive => ValidSettings with { ActiveStorageGeneration = restored.Id },
            GenerationHistoryFault.TooMany => ValidSettings with { StorageGenerations = [new(), restored, RestoredGeneration('b'), RestoredGeneration('c')] },
            GenerationHistoryFault.InvalidId => ValidSettings with { StorageGenerations = [new(), restored with { Id = "restore-latest" }] },
            GenerationHistoryFault.InitialWithBackups => ValidSettings with { StorageGenerations = [restored with { Id = "initial" }] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, null)
        };
        Assert.Throws<ArgumentException>(() => settings.Validate("durable"));
    }

    [Theory]
    [DataInlineUnit("staging-recovery", "recovery")]
    [DataInlineUnit("staging-durable", "durable")]
    [DataInlineUnit("staging-cluster", "cluster")]
    [DataInlineUnit("staging-bootstrap", "bootstrap")]
    public void RecognizesOnlyTheFourStagingRoles(string stack, string role) =>
        Assert.Equal(role, Infrastructure.RoleForStack(stack));

    [Theory]
    [DataInlineUnit("production-cluster")]
    [DataInlineUnit("staging")]
    [DataInlineUnit("other-staging-cluster")]
    public void RejectsUnrecognizedStacks(string stack) =>
        Assert.Throws<ArgumentException>(() => Infrastructure.RoleForStack(stack));

    [Theory]
    [DataInlineUnit("refs/tags/2026.10.1-preview000", false)]
    [DataInlineUnit("refs/tags/2026.10.1-preview001", true)]
    [DataInlineUnit("refs/tags/2026.10.1-preview999", true)]
    [DataInlineUnit("refs/tags/2026.13.1", false)]
    [DataInlineUnit("refs/tags/2026.10.1", true)]
    [DataInlineUnit("master", false)]
    public void RequiresPublishedReleaseTagShape(string tag, bool accepted) =>
        Assert.Equal(accepted, InfrastructureSettings.IsGitOpsTag(tag));

    public enum BackupMappingFault { Missing, Duplicate, WrongKind, Null }

    public enum GenerationHistoryFault { MissingInitial, DuplicateGeneration, MissingActive, TooMany, InvalidId, InitialWithBackups }
}
