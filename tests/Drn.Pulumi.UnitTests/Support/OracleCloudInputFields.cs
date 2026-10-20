namespace Drn.Pulumi.UnitTests.Support;

// Pulumi's serialized input names are not the OCI CLI's kebab-case names.
internal static class OracleCloudInputFields
{
    internal const string CompartmentId = "compartmentId";
    internal const string AvailabilityDomain = "availabilityDomain";
    internal const string FreeformTags = "freeformTags";
    internal const string SizeInGbs = "sizeInGbs";
    internal const string VpusPerGb = "vpusPerGb";
    internal const string SourceDetails = "sourceDetails";
    internal const string NodeShape = "nodeShape";
    internal const string NodeShapeConfig = "nodeShapeConfig";
    internal const string Ocpus = "ocpus";
    internal const string MemoryInGbs = "memoryInGbs";
    internal const string NodeSourceDetails = "nodeSourceDetails";
    internal const string BootVolumeSizeInGbs = "bootVolumeSizeInGbs";
    internal const string NodeConfigDetails = "nodeConfigDetails";
    internal const string PodNetworkOptions = "nodePoolPodNetworkOptionDetails";
    internal const string MaxPodsPerNode = "maxPodsPerNode";
    internal const string PlacementConfigs = "placementConfigs";
    internal const string CniType = "cniType";
    internal const string EndpointConfig = "endpointConfig";
    internal const string SubnetId = "subnetId";
    internal const string IngressSecurityRules = "ingressSecurityRules";
    internal const string Protocol = "protocol";
    internal const string IcmpOptions = "icmpOptions";
    internal const string CidrBlock = "cidrBlock";
}
