using System.Text.Json;
using System.Text.Json.Nodes;

namespace Drn.Infrastructure.Operator.UnitTests.Support;

internal static class KubernetesKinds
{
    internal const string Pod = "Pod";
    internal const string PersistentVolume = "PersistentVolume";
    internal const string PersistentVolumeClaim = "PersistentVolumeClaim";
    internal const string Deployment = "Deployment";
    internal const string StatefulSet = "StatefulSet";
}

internal static class KubernetesJson
{
    internal static JsonObject Object(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();

    internal static string List(IEnumerable<JsonObject> items) => new JsonObject
    {
        ["items"] = new JsonArray(items.Select(item => item.DeepClone()).ToArray())
    }.ToJsonString();

    internal static JsonObject Controller(string name, string kind, int replicas) => Object(new
    {
        kind,
        metadata = new { name, generation = 1 },
        spec = new { replicas, selector = new { matchLabels = new { app = name } } },
        status = new { observedGeneration = 1, replicas, readyReplicas = replicas }
    });
}
