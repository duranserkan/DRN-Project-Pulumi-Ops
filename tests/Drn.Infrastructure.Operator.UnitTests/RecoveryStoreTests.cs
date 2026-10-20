using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drn.Infrastructure.Operator.Recovery;
using Xunit;
using static System.IO.File;

namespace Drn.Infrastructure.Operator.UnitTests;

[UnsupportedOSPlatform("windows")]
public sealed class RecoveryStoreTests
{
    [Theory]
    [DataInlineUnit]
    public async Task EncryptedRoundTripRejectsStaleWritesAndDoesNotExposeCredentials(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        var first = await fixture.Store.WriteAsync("state", new Payload("credential-value"), null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("credential-value", Encoding.UTF8.GetString(fixture.Runner.Objects[OperatorTestData.StateObject].Content));
        Assert.Equal("credential-value", (await fixture.Store.ReadAsync<Payload>("state", TestContext.Current.CancellationToken))!.Value.Secret);
        var next = await fixture.Store.WriteAsync("state", new Payload("next-value"), first, TestContext.Current.CancellationToken);
        Assert.NotEqual(first, next);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.WriteAsync("state", new Payload("stale"), first, TestContext.Current.CancellationToken));
        Assert.Equal("next-value", (await fixture.Store.ReadAsync<Payload>("state", TestContext.Current.CancellationToken))!.Value.Secret);
    }

    [Theory]
    [DataInlineUnit]
    public async Task ExistingLockExitZeroDoesNotAcquireOrDeleteAnotherOwnersLock(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        var held = await fixture.Store.AcquireAsync(Guid.NewGuid(), "backup", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.AcquireAsync(Guid.NewGuid(), "restore", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReleaseAsync(held with { OwnerId = Guid.NewGuid() }, TestContext.Current.CancellationToken));
        Assert.Equal(held.OwnerId, (await fixture.Store.ReadLockAsync(TestContext.Current.CancellationToken))!.OwnerId);
        await fixture.Store.ReleaseAsync(held, TestContext.Current.CancellationToken);
        Assert.Null(await fixture.Store.ReadLockAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [DataInlineUnit]
    public async Task CiphertextCannotBeSubstitutedUnderAnotherObjectName(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.Store.WriteAsync("state", new Payload("secret"), null, TestContext.Current.CancellationToken);
        fixture.Runner.Objects["staging/other.json"] = fixture.Runner.Objects[OperatorTestData.StateObject];
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReadAsync<Payload>("other", TestContext.Current.CancellationToken));
    }

    [Theory]
    [DataInlineUnit]
    public async Task ReadFailureIsNeverTreatedAsMissingState(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        fixture.Runner.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReadAsync<Payload>("state", TestContext.Current.CancellationToken));
    }

    [Theory]
    [DataInlineUnit]
    public async Task KeyGenerationNeverReplacesAnExistingKeyAndPermissionsAreRequired(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        var original = await ReadAllBytesAsync(fixture.KeyPath, TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(() => RecoveryStore.GenerateKey(fixture.KeyPath));
        Assert.Equal(original, await ReadAllBytesAsync(fixture.KeyPath, TestContext.Current.CancellationToken));
        SetUnixFileMode(fixture.KeyPath, UnixFileMode.None | UnixFileMode.GroupRead | UnixFileMode.UserWrite | UnixFileMode.UserRead);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.WriteAsync("state", new Payload("secret"), null, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Runner.Objects);
    }

    [Theory]
    [DataInlineUnit]
    public async Task ReplacedLockVersionCannotBeReleasedEvenByTheSameOwner(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        var held = await fixture.Store.AcquireAsync(Guid.NewGuid(), "backup", TestContext.Current.CancellationToken);
        var original = fixture.Runner.Objects[OperatorTestData.LockObject];
        fixture.Runner.Objects[OperatorTestData.LockObject] = original with { ETag = "replacement-version" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReleaseAsync(held, TestContext.Current.CancellationToken));

        Assert.Contains("ownership changed", error.Message);
        Assert.Equal("replacement-version", fixture.Runner.Objects[OperatorTestData.LockObject].ETag);
        Assert.Equal(held.OwnerId, (await fixture.Store.ReadLockAsync(TestContext.Current.CancellationToken))!.OwnerId);
    }

    [Theory]
    [DataInlineUnit]
    public async Task AuthenticatedCiphertextRejectsTamperingWithoutReturningPlaintext(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        await fixture.Store.WriteAsync("state", new Payload("private-content"), null, TestContext.Current.CancellationToken);
        var original = fixture.Runner.Objects[OperatorTestData.StateObject];
        var envelope = JsonNode.Parse(original.Content)!.AsObject();
        var ciphertext = Convert.FromBase64String(envelope["ciphertext"]!.GetValue<string>());
        ciphertext[0] ^= 1;
        envelope["ciphertext"] = Convert.ToBase64String(ciphertext);
        fixture.Runner.Objects[OperatorTestData.StateObject] = original with { Content = Encoding.UTF8.GetBytes(envelope.ToJsonString()) };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReadAsync<Payload>("state", TestContext.Current.CancellationToken));

        Assert.Contains("could not be authenticated", error.Message);
        Assert.DoesNotContain("private-content", error.ToString());
    }

    [Theory]
    [DataInlineUnit]
    public async Task PrefixMatchesDoNotSubstituteForTheExactStateObject(DrnTestContextUnit context)
    {
        var fixture = new Fixture(context);
        fixture.Runner.Objects[OperatorTestData.StateObject + ".previous"] = new RemoteObject("old", []);
        Assert.Null(await fixture.Store.ReadAsync<Payload>("state", TestContext.Current.CancellationToken));
    }

    private sealed record Payload(string Secret);
    private sealed class Fixture
    {
        public string KeyPath { get; }
        public ObjectStoreRunner Runner { get; } = new();
        public RecoveryStore Store { get; }
        public Fixture(DrnTestContextUnit context)
        {
            var directory = context.GetTempPath();
            SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            KeyPath = Path.Combine(directory, "key");
            RecoveryStore.GenerateKey(KeyPath);
            Store = new RecoveryStore(Runner, "namespace", "bucket", KeyPath, new Dictionary<string, string>());
        }
    }
}
