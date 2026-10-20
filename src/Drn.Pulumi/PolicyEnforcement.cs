using System.Collections.Immutable;
using Pulumi;
using Pulumi.Kubernetes.Yaml;
using Kubernetes = Pulumi.Kubernetes;

namespace Drn.Pulumi;

public static class PolicyEnforcement
{
    // Oracle tests this pair with Kubernetes 1.36 and OCI VCN-native pod networking.
    // https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengsettingupcalico.htm
    public const string CalicoVersion = "3.32.0";
    public const string ManifestUrl = "https://raw.githubusercontent.com/projectcalico/calico/eb1cf57823a1dd8d25c48b82fd023ea9e3e17996/manifests/calico-policy-only.yaml";
    public const string NodeImage = "quay.io/calico/node:v3.32.0@sha256:f4fafd8ba641d96c5a91b01e5a519117d77d55dee789a3562ba3ad4aa125b36a";
    public const string ControllerImage = "quay.io/calico/kube-controllers:v3.32.0@sha256:adf0ac895796d21bca5383bc81c4cd2614be3a4308085b47857d7999f4cc2b1f";

    // Configure the host before OKE registers it. Preserve Oracle's required bootstrap logic.
    // https://docs.oracle.com/en-us/iaas/Content/ContEng/Tasks/contengusingcustomcloudinitscripts.htm
    public const string CloudInit = """
        #!/bin/bash
        set -euo pipefail
        drn_max_map_count="$(sysctl -n vm.max_map_count)"
        if [ "$drn_max_map_count" -lt 262144 ]; then drn_max_map_count=262144; fi
        printf 'vm.max_map_count=%s\n' "$drn_max_map_count" > /etc/sysctl.d/99-drn-graylog.conf
        sysctl -p /etc/sysctl.d/99-drn-graylog.conf
        curl --fail -H "Authorization: Bearer Oracle" -L0 http://169.254.169.254/opc/v2/instance/metadata/oke_init_script | base64 --decode >/var/run/oke-init.sh
        bash /var/run/oke-init.sh
        """;

    public static ComponentResource Create(Kubernetes.Provider provider, params Resource[] dependencies) =>
        new ConfigFile("calico-policy", new ConfigFileArgs
        {
            File = ManifestUrl,
            Transformations = { (resource, _) => Prepare(resource) }
        }, new ComponentResourceOptions { Providers = { provider }, DependsOn = dependencies });

    public static ImmutableDictionary<string, object> Prepare(ImmutableDictionary<string, object> resource)
    {
        var name = (string)Map(resource, "metadata")["name"];
        // This small pool uses direct API access and OCI CNI, without Typha or Calico's CNI installer.
        if (name is "calico-typha" or "calico-cni-plugin" or "calico-config")
            return new Dictionary<string, object>
            {
                ["apiVersion"] = "v1", ["kind"] = "List", ["items"] = Array.Empty<object>()
            }.ToImmutableDictionary();
        if (Equals(resource["kind"], "DaemonSet") && name == "calico-node")
            return PrepareNode(resource);
        if (Equals(resource["kind"], "Deployment") && name == "calico-kube-controllers")
            return PrepareController(resource);
        return resource;
    }

    private static ImmutableDictionary<string, object> PrepareNode(ImmutableDictionary<string, object> resource)
    {
        var spec = Map(resource, "spec");
        var template = Map(spec, "template");
        var pod = Map(template, "spec");
        var node = SingleContainer(pod, "calico-node", "quay.io/calico/node:v3.32.0");
        var environment = Sequence(node, "env").Where(value => !Equals(value["name"], "FELIX_TYPHAK8SSERVICENAME"))
            .ToDictionary(value => (string)value["name"], value => value);
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["FELIX_INTERFACEPREFIX"] = "oci", ["NO_DEFAULT_POOLS"] = "true",
            ["FELIX_CHAININSERTMODE"] = "Append", ["FELIX_IPTABLESMANGLEALLOWACTION"] = "Return",
            ["FELIX_IPTABLESBACKEND"] = "NFT", ["FELIX_BPFENABLED"] = "false"
        })
            environment[key] = new Dictionary<string, object> { ["name"] = key, ["value"] = value }.ToImmutableDictionary();
        node = node.Remove("envFrom").SetItem("image", NodeImage)
            .SetItem("env", environment.Values.Cast<object>().ToImmutableArray())
            .SetItem("volumeMounts", Sequence(node, "volumeMounts")
                .Where(value => !IsUnusedVolume(value)).Cast<object>().ToImmutableArray());
        pod = pod.Remove("initContainers")
            .SetItem("containers", new object[] { node }.ToImmutableArray())
            .SetItem("volumes", Sequence(pod, "volumes")
                .Where(value => !IsUnusedVolume(value)).Cast<object>().ToImmutableArray());
        return resource.SetItem("spec", spec.SetItem("template", DisableInjection(template).SetItem("spec", pod)));
    }

    private static ImmutableDictionary<string, object> PrepareController(ImmutableDictionary<string, object> resource)
    {
        var spec = Map(resource, "spec");
        var template = Map(spec, "template");
        var pod = Map(template, "spec");
        var controller = SingleContainer(pod, "calico-kube-controllers", "quay.io/calico/kube-controllers:v3.32.0")
            .SetItem("image", ControllerImage);
        return resource.SetItem("spec", spec.SetItem("template", DisableInjection(template)
            .SetItem("spec", pod.SetItem("containers", new object[] { controller }.ToImmutableArray()))));
    }

    private static ImmutableDictionary<string, object> DisableInjection(ImmutableDictionary<string, object> template)
    {
        var metadata = Map(template, "metadata");
        var annotations = metadata.TryGetValue("annotations", out var existing)
            ? (ImmutableDictionary<string, object>)existing : ImmutableDictionary<string, object>.Empty;
        return template.SetItem("metadata", metadata.SetItem("annotations", annotations.SetItem("linkerd.io/inject", "disabled")));
    }

    private static ImmutableDictionary<string, object> SingleContainer(ImmutableDictionary<string, object> pod, string name, string image)
    {
        var containers = Sequence(pod, "containers").ToArray();
        if (containers.Length != 1 || !Equals(containers[0]["name"], name) || !Equals(containers[0]["image"], image))
            throw new InvalidOperationException("The pinned Calico manifest no longer matches its reviewed container contract.");
        return containers[0];
    }

    private static bool IsUnusedVolume(ImmutableDictionary<string, object> value) =>
        (string)value["name"] is "cni-bin-dir" or "cni-net-dir" or "cni-log-dir" or "bpffs" or "sys-fs" or "nodeproc";

    private static IEnumerable<ImmutableDictionary<string, object>> Sequence(ImmutableDictionary<string, object> resource, string key) =>
        ((IEnumerable<object>)resource[key]).Select(value => (ImmutableDictionary<string, object>)value);

    private static ImmutableDictionary<string, object> Map(ImmutableDictionary<string, object> resource, string key) =>
        (ImmutableDictionary<string, object>)resource[key];
}
