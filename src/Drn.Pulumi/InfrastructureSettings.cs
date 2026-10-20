using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Drn.Pulumi;

public sealed record InfrastructureSettings
{
    [JsonPropertyName("region")] public string Region { get; init; } = "";
    [JsonPropertyName("availabilityDomain")] public string AvailabilityDomain { get; init; } = "";
    [JsonPropertyName("clusterCompartmentId")] public string ClusterCompartmentId { get; init; } = "";
    [JsonPropertyName("durableCompartmentId")] public string DurableCompartmentId { get; init; } = "";
    [JsonPropertyName("kubernetesVersion")] public string KubernetesVersion { get; init; } = "";
    [JsonPropertyName("nodeImageId")] public string NodeImageId { get; init; } = "";
    [JsonPropertyName("apiAllowedCidrs")] public string[] ApiAllowedCidrs { get; init; } = [];
    [JsonPropertyName("ociProfile")] public string OciProfile { get; init; } = "DRN-STAGING";
    [JsonPropertyName("ociAuth")] public string OciAuth { get; init; } = "SecurityToken";
    [JsonPropertyName("objectStorageNamespace")] public string ObjectStorageNamespace { get; init; } = "";
    [JsonPropertyName("recoveryBucketName")] public string RecoveryBucketName { get; init; } = "";
    [JsonPropertyName("storageGenerations")] public StorageGeneration[] StorageGenerations { get; init; } = [new()];
    [JsonPropertyName("activeStorageGeneration")] public string ActiveStorageGeneration { get; init; } = "initial";
    [JsonPropertyName("allowBillableStorage")] public bool AllowBillableStorage { get; init; }
    [JsonPropertyName("workers")] public int Workers { get; init; } = 1;
    [JsonPropertyName("faultDomains")] public string[] FaultDomains { get; init; } = [];
    [JsonPropertyName("nodeShape")] public string NodeShape { get; init; } = "VM.Standard.A1.Flex";
    [JsonPropertyName("nodeOcpus")] public int NodeOcpus { get; init; } = 4;
    [JsonPropertyName("nodeMemoryInGbs")] public double NodeMemoryInGbs { get; init; } = 24;
    [JsonPropertyName("bootVolumeSizeInGbs")] public int BootVolumeSizeInGbs { get; init; } = StorageClaims.BootSizeInGbs;
    [JsonPropertyName("maxPodsPerNode")] public int MaxPodsPerNode { get; init; } = 93;
    [JsonPropertyName("durableStack")] public string DurableStack { get; init; } = "";
    [JsonPropertyName("clusterStack")] public string ClusterStack { get; init; } = "";
    [JsonPropertyName("gitOpsRepository")] public string GitOpsRepository { get; init; } = "https://github.com/duranserkan/DRN-Project-Argo-CD-Gitops.git";
    [JsonPropertyName("gitOpsTag")] public string GitOpsTag { get; init; } = "";
    [JsonPropertyName("gitOpsCommit")] public string GitOpsCommit { get; init; } = "";
    [JsonPropertyName("gitOpsDirectory")] public string GitOpsDirectory { get; init; } = "";
    [JsonPropertyName("kubeconfigPath")] public string KubeconfigPath { get; init; } = "";

    [JsonIgnore] public string NodeArchitecture => NodeShape == "VM.Standard.A1.Flex" ? "AARCH64" : "X86_64";
    [JsonIgnore] public int RequiredPodVnics => (MaxPodsPerNode + 30) / 31;
    [JsonIgnore] public StorageGeneration ActiveStorage => StorageGenerations.Single(g => g.Id == ActiveStorageGeneration);

    public void Validate(string role)
    {
        if (role is not ("recovery" or "durable" or "cluster" or "bootstrap"))
            throw new ArgumentException("Only recovery, durable, cluster and bootstrap staging roles are supported.");
        Require(Region, nameof(Region));
        Require(AvailabilityDomain, nameof(AvailabilityDomain));
        Require(OciProfile, nameof(OciProfile));
        if (OciAuth is not ("SecurityToken" or "InstancePrincipal"))
            throw new ArgumentException("ociAuth must be SecurityToken or InstancePrincipal.");
        Require(ObjectStorageNamespace, nameof(ObjectStorageNamespace));
        if (!Regex.IsMatch(RecoveryBucketName ?? "", "^[a-z0-9][a-z0-9-]{2,62}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Use a distinct lowercase recovery bucket name with 3 to 63 letters, numbers or hyphens.");
        RequireOcid(DurableCompartmentId, "compartment", nameof(DurableCompartmentId));
        RequireOcid(ClusterCompartmentId, "compartment", nameof(ClusterCompartmentId));
        if (DurableCompartmentId == ClusterCompartmentId)
            throw new ArgumentException("Use separate durable and cluster compartments.");
        ValidateCompute();
        ValidateStorageGenerations();
        if (!AllowBillableStorage)
            throw new ArgumentException($"Configured storage includes {StorageGenerations.Sum(g => g.TotalSizeInGbs)} GB of retained data and {(long)Workers * BootVolumeSizeInGbs} GB of worker boot disks. Set allowBillableStorage only after reviewing cost and remaining allowances.");
        if (role is "recovery" or "durable") return;

        RequireOcid(NodeImageId, "image", nameof(NodeImageId));
        if (!Regex.IsMatch(KubernetesVersion ?? "", @"\Av1\.36\.\d+\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Use an exact OKE v1.36 patch with Oracle Linux 8, the supported Calico 3.32 policy profile.");
        if (ApiAllowedCidrs is not { Length: > 0 } || ApiAllowedCidrs.Any(cidr => !IsRestrictedIpv4Cidr(cidr)))
            throw new ArgumentException("ApiAllowedCidrs must contain explicit IPv4 network CIDRs with prefixes /1 through /32. World access and host bits are rejected.");
        if (role == "cluster") return;

        RequireStack(DurableStack, "staging-durable");
        RequireStack(ClusterStack, "staging-cluster");
        if (!Uri.TryCreate(GitOpsRepository, UriKind.Absolute, out var repository) ||
            repository.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(repository.UserInfo) ||
            !string.IsNullOrEmpty(repository.Query) || !string.IsNullOrEmpty(repository.Fragment))
            throw new ArgumentException("GitOpsRepository must be an HTTPS repository URL without embedded credentials, query or fragment.");
        if (!IsGitOpsTag(GitOpsTag))
            throw new ArgumentException("GitOpsTag must select an immutable DRN release through refs/tags/.");
        if (!Regex.IsMatch(GitOpsCommit ?? "", @"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("GitOpsCommit must be the verified lowercase 40-character commit SHA.");
        ValidateGitOpsSnapshot(GitOpsDirectory);
        if (!Path.IsPathFullyQualified(KubeconfigPath) || !File.Exists(KubeconfigPath))
            throw new ArgumentException("KubeconfigPath must identify an existing dedicated kubeconfig file.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(KubeconfigPath) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new ArgumentException("KubeconfigPath must be private to its owner (chmod 600).");
    }

    public static bool IsGitOpsTag(string? value) => Regex.IsMatch(value ?? "",
        @"\Arefs/tags/\d{4}\.(?:[1-9]|1[0-2])\.[1-9]\d*(?:-preview(?!000)\d{3})?\z", RegexOptions.CultureInvariant);

    private void ValidateCompute()
    {
        if (NodeShape is not ("VM.Standard.A1.Flex" or "VM.Standard.E4.Flex" or "VM.Standard.E5.Flex" or
            "VM.Standard.E6.Flex" or "VM.Standard3.Flex"))
            throw new ArgumentException("Use an OKE-compatible standard Flex shape: A1, E4, E5, E6 or Standard3.");
        if (NodeOcpus < 1 || !double.IsFinite(NodeMemoryInGbs) || NodeMemoryInGbs <= 0)
            throw new ArgumentException("nodeOcpus must be a positive integer and nodeMemoryInGbs must be positive and finite. Live preflight checks shape and image limits.");
        if (BootVolumeSizeInGbs is < 50 or > 32768)
            throw new ArgumentException("bootVolumeSizeInGbs must be between 50 and 32768 GB and meet the selected image minimum.");
        if (MaxPodsPerNode is < 1 or > 110 || Workers is < 0 or > 7 ||
            Workers * RequiredPodVnics * 32 > 253)
            throw new ArgumentException("workers and maxPodsPerNode exceed the existing /24 pod subnet budget. Use 1 to 110 pods per node and at most 7 workers, with 32 addresses reserved per 31 pods on each node. Zero workers is reserved for pause.");
        if (FaultDomains is null || FaultDomains.Length > 3 ||
            FaultDomains.Any(fd => fd is not ("FAULT-DOMAIN-1" or "FAULT-DOMAIN-2" or "FAULT-DOMAIN-3")) ||
            FaultDomains.Distinct(StringComparer.Ordinal).Count() != FaultDomains.Length)
            throw new ArgumentException("faultDomains must be an empty array for automatic placement or distinct FAULT-DOMAIN-1, FAULT-DOMAIN-2 and FAULT-DOMAIN-3 values.");
    }

    private void ValidateStorageGenerations()
    {
        if (StorageGenerations is not { Length: > 0 and <= 3 } ||
            StorageGenerations.Any(g => g is null) ||
            StorageGenerations.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != StorageGenerations.Length ||
            !StorageGenerations.Any(g => g.Id == ActiveStorageGeneration) ||
            StorageGenerations[0].Id != "initial" || StorageGenerations[0].BackupIds is not null)
            throw new ArgumentException("Storage generations must retain initial and select exactly one declared active generation; at most three are supported.");
        foreach (var generation in StorageGenerations) generation.ValidateVolumes();
        foreach (var generation in StorageGenerations.Skip(1))
            if (!Regex.IsMatch(generation.Id ?? "", "^[a-f0-9]{32}$", RegexOptions.CultureInvariant) ||
                generation.BackupIds is null || generation.BackupIds.Count != StorageClaims.All.Count ||
                StorageClaims.All.Any(c => !generation.BackupIds.TryGetValue(c.Key, out var id) ||
                    string.IsNullOrWhiteSpace(id) || !id.StartsWith("ocid1.volumebackup.", StringComparison.Ordinal)) ||
                generation.BackupIds.Values.Distinct(StringComparer.Ordinal).Count() != StorageClaims.All.Count)
                throw new ArgumentException("A restored storage generation requires seven distinct, explicitly mapped volume backups.");
    }

    public static void ValidateGitOpsSnapshot(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new ArgumentException("GitOpsDirectory must be the absolute path of the verified release checkout.");
        foreach (var path in Bootstrap.RequiredFiles)
            if (!File.Exists(Path.Combine(directory, path)))
                throw new ArgumentException($"The selected GitOps release is missing {path}.");
        if (!File.ReadAllText(Path.Combine(directory, "deployment/values.yaml")).Contains("infrastructureProfile:", StringComparison.Ordinal) ||
            !File.ReadAllText(Path.Combine(directory, "deployment/values.yaml")).Contains("activationStage:", StringComparison.Ordinal) ||
            !File.ReadAllText(Path.Combine(directory, "deployment/templates/validate.yaml")).Contains("oci-retained", StringComparison.Ordinal))
            throw new ArgumentException("Select a GitOps release declaring and validating the OCI recovery profile and activation stages.");
    }

    public static bool IsRestrictedIpv4Cidr(string? cidr)
    {
        var parts = cidr?.Split('/');
        if (parts is not { Length: 2 } || !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != parts[0] ||
            !int.TryParse(parts[1], out var prefix) || prefix is < 1 or > 32)
            return false;
        var bytes = address.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var hostMask = prefix == 32 ? 0 : uint.MaxValue >> prefix;
        return (value & hostMask) == 0;
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value != value.Trim())
            throw new ArgumentException($"{name} must be supplied without surrounding whitespace or control characters.");
    }

    private static void RequireOcid(string? value, string type, string name)
    {
        Require(value, name);
        if (!value!.StartsWith($"ocid1.{type}.", StringComparison.Ordinal) || value.Contains(' '))
            throw new ArgumentException($"{name} must be an OCI {type} OCID.");
    }

    private static void RequireStack(string value, string suffix)
    {
        var parts = value.Split('/');
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) || parts[1] != "drn-pulumi" || parts[2] != suffix)
            throw new ArgumentException($"Use the fully qualified organization/drn-pulumi/{suffix} stack reference.");
    }
}

public sealed record StorageGeneration
{
    [JsonPropertyName("id")] public string Id { get; init; } = "initial";
    [JsonPropertyName("backupIds")] public Dictionary<string, string>? BackupIds { get; init; }
    [JsonPropertyName("volumes")] public Dictionary<string, VolumeSettings>? Volumes { get; init; }

    public VolumeSettings Volume(string claim) => Volumes is not null && Volumes.TryGetValue(claim, out var settings)
        ? settings : new VolumeSettings();

    [JsonIgnore] public long TotalSizeInGbs => StorageClaims.All.Sum(claim => (long)Volume(claim.Key).SizeInGbs);

    public void ValidateVolumes()
    {
        if (Volumes is not null && Volumes.Any(pair => !StorageClaims.All.Any(c => c.Key == pair.Key) || pair.Value is null))
            throw new ArgumentException("volumes must contain only known storage claim keys with non-null settings.");
        foreach (var claim in StorageClaims.All) Volume(claim.Key).Validate();
    }

    public bool HasSameVolumes(StorageGeneration other) =>
        StorageClaims.All.All(claim => Volume(claim.Key) == other.Volume(claim.Key));
}

public sealed record VolumeSettings
{
    [JsonPropertyName("sizeInGbs")] public int SizeInGbs { get; init; } = StorageClaims.VolumeSizeInGbs;
    [JsonPropertyName("performance")] public string Performance { get; init; } = "balanced";
    [JsonIgnore] public int VpusPerGb => Performance switch
    {
        "lower-cost" => 0, "balanced" => 10, "higher-performance" => 20,
        _ => throw new ArgumentException("Use lower-cost, balanced or higher-performance OCI block storage.")
    };

    public void Validate()
    {
        if (SizeInGbs is < 50 or > 32768)
            throw new ArgumentException("Data volume sizeInGbs must be between 50 and 32768 GB.");
        _ = VpusPerGb;
    }
}
