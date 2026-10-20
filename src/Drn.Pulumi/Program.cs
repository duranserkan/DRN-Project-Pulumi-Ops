using Pulumi;

namespace Drn.Pulumi;

public static class Program
{
    public static Task<int> Main() => Deployment.RunAsync(() =>
    {
        var settings = new Config().RequireObject<InfrastructureSettings>("settings");
        return Infrastructure.Create(settings, Deployment.Instance.StackName);
    });
}
