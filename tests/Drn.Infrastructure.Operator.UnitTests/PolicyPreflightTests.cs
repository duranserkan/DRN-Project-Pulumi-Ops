using System.Text.Json;
using Drn.Infrastructure.Operator;
using Xunit;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed class PolicyPreflightTests
{
    private const string Kubeconfig = "/private/staging.kubeconfig";
    private const string OracleCloudProfile = "DRN-STAGING";
    private const string OracleCloudProfileVariable = "OCI_CLI_PROFILE";
    private const string ApplicationController = "argocd-application-controller";
    [Fact]
    public async Task EveryWorkerGetsCsiAndHostChecksWithCrossNodePolicyProbes()
    {
        var runner = new ProbeRunner { WorkerCount = 3 };
        await Create(runner, 3).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, runner.Calls.Count(call => call.Contains("csinode")));
        Assert.Equal(3, runner.Calls.Count(call => call.Contains("/proc/sys/vm/max_map_count")));
        var probes = runner.Inputs.Where(input => input.GetProperty("kind").GetString() == "List").ToArray();
        Assert.Equal(3, probes.Length);
        Assert.Equal(3, probes.Select(p => p.GetProperty("items")[0].GetProperty("spec").GetProperty("nodeName").GetString()).Distinct().Count());
        Assert.All(probes, probe => Assert.NotEqual(
            probe.GetProperty("items")[0].GetProperty("spec").GetProperty("nodeName").GetString(),
            probe.GetProperty("items")[1].GetProperty("spec").GetProperty("nodeName").GetString()));
        Assert.Equal(3, runner.Calls.Count(call => call.Contains("delete")));
    }

    [Theory]
    [DataInlineUnit(2, 3, false)]
    [DataInlineUnit(4, 3, false)]
    [DataInlineUnit(3, 3, true)]
    public async Task MissingOrUnreadyWorkersCannotPass(int actual, int expected, bool unready)
    {
        var runner = new ProbeRunner { WorkerCount = actual, LastWorkerUnready = unready };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner, expected)
            .RunAsync(TestContext.Current.CancellationToken));
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task ChecksBothClientsBeforePolicyThenAllowedDeniedAllowedAndCleansUp()
    {
        var runner = new ProbeRunner();
        await Create(runner).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "allowed", "denied", "allowed", "deny-check", "allowed" }, runner.Connections);
        Assert.Empty(runner.RetryDelays);
        var policy = Assert.Single(runner.Inputs, input => input.GetProperty("kind").GetString() == "NetworkPolicy");
        Assert.Equal("server", policy.GetProperty("spec").GetProperty("podSelector").GetProperty("matchLabels").GetProperty("role").GetString());
        Assert.Equal("allowed", policy.GetProperty("spec").GetProperty("ingress")[0].GetProperty("from")[0]
            .GetProperty("podSelector").GetProperty("matchLabels").GetProperty("role").GetString());
        Assert.Contains("delete", runner.Calls[^1]);
        Assert.All(runner.Calls, call =>
        {
            Assert.Equal(CommandArguments.KubectlKubeconfig, call[0]);
            Assert.Equal(Kubeconfig, call[1]);
            Assert.Contains("--request-timeout=30s", call);
            Assert.DoesNotContain(call, argument => argument.Contains("apiVersion", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task ProbeHasNoCredentialsPrivilegeOrWritableRootFilesystem()
    {
        var runner = new ProbeRunner();
        await Create(runner).RunAsync(TestContext.Current.CancellationToken);
        var list = Assert.Single(runner.Inputs, input => input.GetProperty("kind").GetString() == "List");
        Assert.Equal(3, list.GetProperty("items").GetArrayLength());
        foreach (var pod in list.GetProperty("items").EnumerateArray())
        {
            var spec = pod.GetProperty("spec");
            Assert.False(spec.GetProperty("automountServiceAccountToken").GetBoolean());
            Assert.True(spec.GetProperty("securityContext").GetProperty("runAsNonRoot").GetBoolean());
            var container = spec.GetProperty("containers")[0];
            Assert.Equal(PolicyPreflight.ProbeImage, container.GetProperty("image").GetString());
            Assert.True(container.GetProperty("securityContext").GetProperty("readOnlyRootFilesystem").GetBoolean());
            Assert.False(container.GetProperty("securityContext").GetProperty("allowPrivilegeEscalation").GetBoolean());
            Assert.False(container.GetProperty("securityContext").TryGetProperty("privileged", out _));
            Assert.False(spec.TryGetProperty("hostNetwork", out _));
            Assert.DoesNotContain(spec.GetProperty("volumes").EnumerateArray(), volume => volume.TryGetProperty("hostPath", out _));
        }
    }

    [Theory]
    [DataInlineUnit("0")]
    [DataInlineUnit("262143")]
    [DataInlineUnit("invalid")]
    public async Task MissingHostMinimumBlocksPolicyAndWorkloadActivation(string mapCount)
    {
        var runner = new ProbeRunner { MapCount = mapCount };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("vm.max_map_count", error.Message);
        Assert.Empty(runner.Connections);
        Assert.Contains("delete", runner.Calls[^1]);
    }

    [Fact]
    public async Task FailedExecIsNeverAcceptedAsPolicyDenial()
    {
        var runner = new ProbeRunner { DenialExecFails = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Simulated exec transport failure.", error.Message);
        Assert.Equal(new[] { "allowed", "denied", "allowed", "deny-check" }, runner.Connections);
        Assert.Contains("delete", runner.Calls[^1]);
    }

    [Fact]
    public async Task DenialWithADeadServerCannotPass()
    {
        var runner = new ProbeRunner { ServerFailsAfterDenial = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "allowed", "denied", "allowed", "deny-check", "allowed" }, runner.Connections);
        Assert.Contains("delete", runner.Calls[^1]);
    }

    [Fact]
    public async Task InvalidDenialOutputCannotPass()
    {
        var runner = new ProbeRunner { DenialResult = "unknown" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("delete", runner.Calls[^1]);
    }

    [Fact]
    public async Task ReachableForbiddenClientExhaustsTheBoundedPolicyWaitAndFails()
    {
        var runner = new ProbeRunner { DenialResult = "reachable" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("did not block", error.Message);
        Assert.Equal(10, runner.Connections.Count(connection => connection == "deny-check"));
        Assert.Equal(9, runner.RetryDelays.Count);
        Assert.All(runner.RetryDelays, delay => Assert.Equal(TimeSpan.FromSeconds(1), delay));
        Assert.Contains("delete", runner.Calls[^1]);
    }

    [Fact]
    public async Task PolicyBecomingEffectiveStopsRetriesAndRechecksTheAllowedClient()
    {
        var runner = new ProbeRunner();
        runner.DenialResults.Enqueue("reachable");
        runner.DenialResults.Enqueue("reachable");

        await Create(runner).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "allowed", "denied", "allowed", "deny-check", "deny-check", "deny-check", "allowed" },
            runner.Connections);
        Assert.Equal(2, runner.RetryDelays.Count);
        Assert.All(runner.RetryDelays, delay => Assert.Equal(TimeSpan.FromSeconds(1), delay));
        Assert.Contains("delete", runner.Calls[^1]);
    }

    [Fact]
    public async Task CancellationDuringRetryDelayStopsProbesAndStillCleansUp()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new ProbeRunner { DenialResult = "reachable" };
        var pendingDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Create(runner, delayAsync: (delay, token) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(1), delay);
            Assert.True(token.CanBeCanceled);
            delayEntered.SetResult();
            return pendingDelay.Task.WaitAsync(token);
        }).RunAsync(cancellation.Token);

        await Task.WhenAny(delayEntered.Task, run).WaitAsync(TestContext.Current.CancellationToken);
        if (run.IsCompleted) await run;
        Assert.True(delayEntered.Task.IsCompletedSuccessfully);
        var wasWaiting = !run.IsCompleted;
        var attemptsBeforeCancellation = runner.Connections.Count(connection => connection == "deny-check");
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(wasWaiting);
        Assert.Equal(1, attemptsBeforeCancellation);
        Assert.Single(runner.Connections, connection => connection == "deny-check");
        Assert.Contains("delete", runner.Calls[^1]);
        Assert.False(runner.CleanupTokenWasCancelled);
    }

    [Fact]
    public async Task CleanupFailureBlocksAnOtherwiseSuccessfulProbe()
    {
        var runner = new ProbeRunner { CleanupFails = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("could not remove drn-policy-check-", error.Message);
        Assert.Contains("activation remains blocked", error.Message);
    }

    [Fact]
    public async Task CancellationStillUsesAnIndependentCleanupDeadline()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new ProbeRunner { CancelDuringDenial = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(runner).RunAsync(cancellation.Token));
        Assert.Contains("delete", runner.Calls[^1]);
        Assert.False(runner.CleanupTokenWasCancelled);
    }

    [Theory]
    [DataInlineUnit(true, false)]
    [DataInlineUnit(false, true)]
    public async Task MissingCsiOrSystemControllerStopsBeforeCreatingProbe(bool missingCsi, bool missingController)
    {
        var runner = new ProbeRunner { MissingCsi = missingCsi, MissingController = missingController };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
        Assert.Empty(runner.Inputs);
        Assert.DoesNotContain(runner.Calls, call => call.Contains("delete"));
    }

    [Fact]
    public async Task StartupChecksPolicyWhileApplicationControllerIsDeliberatelyFrozen()
    {
        var runner = new ProbeRunner { StoppedController = ApplicationController };
        await Create(runner).RunAsync(TestContext.Current.CancellationToken, allowFrozenApplicationController: true);

        Assert.Equal(new[] { "allowed", "denied", "allowed", "deny-check", "allowed" }, runner.Connections);
        Assert.DoesNotContain(runner.Calls, call => call.Contains("rollout") && call.Contains("statefulset/argocd-application-controller"));
        Assert.Contains(runner.Calls, call => call.Contains("rollout") && call.Contains("deployment/argocd-server"));
        Assert.Contains("delete", runner.Calls[^1]);
    }

    [Theory]
    [DataInlineUnit(ApplicationController, false)]
    [DataInlineUnit("argocd-server", true)]
    [DataInlineUnit("argocd-repo-server", true)]
    [DataInlineUnit("coredns", true)]
    public async Task FrozenExceptionNeverAcceptsAnUnhealthyRunningCheckOrOtherController(string controller, bool startup)
    {
        var runner = new ProbeRunner { StoppedController = controller };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken,
            allowFrozenApplicationController: startup));
        Assert.Empty(runner.Inputs);
    }

    [Fact]
    public async Task StartupStillRequiresAnAvailableApplicationControllerWhenItIsRunning()
    {
        var runner = new ProbeRunner();
        await Create(runner).RunAsync(TestContext.Current.CancellationToken, allowFrozenApplicationController: true);
        Assert.Contains(runner.Calls, call => call.Contains("rollout") && call.Contains("statefulset/argocd-application-controller"));
    }

    [Fact]
    public async Task CleanupFailurePreservesTheOriginalProbeFailure()
    {
        var runner = new ProbeRunner { DenialExecFails = true, CleanupFails = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
        var failures = Assert.IsType<AggregateException>(error.InnerException);
        failures.InnerExceptions.Select(failure => failure.Message).Should().Equal(
            "Simulated exec transport failure.", "Simulated cleanup failure.");
        Assert.Contains("delete", runner.Calls[^1]);
    }

    private static PolicyPreflight Create(ProbeRunner runner, int expectedWorkers = 1,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null) => new(runner,
        new Dictionary<string, string> { [OracleCloudProfileVariable] = OracleCloudProfile }, Kubeconfig, expectedWorkers,
        delayAsync ?? runner.DelayAsync);

    private sealed class ProbeRunner : ICommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public List<JsonElement> Inputs { get; } = [];
        public List<string> Connections { get; } = [];
        public List<TimeSpan> RetryDelays { get; } = [];
        public Queue<string> DenialResults { get; } = new();
        public string MapCount { get; init; } = "262144\n";
        public string DenialResult { get; init; } = "blocked";
        public bool DenialExecFails { get; init; }
        public bool ServerFailsAfterDenial { get; init; }
        public bool CleanupFails { get; init; }
        public bool MissingCsi { get; init; }
        public bool MissingController { get; init; }
        public int WorkerCount { get; init; } = 1;
        public bool LastWorkerUnready { get; init; }
        public string? StoppedController { get; init; }
        public CancellationTokenSource? CancelDuringDenial { get; init; }
        public bool CleanupTokenWasCancelled { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RetryDelays.Add(delay);
            return Task.CompletedTask;
        }

        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, bool displayOutput, CancellationToken cancellationToken,
            string? standardInput = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(CommandArguments.Kubectl, executable);
            Assert.False(displayOutput);
            Assert.Equal(OracleCloudProfile, environment[OracleCloudProfileVariable]);
            CommandArguments.Prefix(arguments, CommandArguments.KubectlKubeconfig, Kubeconfig, "--request-timeout=30s");
            Calls.Add(arguments.ToArray());
            if (standardInput is not null)
            {
                using var input = JsonDocument.Parse(standardInput);
                Inputs.Add(input.RootElement.Clone());
            }
            if (arguments.Contains("delete"))
            {
                CommandArguments.Prefix(arguments.Skip(3).ToArray(), "delete", "namespace");
                CleanupTokenWasCancelled = cancellationToken.IsCancellationRequested;
                if (CleanupFails) throw new InvalidOperationException("Simulated cleanup failure.");
                return Task.FromResult("");
            }
            if (arguments.Contains("get"))
            {
                if (arguments.Contains("nodes")) return Task.FromResult(JsonSerializer.Serialize(new
                {
                    items = Enumerable.Range(0, WorkerCount).Select(index => new
                    {
                        metadata = new { name = WorkerCount == 1 ? "worker" : $"worker-{index}" }, spec = new { },
                        status = new { conditions = new[] { new { type = "Ready", status = LastWorkerUnready && index == WorkerCount - 1 ? "False" : "True" } } }
                    })
                }));
                if (arguments.Contains("csinode")) return Task.FromResult(MissingCsi
                    ? "{\"spec\":{\"drivers\":[]}}"
                    : "{\"spec\":{\"drivers\":[{\"name\":\"blockvolume.csi.oraclecloud.com\"}]}}");
                if (arguments.Contains("csidriver")) return Task.FromResult("blockvolume.csi.oraclecloud.com");
                if (arguments.Contains("deployments,daemonsets,statefulsets"))
                {
                    var names = arguments.Contains("kube-system")
                        ? new[] { "calico-node", "calico-kube-controllers", "coredns" }
                        : new[] { "argocd-server", "argocd-repo-server", "argocd-application-controller" };
                    if (MissingController) names = names.Skip(1).ToArray();
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        items = names.Select(name => new
                        {
                            kind = name == "calico-node" ? "DaemonSet" : name == "argocd-application-controller" ? "StatefulSet" : "Deployment",
                            metadata = new { name }, spec = new { replicas = name == StoppedController ? 0 : 1 }
                        })
                    }));
                }
                if (arguments.Contains("pod")) return Task.FromResult(JsonSerializer.Serialize(new
                {
                    spec = new { nodeName = Inputs.Last(input => input.GetProperty("kind").GetString() == "List")
                        .GetProperty("items")[0].GetProperty("spec").GetProperty("nodeName").GetString() },
                    status = new { podIP = "10.42.2.8" }
                }));
            }
            if (arguments.Contains("exec"))
            {
                if (arguments.Contains("cat")) return Task.FromResult(MapCount);
                if (arguments.Contains("nslookup")) return Task.FromResult("kubernetes.default.svc.cluster.local");
                if (arguments.Contains("sh"))
                {
                    Connections.Add("deny-check");
                    if (DenialExecFails) throw new InvalidOperationException("Simulated exec transport failure.");
                    CancelDuringDenial?.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(DenialResults.TryDequeue(out var result) ? result : DenialResult);
                }
                if (arguments.Contains("wget"))
                {
                    Connections.Add(arguments.Contains("allowed") ? "allowed" : "denied");
                    return Task.FromResult(ServerFailsAfterDenial && Connections.Contains("deny-check") ? "wrong-server" : "drn-policy-ok");
                }
            }
            var action = arguments[3];
            if (action is "wait" or "rollout") return Task.FromResult("");
            if (action == "create")
            {
                CommandArguments.Prefix(arguments.Skip(3).ToArray(), "create", "-f", "-");
                Assert.NotNull(standardInput);
                return Task.FromResult("");
            }
            throw new Xunit.Sdk.XunitException($"Unexpected kubectl command: {string.Join(' ', arguments)}");
        }
    }
}
