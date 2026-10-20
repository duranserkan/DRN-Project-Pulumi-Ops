using System.Text.Json;
using Drn.Infrastructure.Operator.Recovery;
using Drn.Pulumi;

namespace Drn.Infrastructure.Operator.UnitTests.Support;

internal static class OperatorTestData
{
    internal const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string StateObject = "staging/state.json";
    internal const string LockObject = "staging/lock.json";
    internal static DateTimeOffset Timestamp => new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    // Generate real credentials once for setup-only consumers. Never share mutable JSON or key objects.
    private static readonly Lazy<string> CredentialJson = new(() => JsonSerializer.Serialize(
        CredentialCheckpoint.Create(Commit, DateTimeOffset.UtcNow), OperatorConfiguration.JsonOptions));

    internal static CredentialCheckpoint Credentials() =>
        JsonSerializer.Deserialize<CredentialCheckpoint>(CredentialJson.Value, OperatorConfiguration.JsonOptions)!
            with { Generation = Guid.NewGuid().ToString("N") };

    internal static LifecycleState InitializedState(OperatorConfiguration configuration, CredentialCheckpoint credentials) => new()
    {
        Organization = configuration.Organization, Region = configuration.Infrastructure.Region,
        AvailabilityDomain = configuration.Infrastructure.AvailabilityDomain,
        ClusterCompartmentId = configuration.Infrastructure.ClusterCompartmentId,
        DurableCompartmentId = configuration.Infrastructure.DurableCompartmentId,
        GitOpsCommit = configuration.Infrastructure.GitOpsCommit, GitOpsTag = configuration.Infrastructure.GitOpsTag,
        CredentialGeneration = credentials.Generation, CredentialHash = credentials.StableIdentityHash(),
        OperationId = Guid.NewGuid(), StorageGenerations = configuration.Infrastructure.StorageGenerations
    };

    internal static OperatorConfiguration Configuration() => new()
    {
        Organization = "example", RecoveryKeyPath = "/private/recovery.key",
        Infrastructure = new InfrastructureSettings
        {
            Region = "eu-frankfurt-1", AvailabilityDomain = "example:EU-FRANKFURT-1-AD-1",
            ClusterCompartmentId = "ocid1.compartment.oc1..cluster-test", DurableCompartmentId = "ocid1.compartment.oc1..durable-test",
            KubernetesVersion = "v1.36.1", NodeImageId = "ocid1.image.oc1.eu-frankfurt-1.test", ApiAllowedCidrs = ["192.0.2.1/32"],
            AllowBillableStorage = true, ObjectStorageNamespace = "namespace", RecoveryBucketName = "drn-recovery",
            GitOpsTag = "refs/tags/2026.10.1-preview001", GitOpsCommit = Commit
        }
    };

    internal static string CreateWorkspace(DrnTestContextUnit context)
    {
        var directory = context.GetTempPath();
        var project = Path.Combine(directory, "src", "Drn.Pulumi");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Pulumi.yaml"), "name: drn-pulumi");
        return directory;
    }
}
