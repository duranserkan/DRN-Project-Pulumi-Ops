using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Drn.Infrastructure.Operator;

public sealed class PolicyPreflight(ICommandRunner runner, IReadOnlyDictionary<string, string> environment, string kubeconfig,
    int expectedWorkers = 1, Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
{
    // Official multi-architecture registry index, including linux/arm64/v8.
    public const string ProbeImage = "docker.io/library/busybox:1.37.0@sha256:bdf57e528e45e4433820e045b29b4597825a1c9e38353532d90a01445013f82e";
    private const string Response = "drn-policy-ok";
    private const string CsiDriver = "blockvolume.csi.oraclecloud.com";
    private const string DeniedProbe = "if wget -Y off -T 3 -q -O /dev/null \"$1\"; then printf reachable; else printf blocked; fi";
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delayAsync ?? Task.Delay;

    public async Task RunAsync(CancellationToken token = default, bool allowFrozenApplicationController = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        token = deadline.Token;
        await KubectlAsync(["wait", "nodes", "--all", "--for=condition=Ready", "--timeout=300s"], token);
        using var nodes = JsonDocument.Parse(await KubectlAsync(["get", "nodes", "-o", "json"], token));
        var workers = RequireNodes(nodes.RootElement, expectedWorkers);
        foreach (var node in workers)
        {
            using var csi = JsonDocument.Parse(await KubectlAsync(["get", "csinode", node, "-o", "json"], token));
            if (!csi.RootElement.GetProperty("spec").GetProperty("drivers").EnumerateArray()
                    .Any(driver => driver.GetProperty("name").GetString() == CsiDriver))
                throw new InvalidOperationException($"Worker {node} has not registered the OCI block-volume CSI driver.");
        }
        await KubectlAsync(["get", "csidriver", CsiDriver, "-o", "name"], token);
        await WaitForControllersAsync("kube-system", ["calico-node", "calico-kube-controllers", "coredns"], token);
        await WaitForControllersAsync("argocd", ["argocd-server", "argocd-repo-server", "argocd-application-controller"], token,
            allowFrozenApplicationController);

        for (var index = 0; index < workers.Length; index++)
            await ProbeNodeAsync(workers[index], workers[(index + 1) % workers.Length], token);
    }

    private async Task ProbeNodeAsync(string node, string clientNode, CancellationToken token)
    {
        var probeNamespace = $"drn-policy-check-{Guid.NewGuid():N}";
        Exception? failure = null;
        try
        {
            await KubectlAsync(["create", "-f", "-"], token, JsonSerializer.Serialize(new
            {
                apiVersion = "v1", kind = "Namespace",
                metadata = new
                {
                    name = probeNamespace,
                    labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/managed-by"] = "drn-infra",
                        ["pod-security.kubernetes.io/enforce"] = "restricted"
                    },
                    annotations = new Dictionary<string, string> { ["linkerd.io/inject"] = "disabled" }
                }
            }));
            await KubectlAsync(["create", "-f", "-"], token, ProbePods(probeNamespace, node, clientNode));
            await KubectlAsync(["wait", "pods", "--all", "-n", probeNamespace, "--for=condition=Ready", "--timeout=120s"], token);
            using var server = JsonDocument.Parse(await KubectlAsync(["get", "pod", "server", "-n", probeNamespace, "-o", "json"], token));
            if (server.RootElement.GetProperty("spec").GetProperty("nodeName").GetString() != node ||
                !IPAddress.TryParse(server.RootElement.GetProperty("status").GetProperty("podIP").GetString(), out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException("The policy probe did not start on the expected IPv4 worker.");
            var url = $"http://{address}:8080/";
            var mapCount = await ExecAsync(probeNamespace, "server", ["cat", "/proc/sys/vm/max_map_count"], token);
            if (!long.TryParse(mapCount.Trim(), out var value) || value < 262144)
                throw new InvalidOperationException("The worker requires vm.max_map_count >= 262144 before workloads can start.");
            await ExecAsync(probeNamespace, "allowed", ["nslookup", "kubernetes.default.svc.cluster.local"], token);

            // Establish that both clients and the server work before introducing the policy.
            await RequireReachableAsync(probeNamespace, "allowed", url, token);
            await RequireReachableAsync(probeNamespace, "denied", url, token);
            await KubectlAsync(["create", "-f", "-"], token, ProbePolicy(probeNamespace));
            await RequireReachableAsync(probeNamespace, "allowed", url, token);
            var blocked = false;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                // Only a marker from the container counts as a denial. A failed kubectl exec must fail preflight.
                var result = await ExecAsync(probeNamespace, "denied", ["sh", "-c", DeniedProbe, "probe", url], token);
                if (result == "blocked") { blocked = true; break; }
                if (result != "reachable") throw new InvalidOperationException("The policy probe returned an invalid result.");
                if (attempt < 9) await delay(TimeSpan.FromSeconds(1), token);
            }
            if (!blocked) throw new InvalidOperationException("NetworkPolicy did not block the forbidden client. Workload activation is blocked.");
            await RequireReachableAsync(probeNamespace, "allowed", url, token);
        }
        catch (Exception error)
        {
            failure = error;
        }

        // Cancellation still requires cleanup. Never let an incomplete probe appear successful.
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await KubectlAsync(["delete", "namespace", probeNamespace, "--ignore-not-found=true", "--wait=true", "--timeout=90s"], cleanupDeadline.Token);
        }
        catch (Exception cleanupError)
        {
            throw new InvalidOperationException($"Policy preflight could not remove {probeNamespace}. Workload activation remains blocked.",
                failure is null ? cleanupError : new AggregateException(failure, cleanupError));
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task WaitForControllersAsync(string @namespace, string[] required, CancellationToken token,
        bool allowFrozenApplicationController = false)
    {
        using var document = JsonDocument.Parse(await KubectlAsync(
            ["get", "deployments,daemonsets,statefulsets", "-n", @namespace, "-o", "json"], token));
        var controllers = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        if (required.Any(name => !controllers.Any(item => item.GetProperty("metadata").GetProperty("name").GetString() == name)))
            throw new InvalidOperationException($"Required controllers are missing from {@namespace}.");
        foreach (var controller in controllers)
        {
            var kind = controller.GetProperty("kind").GetString();
            if (kind is not ("Deployment" or "DaemonSet" or "StatefulSet"))
                throw new InvalidOperationException("The controller inventory contains an unexpected resource kind.");
            var name = controller.GetProperty("metadata").GetProperty("name").GetString();
            var spec = controller.GetProperty("spec");
            if (spec.TryGetProperty("replicas", out var replicas) && replicas.GetInt32() < 1)
            {
                // Startup freezes reconciliation. ActivateAsync verifies readiness before submitting any sync.
                if (allowFrozenApplicationController && @namespace == "argocd" && kind == "StatefulSet" &&
                    name == "argocd-application-controller" && replicas.GetInt32() == 0)
                    continue;
                throw new InvalidOperationException($"A controller in {@namespace} has no running replicas.");
            }
            await KubectlAsync(["rollout", "status", $"{kind.ToLowerInvariant()}/{name}", "-n", @namespace, "--timeout=300s"], token);
        }
    }

    private static string[] RequireNodes(JsonElement root, int expected)
    {
        var nodes = root.GetProperty("items");
        if (expected < 1 || nodes.GetArrayLength() != expected)
            throw new InvalidOperationException($"Policy preflight requires exactly {expected} staging workers.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.GetProperty("spec").TryGetProperty("unschedulable", out var unschedulable) && unschedulable.GetBoolean() ||
                !node.GetProperty("status").GetProperty("conditions").EnumerateArray()
                    .Any(condition => condition.GetProperty("type").GetString() == "Ready" && condition.GetProperty("status").GetString() == "True"))
                throw new InvalidOperationException("Every staging worker must be Ready and schedulable.");
            var name = node.GetProperty("metadata").GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                throw new InvalidOperationException("A staging worker name is missing or duplicated.");
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    private async Task RequireReachableAsync(string @namespace, string pod, string url, CancellationToken token)
    {
        var response = await ExecAsync(@namespace, pod, ["wget", "-Y", "off", "-T", "3", "-q", "-O", "-", url], token);
        if (response != Response) throw new InvalidOperationException("The permitted policy-probe connection did not reach its server.");
    }

    private Task<string> ExecAsync(string @namespace, string pod, string[] command, CancellationToken token) =>
        KubectlAsync(["exec", "-n", @namespace, pod, "-c", "probe", "--", .. command], token);

    private Task<string> KubectlAsync(string[] arguments, CancellationToken token, string? input = null) =>
        runner.RunAsync("kubectl", ["--kubeconfig", kubeconfig, "--request-timeout=30s", .. arguments], environment, false, token, standardInput: input);

    private static string ProbePods(string @namespace, string serverNode, string clientNode) => JsonSerializer.Serialize(new
    {
        apiVersion = "v1", kind = "List", items = new[] { "server", "allowed", "denied" }.Select(role => new
        {
            apiVersion = "v1", kind = "Pod",
            metadata = new
            {
                name = role, @namespace, labels = new { role },
                annotations = new Dictionary<string, string> { ["linkerd.io/inject"] = "disabled" }
            },
            spec = new
            {
                nodeName = role == "server" ? serverNode : clientNode,
                automountServiceAccountToken = false, restartPolicy = "Never", activeDeadlineSeconds = 600,
                terminationGracePeriodSeconds = 1,
                securityContext = new { runAsNonRoot = true, runAsUser = 65532, runAsGroup = 65532, fsGroup = 65532, seccompProfile = new { type = "RuntimeDefault" } },
                containers = new[] { new
                {
                    name = "probe", image = ProbeImage,
                    command = role == "server"
                        ? new[] { "sh", "-ec", "printf drn-policy-ok > /tmp/index.html; exec httpd -f -p 8080 -h /tmp" }
                        : new[] { "sleep", "600" },
                    readinessProbe = role == "server"
                        ? new { httpGet = new { path = "/", port = 8080 }, periodSeconds = 1, timeoutSeconds = 1 }
                        : null,
                    securityContext = new { allowPrivilegeEscalation = false, readOnlyRootFilesystem = true, capabilities = new { drop = new[] { "ALL" } } },
                    resources = new { requests = new { cpu = "10m", memory = "16Mi" }, limits = new { cpu = "100m", memory = "32Mi" } },
                    volumeMounts = new[] { new { name = "scratch", mountPath = "/tmp" } }
                } },
                volumes = new[] { new { name = "scratch", emptyDir = new { sizeLimit = "1Mi" } } }
            }
        })
    });

    private static string ProbePolicy(string @namespace) => JsonSerializer.Serialize(new
    {
        apiVersion = "networking.k8s.io/v1", kind = "NetworkPolicy",
        metadata = new { name = "server", @namespace },
        spec = new
        {
            podSelector = new { matchLabels = new { role = "server" } }, policyTypes = new[] { "Ingress" },
            ingress = new[] { new
            {
                from = new[] { new { podSelector = new { matchLabels = new { role = "allowed" } } } },
                ports = new[] { new { protocol = "TCP", port = 8080 } }
            } }
        }
    });
}
