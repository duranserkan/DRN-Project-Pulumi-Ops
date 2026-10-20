using Pulumi;
using Pulumi.Oci.ContainerEngine;
using Pulumi.Oci.ContainerEngine.Inputs;
using Pulumi.Oci.Core;
using Pulumi.Oci.Core.Inputs;
using Oci = Pulumi.Oci;
using OkeCluster = Pulumi.Oci.ContainerEngine.Cluster;

namespace Drn.Pulumi;

internal static class Cluster
{
    internal const string VcnCidr = "10.42.0.0/16";
    internal const string ApiCidr = "10.42.0.0/28";
    internal const string WorkerCidr = "10.42.1.0/24";
    internal const string PodCidr = "10.42.2.0/24";
    internal const string ServiceCidr = "10.96.0.0/16";

    internal static IDictionary<string, object?> Create(InfrastructureSettings settings, Oci.Provider provider)
    {
        CustomResourceOptions Options() => new() { Provider = provider };
        var vcn = new Vcn("staging-network", new VcnArgs
        {
            CompartmentId = settings.ClusterCompartmentId,
            DisplayName = "drn-staging",
            DnsLabel = "drnstaging",
            CidrBlocks = { VcnCidr }
        }, Options());
        var gateway = new InternetGateway("staging-internet", new InternetGatewayArgs
        {
            CompartmentId = settings.ClusterCompartmentId,
            VcnId = vcn.Id,
            DisplayName = "drn-staging",
            Enabled = true
        }, Options());
        var routes = new RouteTable("staging-routes", new RouteTableArgs
        {
            CompartmentId = settings.ClusterCompartmentId,
            VcnId = vcn.Id,
            RouteRules = { new RouteTableRouteRuleArgs
            {
                Destination = "0.0.0.0/0", DestinationType = "CIDR_BLOCK", NetworkEntityId = gateway.Id
            } }
        }, Options());
        var nat = new NatGateway("staging-nat", new NatGatewayArgs
        {
            CompartmentId = settings.ClusterCompartmentId, VcnId = vcn.Id, BlockTraffic = false
        }, Options());
        var services = GetServices.Invoke(new GetServicesInvokeArgs(), new InvokeOptions { Provider = provider })
            .Apply(result => result.Services.Single(service => service.CidrBlock.EndsWith("-services-in-oracle-services-network", StringComparison.Ordinal)));
        var serviceGateway = new ServiceGateway("staging-services", new ServiceGatewayArgs
        {
            CompartmentId = settings.ClusterCompartmentId, VcnId = vcn.Id,
            Services = { new ServiceGatewayServiceArgs { ServiceId = services.Apply(service => service.Id) } }
        }, Options());
        var privateRoutes = new RouteTable("staging-private-routes", new RouteTableArgs
        {
            CompartmentId = settings.ClusterCompartmentId, VcnId = vcn.Id,
            RouteRules =
            {
                new RouteTableRouteRuleArgs { Destination = "0.0.0.0/0", DestinationType = "CIDR_BLOCK", NetworkEntityId = nat.Id },
                new RouteTableRouteRuleArgs
                {
                    Destination = services.Apply(service => service.CidrBlock),
                    DestinationType = "SERVICE_CIDR_BLOCK", NetworkEntityId = serviceGateway.Id
                }
            }
        }, Options());
        var apiIngress = new InputList<SecurityListIngressSecurityRuleArgs>();
        foreach (var cidr in settings.ApiAllowedCidrs)
            apiIngress.Add(Tcp(cidr, 6443));
        apiIngress.Add(Tcp(WorkerCidr, 6443));
        apiIngress.Add(Tcp(WorkerCidr, 12250));
        apiIngress.Add(Tcp(PodCidr, 6443));
        apiIngress.Add(Tcp(PodCidr, 12250));
        apiIngress.Add(PathMtu(WorkerCidr));
        var apiSecurity = new SecurityList("staging-api-security", new SecurityListArgs
        {
            CompartmentId = settings.ClusterCompartmentId,
            VcnId = vcn.Id,
            IngressSecurityRules = apiIngress,
            EgressSecurityRules = { InternetEgress() }
        }, Options());
        var workerSecurity = new SecurityList("staging-worker-security", new SecurityListArgs
        {
            CompartmentId = settings.ClusterCompartmentId,
            VcnId = vcn.Id,
            IngressSecurityRules =
            {
                new SecurityListIngressSecurityRuleArgs { Source = WorkerCidr, Protocol = "all", Description = "Worker communication" },
                new SecurityListIngressSecurityRuleArgs { Source = PodCidr, Protocol = "all", Description = "Native pod communication" },
                new SecurityListIngressSecurityRuleArgs { Source = ApiCidr, Protocol = "6", Description = "Managed control plane to kubelet and webhooks" },
                PathMtu("0.0.0.0/0")
            },
            EgressSecurityRules = { InternetEgress() }
        }, Options());
        var podSecurity = new SecurityList("staging-pod-security", new SecurityListArgs
        {
            CompartmentId = settings.ClusterCompartmentId, VcnId = vcn.Id,
            IngressSecurityRules =
            {
                new SecurityListIngressSecurityRuleArgs { Source = ApiCidr, Protocol = "all" },
                new SecurityListIngressSecurityRuleArgs { Source = WorkerCidr, Protocol = "all" },
                new SecurityListIngressSecurityRuleArgs { Source = PodCidr, Protocol = "all" }
            },
            EgressSecurityRules = { InternetEgress() }
        }, Options());
        var apiSubnet = new Subnet("staging-api-subnet", new SubnetArgs
        {
            CompartmentId = settings.ClusterCompartmentId,
            VcnId = vcn.Id,
            CidrBlock = ApiCidr,
            DnsLabel = "api",
            RouteTableId = routes.Id,
            SecurityListIds = { apiSecurity.Id },
            ProhibitPublicIpOnVnic = false
        }, Options());
        var workerSubnet = new Subnet("staging-worker-subnet", new SubnetArgs
        {
            CompartmentId = settings.ClusterCompartmentId,
            VcnId = vcn.Id,
            CidrBlock = WorkerCidr,
            DnsLabel = "workers",
            RouteTableId = privateRoutes.Id,
            SecurityListIds = { workerSecurity.Id },
            ProhibitPublicIpOnVnic = true
        }, Options());
        var podSubnet = new Subnet("staging-pod-subnet", new SubnetArgs
        {
            CompartmentId = settings.ClusterCompartmentId, VcnId = vcn.Id,
            CidrBlock = PodCidr, DnsLabel = "pods", RouteTableId = privateRoutes.Id,
            SecurityListIds = { podSecurity.Id }, ProhibitPublicIpOnVnic = true
        }, Options());
        var cluster = new OkeCluster("staging-cluster", new ClusterArgs
        {
            CompartmentId = settings.ClusterCompartmentId,
            VcnId = vcn.Id,
            Name = "drn-staging",
            Type = "BASIC_CLUSTER",
            KubernetesVersion = settings.KubernetesVersion,
            EndpointConfig = new ClusterEndpointConfigArgs { SubnetId = apiSubnet.Id, IsPublicIpEnabled = true },
            ClusterPodNetworkOptions = { new ClusterClusterPodNetworkOptionArgs { CniType = "OCI_VCN_IP_NATIVE" } },
            Options = new ClusterOptionsArgs
            {
                KubernetesNetworkConfig = new ClusterOptionsKubernetesNetworkConfigArgs
                {
                    ServicesCidr = ServiceCidr
                }
            }
        }, Options());
        var placement = new NodePoolNodeConfigDetailsPlacementConfigArgs
        {
            AvailabilityDomain = settings.AvailabilityDomain, SubnetId = workerSubnet.Id
        };
        if (settings.FaultDomains.Length > 0) placement.FaultDomains = settings.FaultDomains;
        var nodePool = new NodePool("staging-workers", new NodePoolArgs
        {
            CompartmentId = settings.ClusterCompartmentId,
            ClusterId = cluster.Id,
            Name = "drn-staging",
            KubernetesVersion = settings.KubernetesVersion,
            NodeShape = settings.NodeShape,
            NodeMetadata = { ["user_data"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(PolicyEnforcement.CloudInit)) },
            NodeShapeConfig = new NodePoolNodeShapeConfigArgs { Ocpus = settings.NodeOcpus, MemoryInGbs = settings.NodeMemoryInGbs },
            NodeSourceDetails = new NodePoolNodeSourceDetailsArgs
            {
                ImageId = settings.NodeImageId, SourceType = "IMAGE",
                BootVolumeSizeInGbs = settings.BootVolumeSizeInGbs.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            NodeConfigDetails = new NodePoolNodeConfigDetailsArgs
            {
                Size = settings.Workers,
                IsPvEncryptionInTransitEnabled = true,
                NodePoolPodNetworkOptionDetails = new NodePoolNodeConfigDetailsNodePoolPodNetworkOptionDetailsArgs
                {
                    CniType = "OCI_VCN_IP_NATIVE", MaxPodsPerNode = settings.MaxPodsPerNode, PodSubnetIds = { podSubnet.Id }
                },
                PlacementConfigs = { placement }
            },
            NodeEvictionNodePoolSettings = new NodePoolNodeEvictionNodePoolSettingsArgs
            {
                EvictionGraceDuration = "PT1H", IsForceActionAfterGraceDuration = false, IsForceDeleteAfterGraceDuration = false
            }
        }, new CustomResourceOptions { Provider = provider, DeleteBeforeReplace = true });
        return new Dictionary<string, object?>
        {
            ["clusterId"] = cluster.Id,
            ["nodePoolId"] = nodePool.Id,
            ["region"] = settings.Region,
            ["availabilityDomain"] = settings.AvailabilityDomain,
            ["workers"] = settings.Workers,
            ["podNetwork"] = "OCI_VCN_IP_NATIVE"
        };
    }

    private static SecurityListIngressSecurityRuleArgs Tcp(string source, int port) => new()
    {
        Source = source,
        Protocol = "6",
        TcpOptions = new SecurityListIngressSecurityRuleTcpOptionsArgs { Min = port, Max = port }
    };

    private static SecurityListIngressSecurityRuleArgs PathMtu(string source) => new()
    {
        Source = source,
        Protocol = "1",
        IcmpOptions = new SecurityListIngressSecurityRuleIcmpOptionsArgs { Type = 3, Code = 4 },
        Description = "Path MTU discovery"
    };

    private static SecurityListEgressSecurityRuleArgs InternetEgress() => new()
    {
        Destination = "0.0.0.0/0", Protocol = "all", Description = "OCI services, DNS and pinned public dependencies"
    };
}
