using Drn.Pulumi;

namespace Drn.Pulumi.UnitTests.Support;

internal static class PulumiTestData
{
    internal static InfrastructureSettings ValidSettings => new()
    {
        Region = "eu-frankfurt-1",
        AvailabilityDomain = "test:EU-FRANKFURT-1-AD-1",
        ClusterCompartmentId = "ocid1.compartment.oc1..cluster",
        DurableCompartmentId = "ocid1.compartment.oc1..durable",
        KubernetesVersion = "v1.36.1",
        NodeImageId = "ocid1.image.oc1.eu-frankfurt-1.example",
        ApiAllowedCidrs = ["203.0.113.10/32"],
        AllowBillableStorage = true,
        ObjectStorageNamespace = "example-namespace",
        RecoveryBucketName = "drn-staging-recovery",
        DurableStack = "example/drn-pulumi/staging-durable",
        ClusterStack = "example/drn-pulumi/staging-cluster",
        GitOpsTag = "refs/tags/2026.10.1-preview003",
        GitOpsCommit = new string('a', 40)
    };

    internal static StorageGeneration RestoredGeneration(char id = 'a') => new()
    {
        Id = new string(id, 32),
        BackupIds = StorageClaims.All.ToDictionary(claim => claim.Key,
            claim => $"ocid1.volumebackup.oc1.eu-frankfurt-1.{id}-{claim.Key}")
    };
}
