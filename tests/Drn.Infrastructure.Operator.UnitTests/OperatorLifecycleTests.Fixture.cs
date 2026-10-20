using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Drn.Infrastructure.Operator.Recovery;
using Xunit;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed partial class OperatorLifecycleTests
{
    // The operator, encrypted recovery store, cloud checks, policy probe and Kubernetes lifecycle
    // execute normally. Only external commands and the native database transport are simulated.
    private sealed class Fixture
    {
        public string Directory { get; }
        public OperatorConfiguration Configuration { get; set; }
        public Api Api { get; } = new();
        public List<TimeSpan> PollDelays { get; } = [];
        private readonly NativeBackup native;

        public Fixture(DrnTestContextUnit context)
        {
            Directory = OperatorTestData.CreateWorkspace(context);
            var key = Path.Combine(Directory, "key");
            RecoveryStore.GenerateKey(key);
            Configuration = OperatorTestData.Configuration() with { RecoveryKeyPath = key, RecoveryKeyBackedUp = true, BackupRetentionCount = 10 };
            native = new NativeBackup(Api);
        }

        private RecoveryStore Store => new(Api, Configuration.Infrastructure.ObjectStorageNamespace,
            Configuration.Infrastructure.RecoveryBucketName, Configuration.RecoveryKeyPath, new Dictionary<string, string>());

        // Setup for tests of later transitions. Initialization scenarios still execute the real command.
        public async Task SeedInitializedAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Empty(Api.Objects);
            var credentials = OperatorTestData.Credentials();
            var state = OperatorTestData.InitializedState(Configuration, credentials);
            state.Validate(Configuration, credentials);
            await Store.WriteAsync($"credentials/{credentials.Generation}", credentials, null, token);
            await Store.WriteAsync("state", state, null, token);
        }

        public async Task AssertInitializedSetupMatchesAsync()
        {
            var actual = await StateAsync();
            var credentials = (await Store.ReadAsync<CredentialCheckpoint>(
                $"credentials/{actual.CredentialGeneration}", TestContext.Current.CancellationToken))!.Value;
            var expected = OperatorTestData.InitializedState(Configuration, credentials) with { OperationId = actual.OperationId };
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        }

        public async Task ExecuteAsync(string name, CancellationToken token, string? backup = null, bool yes = true)
        {
            Api.Configuration = Configuration;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            await new InfrastructureOperator(Directory, Configuration, Api, native, DelayAsync)
                .ExecuteAsync(new OperatorCommand(name, "unused", yes, Backup: backup, AllowAdditionalStorage: backup is not null), deadline.Token);
        }

        private Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(TimeSpan.FromSeconds(5), delay);
            PollDelays.Add(delay);
            return Task.CompletedTask;
        }

        public async Task<LifecycleState> StateAsync() =>
            (await Store.ReadAsync<LifecycleState>("state", TestContext.Current.CancellationToken))!.Value;

        public async Task UnlockAsync()
        {
            var held = await Store.ReadLockAsync(TestContext.Current.CancellationToken);
            await new InfrastructureOperator(Directory, Configuration, Api, native)
                .ExecuteAsync(new OperatorCommand("unlock", "unused", true, Owner: held!.OwnerId), TestContext.Current.CancellationToken);
        }

        public async Task SaveStateAsync(LifecycleState state)
        {
            var store = Store;
            var current = await store.ReadAsync<LifecycleState>("state", TestContext.Current.CancellationToken);
            await store.WriteAsync("state", state, current!.ETag, TestContext.Current.CancellationToken);
        }
    }

    private sealed class NativeBackup(Api api) : INativeDatabaseBackup
    {
        private static readonly string Ca = CreateCa();
        public Task<NativeBackupPreparation> PrepareAsync(Guid operationId, CancellationToken cancellationToken)
        {
            Assert.Equal(0, api.ControllerReplicas);
            return Task.FromResult(new NativeBackupPreparation(operationId, Ca));
        }
        public async Task<NativeBackupManifest> TakeAsync(NativeBackupPreparation preparation,
            Func<CancellationToken, Task> stopDataNode, CancellationToken cancellationToken)
        {
            Assert.Equal(0, api.Replicas["deployment/sample"]);
            Assert.Equal(0, api.Replicas["deployment/nexus"]);
            Assert.Equal(0, api.Replicas["statefulset/graylog"]);
            await stopDataNode(cancellationToken);
            var id = preparation.OperationId;
            return new NativeBackupManifest(id, $"drn-{id:N}", "3.7.0", ["graylog_0"],
                $"/var/lib/postgresql/data/drn-backups/{id:N}.dump", $"/data/drn-backups/{id:N}.archive.gz", "17.7", Ca);
        }
        public async Task RestoreAsync(NativeBackupManifest manifest, string clonedGenerationId,
            Func<CancellationToken, Task> stopDataNode, Func<CancellationToken, Task> startDataNode, CancellationToken cancellationToken)
        {
            NativeDatabaseBackup.ValidateRestoreTarget(manifest, clonedGenerationId);
            Assert.Equal(0, api.ControllerReplicas);
            Assert.Equal(0, api.Replicas["deployment/sample"]);
            Assert.Equal(0, api.Replicas["deployment/nexus"]);
            Assert.Equal(0, api.Replicas["statefulset/graylog"]);
            await stopDataNode(cancellationToken);
            api.RestoredCheckpoints.Add(manifest.OperationId.ToString("N"));
            await startDataNode(cancellationToken);
        }
        public Task PruneAsync(NativeBackupManifest current, IReadOnlyCollection<NativeBackupManifest> obsolete, CancellationToken cancellationToken)
        {
            Assert.DoesNotContain(obsolete, item => item.OperationId == current.OperationId);
            api.NativePruneCount++;
            return Task.CompletedTask;
        }
        private static string CreateCa()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=lifecycle-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var certificate = request.CreateSelfSigned(OperatorTestData.Timestamp.AddDays(-1), OperatorTestData.Timestamp.AddYears(10));
            return certificate.ExportCertificatePem();
        }
    }
}
