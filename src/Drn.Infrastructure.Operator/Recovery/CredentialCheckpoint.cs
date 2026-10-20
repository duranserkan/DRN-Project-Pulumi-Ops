using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Drn.Infrastructure.Operator.Recovery;

/// <summary>Stable installation identities. Save an encrypted checkpoint before applying its manifests.</summary>
public sealed record CredentialCheckpoint
{
    public int SchemaVersion { get; init; } = 1;
    public string Generation { get; init; } = "";
    public string GitOpsCommit { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public JsonObject[] Objects { get; init; } = [];

    private const string InvalidMessage = "Credential checkpoint is invalid, incomplete, or near expiry. Restore a verified checkpoint.";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] SettingsKeys = ["AllowedHosts", "DrnAppFeatures__SeedKey", "NexusAppSettings__Keys__0__KeyMaterial", "NexusAppSettings__Keys__0__Format", "NexusAppSettings__Keys__0__Default"];
    private static readonly Dictionary<string, string[]> RequiredKeys = new(StringComparer.Ordinal)
    {
        ["drn-project/nexus-settings"] = SettingsKeys,
        ["drn-project/sample-settings"] = SettingsKeys,
        ["drn-project/postgresql-app"] = ["username", "password", "pgpass"],
        ["drn-project/postgresql-ca"] = ["ca.crt", "ca.key"],
        ["graylog/mongo-graylog-password"] = ["password"],
        ["graylog/graylog-scram-scram-credentials"] = ["sha1-salt", "sha-1-server-key", "sha-1-stored-key", "sha256-salt", "sha-256-server-key", "sha-256-stored-key"],
        ["graylog/mongodb-agent-password"] = ["password"],
        ["graylog/mongodb-keyfile"] = ["keyfile"],
        ["graylog/graylog-admin-password"] = ["password"],
        ["graylog/graylog-credentials"] = ["GRAYLOG_ROOT_USERNAME", "GRAYLOG_ROOT_PASSWORD_SHA2", "GRAYLOG_PASSWORD_SECRET", "GRAYLOG_MONGODB_URI"],
        ["cert-manager/linkerd-identity-trust-roots"] = ["tls.crt", "tls.key", "ca.crt"],
        ["kube-system/sealed-secrets-key"] = ["tls.crt", "tls.key"],
        ["drn-project/application-secret-bootstrap-initialized"] = ["version"],
        ["graylog/graylog-credential-bootstrap-initialized"] = ["version"]
    };

    public static CredentialCheckpoint Create(string gitOpsCommit, DateTimeOffset now)
    {
        if (!Regex.IsMatch(gitOpsCommit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A full lowercase GitOps commit is required.");
        var objects = new List<JsonObject>();
        var seed = "Aa0!" + string.Join("-", RandomNumberGenerator.GetBytes(32).Select(value => value.ToString("x2")));
        var nexusKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        foreach (var app in new[] { "nexus", "sample" })
            objects.Add(Secret("drn-project", app + "-settings", new()
            {
                [SettingsKeys[0]] = Hosts(app), [SettingsKeys[1]] = seed, [SettingsKeys[2]] = nexusKey,
                [SettingsKeys[3]] = "Base64", [SettingsKeys[4]] = "true"
            }));
        var pgPassword = Password(48);
        objects.Add(Secret("drn-project", "postgresql-app", new()
        {
            ["username"] = "drn", ["password"] = pgPassword,
            ["pgpass"] = $"postgresql-rw:5432:*:drn:{pgPassword}"
        }, "kubernetes.io/basic-auth"));
        var pgCa = CreateEcAuthority("postgresql", now);
        objects.Add(Secret("drn-project", "postgresql-ca", new() { ["ca.crt"] = pgCa.Certificate, ["ca.key"] = pgCa.Key }));
        var mongoPassword = Password(48);
        objects.Add(Secret("graylog", "mongo-graylog-password", new() { ["password"] = mongoPassword }));
        objects.Add(Secret("graylog", "graylog-scram-scram-credentials", Scram(mongoPassword,
            RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(28))));
        objects.Add(Secret("graylog", "mongodb-agent-password", new() { ["password"] = Password(32) }));
        objects.Add(Secret("graylog", "mongodb-keyfile", new() { ["keyfile"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(500)) }));
        var adminPassword = Password(32);
        objects.Add(Secret("graylog", "graylog-admin-password", new() { ["password"] = adminPassword }));
        objects.Add(Secret("graylog", "graylog-credentials", new()
        {
            ["GRAYLOG_ROOT_USERNAME"] = "admin", ["GRAYLOG_ROOT_PASSWORD_SHA2"] = Hash(adminPassword),
            ["GRAYLOG_PASSWORD_SECRET"] = Password(48), ["GRAYLOG_MONGODB_URI"] = MongoUri(mongoPassword)
        }));
        var root = CreateEcAuthority("root.linkerd.cluster.local", now);
        objects.Add(Secret("cert-manager", "linkerd-identity-trust-roots", new()
        {
            ["tls.crt"] = root.Certificate, ["tls.key"] = root.Key, ["ca.crt"] = root.Certificate
        }, "kubernetes.io/tls"));
        using var rsa = RSA.Create(4096);
        var request = new CertificateRequest("CN=sealed-secrets", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
        var sealedKey = Secret("kube-system", "sealed-secrets-key", new()
        {
            ["tls.crt"] = certificate.ExportCertificatePem(), ["tls.key"] = rsa.ExportRSAPrivateKeyPem()
        }, "kubernetes.io/tls");
        sealedKey["metadata"]!["labels"] = new JsonObject { ["sealedsecrets.bitnami.com/sealed-secrets-key"] = "active" };
        objects.Add(sealedKey);
        objects.Add(Marker("drn-project", "application-secret-bootstrap-initialized"));
        objects.Add(Marker("graylog", "graylog-credential-bootstrap-initialized"));
        var checkpoint = new CredentialCheckpoint
        {
            Generation = Guid.NewGuid().ToString("N"), GitOpsCommit = gitOpsCommit, CreatedAt = now, Objects = objects.ToArray()
        };
        checkpoint.Validate(now);
        return checkpoint;
    }

    public void Validate(DateTimeOffset now)
    {
        try
        {
            Require(SchemaVersion == 1 && Guid.TryParseExact(Generation, "N", out _) &&
                    Regex.IsMatch(GitOpsCommit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) &&
                    CreatedAt <= now.AddMinutes(5) && CreatedAt > DateTimeOffset.UnixEpoch && Objects.Length == RequiredKeys.Count);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in Objects)
            {
                var identity = Identity(item);
                Require(identities.Add(identity) && RequiredKeys.ContainsKey(identity));
                Require(item["apiVersion"]?.GetValue<string>() == "v1" && item["immutable"]?.GetValue<bool>() == true);
                var marker = identity.EndsWith("-initialized", StringComparison.Ordinal);
                Require(item["kind"]?.GetValue<string>() == (marker ? "ConfigMap" : "Secret"));
                Require(item["metadata"] is JsonObject metadata && metadata["ownerReferences"] is null &&
                        metadata["uid"] is null && metadata["resourceVersion"] is null && metadata["deletionTimestamp"] is null);
                Require(item["stringData"] is null && item["binaryData"] is null);
                var values = Data(item);
                Require(values.Count == RequiredKeys[identity].Length && RequiredKeys[identity].All(values.ContainsKey));
                foreach (var key in RequiredKeys[identity]) Require(Value(item, key).Length > 0);
                if (marker) Require(Value(item, "version") == "1" && item["type"] is null);
                else Require(item["type"]?.GetValue<string>() == SecretType(identity));
            }
            var nexus = Find("drn-project/nexus-settings");
            foreach (var app in new[] { "nexus", "sample" })
            {
                var settings = Find($"drn-project/{app}-settings");
                Require(Value(settings, SettingsKeys[0]) == Hosts(app));
                Require(Regex.IsMatch(Value(settings, SettingsKeys[1]), "^Aa0![0-9a-f]{2}(-[0-9a-f]{2}){31}$", RegexOptions.CultureInvariant));
                Require(Convert.FromBase64String(Value(settings, SettingsKeys[2])).Length == 32);
                Require(Value(settings, SettingsKeys[3]) == "Base64" && Value(settings, SettingsKeys[4]) == "true");
                foreach (var key in SettingsKeys.Skip(1)) Require(Value(settings, key) == Value(nexus, key));
            }
            var pg = Find("drn-project/postgresql-app");
            Require(Value(pg, "username") == "drn" && SafePassword(Value(pg, "password"), 64));
            Require(Value(pg, "pgpass") == $"postgresql-rw:5432:*:drn:{Value(pg, "password")}");
            var mongoPassword = Value(Find("graylog/mongo-graylog-password"), "password");
            Require(SafePassword(mongoPassword, 64));
            var scram = Find("graylog/graylog-scram-scram-credentials");
            var salt1 = Convert.FromBase64String(Value(scram, "sha1-salt"));
            var salt256 = Convert.FromBase64String(Value(scram, "sha256-salt"));
            Require(salt1.Length == 16 && salt256.Length == 28);
            foreach (var pair in Scram(mongoPassword, salt1, salt256)) Require(Value(scram, pair.Key) == pair.Value);
            Require(SafePassword(Value(Find("graylog/mongodb-agent-password"), "password"), 43));
            Require(Convert.FromBase64String(Value(Find("graylog/mongodb-keyfile"), "keyfile")).Length == 500);
            var admin = Value(Find("graylog/graylog-admin-password"), "password");
            Require(SafePassword(admin, 43));
            var graylog = Find("graylog/graylog-credentials");
            Require(Value(graylog, "GRAYLOG_ROOT_USERNAME") == "admin" && Value(graylog, "GRAYLOG_ROOT_PASSWORD_SHA2") == Hash(admin));
            Require(Value(graylog, "GRAYLOG_MONGODB_URI") == MongoUri(mongoPassword) && SafePassword(Value(graylog, "GRAYLOG_PASSWORD_SECRET"), 64));
            ValidateAuthority(Find("drn-project/postgresql-ca"), "ca.crt", "ca.key", now, rsa: false);
            var trust = Find("cert-manager/linkerd-identity-trust-roots");
            Require(Value(trust, "tls.crt") == Value(trust, "ca.crt"));
            ValidateAuthority(trust, "tls.crt", "tls.key", now, rsa: false);
            var sealedKey = Find("kube-system/sealed-secrets-key");
            Require(sealedKey["metadata"]?["labels"]?["sealedsecrets.bitnami.com/sealed-secrets-key"]?.GetValue<string>() == "active");
            ValidateAuthority(sealedKey, "tls.crt", "tls.key", now, rsa: true);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or CryptographicException or NullReferenceException)
        {
            // Never include malformed JSON, certificate material, credentials or inner exception text.
            throw new InvalidOperationException(InvalidMessage);
        }
    }

    public string ManifestJson() => new JsonObject
    {
        ["apiVersion"] = "v1", ["kind"] = "List", ["items"] = new JsonArray(Objects.Select(item => item.DeepClone()).ToArray())
    }.ToJsonString();

    /// <summary>Ignores Kubernetes bookkeeping while detecting any stable data or identity change.</summary>
    public string StableIdentityHash() => Hash(Canonical(Objects));

    public void ValidateLive(JsonObject[] liveObjects, DateTimeOffset now)
    {
        Validate(now);
        try
        {
            Require(liveObjects.Length == Objects.Length && Canonical(liveObjects) == Canonical(Objects));
            Require(liveObjects.All(item => item["metadata"]?["deletionTimestamp"] is null &&
                (item["metadata"]?["ownerReferences"] is null || item["metadata"]!["ownerReferences"] is JsonArray { Count: 0 })));
            Require(liveObjects.Single(item => Identity(item) == "kube-system/sealed-secrets-key")
                ["metadata"]?["labels"]?["sealedsecrets.bitnami.com/sealed-secrets-key"]?.GetValue<string>() == "active");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or NullReferenceException)
        {
            throw new InvalidOperationException("Live credentials differ from the saved checkpoint. Restore the verified identities before activation.");
        }
    }

    private JsonObject Find(string identity) => Objects.Single(item => Identity(item) == identity);
    private static string Identity(JsonObject item) => item["metadata"]!["namespace"]!.GetValue<string>() + "/" + item["metadata"]!["name"]!.GetValue<string>();
    private static JsonObject Data(JsonObject item) => item["data"]!.AsObject();
    private static string Value(JsonObject item, string key)
    {
        var value = Data(item)[key]!.GetValue<string>();
        if (item["kind"]!.GetValue<string>() == "ConfigMap") return value;
        var bytes = Convert.FromBase64String(value);
        Require(Convert.ToBase64String(bytes) == value);
        return Utf8.GetString(bytes);
    }
    private static string SecretType(string identity) => identity switch
    {
        "drn-project/postgresql-app" => "kubernetes.io/basic-auth",
        "cert-manager/linkerd-identity-trust-roots" or "kube-system/sealed-secrets-key" => "kubernetes.io/tls",
        _ => "Opaque"
    };
    private static JsonObject Secret(string ns, string name, Dictionary<string, string> values, string type = "Opaque")
    {
        var data = new JsonObject();
        foreach (var pair in values) data[pair.Key] = Convert.ToBase64String(Utf8.GetBytes(pair.Value));
        return new JsonObject { ["apiVersion"] = "v1", ["kind"] = "Secret", ["metadata"] = new JsonObject { ["namespace"] = ns, ["name"] = name }, ["type"] = type, ["immutable"] = true, ["data"] = data };
    }
    private static JsonObject Marker(string ns, string name) => new()
    {
        ["apiVersion"] = "v1", ["kind"] = "ConfigMap", ["metadata"] = new JsonObject { ["namespace"] = ns, ["name"] = name },
        ["immutable"] = true, ["data"] = new JsonObject { ["version"] = "1" }
    };
    private static string Hosts(string app) => $"{app};{app}.drn-project;{app}.drn-project.svc;{app}.drn-project.svc.cluster.local;localhost;127.0.0.1";
    private static string MongoUri(string password) => $"mongodb://graylog:{password}@mongodb-0.mongodb-svc.graylog.svc.cluster.local:27017/graylog?replicaSet=mongodb&ssl=true";
    private static string Password(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool SafePassword(string value, int length) => value.Length == length && Regex.IsMatch(value, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(value)));
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException(InvalidMessage); }

    private static (string Certificate, string Key) CreateEcAuthority(string commonName, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + commonName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(20));
        // CNPG expects SEC1 EC PRIVATE KEY, rather than PKCS#8 PRIVATE KEY.
        return (certificate.ExportCertificatePem(), key.ExportECPrivateKeyPem());
    }

    private static void ValidateAuthority(JsonObject item, string certificateKey, string privateKey, DateTimeOffset now, bool rsa)
    {
        // Attaching private keys to certificates repeatedly imports them into the macOS keychain.
        // Prove possession and key matching without creating a certificate/private-key identity.
        using var certificate = X509Certificate2.CreateFromPem(Value(item, certificateKey));
        Require(certificate.NotBefore.ToUniversalTime() <= now.UtcDateTime && certificate.NotAfter.ToUniversalTime() > now.AddDays(30).UtcDateTime);
        Require(certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Require(certificate.Subject == certificate.Issuer);
        ReadOnlySpan<byte> challenge = "drn-credential-authority-key-match"u8;
        if (rsa)
        {
            using var publicKey = certificate.GetRSAPublicKey();
            Require(publicKey is not null && publicKey.KeySize >= 4096);
            using var key = RSA.Create();
            key.ImportFromPem(Value(item, privateKey));
            var signature = key.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            Require(publicKey!.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        else
        {
            using var publicKey = certificate.GetECDsaPublicKey();
            Require(publicKey is not null && publicKey.KeySize == 256 && Value(item, privateKey).StartsWith("-----BEGIN EC PRIVATE KEY-----", StringComparison.Ordinal));
            using var key = ECDsa.Create();
            key.ImportFromPem(Value(item, privateKey));
            var signature = key.SignData(challenge, HashAlgorithmName.SHA256);
            Require(publicKey!.VerifyData(challenge, signature, HashAlgorithmName.SHA256));
        }
    }

    private static Dictionary<string, string> Scram(string password, byte[] salt1, byte[] salt256)
    {
        var values = new Dictionary<string, string>();
        // MongoDB SCRAM-SHA-1 uses its historical MD5 username digest. This is protocol-required.
        var digest = Convert.ToHexStringLower(MD5.HashData(Utf8.GetBytes("graylog:mongo:" + password)));
        Add("sha1-salt", "sha-1", digest, salt1, 10000, HashAlgorithmName.SHA1, 20);
        // Generated passwords are ASCII and already normalized for SASLprep.
        Add("sha256-salt", "sha-256", password, salt256, 15000, HashAlgorithmName.SHA256, 32);
        return values;

        void Add(string saltName, string prefix, string input, byte[] salt, int iterations, HashAlgorithmName algorithm, int length)
        {
            var salted = Rfc2898DeriveBytes.Pbkdf2(input, salt, iterations, algorithm, length);
            using HMAC hmac = algorithm == HashAlgorithmName.SHA1 ? new HMACSHA1(salted) : new HMACSHA256(salted);
            var client = hmac.ComputeHash(Utf8.GetBytes("Client Key"));
            var stored = algorithm == HashAlgorithmName.SHA1 ? SHA1.HashData(client) : SHA256.HashData(client);
            values[saltName] = Convert.ToBase64String(salt);
            values[prefix + "-server-key"] = Convert.ToBase64String(hmac.ComputeHash(Utf8.GetBytes("Server Key")));
            values[prefix + "-stored-key"] = Convert.ToBase64String(stored);
            CryptographicOperations.ZeroMemory(salted);
        }
    }

    private static string Canonical(JsonObject[] objects)
    {
        var array = new JsonArray();
        foreach (var item in objects.OrderBy(Identity, StringComparer.Ordinal))
        {
            var data = new JsonObject();
            foreach (var pair in Data(item).OrderBy(pair => pair.Key, StringComparer.Ordinal)) data[pair.Key] = pair.Value?.DeepClone();
            array.Add(new JsonObject { ["identity"] = Identity(item), ["apiVersion"] = item["apiVersion"]?.DeepClone(), ["kind"] = item["kind"]?.DeepClone(), ["type"] = item["type"]?.DeepClone(), ["immutable"] = item["immutable"]?.DeepClone(), ["data"] = data });
        }
        return array.ToJsonString();
    }
}
