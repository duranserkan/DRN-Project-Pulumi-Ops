using System.Collections.Immutable;
using System.Text;
using Drn.Pulumi;
using Pulumi;
using Pulumi.Testing;
using Xunit;
using Oci = Pulumi.Oci;
using static Drn.Pulumi.UnitTests.Support.InputMaps;
using static Drn.Pulumi.UnitTests.Support.ResourceFixture;
using static Drn.Pulumi.UnitTests.Support.OracleCloudInputFields;

namespace Drn.Pulumi.UnitTests;

[Collection(PulumiDeploymentCollection.Name)]
public class ResourceTests
{
    [Fact]
    public async Task RecoveryBucketIsPrivateVersionedAndProtectedInTheDurableCompartment()
    {
        var mocks = await CreateAsync("staging-recovery");
        var bucket = mocks.Resource(OracleCloudResourceTokens.Bucket);
        Assert.Equal(PulumiTestData.ValidSettings.RecoveryBucketName, bucket.Inputs["name"]);
        Assert.Equal(PulumiTestData.ValidSettings.ObjectStorageNamespace, bucket.Inputs["namespace"]);
        Assert.Equal(PulumiTestData.ValidSettings.DurableCompartmentId, bucket.Inputs[CompartmentId]);
        Assert.Equal("NoPublicAccess", bucket.Inputs["accessType"]);
        Assert.Equal("Enabled", bucket.Inputs["versioning"]);
        Assert.Equal("Standard", bucket.Inputs["storageTier"]);
        AssertProtected<Oci.ObjectStorage.Bucket>(mocks, 1);
        Assert.DoesNotContain(mocks.Resources, resource => resource.Type?.StartsWith("oci:Core/", StringComparison.Ordinal)??false);
    }

    [Fact]
    public async Task DurableStackCreatesExactlySevenMinimumVolumesInTheDurableCompartment()
    {
        var mocks = await CreateAsync("staging-durable");
        var volumes = mocks.Resources.Where(resource => resource.Type == OracleCloudResourceTokens.Volume).ToArray();
        Assert.Equal(7, volumes.Length);
        Assert.Equal(7, volumes.Select(volume => Map(volume.Inputs, FreeformTags)["claim"]).Distinct().Count());
        foreach (var volume in volumes)
        {
            Assert.Equal(PulumiTestData.ValidSettings.DurableCompartmentId, volume.Inputs[CompartmentId]);
            Assert.Equal(PulumiTestData.ValidSettings.AvailabilityDomain, volume.Inputs[AvailabilityDomain]);
            Assert.Equal("50", volume.Inputs[SizeInGbs]);
            Assert.Equal("10", volume.Inputs[VpusPerGb]);
            Assert.Equal(false, volume.Inputs["isAutoTuneEnabled"]);
            Assert.Equal("initial", Map(volume.Inputs, FreeformTags)["generation"]);
            Assert.False(volume.Inputs.ContainsKey(SourceDetails));
        }
        AssertProtected<Oci.Core.Volume>(mocks, 7);
        AssertProtected<Oci.Core.VolumeGroup>(mocks, 1);
        var group = mocks.Resource(OracleCloudResourceTokens.VolumeGroup);
        AssertGroupContains(group, volumes);
        var assignment = mocks.Resource(OracleCloudResourceTokens.BackupPolicyAssignment);
        Assert.Equal($"{group.Name}-id", assignment.Inputs["assetId"]);
        var policy = mocks.Resource(OracleCloudResourceTokens.BackupPolicy);
        var schedule = Assert.Single(Maps(policy.Inputs, "schedules"));
        Assert.Equal("ONE_DAY", schedule["period"]);
        Assert.Equal("INCREMENTAL", schedule["backupType"]);
        Assert.Equal(604800, Convert.ToInt32(schedule["retentionSeconds"]));
        Assert.Equal("UTC", schedule["timeZone"]);
        Assert.DoesNotContain(mocks.Resources, resource => resource.Type == OracleCloudResourceTokens.Cluster);
        var provider = mocks.Resource(OracleCloudResourceTokens.Provider);
        Assert.Equal("SecurityToken", provider.Inputs["auth"]);
        Assert.Equal("DRN-STAGING", provider.Inputs["configFileProfile"]);
    }

    [Fact]
    public async Task RestoredGenerationUsesMappedBackupsWithoutReplacingInitialVolumes()
    {
        var generation = PulumiTestData.RestoredGeneration();
        var mocks = await CreateAsync("staging-durable", PulumiTestData.ValidSettings with
        {
            StorageGenerations = [new(), generation], ActiveStorageGeneration = generation.Id
        });
        var volumes = mocks.Resources.Where(resource => resource.Type == OracleCloudResourceTokens.Volume).ToArray();
        Assert.Equal(14, volumes.Length);
        AssertProtected<Oci.Core.Volume>(mocks, 14);
        AssertProtected<Oci.Core.VolumeGroup>(mocks, 2);
        foreach (var claim in StorageClaims.All)
        {
            var original = Assert.Single(volumes, volume => volume.Name == $"staging-{claim.Key}");
            Assert.False(original.Inputs.ContainsKey(SourceDetails));
            var restored = Assert.Single(volumes, volume => volume.Name == $"staging-{generation.Id}-{claim.Key}");
            var source = Map(restored.Inputs, SourceDetails);
            Assert.Equal("volumeBackup", source["type"]);
            Assert.Equal(generation.BackupIds![claim.Key], source["id"]);
            Assert.Equal(generation.Id, Map(restored.Inputs, FreeformTags)["generation"]);
        }
        var groups = mocks.Resources.Where(resource => resource.Type == OracleCloudResourceTokens.VolumeGroup).ToArray();
        Assert.Equal(2, groups.Length);
        foreach (var group in groups)
        {
            var selected = group.Name == "staging-data" ? "initial" : generation.Id;
            AssertGroupContains(group, volumes.Where(volume => Equals(Map(volume.Inputs, FreeformTags)["generation"], selected)));
        }
        var assignment = mocks.Resource(OracleCloudResourceTokens.BackupPolicyAssignment);
        Assert.Equal($"staging-{generation.Id}-data-id", assignment.Inputs["assetId"]);
    }

    [Fact]
    public async Task VolumeProfilesRemainSpecificToTheirGeneration()
    {
        var initial = new StorageGeneration { Volumes = new() { ["postgresql"] = new VolumeSettings
        { SizeInGbs = 100, Performance = "higher-performance" } } };
        var restored = PulumiTestData.RestoredGeneration() with { Volumes = new() { ["postgresql"] = new VolumeSettings
        { SizeInGbs = 200, Performance = "lower-cost" } } };
        var mocks = await CreateAsync("staging-durable", PulumiTestData.ValidSettings with
        { StorageGenerations = [initial, restored], ActiveStorageGeneration = restored.Id });
        var original = Assert.Single(mocks.Resources, r => r.Name == "staging-postgresql");
        var clone = Assert.Single(mocks.Resources, r => r.Name == $"staging-{restored.Id}-postgresql");
        Assert.Equal("100", original.Inputs[SizeInGbs]);
        Assert.Equal("20", original.Inputs[VpusPerGb]);
        Assert.Equal("200", clone.Inputs[SizeInGbs]);
        Assert.Equal("0", clone.Inputs[VpusPerGb]);
        AssertProtected<Oci.Core.Volume>(mocks, 14);
    }

    [Fact]
    public async Task NonDefaultWorkerSettingsReachTheNodePool()
    {
        var mocks = await CreateAsync("staging-cluster", PulumiTestData.ValidSettings with
        {
            Workers = 3, MaxPodsPerNode = 31, NodeShape = "VM.Standard.E5.Flex", NodeOcpus = 2,
            NodeMemoryInGbs = 16, BootVolumeSizeInGbs = 100, FaultDomains = ["FAULT-DOMAIN-1", "FAULT-DOMAIN-3"]
        });
        var worker = mocks.Resource(OracleCloudResourceTokens.NodePool);
        Assert.Equal("VM.Standard.E5.Flex", worker.Inputs[NodeShape]);
        Assert.Equal(2, Convert.ToInt32(Map(worker.Inputs, NodeShapeConfig)[Ocpus]));
        Assert.Equal(16, Convert.ToInt32(Map(worker.Inputs, NodeShapeConfig)[MemoryInGbs]));
        Assert.Equal("100", Map(worker.Inputs, NodeSourceDetails)[BootVolumeSizeInGbs]);
        var config = Map(worker.Inputs, NodeConfigDetails);
        Assert.Equal(3, Convert.ToInt32(config["size"]));
        Assert.Equal(31, Convert.ToInt32(Map(config, PodNetworkOptions)[MaxPodsPerNode]));
        var placement = Assert.Single(Maps(config, PlacementConfigs));
        Assert.Equal(PulumiTestData.ValidSettings.AvailabilityDomain, placement[AvailabilityDomain]);
        Assert.Equal(new[] { "FAULT-DOMAIN-1", "FAULT-DOMAIN-3" }, ((IEnumerable<object>)placement["faultDomains"]).Cast<string>());
    }

    [Theory]
    [DataInlineUnit(0)]
    [DataInlineUnit(1)]
    public async Task ClusterKeepsApprovedHardwareWithPrivateNativePodsAndRestrictedApiIngress(int workers)
    {
        var mocks = await CreateAsync("staging-cluster", PulumiTestData.ValidSettings with { Workers = workers });
        var cluster = mocks.Resource(OracleCloudResourceTokens.Cluster);
        Assert.Equal("BASIC_CLUSTER", cluster.Inputs["type"]);
        Assert.Equal("OCI_VCN_IP_NATIVE", Assert.Single(Maps(cluster.Inputs, "clusterPodNetworkOptions"))[CniType]);
        Assert.Equal(true, Map(cluster.Inputs, EndpointConfig)["isPublicIpEnabled"]);
        Assert.Equal("staging-api-subnet-id", Map(cluster.Inputs, EndpointConfig)[SubnetId]);
        Assert.False(Map(Map(cluster.Inputs, "options"), "kubernetesNetworkConfig").ContainsKey("podsCidr"));
        var worker = mocks.Resource(OracleCloudResourceTokens.NodePool);
        Assert.Equal("VM.Standard.A1.Flex", worker.Inputs[NodeShape]);
        Assert.Equal(4, Convert.ToInt32(Map(worker.Inputs, NodeShapeConfig)[Ocpus]));
        Assert.Equal(24, Convert.ToInt32(Map(worker.Inputs, NodeShapeConfig)[MemoryInGbs]));
        Assert.Equal("64", Map(worker.Inputs, NodeSourceDetails)[BootVolumeSizeInGbs]);
        var nodeConfig = Map(worker.Inputs, NodeConfigDetails);
        Assert.Equal(workers, Convert.ToInt32(nodeConfig["size"]));
        Assert.Equal(true, nodeConfig["isPvEncryptionInTransitEnabled"]);
        var native = Map(nodeConfig, PodNetworkOptions);
        Assert.Equal("OCI_VCN_IP_NATIVE", native[CniType]);
        Assert.Equal(93, Convert.ToInt32(native[MaxPodsPerNode]));
        Assert.Equal(new[] { "staging-pod-subnet-id" }, ((IEnumerable<object>)native["podSubnetIds"]).Cast<string>());
        var placement = Assert.Single(Maps(nodeConfig, PlacementConfigs));
        Assert.Equal("staging-worker-subnet-id", placement[SubnetId]);
        Assert.Equal(PulumiTestData.ValidSettings.AvailabilityDomain, placement[AvailabilityDomain]);
        var cloudInit = Encoding.UTF8.GetString(Convert.FromBase64String((string)Map(worker.Inputs, "nodeMetadata")["user_data"]));
        Assert.Equal(PolicyEnforcement.CloudInit, cloudInit);
        Assert.Contains("vm.max_map_count", cloudInit);
        Assert.Contains("bash /var/run/oke-init.sh", cloudInit);
        foreach (var name in new[] { "staging-worker-subnet", "staging-pod-subnet" })
        {
            var subnet = Assert.Single(mocks.Resources, resource => resource.Name == name);
            Assert.Equal(true, subnet.Inputs["prohibitPublicIpOnVnic"]);
            Assert.Equal("staging-private-routes-id", subnet.Inputs["routeTableId"]);
        }
        var podSubnet = Assert.Single(mocks.Resources, resource => resource.Name == "staging-pod-subnet");
        Assert.Equal("10.42.2.0/24", podSubnet.Inputs[CidrBlock]);
        Assert.False(worker.Inputs.ContainsKey("sshPublicKey"));
        var security = mocks.Resources.Where(resource => resource.Type == OracleCloudResourceTokens.SecurityList).ToArray();
        security.Should().HaveCount(3, "API, worker and pod subnets each need a security list");
        foreach (var list in security)
        {
            var rules = (ImmutableArray<object>)list.Inputs[IngressSecurityRules];
            foreach (var value in rules)
            {
                var rule = (ImmutableDictionary<string, object>)value;
                if (Equals(rule["source"], "0.0.0.0/0"))
                {
                    Assert.Equal("1", rule[Protocol]);
                    Assert.Equal(3, Convert.ToInt32(Map(rule, IcmpOptions)["type"]));
                    Assert.Equal(4, Convert.ToInt32(Map(rule, IcmpOptions)["code"]));
                }
            }
        }
        var apiSecurity = Assert.Single(mocks.Resources, resource => resource.Name == "staging-api-security");
        Assert.Contains(Maps(apiSecurity.Inputs, IngressSecurityRules), rule =>
            Equals(rule["source"], PulumiTestData.ValidSettings.ApiAllowedCidrs[0]) &&
            Equals(rule[Protocol], "6") && Convert.ToInt32(Map(rule, "tcpOptions")["min"]) == 6443);
        Assert.DoesNotContain(mocks.Resources, resource => resource.Type == OracleCloudResourceTokens.Volume);
    }

    [Fact]
    public async Task PrivateRoutesSelectNatAndAllOracleServicesRatherThanObjectStorageOnly()
    {
        var mocks = await CreateAsync("staging-cluster");
        Assert.Single(mocks.Calls, call => call.Token == OracleCloudResourceTokens.GetServices);
        var gateway = mocks.Resource(OracleCloudResourceTokens.ServiceGateway);
        Assert.Equal(OracleCloudServices.AllServicesId, Assert.Single(Maps(gateway.Inputs, "services"))["serviceId"]);
        var routes = Assert.Single(mocks.Resources, resource => resource.Name == "staging-private-routes");
        var rules = Maps(routes.Inputs, "routeRules").ToArray();
        Assert.Equal(2, rules.Length);
        Assert.Contains(rules, rule => Equals(rule["destination"], "0.0.0.0/0") && Equals(rule["networkEntityId"], "staging-nat-id"));
        Assert.Contains(rules, rule => Equals(rule["destination"], OracleCloudServices.AllServicesCidr) &&
            Equals(rule["destinationType"], "SERVICE_CIDR_BLOCK") && Equals(rule["networkEntityId"], "staging-services-id"));
    }

    private static void AssertGroupContains(MockResourceArgs group, IEnumerable<MockResourceArgs> volumes)
    {
        var source = Map(group.Inputs, SourceDetails);
        Assert.Equal("volumeIds", source["type"]);
        Assert.Equal(volumes.Select(volume => $"{volume.Name}-id").Order(), ((IEnumerable<object>)source["volumeIds"]).Cast<string>().Order());
    }

    private static void AssertProtected<T>(RecordingMocks mocks, int count) where T : Resource
    {
        var registrations = mocks.Registrations.Where(registration => registration.Resource is T).ToArray();
        Assert.Equal(count, registrations.Length);
        Assert.All(registrations, registration => Assert.True(registration.Options.Protect));
    }

}
