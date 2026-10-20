using System.Text.Json;
using System.Text.Json.Nodes;
using Drn.Pulumi;
using Xunit;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed partial class OperatorLifecycleTests
{
    private sealed record CloudBackup(string Id, string Group, DateTimeOffset Created, Dictionary<string, string> Sources, StorageGeneration Profile);
    private sealed class Api : ICommandRunner
    {
        public OperatorConfiguration Configuration { get; set; } = OperatorTestData.Configuration();
        private readonly ObjectStoreRunner objectStore = new();
        public Dictionary<string, RemoteObject> Objects => objectStore.Objects;
        public Dictionary<string, int> Replicas { get; } = [];
        public int ControllerReplicas => Replicas.GetValueOrDefault("statefulset/argocd-application-controller");
        public int Workers { get; private set; }
        public InfrastructureSettings? DeployedCompute { get; private set; }
        public List<int> ControllerReplicasAtBootstrap { get; } = [];
        public List<string> Activations { get; } = [];
        public List<string> RestoredCheckpoints { get; } = [];
        public int NativePruneCount { get; set; }
        public int PolicyProbesCreated { get; private set; }
        public string? ResolvedCommit { get; set; }
        public bool FailNextPolicyProbe { get; set; }
        public bool ReplaceBackupMembers { get; set; }
        public Action<JsonObject>? ConfigureClusterExport { get; set; }
        public Dictionary<string, string> StackExports { get; } = [];
        public Action<string, JsonObject>? ConfigureOutputs { get; set; }
        public bool PartialBootstrap { get; set; }
        public string? FailApply { get; set; }
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];
        public string? FailPolicyOnWorker { get; set; }
        public CancellationTokenSource? CancelPolicyProbe { get; set; }
        public List<(string Worker, string Namespace)> ProbeNamespaces { get; } = [];
        public List<string> CompletedPolicyWorkers { get; } = [];
        public List<string> DeletedProbeNamespaces { get; } = [];
        private readonly Dictionary<string, JsonObject> resources = [];
        private readonly Dictionary<string, InfrastructureSettings> settings = [];
        private readonly Dictionary<string, CloudBackup> backups = [];
        private readonly HashSet<string> verifiedPolicyDenials = [];
        private Dictionary<string, string> volumes = [];
        private string generation = "initial";
        private string probeNode = "worker";
        private bool clusterExists;
        private bool bootstrapExists;
        private string Commit => Configuration.Infrastructure.GitOpsCommit;
        private string Compartment => Configuration.Infrastructure.DurableCompartmentId;
        private const string OracleCloudNodePool = "ocid1.nodepool.oc1..test";
        private const string OracleCloudInstance = "ocid1.instance.oc1..test";

        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, bool displayOutput, CancellationToken cancellationToken, string? standardInput = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((executable, arguments.ToArray()));
            var args = arguments.ToList();
            string Arg(string key) => CommandArguments.Required(args, key);
            if (executable == "pulumi") return Task.FromResult(Pulumi(args));
            if (executable == "git")
            {
                if (args.Contains("clone"))
                {
                    foreach (var path in Bootstrap.RequiredFiles)
                    {
                        var file = Path.Combine(args[^1], path);
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, "infrastructureProfile: oci-retained\nactivationStage: prepare");
                    }
                }
                if (args.Contains("get-url")) return Task.FromResult(Configuration.Infrastructure.GitOpsRepository);
                if (args.Contains("rev-parse")) return Task.FromResult(ResolvedCommit ?? Commit);
                if (args.Contains("clone") || args.Contains("status") || args.Contains("fetch") || args.Contains("checkout")) return Task.FromResult("");
                throw new Xunit.Sdk.XunitException($"Unexpected Git command: {string.Join(' ', args)}");
            }
            if (executable == "kubectl") return Task.FromResult(Kubectl(args, standardInput, cancellationToken));
            if (executable != CommandArguments.OracleCloudCli) throw new Xunit.Sdk.XunitException("Unexpected executable: " + executable);
            if (args.SequenceEqual(["--version"])) return Task.FromResult(InfrastructureOperator.OciCliVersion);
            if (args[0] == "os") return objectStore.RunAsync(executable, arguments, environment, displayOutput, cancellationToken, standardInput);
            if (args.Contains("create-kubeconfig")) { File.WriteAllText(Arg(CommandArguments.OracleCloudFile), "ocid1.cluster.oc1..test"); return Task.FromResult(""); }
            if (args[0] == "bv") return Task.FromResult(VolumeBackup(args));
            CommandArguments.OracleCloudRead(args);
            if (args.Contains("node-pool-options")) return Task.FromResult(Json(new { data = new Dictionary<string, object>
            {
                ["kubernetes-versions"] = new[] { Configuration.Infrastructure.KubernetesVersion }, ["shapes"] = new[] { Configuration.Infrastructure.NodeShape },
                ["sources"] = new[] { new Dictionary<string, object> { ["image-id"] = Configuration.Infrastructure.NodeImageId,
                    ["source-type"] = "IMAGE", ["source-name"] = "Oracle-Linux-8.10-aarch64-OKE-1.36.1" } }
            } }));
            if (args.Contains("image")) return Task.FromResult(Json(new { data = new Dictionary<string, object>
            { ["id"] = Configuration.Infrastructure.NodeImageId, ["lifecycle-state"] = "AVAILABLE", ["operating-system"] = "Oracle Linux",
                ["operating-system-version"] = "8.10", ["display-name"] = "Oracle-Linux-8.10-aarch64-OKE-1.36.1", ["size-in-mbs"] = 50 * 1024 } }));
            if (args.Contains("shape")) return Task.FromResult(new JsonObject
            { ["data"] = new JsonArray(OracleCloudMetadata.Shape(Configuration.Infrastructure)) }.ToJsonString());
            if (args.Contains("image-shape-compatibility-entry")) return Task.FromResult(new JsonObject
            { ["data"] = OracleCloudMetadata.ImageCompatibility(Configuration.Infrastructure) }.ToJsonString());
            if (args.Contains("node-pool")) return Task.FromResult(Json(new { data = new Dictionary<string, object>
            { ["id"] = OracleCloudNodePool, ["compartment-id"] = Configuration.Infrastructure.ClusterCompartmentId,
                ["nodes"] = new[] { new Dictionary<string, object> { ["id"] = OracleCloudInstance, ["node-pool-id"] = OracleCloudNodePool, ["lifecycle-state"] = "ACTIVE" } } } }));
            if (args.Contains("instance")) return Task.FromResult(Json(new { data = new Dictionary<string, object>
            { ["id"] = OracleCloudInstance, ["compartment-id"] = Configuration.Infrastructure.ClusterCompartmentId, ["lifecycle-state"] = Workers == 0 ? "TERMINATED" : "RUNNING" } }));
            if (args.Contains("volume-attachment")) { Assert.Equal(0, Workers); return Task.FromResult("{\"data\":[]}"); }
            throw new Xunit.Sdk.XunitException("Unexpected OCI: " + string.Join(' ', args));
        }

        private string Pulumi(List<string> args)
        {
            CommandArguments.Prefix(args, CommandArguments.PulumiWorkingDirectory);
            _ = CommandArguments.Required(args, CommandArguments.PulumiWorkingDirectory);
            args.RemoveRange(0, 2);
            var role = CommandArguments.Required(args, CommandArguments.Stack).Split('-')[^1];
            if (args.Take(2).SequenceEqual(["stack", "export"]))
            {
                if (StackExports.TryGetValue(role, out var checkpoint)) return checkpoint;
                if (role == "bootstrap" && (bootstrapExists || PartialBootstrap))
                    return "{\"deployment\":{\"resources\":[{\"type\":\"kubernetes:core/v1:Namespace\"}]}}";
                if (role != "cluster" || !clusterExists) return "{}";
                var deployed = DeployedCompute!;
                var exported = Node(new { deployment = new { resources = new[] { new
                {
                    type = "oci:ContainerEngine/nodePool:NodePool", inputs = new
                    {
                        nodeShape = deployed.NodeShape, kubernetesVersion = deployed.KubernetesVersion,
                        compartmentId = deployed.ClusterCompartmentId,
                        nodeShapeConfig = new { ocpus = deployed.NodeOcpus, memoryInGbs = deployed.NodeMemoryInGbs },
                        nodeSourceDetails = new { imageId = deployed.NodeImageId, bootVolumeSizeInGbs = deployed.BootVolumeSizeInGbs.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                        nodeConfigDetails = new
                        {
                            placementConfigs = new[] { new { availabilityDomain = deployed.AvailabilityDomain, faultDomains = deployed.FaultDomains } },
                            nodePoolPodNetworkOptionDetails = new { maxPodsPerNode = deployed.MaxPodsPerNode }
                        }
                    }
                } } } });
                ConfigureClusterExport?.Invoke(exported);
                return exported.ToJsonString();
            }
            if (args.Take(2).SequenceEqual(["stack", "output"]))
            {
                var output = role switch
                {
                    "recovery" => Node(new { bucketName = "drn-recovery", @namespace = "namespace", region = "eu-frankfurt-1" }),
                    "durable" when volumes.Count > 0 => Node(new { volumeIds = volumes, volumeGroupId = "ocid1.volumegroup.oc1.." + generation, storageGeneration = generation }),
                    "cluster" when clusterExists => Node(new { clusterId = "ocid1.cluster.oc1..test", nodePoolId = OracleCloudNodePool, podNetwork = "OCI_VCN_IP_NATIVE", workers = Workers }),
                    _ => new JsonObject()
                };
                ConfigureOutputs?.Invoke(role, output);
                return output.ToJsonString();
            }
            if (args[0] == "config")
            {
                CommandArguments.Prefix(args, "config", "set", "drn-pulumi:settings");
                settings[role] = JsonSerializer.Deserialize<InfrastructureSettings>(args[3], OperatorConfiguration.JsonOptions)!;
                return "";
            }
            if (args[0] == "destroy")
            {
                Assert.Equal(0, Workers);
                if (role == "bootstrap") { bootstrapExists = false; resources.Clear(); Replicas.Clear(); }
                if (role == "cluster") clusterExists = false;
                return "";
            }
            if (args[0] == "up")
            {
                if (FailApply == role) throw new InvalidOperationException("Simulated stack apply failure.");
                if (role == "durable")
                {
                    generation = settings[role].ActiveStorageGeneration;
                    volumes = StorageClaims.All.ToDictionary(c => c.Key, c => $"ocid1.volume.oc1..{generation}-{c.Key}");
                }
                if (role == "cluster") { clusterExists = true; Workers = settings[role].Workers; DeployedCompute = settings[role]; }
                if (role == "bootstrap")
                {
                    // Pulumi does not repair a controller scaled outside its unchanged Helm release.
                    if (!bootstrapExists) Replicas["statefulset/argocd-application-controller"] = 1;
                    ControllerReplicasAtBootstrap.Add(ControllerReplicas);
                    bootstrapExists = true;
                    var root = Node(new { apiVersion = "argoproj.io/v1alpha1", kind = "Application", metadata = new { name = "drn-project", @namespace = "argocd" },
                        spec = new { project = "drn-project-bootstrap", source = new { targetRevision = Commit, helm = new { parameters = new[]
                        { new { name = "infrastructureProfile", value = "oci-retained" }, new { name = "activationStage", value = "prepare" } } } } } });
                    Store(root);
                    foreach (var claim in StorageClaims.All) Store(Node(new { kind = KubernetesKinds.PersistentVolume, metadata = new { name = "drn-staging-" + claim.Key }, spec = new
                    { csi = new { driver = "blockvolume.csi.oraclecloud.com", volumeHandle = volumes[claim.Key] }, storageClassName = StorageClaims.StorageClass,
                        persistentVolumeReclaimPolicy = "Retain", volumeMode = "Filesystem", claimRef = new { name = claim.ClaimName, @namespace = claim.Namespace, uid = "claim-" + claim.Key } } }));
                }
                return "";
            }
            if (args[0] == "preview") return "";
            throw new Xunit.Sdk.XunitException("Unexpected Pulumi: " + string.Join(' ', args));
        }

        private string VolumeBackup(List<string> args)
        {
            string Arg(string key) => CommandArguments.Required(args, key);
            if (args[1] == "volume-group")
            {
                CommandArguments.Prefix(args, "bv", "volume-group", "get");
                return Json(new { data = new Dictionary<string, object>
                { ["id"] = Arg(CommandArguments.OracleCloudVolumeGroupId), ["lifecycle-state"] = "AVAILABLE", ["compartment-id"] = Compartment, ["volume-ids"] = volumes.Values } });
            }
            if (args[2] == "create")
            {
                CommandArguments.Prefix(args, "bv", "volume-group-backup", "create");
                Assert.Equal(0, Workers);
                Assert.Equal(0, ControllerReplicas);
                var id = "ocid1.volumegroupbackup.oc1.." + Guid.NewGuid().ToString("N");
                backups[id] = new CloudBackup(id, Arg(CommandArguments.OracleCloudVolumeGroupId), DateTimeOffset.UtcNow, new(volumes), settings["durable"].ActiveStorage);
                return Json(new { data = new { id } });
            }
            if (args[1] == "volume-group-backup")
            {
                CommandArguments.Prefix(args, "bv", "volume-group-backup", "get");
                var item = backups[Arg(CommandArguments.OracleCloudVolumeGroupBackupId)];
                return Json(new { data = new Dictionary<string, object> { ["id"] = item.Id, ["lifecycle-state"] = "AVAILABLE", ["compartment-id"] = Compartment,
                    ["volume-group-id"] = item.Group, ["time-created"] = item.Created,
                    ["volume-backup-ids"] = item.Sources.Keys.Select(key => item.Id.Replace("volumegroupbackup", "volumebackup", StringComparison.Ordinal) + ":" + key + (ReplaceBackupMembers ? "-replacement" : "")) } });
            }
            CommandArguments.Prefix(args, "bv", "backup", "get");
            var member = Arg(CommandArguments.OracleCloudVolumeBackupId);
            var parts = member.Split(':');
            var sourceBackup = backups[parts[0].Replace("volumebackup", "volumegroupbackup", StringComparison.Ordinal)];
            var claimKey = parts[1].Replace("-replacement", "", StringComparison.Ordinal);
            return Json(new { data = new Dictionary<string, object> { ["id"] = member, ["lifecycle-state"] = "AVAILABLE", ["compartment-id"] = Compartment,
                ["size-in-gbs"] = sourceBackup.Profile.Volume(claimKey).SizeInGbs, ["volume-id"] = sourceBackup.Sources[claimKey] } });
        }

        private string Kubectl(List<string> args, string? input, CancellationToken token)
        {
            if (args[0] == CommandArguments.KubectlKubeconfig) args.RemoveRange(0, 2);
            args.RemoveAll(arg => arg.StartsWith("--request-timeout=", StringComparison.Ordinal));
            var ns = args.Contains("-n") ? CommandArguments.Required(args, "-n") : "";
            if (args[0] is "wait" or "rollout") return "";
            if (args[0] == "scale") { Replicas[args[1]] = int.Parse(args[^1].Split('=')[1]); return ""; }
            if (args[0] == "annotate") return "";
            if (args[0] == "exec")
            {
                if (args.Contains("sh") && FailNextPolicyProbe) { FailNextPolicyProbe = false; throw new InvalidOperationException("Injected policy failure."); }
                if (args.Contains("sh") && probeNode == FailPolicyOnWorker)
                {
                    CancelPolicyProbe?.Cancel();
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException($"Injected policy failure on {probeNode}.");
                }
                if (args.Contains("sh")) verifiedPolicyDenials.Add(ns);
                if (args.Contains("wget") && args.Contains("allowed") && verifiedPolicyDenials.Remove(ns))
                    CompletedPolicyWorkers.Add(probeNode);
                if (args.Contains("/proc/sys/vm/max_map_count")) return "262144";
                if (args.Contains("sh")) return "blocked";
                if (args.Contains("wget")) return "drn-policy-ok";
                if (args.Contains("nslookup")) return "resolved";
                throw new Xunit.Sdk.XunitException($"Unexpected exec command: {string.Join(' ', args)}");
            }
            if (args[0] == "get")
            {
                if (args[1] == "nodes") return Json(new { items = Enumerable.Range(0, Workers).Select(i => new
                { metadata = new { name = Workers == 1 ? "worker" : $"worker-{i}" }, spec = new { }, status = new { conditions = new[] { new { type = "Ready", status = "True" } } } }) });
                if (args[1] == "csinode") return Json(new { spec = new { drivers = new[] { new { name = "blockvolume.csi.oraclecloud.com" } } } });
                if (args[1] == "csidriver") return "blockvolume.csi.oraclecloud.com";
                if (args[1] == "deployments,daemonsets,statefulsets") return List((ns == "argocd" ? new[] { "argocd-server", "argocd-repo-server", "argocd-application-controller" } : new[] { "calico-node", "calico-kube-controllers", "coredns" })
                    .Select(name => Controller(name, name == "argocd-application-controller" ? KubernetesKinds.StatefulSet : KubernetesKinds.Deployment, name == "argocd-application-controller" ? ControllerReplicas : 1)));
                if (args[1] == "crd") return bootstrapExists ? "{}" : "";
                if (args[1] == "applications") return List(resources.Values.Where(jsonObject => Text(jsonObject, "kind") == "Application"));
                if (args[1] == "pods") return "{\"items\":[]}";
                if (args[1] == "pvc" && args.Contains("-A")) return List(resources.Where(p => p.Key.StartsWith("persistentvolumeclaim/", StringComparison.Ordinal)).Select(p => p.Value));
                if (args[1] == "deployments") return List([Controller("mongodb-operator", KubernetesKinds.Deployment, Replicas.GetValueOrDefault("deployment/mongodb-operator", 1))]);
                if (args[1] == "pod" && args[2] == "server") return Json(new { spec = new { nodeName = probeNode }, status = new { podIP = "10.0.0.1" } });
                if (resources.TryGetValue(Key(args[1], ns, args[2]), out var item)) return item.ToJsonString();
                if (args[1] is "deployment" or "statefulset") return Controller(args[2], args[1] == "deployment" ? KubernetesKinds.Deployment : KubernetesKinds.StatefulSet, Replicas.GetValueOrDefault(args[1] + "/" + args[2], 1)).ToJsonString();
                args[1].ToLowerInvariant().Should().BeOneOf("secret", "configmap", "cluster.postgresql.cnpg.io", "pvc", "pv", "pod");
                args.Should().Contain(CommandArguments.KubectlIgnoreNotFound, "only an optional read can report a missing resource");
                return "";
            }
            if (args[0] is "create" or "apply")
            {
                var item = JsonNode.Parse(input!)!.AsObject();
                if (Text(item, "kind") == "List")
                {
                    probeNode = item["items"]![0]!["spec"]!["nodeName"]!.GetValue<string>();
                    ProbeNamespaces.Add((probeNode, item["items"]![0]!["metadata"]!["namespace"]!.GetValue<string>()));
                    return "";
                }
                if (Text(item, "kind") == "NetworkPolicy") PolicyProbesCreated++;
                if (Text(item, "kind") == "Cluster") item["metadata"]!["uid"] = "postgres-uid";
                Store(item);
                return "";
            }
            if (args[0] == "replace") { Store(JsonNode.Parse(input!)!.AsObject()); return ""; }
            if (args[0] == "delete")
            {
                if (args[1] == "namespace")
                {
                    token.IsCancellationRequested.Should().BeFalse("cleanup must use an independent deadline");
                    DeletedProbeNamespaces.Add(args[2]);
                    return "";
                }
                var path = args[2].Split('/');
                var key = Key(path[5], path[4], path[6]);
                Assert.Equal(resources[key]["metadata"]!["uid"]!.GetValue<string>(), JsonNode.Parse(input!)!["preconditions"]!["uid"]!.GetValue<string>());
                resources.Remove(key);
                return "";
            }
            if (args[0] == "patch")
            {
                var patch = JsonNode.Parse(input!)!.AsObject();
                if (args[1] == "appproject") return "";
                var item = resources[Key(args[1], ns, args[2])];
                if (patch["spec"]?["source"]?["helm"]?["parameters"] is JsonArray parameters)
                    item["spec"]!["source"]!["helm"]!["parameters"] = parameters.DeepClone();
                if (patch["metadata"]?["annotations"] is JsonObject annotations)
                    foreach (var pair in annotations) item["metadata"]!["annotations"]![pair.Key] = pair.Value?.DeepClone();
                if (patch["operation"] is JsonObject operation)
                {
                    Assert.Equal(1, ControllerReplicas);
                    item["status"] = Node(new { sync = new { status = "Synced" }, health = new { status = "Healthy" },
                        operationState = new { phase = "Succeeded", operation, syncResult = new { revision = Commit } } });
                    if (args[2] == "drn-project")
                    {
                        var stage = item["spec"]!["source"]!["helm"]!["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "activationStage")!["value"]!.GetValue<string>();
                        Activations.Add(stage);
                        Reconcile(stage);
                    }
                }
                return "";
            }
            throw new Xunit.Sdk.XunitException("Unexpected kubectl: " + string.Join(' ', args));
        }

        private void Reconcile(string stage)
        {
            foreach (var claim in StorageClaims.All)
            {
                var item = Node(new { apiVersion = "v1", kind = KubernetesKinds.PersistentVolumeClaim, metadata = new { name = claim.ClaimName, @namespace = claim.Namespace, uid = "claim-" + claim.Key },
                    spec = new { storageClassName = StorageClaims.StorageClass, volumeName = "drn-staging-" + claim.Key } });
                if (claim.Key == "postgresql")
                {
                    item["metadata"]!["labels"] = Node(new Dictionary<string, string> { ["cnpg.io/cluster"] = "postgresql", ["cnpg.io/instanceName"] = "postgresql-1", ["cnpg.io/pvcRole"] = "PG_DATA" });
                    item["metadata"]!["annotations"] = Node(new Dictionary<string, string> { ["cnpg.io/nodeSerial"] = "1", ["cnpg.io/pvcStatus"] = "ready" });
                    item["metadata"]!["ownerReferences"] = JsonSerializer.SerializeToNode(new[] { new { apiVersion = "postgresql.cnpg.io/v1", kind = "Cluster", name = "postgresql", uid = "postgres-uid" } });
                }
                Store(item);
            }
            if (!resources.ContainsKey(Key("cluster", "drn-project", "postgresql"))) Store(JsonNode.Parse("""
                {"apiVersion":"postgresql.cnpg.io/v1","kind":"Cluster","metadata":{"name":"postgresql","namespace":"drn-project","uid":"postgres-uid","annotations":{}},"spec":{"instances":1,"storage":{"storageClass":"drn-retained"},"bootstrap":{"initdb":{"secret":{"name":"postgresql-app"}}},"certificates":{"serverCASecret":"postgresql-ca","clientCASecret":"postgresql-ca"}},"status":{"currentPrimary":"postgresql-1","targetPrimary":"postgresql-1","latestGeneratedNode":1}}
                """)!.AsObject());
            foreach (var name in new[] { "sample", "nexus" })
            {
                Replicas["deployment/" + name] = stage == "workloads" ? 1 : 0;
                Store(Node(new { kind = "Application", metadata = new { name, @namespace = "argocd" }, spec = new { source = new { targetRevision = Commit,
                    kustomize = new { patches = new[] { new { target = new { kind = KubernetesKinds.Deployment, name }, patch = "- op: add\n  path: /spec/replicas\n  value: 0" } } } } } }));
            }
            foreach (var name in new[] { "mongodb", "graylog-datanode", "graylog" }) Replicas["statefulset/" + name] = 1;
        }
        private void Store(JsonObject item)
        {
            if (item["metadata"] is JsonObject metadata)
                resources[Key(Text(item, "kind"), Text(metadata, "namespace"), Text(metadata, "name"))] = item;
        }
        private static string Key(string kind, string ns, string name) => (kind.ToLowerInvariant() switch
        { "cluster.postgresql.cnpg.io" => "cluster", "pvc" or "persistentvolumeclaims" => "persistentvolumeclaim", "pv" => "persistentvolume", var value => value }) + "/" + ns + "/" + name;
        private static string Text(JsonNode item, string key) => item[key]?.GetValue<string>() ?? "";
        private static string Json(object value) => JsonSerializer.Serialize(value);
        private static JsonObject Node(object value) => KubernetesJson.Object(value);
        private static string List(IEnumerable<JsonObject> values) => KubernetesJson.List(values);
        private static JsonObject Controller(string name, string kind, int replicas) => KubernetesJson.Controller(name, kind, replicas);
    }
}
