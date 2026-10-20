using System.Text.Json.Nodes;
using Drn.Pulumi;

namespace Drn.Infrastructure.Operator.UnitTests.Support;

internal static class OracleCloudMetadata
{
    // OCI CLI field names differ from both Pulumi inputs and the OCI REST model.
    internal const string CpuOptions = "ocpu-options";
    internal const string MemoryOptions = "memory-options";
    internal const string CpuConstraints = "ocpu-constraints";
    internal const string MemoryConstraints = "memory-constraints";
    internal const string VnicOptions = "max-vnic-attachment-options";
    internal const string FixedVnics = "max-vnic-attachments";
    internal const string Minimum = "min";
    internal const string Maximum = "max";
    internal const string ShapeMemoryMinimum = "min-in-g-bs";
    internal const string ShapeMemoryMaximum = "max-in-g-bs";
    internal const string ImageMemoryMinimum = "min-in-gbs";
    internal const string ImageMemoryMaximum = "max-in-gbs";
    internal const string MemoryPerCpuMinimum = "min-per-ocpu-in-gbs";
    internal const string MemoryPerCpuMaximum = "max-per-ocpu-in-gbs";
    internal const string VnicsPerCpu = "default-per-ocpu";

    internal static JsonObject Shape(InfrastructureSettings settings) => new()
    {
        ["shape"] = settings.NodeShape,
        [CpuOptions] = new JsonObject { [Minimum] = 1, [Maximum] = 64 },
        [MemoryOptions] = new JsonObject { [ShapeMemoryMinimum] = 1, [ShapeMemoryMaximum] = 1024,
            [MemoryPerCpuMinimum] = 1, [MemoryPerCpuMaximum] = 64 },
        [FixedVnics] = 24,
        [VnicOptions] = new JsonObject { [Minimum] = 2, [Maximum] = 24, [VnicsPerCpu] = 1 }
    };

    internal static JsonObject ImageCompatibility(InfrastructureSettings settings) => new()
    {
        ["image-id"] = settings.NodeImageId, ["shape"] = settings.NodeShape,
        [CpuConstraints] = new JsonObject { [Minimum] = 1, [Maximum] = 64 },
        [MemoryConstraints] = new JsonObject { [ImageMemoryMinimum] = 1, [ImageMemoryMaximum] = 1024 }
    };
}
