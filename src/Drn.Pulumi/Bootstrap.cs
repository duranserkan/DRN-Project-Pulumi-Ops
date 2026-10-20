using System.Collections.Immutable;
using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Storage.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Yaml;
using Kubernetes = Pulumi.Kubernetes;
using ReleaseArgs = Pulumi.Kubernetes.Types.Inputs.Helm.V3.ReleaseArgs;

namespace Drn.Pulumi;

public static class Bootstrap
{
    public const string ArgoChartVersion = "10.10.5";
    public static IReadOnlyList<string> RequiredFiles { get; } = Array.AsReadOnly<string>(
    [
        "infrastructure/argocd/custom-values.yaml", "infrastructure/argocd/default-project.yaml",
        "apps/bootstrap-project.yaml", "apps/staging/application.yaml",
        "deployment/values.yaml", "deployment/templates/validate.yaml"
    ]);

    internal static IDictionary<string, object?> Create(InfrastructureSettings settings)
    {
        var durable = new StackReference(settings.DurableStack);
        var cluster = new StackReference(settings.ClusterStack);
        var volumeIds = Output.Tuple(durable.RequireOutput("region"), durable.RequireOutput("availabilityDomain"),
            cluster.RequireOutput("region"), cluster.RequireOutput("availabilityDomain"), durable.RequireOutput("volumeIds"))
            .Apply(values =>
            {
                if (!Equals(values.Item1, settings.Region) || !Equals(values.Item3, settings.Region) ||
                    !Equals(values.Item2, settings.AvailabilityDomain) || !Equals(values.Item4, settings.AvailabilityDomain))
                    throw new InvalidOperationException("Durable and cluster stacks must match the configured region and availability domain.");
                return (ImmutableDictionary<string, object>)values.Item5;
            });
        var kubeconfig = cluster.RequireOutput("clusterId").Apply(id =>
        {
            var contents = File.ReadAllText(settings.KubeconfigPath);
            if (id is not string clusterId || !contents.Contains(clusterId, StringComparison.Ordinal))
                throw new InvalidOperationException("The kubeconfig does not identify the configured cluster stack. Regenerate it with drn-infra kubeconfig.");
            return contents;
        });
        var provider = new Kubernetes.Provider("staging-kubernetes", new Kubernetes.ProviderArgs
        {
            KubeConfig = Output.CreateSecret(kubeconfig),
            EnableServerSideApply = true
        });
        var policies = PolicyEnforcement.Create(provider);
        var storageClass = new StorageClass(StorageClaims.StorageClass, new Kubernetes.Types.Inputs.Storage.V1.StorageClassArgs
        {
            Metadata = new ObjectMetaArgs { Name = StorageClaims.StorageClass },
            Provisioner = "kubernetes.io/no-provisioner",
            ReclaimPolicy = "Retain",
            VolumeBindingMode = "WaitForFirstConsumer",
            AllowVolumeExpansion = false
        }, new CustomResourceOptions { Provider = provider });
        var volumeSizes = durable.GetOutput("volumeSizesInGbs").Apply(value => ReadVolumeSizes(value, settings.ActiveStorage));
        foreach (var claim in StorageClaims.All)
            _ = new PersistentVolume($"staging-{claim.Key}", new PersistentVolumeArgs
            {
                Metadata = new ObjectMetaArgs { Name = $"drn-staging-{claim.Key}" },
                Spec = new PersistentVolumeSpecArgs
                {
                    AccessModes = { "ReadWriteOnce" },
                    Capacity = { ["storage"] = volumeSizes.Apply(sizes => $"{sizes[claim.Key]}Gi") },
                    StorageClassName = StorageClaims.StorageClass,
                    PersistentVolumeReclaimPolicy = "Retain",
                    VolumeMode = "Filesystem",
                    ClaimRef = new ObjectReferenceArgs { Namespace = claim.Namespace, Name = claim.ClaimName },
                    Csi = new CSIPersistentVolumeSourceArgs
                    {
                        Driver = "blockvolume.csi.oraclecloud.com",
                        VolumeHandle = volumeIds.Apply(ids => ids.TryGetValue(claim.Key, out var id) && id is string value
                            ? value : throw new InvalidOperationException($"Durable stack has no volume for {claim.Key}.")),
                        FsType = "ext4",
                        VolumeAttributes = { ["attachment-type"] = "paravirtualized" }
                    }
                }
            }, new CustomResourceOptions { Provider = provider, DependsOn = { storageClass } });

        var argoNamespace = new Namespace("argocd", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = "argocd" }
        }, new CustomResourceOptions { Provider = provider });
        var argo = new Release("argocd", new ReleaseArgs
        {
            Name = "argocd",
            Namespace = "argocd",
            Chart = "argo-cd",
            Version = ArgoChartVersion,
            RepositoryOpts = new RepositoryOptsArgs { Repo = "https://argoproj.github.io/argo-helm" },
            ValueYamlFiles = { new FileAsset(Path.Combine(settings.GitOpsDirectory, RequiredFiles[0])) },
            Atomic = true,
            Timeout = 600
        }, new CustomResourceOptions { Provider = provider, DependsOn = { argoNamespace, policies } });
        var defaultProject = new ConfigFile("restricted-default-project", new ConfigFileArgs
        {
            File = Path.Combine(settings.GitOpsDirectory, RequiredFiles[1])
        }, new ComponentResourceOptions { Providers = { provider }, DependsOn = { argo } });
        var bootstrapProject = new ConfigFile("bootstrap-project", new ConfigFileArgs
        {
            File = Path.Combine(settings.GitOpsDirectory, RequiredFiles[2]),
            Transformations = { (resource, _) => PrepareBootstrapProject(resource, settings.GitOpsRepository) }
        }, new ComponentResourceOptions { Providers = { provider }, DependsOn = { defaultProject } });
        _ = new ConfigFile("staging-root", new ConfigFileArgs
        {
            File = Path.Combine(settings.GitOpsDirectory, RequiredFiles[3]),
            // The operator opens this gate only after credential readback and live policy checks.
            SkipAwait = true,
            Transformations = { (resource, _) => PrepareRoot(resource, settings) }
        }, new ComponentResourceOptions { Providers = { provider }, DependsOn = { bootstrapProject } });
        return new Dictionary<string, object?>
        {
            ["gitOpsTag"] = settings.GitOpsTag,
            ["gitOpsCommit"] = settings.GitOpsCommit,
            ["storageClass"] = StorageClaims.StorageClass,
            ["workloadSyncEnabled"] = false
        };
    }

    public static IReadOnlyDictionary<string, int> ReadVolumeSizes(object? output, StorageGeneration generation)
    {
        generation.ValidateVolumes();
        var sizes = new Dictionary<string, int>();
        if (output is not null && output is not ImmutableDictionary<string, object>)
            throw new InvalidOperationException("Durable volume sizes have an invalid format.");
        var values = output as ImmutableDictionary<string, object>;
        if (values is not null && !StorageClaims.All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal).SetEquals(values.Keys))
            throw new InvalidOperationException("Durable volume sizes must identify all seven claims.");
        foreach (var claim in StorageClaims.All)
        {
            // Legacy stacks without per-claim outputs only provisioned 50 GB volumes.
            var raw = values is null ? StorageClaims.VolumeSizeInGbs : values[claim.Key];
            if (raw is not (int or long or double) || Convert.ToDouble(raw) != generation.Volume(claim.Key).SizeInGbs)
                throw new InvalidOperationException($"Durable capacity for {claim.Key} does not match the recorded storage generation.");
            sizes.Add(claim.Key, generation.Volume(claim.Key).SizeInGbs);
        }
        return sizes;
    }

    public static ImmutableDictionary<string, object> PrepareRoot(ImmutableDictionary<string, object> resource, InfrastructureSettings settings)
    {
        if (!Equals(resource["kind"], "Application"))
            throw new InvalidOperationException("The staging root input must contain only its Application.");
        var spec = Map(resource, "spec");
        var metadata = Map(resource, "metadata");
        var destination = Map(spec, "destination");
        if (!Equals(metadata["name"], "drn-project") || !Equals(metadata["namespace"], "argocd") ||
            !Equals(spec["project"], "drn-project-bootstrap") || metadata.ContainsKey("finalizers") ||
            !Equals(destination["namespace"], "argocd") || !Equals(destination["server"], "https://kubernetes.default.svc"))
            throw new InvalidOperationException("The staging root must retain its restricted identity without cascading deletion finalizers.");
        var source = Map(spec, "source");
        if (!Equals(source["path"], "deployment"))
            throw new InvalidOperationException("The staging root must select the source-owned deployment chart.");
        var helm = Map(source, "helm").SetItem("parameters", new[]
        {
            Parameter("environment", "staging"), Parameter("gitRevision", "$ARGOCD_APP_REVISION"),
            Parameter("releaseTag", settings.GitOpsTag), Parameter("repository", settings.GitOpsRepository),
            Parameter("infrastructureProfile", "oci-retained"), Parameter("activationStage", "prepare")
        }.ToImmutableArray());
        source = source.SetItem("targetRevision", settings.GitOpsCommit).SetItem("repoURL", settings.GitOpsRepository).SetItem("helm", helm);
        var syncPolicy = new Dictionary<string, object> { ["syncOptions"] = new[] { "ApplyOutOfSyncOnly=false" } }.ToImmutableDictionary();
        return resource.Remove("operation").SetItem("spec", spec.SetItem("source", source).SetItem("syncPolicy", syncPolicy));
    }

    public static ImmutableDictionary<string, object> PrepareBootstrapProject(ImmutableDictionary<string, object> resource, string repository)
    {
        if (!Equals(resource["kind"], "AppProject") || !Equals(Map(resource, "metadata")["name"], "drn-project-bootstrap") ||
            !Equals(Map(resource, "metadata")["namespace"], "argocd"))
            throw new InvalidOperationException("The bootstrap project input must contain only drn-project-bootstrap.");
        // The lifecycle coordinator opens this gate after restoring the recorded credential generation.
        var deny = new Dictionary<string, object>
        {
            ["kind"] = "deny", ["schedule"] = "* * * * *", ["duration"] = "24h",
            ["applications"] = new[] { "drn-project" }, ["manualSync"] = false
        }.ToImmutableDictionary();
        return resource.SetItem("spec", Map(resource, "spec")
            .SetItem("sourceRepos", new[] { repository }).SetItem("syncWindows", new[] { deny }.ToImmutableArray()));
    }

    private static ImmutableDictionary<string, object> Map(ImmutableDictionary<string, object> value, string key) =>
        value.TryGetValue(key, out var child) && child is ImmutableDictionary<string, object> map
            ? map : throw new InvalidOperationException($"GitOps input is missing the {key} mapping.");

    private static ImmutableDictionary<string, object> Parameter(string name, string value) => new Dictionary<string, object>
    {
        ["name"] = name, ["value"] = value, ["forceString"] = true
    }.ToImmutableDictionary();
}
