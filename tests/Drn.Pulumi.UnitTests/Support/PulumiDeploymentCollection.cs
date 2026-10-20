using Xunit;

namespace Drn.Pulumi.UnitTests.Support;

[CollectionDefinition(PulumiDeploymentCollection.Name, DisableParallelization = true)]
public sealed class PulumiDeploymentCollection
{
    public const string Name = "Pulumi deployment isolation";
}
