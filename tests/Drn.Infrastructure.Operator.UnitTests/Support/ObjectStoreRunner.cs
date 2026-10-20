using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace Drn.Infrastructure.Operator.UnitTests.Support;

internal sealed record RemoteObject(string ETag, byte[] Content);

// Model the conditional OCI object operations, including the CLI's exit-zero no-overwrite result.
internal sealed class ObjectStoreRunner : ICommandRunner
{
    internal Dictionary<string, RemoteObject> Objects { get; } = new(StringComparer.Ordinal);
    internal bool Fail { get; set; }
    private int revision;

    public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, bool displayOutput, CancellationToken cancellationToken,
        string? standardInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(CommandArguments.OracleCloudCli, executable);
        Assert.False(displayOutput, "Recovery payloads must not be displayed.");
        Assert.Null(standardInput);
        CommandArguments.Prefix(arguments, "os", "object");
        Assert.Equal(CommandArguments.OracleCloudJsonOutput, CommandArguments.Required(arguments, CommandArguments.OracleCloudOutput));
        Assert.Contains(CommandArguments.OracleCloudNoRetry, arguments);
        _ = CommandArguments.Required(arguments, "--namespace-name");
        _ = CommandArguments.Required(arguments, "--bucket-name");
        if (Fail) throw new InvalidOperationException("Simulated OCI failure.");
        Assert.True(arguments.Count > 2, "An object command requires an action.");
        var action = arguments[2];
        var name = CommandArguments.Required(arguments, action == "list" ? "--prefix" : "--name");
        Objects.TryGetValue(name, out var current);
        if (arguments.Contains("--if-match") && current?.ETag != CommandArguments.Required(arguments, "--if-match"))
            throw new InvalidOperationException("Precondition failed.");

        switch (action)
        {
            case "list":
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    data = Objects.Where(pair => pair.Key.StartsWith(name, StringComparison.Ordinal))
                        .Select(pair => new { name = pair.Key, etag = pair.Value.ETag, size = pair.Value.Content.Length })
                }));
            case "put":
                var source = CommandArguments.Required(arguments, CommandArguments.OracleCloudFile);
                if (arguments.Contains("--no-overwrite") && current is not null) return Task.FromResult("");
                var etag = $"revision-{++revision}";
                Objects[name] = new RemoteObject(etag, System.IO.File.ReadAllBytes(source));
                return Task.FromResult(JsonSerializer.Serialize(new { etag }));
            case "get":
                if (current is null) throw new XunitException($"Object {name} must exist before download.");
                System.IO.File.WriteAllBytes(CommandArguments.Required(arguments, CommandArguments.OracleCloudFile), current.Content);
                return Task.FromResult("");
            case "delete":
                if (!Objects.Remove(name)) throw new XunitException($"Object {name} must exist before deletion.");
                return Task.FromResult("");
            default:
                throw new XunitException($"Unexpected OCI object command: {action}");
        }
    }
}
