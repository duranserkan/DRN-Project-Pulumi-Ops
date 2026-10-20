using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Drn.Infrastructure.Operator.Recovery;

public sealed record NativeBackupPreparation(Guid OperationId, string CaCertificate);

public sealed record NativeBackupManifest(Guid OperationId, string SnapshotName, string EngineVersion,
    string[] IndexNames, string PostgresqlDump, string MongodbDump, string PostgresqlVersion, string CaCertificate);

public interface INativeDatabaseBackup
{
    Task<NativeBackupPreparation> PrepareAsync(Guid operationId, CancellationToken cancellationToken);
    Task<NativeBackupManifest> TakeAsync(NativeBackupPreparation preparation,
        Func<CancellationToken, Task> stopDataNode, CancellationToken cancellationToken);
    Task RestoreAsync(NativeBackupManifest manifest, string clonedGenerationId,
        Func<CancellationToken, Task> stopDataNode, Func<CancellationToken, Task> startDataNode,
        CancellationToken cancellationToken);
    Task PruneAsync(NativeBackupManifest current, IReadOnlyCollection<NativeBackupManifest> obsolete,
        CancellationToken cancellationToken);
}

/// <summary>Native database exports staged on retained disks before the offline OCI group checkpoint.</summary>
public sealed class NativeDatabaseBackup(ICommandRunner runner, IReadOnlyDictionary<string, string> environment,
    Func<CancellationToken, Task> assertOwned) : INativeDatabaseBackup
{
    public const string RepositoryName = "drn-recovery";
    public const string RepositoryPath = "/var/lib/graylog-datanode/drn-backups";
    private const string DataNodeHost = "graylog-datanode-0.graylog-datanode-svc.graylog.svc.cluster.local";
    private const string PostgresRoot = "/var/lib/postgresql/data/drn-backups";
    private const string MongoRoot = "/data/drn-backups";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Graylog must still be running here. The caller checkpoints its operation before this method.
    public async Task<NativeBackupPreparation> PrepareAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Native backup requires a recorded operation ID.");
        var principal = $"drn-recovery-{operationId:N}";
        await assertOwned(cancellationToken);
        await using var tunnel = await KubernetesTunnel.OpenAsync(environment, "graylog", "graylog-0", 9000, cancellationToken);
        using var client = await GraylogClientAsync(tunnel, cancellationToken);
        using var response = await SendAsync(client, HttpMethod.Post, "api/ca/clientcert", new
        {
            principal, roles = new[] { "all_access" }, password = "", certificate_lifetime = "P1D"
        }, cancellationToken);
        var ca = Required(response.RootElement, "ca_certificate");
        ValidateCa(ca);
        // The read-only CA APIs expose metadata without PEM. Retrieve its public chain through
        // this supported endpoint, revoke the temporary mapping immediately and discard its key.
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/ca/clientcert/all_access/{principal}");
        await assertOwned(cancellationToken);
        using var revoked = await client.SendAsync(request, cancellationToken);
        if (!revoked.IsSuccessStatusCode)
            throw new InvalidOperationException("Graylog could not revoke the temporary CA lookup certificate mapping.");
        return new NativeBackupPreparation(operationId, ca);
    }

    // Caller stops application writers and Graylog first. Stop DataNode after its snapshot so
    // its heartbeat and registration writes cannot race the subsequent MongoDB archive.
    public async Task<NativeBackupManifest> TakeAsync(NativeBackupPreparation preparation,
        Func<CancellationToken, Task> stopDataNode, CancellationToken cancellationToken)
    {
        var operation = preparation.OperationId.ToString("N");
        var pgDump = $"{PostgresRoot}/{operation}.dump";
        var mongoDump = $"{MongoRoot}/{operation}.archive.gz";
        var search = await TakeSearchAsync(preparation, cancellationToken);
        await assertOwned(cancellationToken);
        await stopDataNode(cancellationToken);
        var pgVersion = (await ExecAsync("drn-project", "postgresql-1", "postgres",
            ["psql", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-d", "postgres", "-c", "SHOW server_version"], cancellationToken)).Trim();
        await ExecAsync("drn-project", "postgresql-1", "postgres", ["sh", "-ceu",
            $"umask 077; mkdir -p {PostgresRoot}; test ! -e {pgDump}; pg_dump --format=custom --create --file={pgDump} drnDb; pg_restore --list {pgDump} >/dev/null"], cancellationToken);
        await MongoExportAsync(mongoDump, restore: false, cancellationToken);
        return new NativeBackupManifest(preparation.OperationId, search.Name, search.Version,
            search.Indices, pgDump, mongoDump, pgVersion, preparation.CaCertificate);
    }

    private async Task<(string Name, string Version, string[] Indices)> TakeSearchAsync(
        NativeBackupPreparation preparation, CancellationToken cancellationToken)
    {
        await assertOwned(cancellationToken);
        await using var tunnel = await KubernetesTunnel.OpenAsync(environment, "graylog", "graylog-datanode-0", 9200, cancellationToken);
        using var ca = X509Certificate2.CreateFromPem(preparation.CaCertificate);
        using var client = tunnel.CreateTlsClient(DataNodeHost, ca);
        using var root = await SendAsync(client, HttpMethod.Get, "", null, cancellationToken);
        var version = Required(root.RootElement.GetProperty("version"), "number");
        var indices = await IndexNamesAsync(client, cancellationToken);
        await RequireSnapshotSpaceAsync(client, cancellationToken);
        await RegisterRepositoryAsync(client, cancellationToken);
        var snapshotName = $"drn-{preparation.OperationId:N}";
        using var snapshot = await SendAsync(client, HttpMethod.Put,
            $"_snapshot/{RepositoryName}/{snapshotName}?wait_for_completion=true", new
            {
                indices = "*,-.*", ignore_unavailable = false, include_global_state = false, partial = false
            }, cancellationToken);
        ValidateSnapshot(snapshot.RootElement.GetProperty("snapshot"), snapshotName, version, indices);
        return (snapshotName, version, indices);
    }

    // Caller selects a NEW restored generation and blocks Graylog and application writers.
    // Database repair can run before Graylog readiness, including after an interrupted restore.
    public async Task RestoreDatabasesAsync(NativeBackupManifest manifest, string clonedGenerationId,
        Func<CancellationToken, Task> stopDataNode, CancellationToken cancellationToken)
    {
        ValidateRestoreTarget(manifest, clonedGenerationId);
        await assertOwned(cancellationToken);
        await stopDataNode(cancellationToken);
        var version = (await ExecAsync("drn-project", "postgresql-1", "postgres",
            ["psql", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-d", "postgres", "-c", "SHOW server_version"], cancellationToken)).Trim();
        if (version != manifest.PostgresqlVersion)
            throw new InvalidOperationException("PostgreSQL version differs from the native backup.");
        await ExecAsync("drn-project", "postgresql-1", "postgres", ["psql", "-X", "-v", "ON_ERROR_STOP=1", "-d", "postgres",
            "-c", "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'drnDb' AND pid <> pg_backend_pid()"], cancellationToken);
        await ExecAsync("drn-project", "postgresql-1", "postgres", ["pg_restore", "--clean", "--if-exists", "--create",
            "--exit-on-error", "--dbname=postgres", manifest.PostgresqlDump], cancellationToken);
        await MongoExportAsync(manifest.MongodbDump, restore: true, cancellationToken);
    }

    public async Task RestoreAsync(NativeBackupManifest manifest, string clonedGenerationId, Func<CancellationToken, Task> stopDataNode,
        Func<CancellationToken, Task> startDataNode, CancellationToken cancellationToken)
    {
        await RestoreDatabasesAsync(manifest, clonedGenerationId, stopDataNode, cancellationToken);
        await assertOwned(cancellationToken);
        await startDataNode(cancellationToken);
        await assertOwned(cancellationToken);
        await using var tunnel = await KubernetesTunnel.OpenAsync(environment, "graylog", "graylog-datanode-0", 9200, cancellationToken);
        using var ca = X509Certificate2.CreateFromPem(manifest.CaCertificate);
        using var client = tunnel.CreateTlsClient(DataNodeHost, ca);
        using var root = await SendAsync(client, HttpMethod.Get, "", null, cancellationToken);
        if (Required(root.RootElement.GetProperty("version"), "number") != manifest.EngineVersion)
            throw new InvalidOperationException("OpenSearch version differs from the native backup.");
        await RegisterRepositoryAsync(client, cancellationToken);
        using (var stored = await SendAsync(client, HttpMethod.Get,
                   $"_snapshot/{RepositoryName}/{manifest.SnapshotName}", null, cancellationToken))
        {
            var snapshots = stored.RootElement.GetProperty("snapshots");
            if (snapshots.GetArrayLength() != 1) throw new InvalidOperationException("Expected exactly one native snapshot.");
            ValidateSnapshot(snapshots[0], manifest.SnapshotName, manifest.EngineVersion, manifest.IndexNames);
        }
        var existing = await IndexNamesAsync(client, cancellationToken);
        if (!existing.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.IndexNames))
            throw new InvalidOperationException("Cloned OpenSearch index inventory differs from the offline checkpoint.");
        var selected = string.Join(',', manifest.IndexNames.Select(Uri.EscapeDataString));
        using (var closed = await SendAsync(client, HttpMethod.Post, $"{selected}/_close", null, cancellationToken))
        {
            if (!closed.RootElement.GetProperty("acknowledged").GetBoolean())
                throw new InvalidOperationException("OpenSearch did not acknowledge closing cloned indices.");
        }
        using (var restored = await SendAsync(client, HttpMethod.Post,
                   $"_snapshot/{RepositoryName}/{manifest.SnapshotName}/_restore?wait_for_completion=true", new
                   {
                       indices = string.Join(',', manifest.IndexNames), ignore_unavailable = false,
                       include_global_state = false, include_aliases = true, partial = false
                   }, cancellationToken))
        {
            var shards = restored.RootElement.GetProperty("snapshot").GetProperty("shards");
            if (shards.GetProperty("total").GetInt32() == 0 || shards.GetProperty("failed").GetInt32() != 0 ||
                shards.GetProperty("successful").GetInt32() != shards.GetProperty("total").GetInt32())
                throw new InvalidOperationException("Native snapshot restore did not recover every shard.");
        }
        using var health = await SendAsync(client, HttpMethod.Get,
            "_cluster/health?wait_for_status=yellow&wait_for_no_initializing_shards=true&wait_for_no_relocating_shards=true&timeout=10m", null, cancellationToken);
        if (health.RootElement.GetProperty("timed_out").GetBoolean() ||
            Required(health.RootElement, "status") is not ("yellow" or "green"))
            throw new InvalidOperationException("Restored search primaries are not ready.");
    }

    public static void ValidateRestoreTarget(NativeBackupManifest manifest, string clonedGenerationId)
    {
        if (!Regex.IsMatch(clonedGenerationId, "^[a-f0-9]{32}$"))
            throw new InvalidOperationException("Native restore requires a new restore generation.");
        ValidateManifest(manifest);
    }

    public static void ValidateManifest(NativeBackupManifest manifest)
    {
        if (manifest.OperationId == Guid.Empty ||
            manifest.SnapshotName != $"drn-{manifest.OperationId:N}" ||
            manifest.PostgresqlDump != $"{PostgresRoot}/{manifest.OperationId:N}.dump" ||
            manifest.MongodbDump != $"{MongoRoot}/{manifest.OperationId:N}.archive.gz" ||
            string.IsNullOrWhiteSpace(manifest.EngineVersion) || string.IsNullOrWhiteSpace(manifest.PostgresqlVersion))
            throw new InvalidOperationException("Native restore requires a validated checkpoint and a new restore generation.");
        ValidateIndices(manifest.IndexNames);
        ValidateCa(manifest.CaCertificate);
    }

    // Invoke only after the caller has verified the retained current OCI group backup. Repository
    // deletion goes through OpenSearch so shared incremental snapshot files are never removed by hand.
    public async Task PruneAsync(NativeBackupManifest current,
        IReadOnlyCollection<NativeBackupManifest> obsolete, CancellationToken cancellationToken)
    {
        ValidateManifest(current);
        foreach (var item in obsolete)
        {
            ValidateManifest(item);
            if (item.OperationId == current.OperationId)
                throw new InvalidOperationException("Cannot prune the current native checkpoint.");
        }
        if (obsolete.Count == 0) return;
        await assertOwned(cancellationToken);
        await using var tunnel = await KubernetesTunnel.OpenAsync(environment, "graylog", "graylog-datanode-0", 9200, cancellationToken);
        using var ca = X509Certificate2.CreateFromPem(current.CaCertificate);
        using var client = tunnel.CreateTlsClient(DataNodeHost, ca);
        using var inventory = await SendAsync(client, HttpMethod.Get, $"_snapshot/{RepositoryName}/_all", null, cancellationToken);
        var existing = inventory.RootElement.GetProperty("snapshots").EnumerateArray()
            .Select(snapshot => Required(snapshot, "snapshot")).ToHashSet(StringComparer.Ordinal);
        if (!existing.Contains(current.SnapshotName))
            throw new InvalidOperationException("Current native checkpoint is missing; refusing retention cleanup.");
        foreach (var item in obsolete)
        {
            if (existing.Contains(item.SnapshotName))
            {
                using var deleted = await SendAsync(client, HttpMethod.Delete,
                    $"_snapshot/{RepositoryName}/{item.SnapshotName}", null, cancellationToken);
                if (!deleted.RootElement.GetProperty("acknowledged").GetBoolean())
                    throw new InvalidOperationException("OpenSearch did not acknowledge native snapshot cleanup.");
                existing.Remove(item.SnapshotName);
            }
            await ExecAsync("drn-project", "postgresql-1", "postgres", ["rm", "-f", item.PostgresqlDump], cancellationToken);
            await ExecAsync("graylog", "mongodb-0", "mongod", ["rm", "-f", item.MongodbDump], cancellationToken);
        }
    }

    public static void ValidateSnapshot(JsonElement snapshot, string name, string version, IReadOnlyCollection<string> indices)
    {
        if (Required(snapshot, "snapshot") != name || Required(snapshot, "state") != "SUCCESS" ||
            Required(snapshot, "version") != version || snapshot.GetProperty("failures").GetArrayLength() != 0 ||
            snapshot.GetProperty("shards").GetProperty("failed").GetInt32() != 0 ||
            snapshot.GetProperty("shards").GetProperty("total").GetInt32() == 0 ||
            snapshot.GetProperty("shards").GetProperty("successful").GetInt32() != snapshot.GetProperty("shards").GetProperty("total").GetInt32() ||
            !snapshot.GetProperty("indices").EnumerateArray().Select(item => item.GetString()!)
                .ToHashSet(StringComparer.Ordinal).SetEquals(indices))
            throw new InvalidOperationException("Native search snapshot is incomplete or belongs to another checkpoint.");
    }

    private static void ValidateIndices(IReadOnlyCollection<string> indices)
    {
        if (indices.Count == 0 || indices.Distinct(StringComparer.Ordinal).Count() != indices.Count ||
            indices.Any(index => !Regex.IsMatch(index, "^[a-z0-9][a-z0-9_.-]{0,254}$") || index.Contains("_warm_", StringComparison.Ordinal)))
            throw new InvalidOperationException("Native recovery supports explicit hot indices in the single-node staging stack only.");
    }

    private async Task MongoExportAsync(string archive, bool restore, CancellationToken cancellationToken)
    {
        var uri = await SecretValueAsync("graylog", "graylog-credentials", "GRAYLOG_MONGODB_URI", cancellationToken);
        using var bundle = JsonDocument.Parse(await runner.RunAsync("kubectl", ["get", "configmap", "graylog-database-trust",
            "-n", "graylog", "-o", "json"], environment, false, cancellationToken));
        var ca = bundle.RootElement.GetProperty("data").GetProperty("ca.crt").GetString()!;
        var temporary = $"/tmp/drn-recovery-{Guid.NewGuid():N}";
        try
        {
            await ExecAsync("graylog", "mongodb-0", "mongod", ["sh", "-ceu", $"umask 077; cat > {temporary}.json"],
                cancellationToken, JsonSerializer.Serialize(new { uri }));
            await ExecAsync("graylog", "mongodb-0", "mongod", ["sh", "-ceu", $"umask 077; cat > {temporary}.crt"], cancellationToken, ca);
            if (!restore)
                await ExecAsync("graylog", "mongodb-0", "mongod", ["sh", "-ceu", $"umask 077; mkdir -p {MongoRoot}; test ! -e {archive}"], cancellationToken);
            List<string> arguments = [restore ? "mongorestore" : "mongodump", "--config", $"{temporary}.json",
                "--ssl", "--sslCAFile", $"{temporary}.crt", $"--archive={archive}", "--gzip"];
            if (restore) arguments.AddRange(["--drop", "--nsInclude=graylog.*", "--stopOnError"]);
            else arguments.AddRange(["--db", "graylog"]);
            // Only fixed flags and validated private paths enter this shell; credentials stay in stdin-created files.
            await ExecAsync("graylog", "mongodb-0", "mongod", ["sh", "-ceu", "umask 077; exec \"$@\"", "drn-native", .. arguments], cancellationToken);
        }
        finally
        {
            await ExecAsync("graylog", "mongodb-0", "mongod", ["rm", "-f", $"{temporary}.json", $"{temporary}.crt"], CancellationToken.None);
        }
    }

    private async Task<HttpClient> GraylogClientAsync(KubernetesTunnel tunnel, CancellationToken cancellationToken)
    {
        var password = await SecretValueAsync("graylog", "graylog-admin-password", "password", cancellationToken);
        var username = await SecretValueAsync("graylog", "graylog-credentials", "GRAYLOG_ROOT_USERNAME", cancellationToken);
        var client = tunnel.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        client.DefaultRequestHeaders.Add("X-Requested-By", "drn-pulumi-ops");
        return client;
    }

    private async Task<string> SecretValueAsync(string namespaceName, string name, string key, CancellationToken cancellationToken)
    {
        using var response = JsonDocument.Parse(await runner.RunAsync("kubectl", ["get", "secret", name, "-n", namespaceName,
            "-o", "json"], environment, false, cancellationToken));
        return Encoding.UTF8.GetString(Convert.FromBase64String(Required(response.RootElement.GetProperty("data"), key)));
    }

    private Task<string> ExecAsync(string namespaceName, string pod, string container, IReadOnlyList<string> args,
        CancellationToken cancellationToken, string? input = null) =>
        runner.RunAsync("kubectl", ["exec", "-i", "-n", namespaceName, pod, "-c", container, "--", .. args],
            environment, false, cancellationToken, input);

    private async Task<string[]> IndexNamesAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var indices = await SendAsync(client, HttpMethod.Get, "_cat/indices/*,-.*?format=json&h=index&expand_wildcards=all", null, cancellationToken);
        var names = indices.RootElement.EnumerateArray().Select(item => Required(item, "index")).Order(StringComparer.Ordinal).ToArray();
        ValidateIndices(names);
        return names;
    }

    private async Task RegisterRepositoryAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var registered = await SendAsync(client, HttpMethod.Put, $"_snapshot/{RepositoryName}",
            new { type = "fs", settings = new { location = RepositoryPath } }, cancellationToken);
        if (!registered.RootElement.GetProperty("acknowledged").GetBoolean())
            throw new InvalidOperationException("DataNode did not acknowledge its native snapshot repository.");
        using var verified = await SendAsync(client, HttpMethod.Post, $"_snapshot/{RepositoryName}/_verify", null, cancellationToken);
        if (verified.RootElement.GetProperty("nodes").EnumerateObject().Count() != 1)
            throw new InvalidOperationException("Filesystem snapshot repository requires exactly the supported single DataNode.");
    }

    private async Task RequireSnapshotSpaceAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var indices = await SendAsync(client, HttpMethod.Get,
            "_cat/indices/*,-.*?format=json&h=pri.store.size&bytes=b&expand_wildcards=all", null, cancellationToken);
        var required = indices.RootElement.EnumerateArray().Sum(index =>
            long.Parse(Required(index, "pri.store.size"), System.Globalization.CultureInfo.InvariantCulture));
        using var nodes = await SendAsync(client, HttpMethod.Get, "_nodes/stats/fs", null, cancellationToken);
        var data = nodes.RootElement.GetProperty("nodes").EnumerateObject().ToArray();
        if (data.Length != 1 || data[0].Value.GetProperty("fs").GetProperty("total").GetProperty("available_in_bytes").GetInt64()
            < required + 256L * 1024 * 1024)
            throw new InvalidOperationException("The retained DataNode disk lacks space for a complete native snapshot and reserve.");
    }

    private async Task<JsonDocument> SendAsync(HttpClient client, HttpMethod method, string path, object? body,
        CancellationToken cancellationToken)
    {
        if (method != HttpMethod.Get) await assertOwned(cancellationToken);
        using var request = new HttpRequestMessage(method, path);
        if (client.BaseAddress?.Scheme == "https")
        {
            var secret = await SecretValueAsync("graylog", "graylog-credentials", "GRAYLOG_PASSWORD_SECRET", cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateSearchToken(secret, DateTimeOffset.UtcNow));
        }
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Native database API request failed with HTTP {(int)response.StatusCode}. Recovery remains locked.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    // Matches Graylog 7.2 IndexerJwtAuthTokenProvider and its admin backend-role mapping.
    // The credential checkpoint already requires a password secret of at least 64 characters.
    public static string CreateSearchToken(string passwordSecret, DateTimeOffset now)
    {
        if (passwordSecret.Length < 64)
            throw new InvalidOperationException("DataNode JWT signing requires the recorded 64-character or longer password secret.");
        var key = Encoding.UTF8.GetBytes(passwordSecret);
        try
        {
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS512", typ = "JWT" }));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                jti = $"drn-recovery-{Guid.NewGuid():N}", os_roles = "admin", sub = "admin", iss = "graylog",
                iat = now.ToUnixTimeSeconds(), nbf = now.AddSeconds(-30).ToUnixTimeSeconds(), exp = now.AddMinutes(5).ToUnixTimeSeconds()
            }));
            var data = $"{header}.{payload}";
            return $"{data}.{Base64Url(HMACSHA512.HashData(key, Encoding.ASCII.GetBytes(data)))}";
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ValidateCa(string pem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            if (certificate.HasPrivateKey || !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
                throw new InvalidOperationException("Native backup requires the public DataNode certificate authority.");
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Native backup contains an invalid DataNode certificate authority.");
        }
    }

    private static string Required(JsonElement value, string key) => value.GetProperty(key).GetString() is { Length: > 0 } text
        ? text : throw new InvalidOperationException("Native backup metadata is incomplete.");
}
