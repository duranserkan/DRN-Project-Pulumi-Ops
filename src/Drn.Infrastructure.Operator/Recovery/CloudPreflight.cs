using System.Text.Json;
using Drn.Pulumi;

namespace Drn.Infrastructure.Operator.Recovery;

/// <summary>Checks cloud compatibility and physical detachment before irreversible lifecycle phases.</summary>
public sealed class CloudPreflight(ICommandRunner runner, IReadOnlyDictionary<string, string> environment,
    InfrastructureSettings settings)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var options = await ReadAsync(["ce", "node-pool-options", "get", "--node-pool-option-id", "all",
            "--compartment-id", settings.ClusterCompartmentId, "--node-pool-k8s-version", settings.KubernetesVersion,
            "--node-pool-os-arch", settings.NodeArchitecture, "--node-pool-os-type", "OL8",
            "--should-list-all-patch-versions", "true"], cancellationToken);
        var data = options.RootElement.GetProperty("data");
        var sources = data.GetProperty("sources").EnumerateArray()
            .Where(source => Text(source, "image-id") == settings.NodeImageId).ToArray();
        if (!Strings(data, "kubernetes-versions").Contains(settings.KubernetesVersion, StringComparer.Ordinal) ||
            !Strings(data, "shapes").Contains(settings.NodeShape, StringComparer.Ordinal) ||
            sources.Length != 1 || Text(sources[0], "source-type") != "IMAGE" ||
            !IsSelectedOkeImageName(Text(sources[0], "source-name")))
            throw new InvalidOperationException("The selected shape and Oracle Linux 8 OKE image are unavailable for the exact Kubernetes version and architecture.");

        using var image = await ReadAsync(["compute", "image", "get", "--image-id", settings.NodeImageId], cancellationToken);
        var metadata = image.RootElement.GetProperty("data");
        var osVersion = Text(metadata, "operating-system-version");
        if (Text(metadata, "id") != settings.NodeImageId || Text(metadata, "lifecycle-state") != "AVAILABLE" ||
            Text(metadata, "operating-system") != "Oracle Linux" ||
            (osVersion != "8" && !osVersion.StartsWith("8.", StringComparison.Ordinal)) ||
            !IsSelectedOkeImageName(Text(metadata, "display-name")))
            throw new InvalidOperationException("Image metadata does not confirm an AVAILABLE Oracle Linux 8 image matching the architecture and exact OKE patch.");
        if (settings.BootVolumeSizeInGbs * 1024L < Number(metadata, "size-in-mbs"))
            throw new InvalidOperationException("The configured boot disk is smaller than the selected image.");

        using var shapes = await ReadAsync(["compute", "shape", "list", "--compartment-id", settings.ClusterCompartmentId,
            "--availability-domain", settings.AvailabilityDomain, "--image-id", settings.NodeImageId, "--all"], cancellationToken);
        var matches = shapes.RootElement.GetProperty("data").EnumerateArray()
            .Where(shape => Text(shape, "shape") == settings.NodeShape).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("The selected shape is not uniquely available for this image and availability domain.");
        ValidateShape(matches[0]);

        using var compatibility = await ReadAsync(["compute", "image-shape-compatibility-entry", "get",
            "--image-id", settings.NodeImageId, "--shape-name", settings.NodeShape], cancellationToken);
        var entry = compatibility.RootElement.GetProperty("data");
        if (Text(entry, "image-id") != settings.NodeImageId || Text(entry, "shape") != settings.NodeShape)
            throw new InvalidOperationException("Image compatibility metadata identifies another image or shape.");
        ValidateImageRange(entry, "ocpu-constraints", "min", "max", settings.NodeOcpus);
        ValidateImageRange(entry, "memory-constraints", "min-in-gbs", "max-in-gbs", settings.NodeMemoryInGbs);

        if (settings.FaultDomains.Length > 0)
        {
            using var domains = await ReadAsync(["iam", "fault-domain", "list", "--compartment-id", settings.ClusterCompartmentId,
                "--availability-domain", settings.AvailabilityDomain, "--all"], cancellationToken);
            var available = domains.RootElement.GetProperty("data").EnumerateArray().Select(fd => Text(fd, "name"))
                .ToHashSet(StringComparer.Ordinal);
            if (settings.FaultDomains.Any(fd => !available.Contains(fd)))
                throw new InvalidOperationException("A selected fault domain is unavailable in the configured availability domain.");
        }
    }

    private void ValidateShape(JsonElement shape)
    {
        var cpus = shape.GetProperty("ocpu-options");
        var memory = shape.GetProperty("memory-options");
        if (settings.NodeOcpus < Number(cpus, "min") || settings.NodeOcpus > Number(cpus, "max") ||
            settings.NodeMemoryInGbs < Number(memory, "min-in-g-bs") || settings.NodeMemoryInGbs > Number(memory, "max-in-g-bs") ||
            settings.NodeMemoryInGbs < Number(memory, "min-per-ocpu-in-gbs") * settings.NodeOcpus ||
            settings.NodeMemoryInGbs > Number(memory, "max-per-ocpu-in-gbs") * settings.NodeOcpus)
            throw new InvalidOperationException("Configured OCPUs and RAM exceed the selected shape's limits.");
        var vnics = shape.TryGetProperty("max-vnic-attachment-options", out var options) && options.ValueKind != JsonValueKind.Null
            ? Math.Min(Number(options, "max"), Math.Max(Number(options, "min"),
                Math.Floor(Number(options, "default-per-ocpu") * settings.NodeOcpus)))
            : Number(shape, "max-vnic-attachments");
        if (vnics < 1 + settings.RequiredPodVnics)
            throw new InvalidOperationException("The selected OCPU count cannot provide enough VNICs for maxPodsPerNode. Reduce pod density or increase OCPUs.");
    }

    private static void ValidateImageRange(JsonElement entry, string key, string min, string max, double requested)
    {
        if (!entry.TryGetProperty(key, out var range) || range.ValueKind == JsonValueKind.Null) return;
        if (range.TryGetProperty(min, out var lower) && lower.ValueKind != JsonValueKind.Null && requested < Number(range, min) ||
            range.TryGetProperty(max, out var upper) && upper.ValueKind != JsonValueKind.Null && requested > Number(range, max))
            throw new InvalidOperationException("Configured resources exceed the selected image's shape constraints.");
    }

    private static double Number(JsonElement item, string key)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number <= 0)
            throw new InvalidOperationException($"OCI compatibility metadata is missing a positive {key} value.");
        return number;
    }

    public async Task<string[]> ReadWorkerInstancesAsync(string nodePoolId, CancellationToken cancellationToken)
    {
        RequireOcid(nodePoolId, "nodepool");
        using var result = await ReadAsync(["ce", "node-pool", "get", "--node-pool-id", nodePoolId], cancellationToken);
        var pool = result.RootElement.GetProperty("data");
        if (Text(pool, "id") != nodePoolId || Text(pool, "compartment-id") != settings.ClusterCompartmentId)
            throw new InvalidOperationException("Node pool identity or compartment does not match the managed cluster.");
        var instances = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in pool.GetProperty("nodes").EnumerateArray())
        {
            var state = Text(node, "lifecycle-state");
            if (state == "DELETED") continue;
            if (state is not ("CREATING" or "ACTIVE" or "UPDATING" or "DELETING" or "FAILING" or "INACTIVE") ||
                Text(node, "node-pool-id") != nodePoolId)
                throw new InvalidOperationException("Node pool contains an unknown worker state or an unexpected worker owner.");
            var id = Text(node, "id");
            RequireOcid(id, "instance");
            if (!instances.Add(id)) throw new InvalidOperationException("Node pool repeats a worker instance identity.");
        }
        return instances.Order(StringComparer.Ordinal).ToArray();
    }

    public async Task RequireDetachedAsync(IReadOnlyCollection<string> volumeIds,
        IReadOnlyCollection<string> oldInstanceIds, CancellationToken cancellationToken)
    {
        if (volumeIds.Count != StorageClaims.All.Count || volumeIds.Distinct(StringComparer.Ordinal).Count() != volumeIds.Count ||
            oldInstanceIds.Distinct(StringComparer.Ordinal).Count() != oldInstanceIds.Count)
            throw new InvalidOperationException("Detachment requires all seven distinct volume identities and distinct recorded worker identities.");
        foreach (var id in volumeIds) RequireOcid(id, "volume");
        foreach (var id in oldInstanceIds) RequireOcid(id, "instance");

        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detached = true;
            foreach (var id in oldInstanceIds)
            {
                using var result = await ReadAsync(["compute", "instance", "get", "--instance-id", id], cancellationToken);
                var instance = result.RootElement.GetProperty("data");
                if (Text(instance, "id") != id || Text(instance, "compartment-id") != settings.ClusterCompartmentId)
                    throw new InvalidOperationException("Worker instance identity or compartment does not match the recorded node pool.");
                var state = Text(instance, "lifecycle-state");
                if (state is not ("TERMINATED" or "TERMINATING" or "PROVISIONING" or "RUNNING" or "STARTING" or "STOPPING" or "STOPPED" or "CREATING_IMAGE" or "MOVING"))
                    throw new InvalidOperationException("Worker instance state is unknown; physical shutdown cannot be established.");
                detached &= state == "TERMINATED";
            }

            // Attachments belong to the instance compartment, although the volumes are durable resources.
            foreach (var id in volumeIds)
            {
                using var result = await ReadAsync(["compute", "volume-attachment", "list", "--compartment-id",
                    settings.ClusterCompartmentId, "--volume-id", id, "--all"], cancellationToken);
                foreach (var attachment in result.RootElement.GetProperty("data").EnumerateArray())
                {
                    if (Text(attachment, "volume-id") != id || Text(attachment, "compartment-id") != settings.ClusterCompartmentId)
                        throw new InvalidOperationException("Attachment identity or compartment does not match the selected generation.");
                    var state = Text(attachment, "lifecycle-state");
                    if (state is not ("ATTACHING" or "ATTACHED" or "DETACHING" or "DETACHED"))
                        throw new InvalidOperationException("Volume attachment state is unknown; detachment cannot be established.");
                    if (state == "DETACHED") continue;
                    if (!oldInstanceIds.Contains(Text(attachment, "instance-id"), StringComparer.Ordinal))
                        throw new InvalidOperationException("A selected data volume is attached to an unrecorded instance. No backup or teardown is permitted.");
                    detached = false;
                }
            }
            if (detached) return;
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException("Workers and data volumes did not become fully detached within 30 minutes. Recovery remains locked.");
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }

    private bool IsSelectedOkeImageName(string value)
    {
        var marker = $"-OKE-{settings.KubernetesVersion.TrimStart('v')}";
        return value.StartsWith("Oracle-Linux-8.", StringComparison.Ordinal) &&
            value.Contains("-aarch64-", StringComparison.Ordinal) == (settings.NodeArchitecture == "AARCH64") &&
            (value.Contains(marker + "-", StringComparison.Ordinal) || value.EndsWith(marker, StringComparison.Ordinal));
    }

    private async Task<JsonDocument> ReadAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        JsonDocument.Parse(await runner.RunAsync("oci", [.. arguments, "--region", settings.Region, "--output", "json", "--no-retry"],
            environment, false, cancellationToken));
    private static string Text(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static string[] Strings(JsonElement item, string key) => item.GetProperty(key).EnumerateArray()
        .Select(value => value.GetString() ?? throw new InvalidOperationException("OCI compatibility metadata is incomplete.")).ToArray();
    private static void RequireOcid(string value, string resource)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith($"ocid1.{resource}.", StringComparison.Ordinal) || value.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("OCI identity is missing or has the wrong resource type.");
    }
}
