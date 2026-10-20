using System.Collections.Immutable;
using Drn.Pulumi;
using Xunit;

namespace Drn.Pulumi.UnitTests;

public class BootstrapTests
{
    [Fact]
    public void BootstrapProjectDeniesManualAndAutomaticSyncUntilTheOperatorOpensTheGate()
    {
        var project = Map(("kind", "AppProject"),
            ("metadata", Map(("name", "drn-project-bootstrap"), ("namespace", "argocd"))), ("spec", Map()));
        var result = Bootstrap.PrepareBootstrapProject(project, PulumiTestData.ValidSettings.GitOpsRepository);
        var spec = (ImmutableDictionary<string, object>)result["spec"];
        Assert.Equal(new[] { PulumiTestData.ValidSettings.GitOpsRepository }, (string[])spec["sourceRepos"]);
        var window = Assert.Single((ImmutableArray<ImmutableDictionary<string, object>>)spec["syncWindows"]);
        Assert.Equal("deny", window["kind"]);
        Assert.Equal("* * * * *", window["schedule"]);
        Assert.Equal("24h", window["duration"]);
        Assert.Equal(false, window["manualSync"]);
        Assert.Equal(new[] { "drn-project" }, (string[])window["applications"]);
    }

    [Fact]
    public void RootUsesVerifiedCommitAndStartsInTheRestrictedPreparationStage()
    {
        var resource = Root();
        var inputSpec = (ImmutableDictionary<string, object>)resource["spec"];
        var inputSource = (ImmutableDictionary<string, object>)inputSpec["source"];
        var helm = Map(("parameters", new[] { Map(("name", "activationStage"), ("value", "workloads")) }.ToImmutableArray()));
        resource = resource.Add("operation", Map(("sync", Map())))
            .SetItem("spec", inputSpec.Add("syncPolicy", Map(("automated", Map(("prune", true)))))
                .SetItem("source", inputSource.SetItem("helm", helm)));
        var result = Bootstrap.PrepareRoot(resource, PulumiTestData.ValidSettings);
        var spec = (ImmutableDictionary<string, object>)result["spec"];
        var source = (ImmutableDictionary<string, object>)spec["source"];
        Assert.Equal(PulumiTestData.ValidSettings.GitOpsCommit, source["targetRevision"]);
        Assert.Equal(PulumiTestData.ValidSettings.GitOpsRepository, source["repoURL"]);
        Assert.False(result.ContainsKey("operation"));
        var policy = (ImmutableDictionary<string, object>)spec["syncPolicy"];
        Assert.False(policy.ContainsKey("automated"));
        Assert.Contains("ApplyOutOfSyncOnly=false", (string[])policy["syncOptions"]);
        var parameters = (ImmutableArray<ImmutableDictionary<string, object>>)((ImmutableDictionary<string, object>)source["helm"])["parameters"];
        Assert.Contains(parameters, parameter => Equals(parameter["name"], "infrastructureProfile") && Equals(parameter["value"], "oci-retained"));
        Assert.Contains(parameters, parameter => Equals(parameter["name"], "releaseTag") && Equals(parameter["value"], PulumiTestData.ValidSettings.GitOpsTag));
        Assert.Contains(parameters, parameter => Equals(parameter["name"], "gitRevision") && Equals(parameter["value"], "$ARGOCD_APP_REVISION"));
        var activation = Assert.Single(parameters, parameter => Equals(parameter["name"], "activationStage"));
        Assert.Equal("prepare", activation["value"]);
        Assert.All(parameters, parameter => Assert.Equal(true, parameter["forceString"]));
        Assert.Equal(parameters.Length, parameters.Select(parameter => parameter["name"]).Distinct().Count());
    }

    [Theory]
    [DataInlineUnit(RootBoundaryFault.Kind)]
    [DataInlineUnit(RootBoundaryFault.Name)]
    [DataInlineUnit(RootBoundaryFault.Namespace)]
    [DataInlineUnit(RootBoundaryFault.Project)]
    [DataInlineUnit(RootBoundaryFault.Destination)]
    [DataInlineUnit(RootBoundaryFault.Path)]
    public void RejectsInputsThatEscapeTheStagingRootBoundary(RootBoundaryFault fault)
    {
        var root = Root();
        var metadata = (ImmutableDictionary<string, object>)root["metadata"];
        var spec = (ImmutableDictionary<string, object>)root["spec"];
        root = fault switch
        {
            RootBoundaryFault.Kind => root.SetItem("kind", "ApplicationSet"),
            RootBoundaryFault.Name => root.SetItem("metadata", metadata.SetItem("name", "other-root")),
            RootBoundaryFault.Namespace => root.SetItem("metadata", metadata.SetItem("namespace", "default")),
            RootBoundaryFault.Project => root.SetItem("spec", spec.SetItem("project", "default")),
            RootBoundaryFault.Destination => root.SetItem("spec", spec.SetItem("destination", Map(("namespace", "argocd"), ("server", "https://other-cluster.invalid")))),
            RootBoundaryFault.Path => root.SetItem("spec", spec.SetItem("source", ((ImmutableDictionary<string, object>)spec["source"]).SetItem("path", "apps"))),
            _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, null)
        };
        Assert.Throws<InvalidOperationException>(() => Bootstrap.PrepareRoot(root, PulumiTestData.ValidSettings));
    }

    [Fact]
    public void RejectsAProjectOutsideTheBootstrapNamespace()
    {
        var project = Map(("kind", "AppProject"),
            ("metadata", Map(("name", "drn-project-bootstrap"), ("namespace", "default"))), ("spec", Map()));
        Assert.Throws<InvalidOperationException>(() => Bootstrap.PrepareBootstrapProject(project, PulumiTestData.ValidSettings.GitOpsRepository));
    }

    [Fact]
    public void RejectsRootWithCascadingDeletionFinalizers()
    {
        var resource = Root();
        var metadata = ((ImmutableDictionary<string, object>)resource["metadata"])
            .Add("finalizers", new[] { "resources-finalizer.argocd.argoproj.io" });
        Assert.Throws<InvalidOperationException>(() => Bootstrap.PrepareRoot(resource.SetItem("metadata", metadata), PulumiTestData.ValidSettings));
    }

    private static ImmutableDictionary<string, object> Root() => Map(
        ("apiVersion", "argoproj.io/v1alpha1"), ("kind", "Application"),
        ("metadata", Map(("name", "drn-project"), ("namespace", "argocd"))),
        ("spec", Map(("project", "drn-project-bootstrap"),
            ("destination", Map(("namespace", "argocd"), ("server", "https://kubernetes.default.svc"))),
            ("source", Map(("path", "deployment"), ("repoURL", "https://example.invalid/old.git"),
                ("targetRevision", "old"), ("helm", Map()))))));

    private static ImmutableDictionary<string, object> Map(params (string Name, object Value)[] values) =>
        InputMaps.Dictionary(values);

    public enum RootBoundaryFault { Kind, Name, Namespace, Project, Destination, Path }
}
