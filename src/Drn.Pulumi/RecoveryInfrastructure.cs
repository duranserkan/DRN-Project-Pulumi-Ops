using Pulumi;
using Pulumi.Oci.ObjectStorage;
using Oci = Pulumi.Oci;

namespace Drn.Pulumi;

internal static class RecoveryInfrastructure
{
    internal static IDictionary<string, object?> Create(InfrastructureSettings settings, Oci.Provider provider)
    {
        var bucket = new Bucket("staging-recovery", new BucketArgs
        {
            Name = settings.RecoveryBucketName,
            Namespace = settings.ObjectStorageNamespace,
            CompartmentId = settings.DurableCompartmentId,
            AccessType = "NoPublicAccess",
            StorageTier = "Standard",
            Versioning = "Enabled"
        }, new CustomResourceOptions { Provider = provider, Protect = true });
        return new Dictionary<string, object?>
        {
            ["bucketName"] = bucket.Name,
            ["namespace"] = settings.ObjectStorageNamespace,
            ["region"] = settings.Region
        };
    }
}
