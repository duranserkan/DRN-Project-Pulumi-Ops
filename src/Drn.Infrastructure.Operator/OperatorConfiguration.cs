using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Drn.Pulumi;

namespace Drn.Infrastructure.Operator;

public sealed record OperatorConfiguration
{
    public string Organization { get; init; } = "";
    public string RecoveryKeyPath { get; init; } = "";
    public bool RecoveryKeyBackedUp { get; init; }
    public int BackupRetentionCount { get; init; } = 7;
    public int MaximumBackupAgeHours { get; init; } = 26;
    public InfrastructureSettings Infrastructure { get; init; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public string Stack(string role) => role is "recovery" or "durable" or "cluster" or "bootstrap"
        ? $"{Organization}/drn-pulumi/staging-{role}"
        : throw new ArgumentException("Unknown infrastructure stack role.");

    public void Validate()
    {
        if (!Regex.IsMatch(Organization, "^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Set organization to your Pulumi organization or individual account name.");
        if (Infrastructure is null)
            throw new ArgumentException("Configuration requires an infrastructure object.");
        if (!Path.IsPathFullyQualified(RecoveryKeyPath) || RecoveryKeyPath.Any(char.IsControl))
            throw new ArgumentException("recoveryKeyPath must be an absolute path to a private 32-byte recovery key.");
        if (BackupRetentionCount is < 2 or > 14 || MaximumBackupAgeHours is < 24 or > 168)
            throw new ArgumentException("Keep 2 to 14 complete backups and a maximum backup age of 24 to 168 hours.");
        (Infrastructure with { DurableStack = Stack("durable"), ClusterStack = Stack("cluster") }).Validate("cluster");
        if (Infrastructure.Workers < 1)
            throw new ArgumentException("Operator configuration describes the desired positive active worker count. Use pause for the zero-worker transition.");
        if (Infrastructure.StorageGenerations.Length != 1 || Infrastructure.ActiveStorageGeneration != "initial")
            throw new ArgumentException("Configure only the initial storage generation. Restore generations are owned by encrypted recovery state.");
        if (!Uri.TryCreate(Infrastructure.GitOpsRepository, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "github.com" || uri.UserInfo != "" ||
            uri.Query != "" || uri.Fragment != "" || !uri.IsDefaultPort ||
            !Regex.IsMatch(uri.AbsolutePath, "^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\\.git)?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("gitOpsRepository must be a public HTTPS GitHub repository URL without credentials.");
        if (!Regex.IsMatch(Infrastructure.GitOpsCommit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("gitOpsCommit must be the full lowercase 40-character release commit.");
        if (!InfrastructureSettings.IsGitOpsTag(Infrastructure.GitOpsTag))
            throw new ArgumentException("gitOpsTag must be a calendar release ref, for example refs/tags/2026.10.1-preview001.");
    }

    public static OperatorConfiguration Load(string path)
    {
        try
        {
            var configuration = JsonSerializer.Deserialize<OperatorConfiguration>(File.ReadAllText(path), JsonOptions)
                ?? throw new ArgumentException("Configuration must be a JSON object.");
            configuration.Validate();
            return configuration;
        }
        catch (JsonException)
        {
            throw new ArgumentException("Invalid configuration JSON. Compare its fields with configuration/staging.example.json.");
        }
    }
}

public sealed record OperatorCommand(string Name, string ConfigurationPath, bool Yes,
    string? Backup = null, Guid? Owner = null, bool AllowAdditionalStorage = false)
{
    private static readonly HashSet<string> Commands =
        ["validate", "keygen", "setup", "initialize", "preview", "durable", "cluster", "up", "bootstrap", "status", "check",
            "kubeconfig", "backup", "pause", "resume", "down", "restore", "release", "unlock"];

    public static OperatorCommand Parse(string[] args)
    {
        if (args.Length < 2 || !Commands.Contains(args[0]) || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
            throw new ArgumentException("Use <command> <configuration.json>. Run with --help for supported commands.");
        var yes = false;
        var confirmed = false;
        var stopped = false;
        var additionalStorage = false;
        string? backup = null;
        Guid? owner = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--yes" && !yes)
                yes = true;
            else if (args[i] == "--confirm" && !confirmed && i + 1 < args.Length && args[++i] == "staging")
                confirmed = true;
            else if (args[i] == "--backup" && backup is null && i + 1 < args.Length &&
                Regex.IsMatch(args[++i], "^[a-f0-9]{32}$")) backup = args[i];
            else if (args[i] == "--owner" && owner is null && i + 1 < args.Length &&
                Guid.TryParseExact(args[++i], "N", out var parsed) && parsed != Guid.Empty) owner = parsed;
            else if (args[i] == "--previous-process-stopped" && !stopped) stopped = true;
            else if (args[i] == "--allow-additional-storage" && !additionalStorage) additionalStorage = true;
            else
                throw new ArgumentException("Unknown, invalid or repeated option. Run with --help.");
        }
        if ((args[0] is "down" or "restore" or "release" or "unlock") != confirmed)
            throw new ArgumentException("down, restore, release and unlock require --confirm staging; other commands do not accept it.");
        if (args[0] == "restore" ? backup is null || !additionalStorage : backup is not null || additionalStorage)
            throw new ArgumentException("restore requires --backup <checkpoint-id> and --allow-additional-storage (another full copy of the selected generation).");
        if (args[0] == "unlock" ? owner is null || !stopped : owner is not null || stopped)
            throw new ArgumentException("unlock requires --owner <operation-id> and --previous-process-stopped. Stop the old process first.");
        return new OperatorCommand(args[0], args[1], yes, backup, owner, additionalStorage);
    }
}
