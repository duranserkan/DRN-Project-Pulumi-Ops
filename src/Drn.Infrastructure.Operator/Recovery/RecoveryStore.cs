using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Drn.Infrastructure.Operator.Recovery;

public sealed record StoredDocument<T>(T Value, string ETag);
public sealed record RecoveryLock(Guid OwnerId, string Operation, DateTimeOffset CreatedAt, string ETag);

/// <summary>Encrypted lifecycle state and a non-expiring, conditionally created OCI object lock.</summary>
public sealed class RecoveryStore(
    ICommandRunner runner, string namespaceName, string bucketName, string keyPath,
    IReadOnlyDictionary<string, string> environment, string objectPrefix = "staging")
{
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void GenerateKey(string path)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Recovery keys require Unix private file permissions.");
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<StoredDocument<T>?> ReadAsync<T>(string name, CancellationToken cancellationToken)
    {
        var objectName = ObjectName(name);
        using var listing = JsonDocument.Parse(await RunAsync(
            ["list", "--prefix", objectName, "--fields", "name,etag,size", "--all"], cancellationToken));
        var matching = listing.RootElement.GetProperty("data").EnumerateArray()
            .Where(item => item.GetProperty("name").GetString() == objectName).ToArray();
        if (matching.Length == 0) return null;
        if (matching.Length != 1 || matching[0].GetProperty("size").GetInt64() > MaximumPayloadBytes)
            throw new InvalidOperationException("Recovery object inventory is invalid.");
        var etag = RequireString(matching[0], "etag");
        return await ReadVersionAsync<T>(objectName, etag, cancellationToken);
    }

    public async Task<string> WriteAsync<T>(string name, T value, string? expectedETag, CancellationToken cancellationToken)
    {
        var objectName = ObjectName(name);
        var cleartext = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        byte[]? encrypted = null;
        try
        {
            if (cleartext.Length > MaximumPayloadBytes / 2)
                throw new InvalidOperationException("Recovery state exceeds the supported size.");
            encrypted = Encrypt(objectName, cleartext);
            using var file = new PrivateFile(encrypted);
            List<string> args = ["put", "--name", objectName, "--file", file.Path,
                "--no-multipart", "--content-type", "application/json"];
            if (expectedETag is null) args.Add("--no-overwrite");
            else args.AddRange(["--if-match", expectedETag, "--force"]);
            var response = await RunAsync(args, cancellationToken);
            // --no-overwrite returns exit zero and no JSON if the object already exists.
            if (string.IsNullOrWhiteSpace(response))
                throw new InvalidOperationException("Recovery object already exists; refusing to overwrite another operation.");
            using var result = JsonDocument.Parse(response);
            var etag = RequireString(result.RootElement, "etag");
            var persisted = await ReadVersionAsync<JsonElement>(objectName, etag, cancellationToken);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(value, JsonOptions), persisted.Value))
                throw new InvalidOperationException("Recovery state readback did not match the written state.");
            return etag;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cleartext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task<RecoveryLock> AcquireAsync(Guid ownerId, string operation, CancellationToken cancellationToken)
    {
        if (ownerId == Guid.Empty || !Regex.IsMatch(operation, "^[a-z][a-z-]{0,39}$"))
            throw new ArgumentException("Recovery lock requires an operation and nonempty owner ID.");
        var contents = new LockContents(ownerId, operation, DateTimeOffset.UtcNow);
        var etag = await WriteAsync("lock", contents, null, cancellationToken);
        return new RecoveryLock(ownerId, operation, contents.CreatedAt, etag);
    }

    public async Task AssertOwnedAsync(RecoveryLock held, CancellationToken cancellationToken)
    {
        var current = await ReadAsync<LockContents>("lock", cancellationToken);
        if (current is null || current.ETag != held.ETag || current.Value.OwnerId != held.OwnerId)
            throw new InvalidOperationException("Recovery lock ownership changed. No further mutation is permitted.");
    }

    public async Task<RecoveryLock?> ReadLockAsync(CancellationToken cancellationToken)
    {
        var current = await ReadAsync<LockContents>("lock", cancellationToken);
        return current is null ? null : new RecoveryLock(current.Value.OwnerId, current.Value.Operation,
            current.Value.CreatedAt, current.ETag);
    }

    public async Task ReleaseAsync(RecoveryLock held, CancellationToken cancellationToken)
    {
        await AssertOwnedAsync(held, cancellationToken);
        await RunAsync(["delete", "--name", ObjectName("lock"), "--if-match", held.ETag, "--force"], cancellationToken);
    }

    private async Task<StoredDocument<T>> ReadVersionAsync<T>(string objectName, string etag, CancellationToken cancellationToken)
    {
        using var file = new PrivateFile();
        await RunAsync(["get", "--name", objectName, "--if-match", etag, "--file", file.Path], cancellationToken);
        if (new FileInfo(file.Path).Length > MaximumPayloadBytes)
            throw new InvalidOperationException("Recovery object exceeds the supported size.");
        var cleartext = Decrypt(objectName, await File.ReadAllBytesAsync(file.Path, cancellationToken));
        try
        {
            return new StoredDocument<T>(JsonSerializer.Deserialize<T>(cleartext, JsonOptions)
                ?? throw new InvalidOperationException("Recovery object contains no document."), etag);
        }
        finally { CryptographicOperations.ZeroMemory(cleartext); }
    }

    private byte[] Encrypt(string objectName, byte[] cleartext)
    {
        var key = ReadKey();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[cleartext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, cleartext, ciphertext, tag, AssociatedData(objectName));
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, nonce, ciphertext, tag), JsonOptions);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private byte[] Decrypt(string objectName, byte[] encoded)
    {
        var key = ReadKey();
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(encoded, JsonOptions);
            if (envelope is null || envelope.Version != 1 || envelope.Nonce?.Length != 12 ||
                envelope.Tag?.Length != 16 || envelope.Ciphertext is null)
                throw new InvalidOperationException("Recovery encryption envelope is invalid.");
            var cleartext = new byte[envelope.Ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, cleartext, AssociatedData(objectName));
            return cleartext;
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Recovery state could not be authenticated. Restore the correct independent recovery key.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private byte[] ReadKey()
    {
        if (OperatingSystem.IsWindows() || new FileInfo(keyPath).LinkTarget is not null ||
            File.GetUnixFileMode(keyPath) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            throw new InvalidOperationException("Recovery key must be a regular private file with mode 600.");
        var key = File.ReadAllBytes(keyPath);
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidOperationException("Recovery key must contain exactly 32 bytes.");
    }

    private byte[] AssociatedData(string name) => Encoding.UTF8.GetBytes($"drn-recovery-v1\n{namespaceName}\n{bucketName}\n{name}");
    private string ObjectName(string name)
    {
        if (!Regex.IsMatch(objectPrefix, "^[a-z0-9][a-z0-9/-]{0,100}$") ||
            !Regex.IsMatch(name, "^[a-z0-9][a-z0-9/-]{0,150}$"))
            throw new ArgumentException("Invalid recovery object name.");
        return $"{objectPrefix}/{name}.json";
    }

    private Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        runner.RunAsync("oci", ["os", "object", .. args, "--namespace-name", namespaceName,
            "--bucket-name", bucketName, "--output", "json", "--no-retry"], environment, false, cancellationToken);

    private static string RequireString(JsonElement value, string property) =>
        value.GetProperty(property).GetString() is { Length: > 0 } text ? text
            : throw new InvalidOperationException("OCI returned incomplete recovery metadata.");

    private sealed record LockContents(Guid OwnerId, string Operation, DateTimeOffset CreatedAt);
    private sealed record Envelope(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
}
