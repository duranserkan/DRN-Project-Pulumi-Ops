using Pulumi;
using Oci = Pulumi.Oci;

namespace Drn.Pulumi;

public static class Infrastructure
{
    public static string RoleForStack(string stackName) => stackName switch
    {
        "staging-recovery" => "recovery",
        "staging-durable" => "durable",
        "staging-cluster" => "cluster",
        "staging-bootstrap" => "bootstrap",
        _ => throw new ArgumentException("Select a staging recovery, durable, cluster or bootstrap stack. Production is disabled.")
    };

    public static IDictionary<string, object?> Create(InfrastructureSettings settings, string stackName)
    {
        var role = RoleForStack(stackName);
        settings.Validate(role);
        if (role == "bootstrap") return Bootstrap.Create(settings);
        var provider = new Oci.Provider("oci", new Oci.ProviderArgs
        {
            Region = settings.Region,
            Auth = settings.OciAuth,
            ConfigFileProfile = settings.OciProfile
        });
        return role switch
        {
            "recovery" => RecoveryInfrastructure.Create(settings, provider),
            "durable" => Durable.Create(settings, provider),
            _ => Cluster.Create(settings, provider)
        };
    }
}
