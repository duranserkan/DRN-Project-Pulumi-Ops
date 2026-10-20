using Xunit;
using Xunit.Sdk;

namespace Drn.Infrastructure.Operator.UnitTests.Support;

internal static class CommandArguments
{
    internal const string OracleCloudCli = "oci";
    internal const string Pulumi = "pulumi";
    internal const string Kubectl = "kubectl";
    internal const string OracleCloudRegion = "--region";
    internal const string OracleCloudFile = "--file";
    internal const string OracleCloudOutput = "--output";
    internal const string OracleCloudJsonOutput = "json";
    internal const string OracleCloudNoRetry = "--no-retry";
    internal const string OracleCloudVolumeId = "--volume-id";
    internal const string OracleCloudVolumeGroupId = "--volume-group-id";
    internal const string OracleCloudVolumeGroupBackupId = "--volume-group-backup-id";
    internal const string OracleCloudVolumeBackupId = "--volume-backup-id";
    internal const string PulumiWorkingDirectory = "--cwd";
    internal const string KubectlKubeconfig = "--kubeconfig";
    internal const string KubectlIgnoreNotFound = "--ignore-not-found";
    internal const string Stack = "--stack";

    internal static string Required(IReadOnlyList<string> arguments, string option)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] != option) continue;
            if (index >= 0) throw new XunitException($"Command '{string.Join(' ', arguments)}' requires exactly one {option}.");
            index = i;
        }
        if (index < 0) throw new XunitException($"Command '{string.Join(' ', arguments)}' requires exactly one {option}.");
        if (index == arguments.Count - 1) throw new XunitException($"{option} requires a value.");
        var value = arguments[index + 1];
        if (string.IsNullOrWhiteSpace(value)) throw new XunitException($"{option} requires a value.");
        if (value.StartsWith("-", StringComparison.Ordinal)) throw new XunitException($"{option} must not consume the next option.");
        return value;
    }

    internal static void Prefix(IReadOnlyList<string> arguments, params string[] expected)
    {
        Assert.True(arguments.Count >= expected.Length, "The fake requires the complete command prefix.");
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], arguments[i]);
    }

    internal static void OracleCloudRead(IReadOnlyList<string> arguments)
    {
        Assert.True(arguments.Count > 2, "An OCI read requires a command family, resource and action.");
        var (family, action) = arguments[1] switch
        {
            "node-pool-options" or "node-pool" => ("ce", "get"),
            "image" or "image-shape-compatibility-entry" or "instance" => ("compute", "get"),
            "shape" or "volume-attachment" => ("compute", "list"),
            "fault-domain" => ("iam", "list"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected OCI resource: {arguments[1]}")
        };
        Prefix(arguments, family, arguments[1], action);
    }
}
