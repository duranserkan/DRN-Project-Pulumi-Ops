using System.Collections.Concurrent;
using Drn.Pulumi;
using Pulumi;
using Pulumi.Testing;

namespace Drn.Pulumi.UnitTests.Support;

internal static class ResourceFixture
{
    internal static async Task<RecordingMocks> CreateAsync(string stackName, InfrastructureSettings? settings = null)
    {
        var mocks = new RecordingMocks();
        RecordingStack.CreateResources = () => Infrastructure.Create(settings ?? PulumiTestData.ValidSettings, stackName);
        RecordingStack.Record = args => mocks.Registrations.Add(args);
        try
        {
            await Deployment.TestAsync<RecordingStack>(mocks, new TestOptions { ProjectName = "drn-pulumi", StackName = stackName });
            return mocks;
        }
        finally
        {
            RecordingStack.CreateResources = null;
            RecordingStack.Record = null;
        }
    }

    // Pulumi mocks omit options. Observe protection without modifying the registration.
    // All fixture consumers must join PulumiDeploymentCollection because these callbacks are process-wide.
    private sealed class RecordingStack : Stack
    {
        internal static Action? CreateResources { get; set; }
        internal static Action<ResourceTransformationArgs>? Record { get; set; }

        public RecordingStack() : base(new StackOptions
        {
            ResourceTransformations = { args => { Record!(args); return null; } }
        }) => CreateResources!();
    }

    internal sealed class RecordingMocks : IMocks
    {
        internal ConcurrentBag<MockResourceArgs> Resources { get; } = [];
        internal ConcurrentBag<MockCallArgs> Calls { get; } = [];
        internal ConcurrentBag<ResourceTransformationArgs> Registrations { get; } = [];

        internal MockResourceArgs Resource(string token) => Resources.Should()
            .ContainSingle(resource => resource.Type == token, $"stack must register exactly one {token}").Which;

        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            Resources.Add(args);
            return Task.FromResult<(string? id, object state)>(($"{args.Name}-id", args.Inputs));
        }

        public Task<object> CallAsync(MockCallArgs args)
        {
            Calls.Add(args);
            if (args.Token != OracleCloudResourceTokens.GetServices)
                throw new Xunit.Sdk.XunitException($"Unexpected provider invocation: {args.Token}");
            return Task.FromResult<object>(new Dictionary<string, object>
            {
                ["id"] = "services",
                ["services"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = "ocid1.service.oc1..object-storage", [OracleCloudInputFields.CidrBlock] = "oci-fra-objectstorage",
                        ["name"] = "OCI FRA Object Storage", ["description"] = "Object Storage"
                    },
                    new Dictionary<string, object>
                    {
                        ["id"] = OracleCloudServices.AllServicesId, [OracleCloudInputFields.CidrBlock] = OracleCloudServices.AllServicesCidr,
                        ["name"] = "All FRA Services In Oracle Services Network", ["description"] = "All services"
                    }
                }
            });
        }
    }
}
