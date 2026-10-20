using System.Collections.Immutable;
using Drn.Pulumi;
using Xunit;

namespace Drn.Pulumi.UnitTests;

public sealed class PolicyEnforcementTests
{
    [Fact]
    public void NodeDoesNotInstallOrReplaceTheOciCni()
    {
        var resource = PolicyEnforcement.Prepare(Node());
        var pod = Map(Map(resource, "spec"), "template");
        Assert.Equal("disabled", Map(Map(pod, "metadata"), "annotations")["linkerd.io/inject"]);
        pod = Map(pod, "spec");
        Assert.False(pod.ContainsKey("initContainers"));
        var node = Assert.Single(Sequence(pod, "containers"));
        Assert.Equal(PolicyEnforcement.NodeImage, node["image"]);
        Assert.False(node.ContainsKey("envFrom"));
        Assert.Equal(new[] { "lib-modules", "var-run-calico" }, Sequence(pod, "volumes").Select(value => value["name"]));
        Assert.Equal(new[] { "lib-modules", "var-run-calico" }, Sequence(node, "volumeMounts").Select(value => value["name"]));
        var env = Sequence(node, "env").ToDictionary(value => (string)value["name"], value => (string)value["value"]);
        Assert.False(env.ContainsKey("FELIX_TYPHAK8SSERVICENAME"));
        Assert.Equal("none", env["CALICO_NETWORKING_BACKEND"]);
        Assert.Equal("false", env["CALICO_MANAGE_CNI"]);
        Assert.Equal("oci", env["FELIX_INTERFACEPREFIX"]);
        Assert.Equal("true", env["NO_DEFAULT_POOLS"]);
        Assert.Equal("Append", env["FELIX_CHAININSERTMODE"]);
        Assert.Equal("Return", env["FELIX_IPTABLESMANGLEALLOWACTION"]);
        Assert.Equal("NFT", env["FELIX_IPTABLESBACKEND"]);
        Assert.Equal("false", env["FELIX_BPFENABLED"]);
        Assert.Equal(true, Map(node, "securityContext")["privileged"]);
        Assert.True(node.ContainsKey("readinessProbe"));
    }

    [Theory]
    [DataInlineUnit("Deployment", "calico-typha")]
    [DataInlineUnit("Service", "calico-typha")]
    [DataInlineUnit("PodDisruptionBudget", "calico-typha")]
    [DataInlineUnit("ServiceAccount", "calico-cni-plugin")]
    [DataInlineUnit("ClusterRole", "calico-cni-plugin")]
    [DataInlineUnit("ClusterRoleBinding", "calico-cni-plugin")]
    [DataInlineUnit("ConfigMap", "calico-config")]
    public void UnusedCniAndTyphaResourcesAreOmitted(string kind, string name)
    {
        var result = PolicyEnforcement.Prepare(Dictionary(("kind", kind), ("metadata", Dictionary(("name", name)))));
        Assert.Equal("v1", result["apiVersion"]);
        Assert.Equal("List", result["kind"]);
        Assert.Empty((object[])result["items"]);
    }

    [Fact]
    public void ControllerUsesVerifiedImageWithoutChangingItsHealthProbes()
    {
        var resource = Controller();
        var originalPod = Map(Map(Map(resource, "spec"), "template"), "spec");
        var originalProbe = Map(Assert.Single(Sequence(originalPod, "containers")), "readinessProbe");
        var result = PolicyEnforcement.Prepare(resource);
        var template = Map(Map(result, "spec"), "template");
        var container = Assert.Single(Sequence(Map(template, "spec"), "containers"));
        Assert.Equal(PolicyEnforcement.ControllerImage, container["image"]);
        Assert.Equal(originalProbe, Map(container, "readinessProbe"));
        Assert.Equal("disabled", Map(Map(template, "metadata"), "annotations")["linkerd.io/inject"]);
    }

    [Fact]
    public void UnexpectedContainerInThePinnedInputFailsClosed()
    {
        var resource = Node();
        var spec = Map(resource, "spec");
        var template = Map(spec, "template");
        var pod = Map(template, "spec");
        var node = Assert.Single(Sequence(pod, "containers")).SetItem("image", "unreviewed-image");
        resource = resource.SetItem("spec", spec.SetItem("template", template.SetItem("spec", pod.SetItem("containers", Array(node)))));
        Assert.Throws<InvalidOperationException>(() => PolicyEnforcement.Prepare(resource));
    }

    [Theory]
    [DataInlineUnit("CustomResourceDefinition", "networkpolicies.crd.projectcalico.org")]
    [DataInlineUnit("ClusterRole", "calico-node")]
    [DataInlineUnit("ClusterRoleBinding", "calico-node")]
    public void CalicoCrdAndSharedRbacRemainUnchanged(string kind, string name)
    {
        var resource = Dictionary(("kind", kind), ("metadata", Dictionary(("name", name))));
        Assert.Same(resource, PolicyEnforcement.Prepare(resource));
    }

    [Fact]
    public void HostConfigurationPersistsTheMinimumBeforeTheRequiredOkeBootstrap()
    {
        var script = PolicyEnforcement.CloudInit;
        Assert.StartsWith("#!/bin/bash\nset -euo pipefail\n", script);
        Assert.Contains("sysctl -n vm.max_map_count", script);
        Assert.Contains("if [ \"$drn_max_map_count\" -lt 262144 ]; then drn_max_map_count=262144; fi", script);
        Assert.Contains("printf 'vm.max_map_count=%s\\n' \"$drn_max_map_count\" > /etc/sysctl.d/99-drn-graylog.conf", script);
        Assert.Contains("curl --fail -H \"Authorization: Bearer Oracle\" -L0 http://169.254.169.254/opc/v2/instance/metadata/oke_init_script | base64 --decode >/var/run/oke-init.sh", script);
        var configure = script.IndexOf("sysctl -p", StringComparison.Ordinal);
        var bootstrap = script.IndexOf("bash /var/run/oke-init.sh", StringComparison.Ordinal);
        configure.Should().BeGreaterThanOrEqualTo(0, "the persistent sysctl settings must be applied");
        bootstrap.Should().BeGreaterThan(configure, "OKE bootstrap must follow host configuration");
        Assert.EndsWith("bash /var/run/oke-init.sh", script);
    }

    private static ImmutableDictionary<string, object> Node()
    {
        var volumes = new[] { "lib-modules", "cni-bin-dir", "cni-net-dir", "cni-log-dir", "var-run-calico", "bpffs", "sys-fs", "nodeproc" }
            .Select(name => (object)Dictionary(("name", name))).ToImmutableArray();
        var node = Dictionary(("name", "calico-node"), ("image", "quay.io/calico/node:v3.32.0"),
            ("env", Array(Environment("FELIX_TYPHAK8SSERVICENAME", "calico-typha"),
                Environment("CALICO_NETWORKING_BACKEND", "none"), Environment("CALICO_MANAGE_CNI", "false"))),
            ("envFrom", Array(Dictionary(("configMapRef", Dictionary(("name", "kubernetes-services-endpoint")))))),
            ("volumeMounts", volumes), ("securityContext", Dictionary(("privileged", true))),
            ("readinessProbe", Dictionary(("exec", Dictionary(("command", new[] { "/bin/calico-node", "-felix-ready" }))))));
        var pod = Dictionary(("containers", Array(node)), ("volumes", volumes),
            ("initContainers", Array(Dictionary(("name", "install-cni")))));
        var template = Dictionary(("metadata", Dictionary()), ("spec", pod));
        return Dictionary(("kind", "DaemonSet"), ("metadata", Dictionary(("name", "calico-node"))),
            ("spec", Dictionary(("template", template))));
    }

    private static ImmutableDictionary<string, object> Controller()
    {
        var probe = Dictionary(("exec", Dictionary(("command", new[] { "/usr/bin/check-status", "-r" }))));
        var container = Dictionary(("name", "calico-kube-controllers"), ("image", "quay.io/calico/kube-controllers:v3.32.0"),
            ("readinessProbe", probe));
        var template = Dictionary(("metadata", Dictionary()), ("spec", Dictionary(("containers", Array(container)))));
        return Dictionary(("kind", "Deployment"), ("metadata", Dictionary(("name", "calico-kube-controllers"))),
            ("spec", Dictionary(("template", template))));
    }

    private static ImmutableDictionary<string, object> Environment(string name, string value) => Dictionary(("name", name), ("value", value));
    private static ImmutableArray<object> Array(params ImmutableDictionary<string, object>[] values) => values.Cast<object>().ToImmutableArray();
    private static IEnumerable<ImmutableDictionary<string, object>> Sequence(ImmutableDictionary<string, object> value, string key) =>
        InputMaps.Maps(value, key);
    private static ImmutableDictionary<string, object> Map(ImmutableDictionary<string, object> value, string key) => InputMaps.Map(value, key);
    private static ImmutableDictionary<string, object> Dictionary(params (string Name, object Value)[] values) =>
        InputMaps.Dictionary(values);
}
