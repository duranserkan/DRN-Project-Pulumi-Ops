using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drn.Infrastructure.Operator.Recovery;
using Xunit;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed class CredentialCheckpointTests
{
    private static DateTimeOffset Now => OperatorTestData.Timestamp;
    // Cache only serialized content. Every consumer receives its own mutable JSON tree.
    private static readonly string OriginalJson = JsonSerializer.Serialize(
        CredentialCheckpoint.Create(OperatorTestData.Commit, Now), OperatorConfiguration.JsonOptions);
    private static CredentialCheckpoint Original => JsonSerializer.Deserialize<CredentialCheckpoint>(OriginalJson, OperatorConfiguration.JsonOptions)!;

    [Fact]
    public void SetupCredentialsKeepMutableContentAndGenerationIsolated()
    {
        var first = OperatorTestData.Credentials();
        var second = OperatorTestData.Credentials();
        var originalHash = second.StableIdentityHash();
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal(originalHash, first.StableIdentityHash());

        Find(first, "sample-settings")["data"]!["AllowedHosts"] = "changed";

        Assert.NotEqual(originalHash, first.StableIdentityHash());
        Assert.Equal(originalHash, second.StableIdentityHash());
        Assert.Equal(originalHash, OperatorTestData.Credentials().StableIdentityHash());
        second.Validate(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void CheckpointRoundTripsWithEveryStableIdentityAndNoDerivedOperatorSecrets()
    {
        var checkpoint = JsonSerializer.Deserialize<CredentialCheckpoint>(JsonSerializer.Serialize(Original, OperatorConfiguration.JsonOptions), OperatorConfiguration.JsonOptions)!;
        checkpoint.Validate(Now);
        Assert.Equal(Original.StableIdentityHash(), checkpoint.StableIdentityHash());
        Assert.Equal(12, checkpoint.Objects.Count(item => item["kind"]!.GetValue<string>() == "Secret"));
        Assert.Equal(2, checkpoint.Objects.Count(item => item["kind"]!.GetValue<string>() == "ConfigMap"));
        Assert.All(checkpoint.Objects, item => Assert.True(item["immutable"]!.GetValue<bool>()));
        Assert.DoesNotContain(checkpoint.Objects, item => item["metadata"]!["name"]!.GetValue<string>() == "mongodb-graylog-graylog");
        using var manifest = JsonDocument.Parse(checkpoint.ManifestJson());
        Assert.Equal("List", manifest.RootElement.GetProperty("kind").GetString());
        Assert.Equal(14, manifest.RootElement.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [DataInlineUnit("graylog-scram-scram-credentials", "sha-1-stored-key")]
    [DataInlineUnit("graylog-scram-scram-credentials", "sha-256-server-key")]
    [DataInlineUnit("graylog-credentials", "GRAYLOG_ROOT_PASSWORD_SHA2")]
    [DataInlineUnit("graylog-credentials", "GRAYLOG_MONGODB_URI")]
    [DataInlineUnit("sample-settings", "NexusAppSettings__Keys__0__KeyMaterial")]
    [DataInlineUnit("postgresql-app", "pgpass")]
    public void MismatchedDerivedCredentialsAreRejectedWithoutExposingTheirContent(string name, string key)
    {
        var checkpoint = Copy();
        Find(checkpoint, name)["data"]![key] = Convert.ToBase64String(Encoding.UTF8.GetBytes("sensitive-corrupt-value"));
        var error = Assert.Throws<InvalidOperationException>(() => checkpoint.Validate(Now));
        Assert.DoesNotContain("sensitive-corrupt-value", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void MissingDuplicatedOrUnexpectedIdentitiesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => (Copy() with { Objects = Original.Objects[..^1] }).Validate(Now));
        var duplicate = Copy();
        duplicate.Objects[0] = (JsonObject)duplicate.Objects[1].DeepClone();
        Assert.Throws<InvalidOperationException>(() => duplicate.Validate(Now));
        var unexpected = Copy();
        unexpected.Objects[0]["metadata"]!["namespace"] = "other";
        Assert.Throws<InvalidOperationException>(() => unexpected.Validate(Now));
    }

    [Fact]
    public void StableMarkersCannotBeMissingMutableOrRecreatedWithAnotherVersion()
    {
        var mutable = Copy();
        Find(mutable, "application-secret-bootstrap-initialized")["immutable"] = false;
        Assert.Throws<InvalidOperationException>(() => mutable.Validate(Now));
        var changed = Copy();
        Find(changed, "graylog-credential-bootstrap-initialized")["data"]!["version"] = "2";
        Assert.Throws<InvalidOperationException>(() => changed.Validate(Now));
    }

    [Fact]
    public void CertificateKeyMismatchAndNearExpiryAreRejected()
    {
        var checkpoint = Copy();
        Find(checkpoint, "postgresql-ca")["data"]!["ca.key"] = Find(checkpoint, "linkerd-identity-trust-roots")["data"]!["tls.key"]!.DeepClone();
        Assert.Throws<InvalidOperationException>(() => checkpoint.Validate(Now));
        Assert.Throws<InvalidOperationException>(() => Original.Validate(Now.AddYears(10).AddDays(-20)));
        Assert.Throws<InvalidOperationException>(() => Original.Validate(Now.AddDays(-1)));
    }

    [Theory]
    [DataInlineUnit("postgresql-ca", "ca.crt", "ca.key")]
    [DataInlineUnit("linkerd-identity-trust-roots", "tls.crt", "tls.key")]
    [DataInlineUnit("sealed-secrets-key", "tls.crt", "tls.key")]
    public void MatchingPublicKeyCannotSubstituteForAuthorityPrivateKey(string name, string certificateField, string keyField)
    {
        var checkpoint = Copy();
        var authority = Find(checkpoint, name);
        var pem = Encoding.UTF8.GetString(Convert.FromBase64String(authority["data"]![certificateField]!.GetValue<string>()));
        using var certificate = X509Certificate2.CreateFromPem(pem);
        using AsymmetricAlgorithm publicKey = name == "sealed-secrets-key"
            ? certificate.GetRSAPublicKey()!
            : certificate.GetECDsaPublicKey()!;
        authority["data"]![keyField] = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey.ExportSubjectPublicKeyInfoPem()));

        var error = Assert.Throws<InvalidOperationException>(() => checkpoint.Validate(Now));

        Assert.Equal("Credential checkpoint is invalid, incomplete, or near expiry. Restore a verified checkpoint.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void UnrelatedRsaPrivateKeyCannotReplaceTheSealedSecretsAuthority()
    {
        var checkpoint = Copy();
        using var otherKey = RSA.Create(4096);
        Find(checkpoint, "sealed-secrets-key")["data"]!["tls.key"] =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(otherKey.ExportRSAPrivateKeyPem()));

        var error = Assert.Throws<InvalidOperationException>(() => checkpoint.Validate(Now));

        Assert.Null(error.InnerException);
        Assert.DoesNotContain("BEGIN", error.ToString());
    }

    [Fact]
    public void MatchingRsaPkcs8PrivateKeyRemainsSupported()
    {
        var checkpoint = Copy();
        var authority = Find(checkpoint, "sealed-secrets-key");
        using var key = RSA.Create();
        key.ImportFromPem(Encoding.UTF8.GetString(Convert.FromBase64String(authority["data"]!["tls.key"]!.GetValue<string>())));
        authority["data"]!["tls.key"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));

        checkpoint.Validate(Now);
    }

    [Fact]
    public void LiveComparisonIgnoresApiBookkeepingAndOrderButRejectsAnyChangedIdentity()
    {
        var live = Copy().Objects.Reverse().ToArray();
        foreach (var item in live)
        {
            item["metadata"]!["uid"] = "server-assigned-uid";
            item["metadata"]!["resourceVersion"] = "123";
            item["metadata"]!["managedFields"] = new JsonArray();
        }
        Original.ValidateLive(live, Now);
        live[0]["data"]!["version"] = "2";
        Assert.Throws<InvalidOperationException>(() => Original.ValidateLive(live, Now));
    }

    [Fact]
    public void LiveSealedSecretsAuthorityMustRemainDiscoverable()
    {
        var live = Copy();
        Find(live, "sealed-secrets-key")["metadata"]!["labels"] = new JsonObject();
        Assert.Throws<InvalidOperationException>(() => Original.ValidateLive(live.Objects, Now));
    }

    [Fact]
    public void StableSecretsCannotBeGarbageCollectedWithARecreatedControllerResource()
    {
        var live = Copy();
        Find(live, "mongo-graylog-password")["metadata"]!["ownerReferences"] = new JsonArray(new JsonObject
        {
            ["apiVersion"] = "mongodbcommunity.mongodb.com/v1", ["kind"] = "MongoDBCommunity", ["name"] = "mongodb", ["uid"] = "old-owner"
        });
        Assert.Throws<InvalidOperationException>(() => Original.ValidateLive(live.Objects, Now));
    }

    [Theory]
    [DataInlineUnit(0)]
    [DataInlineUnit(2)]
    public void UnsupportedCheckpointSchemasFailClosed(int schema) =>
        Assert.Throws<InvalidOperationException>(() => (Original with { SchemaVersion = schema }).Validate(Now));

    [Fact]
    public void GenerationIsIndependentFromCredentialContent()
    {
        var checkpoint = Original with { Generation = Guid.NewGuid().ToString("N") };
        checkpoint.Validate(Now);
        Assert.Equal(Original.StableIdentityHash(), checkpoint.StableIdentityHash());
    }

    private static CredentialCheckpoint Copy() => Original;
    private static JsonObject Find(CredentialCheckpoint checkpoint, string name) => checkpoint.Objects.Single(item => item["metadata"]!["name"]!.GetValue<string>() == name);
}
