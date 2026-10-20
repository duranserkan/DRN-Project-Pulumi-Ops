using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Drn.Infrastructure.Operator.Recovery;
using Drn.Pulumi;
using Xunit;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed class NativeBackupTests
{
    [Fact]
    public void RestoreRejectsOriginalGenerationAndUntrustedDumpPaths()
    {
        var manifest = Manifest();
        Assert.Throws<InvalidOperationException>(() => NativeDatabaseBackup.ValidateRestoreTarget(manifest, "initial"));
        Assert.Throws<InvalidOperationException>(() => NativeDatabaseBackup.ValidateRestoreTarget(
            manifest with { PostgresqlDump = "/var/lib/postgresql/data/pgdata/PG_VERSION" }, Guid.NewGuid().ToString("N")));
        NativeDatabaseBackup.ValidateRestoreTarget(manifest, Guid.NewGuid().ToString("N"));
    }

    [Theory]
    [DataInlineUnit("PARTIAL", 0, "3.7.0", "graylog_0")]
    [DataInlineUnit("SUCCESS", 1, "3.7.0", "graylog_0")]
    [DataInlineUnit("SUCCESS", 0, "2.19.6", "graylog_0")]
    [DataInlineUnit("SUCCESS", 0, "3.7.0", "other_0")]
    public void IncompleteOrMismatchedSnapshotsAreRejected(string state, int failed, string version, string index)
    {
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            snapshot = "checkpoint", state, version, indices = new[] { index }, failures = Array.Empty<object>(),
            shards = new { failed, total = 1, successful = 1 - failed }
        }));
        Assert.Throws<InvalidOperationException>(() => NativeDatabaseBackup.ValidateSnapshot(snapshot.RootElement,
            "checkpoint", "3.7.0", ["graylog_0"]));
    }

    [Fact]
    public void ExactCompletedSnapshotIsAccepted()
    {
        using var snapshot = JsonDocument.Parse("""
            {"snapshot":"checkpoint","state":"SUCCESS","version":"3.7.0","indices":["graylog_0"],
            "failures":[],"shards":{"failed":0,"total":1,"successful":1}}
            """);
        NativeDatabaseBackup.ValidateSnapshot(snapshot.RootElement, "checkpoint", "3.7.0", ["graylog_0"]);
    }

    [Fact]
    public async Task RestoreCannotTouchDatabasesUntilDataNodeHasStopped()
    {
        var manifest = Manifest();
        var runner = new RefuseCommands();
        var lockChecked = false;
        var native = new NativeDatabaseBackup(runner, new Dictionary<string, string>(), _ =>
        {
            lockChecked = true;
            return Task.CompletedTask;
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => native.RestoreAsync(manifest, Guid.NewGuid().ToString("N"), _ =>
            {
                Assert.True(lockChecked);
                throw new InvalidOperationException("DataNode did not stop.");
            }, _ => throw new InvalidOperationException("Must not restart after a failed stop."), TestContext.Current.CancellationToken));
        Assert.Equal("DataNode did not stop.", error.Message);
        Assert.True(lockChecked);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void SearchJwtUsesTheRecordedSecretNativeRoleClaimsAndFiveMinuteLifetime()
    {
        const string secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var now = OperatorTestData.Timestamp;
        var token = NativeDatabaseBackup.CreateSearchToken(secret, now);
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        using var header = JsonDocument.Parse(Decode(parts[0]));
        using var claims = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal("HS512", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("admin", claims.RootElement.GetProperty("os_roles").GetString());
        Assert.Equal("admin", claims.RootElement.GetProperty("sub").GetString());
        Assert.Equal("graylog", claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal(now.ToUnixTimeSeconds(), claims.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(now.AddSeconds(-30).ToUnixTimeSeconds(), claims.RootElement.GetProperty("nbf").GetInt64());
        Assert.Equal(now.AddMinutes(5).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        var expected = HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        Assert.Equal(expected, Decode(parts[2]));
        var wrongKey = HMACSHA512.HashData(Encoding.UTF8.GetBytes(new string('z', 64)), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
        Assert.False(CryptographicOperations.FixedTimeEquals(wrongKey, Decode(parts[2])));
        Assert.DoesNotContain(secret, token, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchJwtRejectsAnUnverifiedShortSecret()
    {
        Assert.Throws<InvalidOperationException>(() => NativeDatabaseBackup.CreateSearchToken("replacement-secret", OperatorTestData.Timestamp));
    }

    [Fact]
    public void RestoreRequiresAPublicCertificateAuthority()
    {
        var manifest = Manifest(certificateAuthority: false);
        Assert.Throws<InvalidOperationException>(() => NativeDatabaseBackup.ValidateManifest(manifest));
    }

    private static NativeBackupManifest Manifest(bool certificateAuthority = true)
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        return new NativeBackupManifest(id, $"drn-{id:N}", "3.7.0", ["graylog_0"],
            $"/var/lib/postgresql/data/drn-backups/{id:N}.dump", $"/data/drn-backups/{id:N}.archive.gz", "18.6",
            CertificateAuthority(certificateAuthority));
    }

    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/')
        .PadRight((value.Length + 3) / 4 * 4, '='));

    private static string CertificateAuthority(bool isCa = true)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Native backup tests", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCa, false, 0, true));
        using var certificate = request.CreateSelfSigned(OperatorTestData.Timestamp.AddDays(-1), OperatorTestData.Timestamp.AddYears(10));
        return certificate.ExportCertificatePem();
    }

    private sealed class RefuseCommands : ICommandRunner
    {
        public int Calls { get; private set; }
        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, bool displayOutput, CancellationToken cancellationToken,
            string? standardInput = null)
        {
            Calls++;
            throw new InvalidOperationException("No database command may run before the writer barrier.");
        }
    }
}
