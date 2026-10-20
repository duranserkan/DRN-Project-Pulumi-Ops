using System.Text.Json.Nodes;
using Drn.Infrastructure.Operator.Recovery;
using Drn.Pulumi;
using Xunit;
using static Drn.Infrastructure.Operator.UnitTests.Support.OracleCloudMetadata;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed class CloudPreflightTests
{
    private const string OracleCloudInstanceId = "ocid1.instance.oc1.test.worker";
    private const string OracleCloudNodePoolId = "ocid1.nodepool.oc1.test.pool";
    private static InfrastructureSettings Settings => new()
    {
        Region = "eu-frankfurt-1", ClusterCompartmentId = "ocid1.compartment.oc1..cluster", AvailabilityDomain = "test:EU-FRANKFURT-1-AD-1",
        NodeImageId = "ocid1.image.oc1.test.image", KubernetesVersion = "v1.36.4"
    };
    private static string[] Volumes => Enumerable.Range(1, 7).Select(i => $"ocid1.volume.oc1.test.volume{i}").ToArray();

    [Fact]
    public async Task ImageSelectionUsesExactVersionArmOracleLinuxAndRegionFilters()
    {
        var runner = new PreflightRunner();
        await Create(runner).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, runner.Calls.Count);
        var options = runner.Calls[0];
        Assert.Equal(Settings.KubernetesVersion, Argument(options, "--node-pool-k8s-version"));
        Assert.Equal("AARCH64", Argument(options, "--node-pool-os-arch"));
        Assert.Equal("OL8", Argument(options, "--node-pool-os-type"));
        Assert.Equal("true", Argument(options, "--should-list-all-patch-versions"));
        Assert.Equal(Settings.NodeShape, Argument(runner.Calls[3], "--shape-name"));
        Assert.All(runner.Calls, call => Assert.Equal(Settings.Region, Argument(call, CommandArguments.OracleCloudRegion)));
    }

    [Theory]
    [DataInlineUnit("VM.Standard.E4.Flex")]
    [DataInlineUnit("VM.Standard.E5.Flex")]
    [DataInlineUnit("VM.Standard.E6.Flex")]
    [DataInlineUnit("VM.Standard3.Flex")]
    public async Task X86ShapesSelectMatchingOkeImages(string shape)
    {
        var selected = Settings with { NodeShape = shape };
        var runner = new PreflightRunner { SelectedSettings = selected };
        await new CloudPreflight(runner, new Dictionary<string, string>(), selected).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("X86_64", Argument(runner.Calls[0], "--node-pool-os-arch"));
        Assert.Equal(selected.NodeImageId, Argument(runner.Calls[2], "--image-id"));
        Assert.Equal(selected.AvailabilityDomain, Argument(runner.Calls[2], "--availability-domain"));
    }

    [Theory]
    [DataInlineUnit(PreflightScenario.BootDiskTooSmall)]
    [DataInlineUnit(PreflightScenario.ShapeCpuLimit)]
    [DataInlineUnit(PreflightScenario.ShapeMemoryLimit)]
    [DataInlineUnit(PreflightScenario.ShapeVnicLimit)]
    [DataInlineUnit(PreflightScenario.ImageCpuLimit)]
    [DataInlineUnit(PreflightScenario.ImageMemoryLimit)]
    [DataInlineUnit(PreflightScenario.CompatibilityIdentityMismatch)]
    public async Task ResourceLimitsFailBeforeProvisioning(PreflightScenario scenario) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(new PreflightRunner { Scenario = scenario })
            .RunAsync(TestContext.Current.CancellationToken));

    [Theory]
    [DataInlineUnit(false, 4, true)]
    [DataInlineUnit(true, 4, true)]
    [DataInlineUnit(false, 3, false)]
    [DataInlineUnit(true, 3, false)]
    public async Task MissingOrNullVnicOptionsUseTheFixedAttachmentLimit(bool explicitNull, int limit, bool compatible)
    {
        var runner = new PreflightRunner { ConfigureShape = shape =>
        {
            if (explicitNull) shape[VnicOptions] = null;
            else shape.Remove(VnicOptions);
            shape[FixedVnics] = limit;
        } };
        if (compatible)
        {
            await Create(runner).RunAsync(TestContext.Current.CancellationToken);
            Assert.Equal(4, runner.Calls.Count);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
            Assert.Contains("enough VNICs", error.Message);
            Assert.Equal(3, runner.Calls.Count);
        }
    }

    [Theory]
    [DataInlineUnit(CpuConstraints, false)]
    [DataInlineUnit(CpuConstraints, true)]
    [DataInlineUnit(MemoryConstraints, false)]
    [DataInlineUnit(MemoryConstraints, true)]
    public async Task OptionalImageConstraintsDoNotDisableTheOtherResourceLimit(string omitted, bool explicitNull)
    {
        Action<JsonObject> omitConstraint = entry =>
        {
            if (explicitNull) entry[omitted] = null;
            else entry.Remove(omitted);
        };
        var runner = new PreflightRunner { ConfigureCompatibility = omitConstraint };
        await Create(runner).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, runner.Calls.Count);

        var restricted = new PreflightRunner { ConfigureCompatibility = entry =>
        {
            omitConstraint(entry);
            if (omitted == CpuConstraints) entry[MemoryConstraints]![ImageMemoryMaximum] = 23;
            else entry[CpuConstraints]![Maximum] = 3;
        } };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(restricted).RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("image's shape constraints", error.Message);
    }

    [Theory]
    [DataInlineUnit(CpuConstraints, Minimum, Maximum, 3, false)]
    [DataInlineUnit(CpuConstraints, Minimum, Maximum, 3, true)]
    [DataInlineUnit(CpuConstraints, Maximum, Minimum, 5, false)]
    [DataInlineUnit(CpuConstraints, Maximum, Minimum, 5, true)]
    [DataInlineUnit(MemoryConstraints, ImageMemoryMinimum, ImageMemoryMaximum, 23, false)]
    [DataInlineUnit(MemoryConstraints, ImageMemoryMinimum, ImageMemoryMaximum, 23, true)]
    [DataInlineUnit(MemoryConstraints, ImageMemoryMaximum, ImageMemoryMinimum, 25, false)]
    [DataInlineUnit(MemoryConstraints, ImageMemoryMaximum, ImageMemoryMinimum, 25, true)]
    public async Task OptionalImageRangeEndpointsStillEnforceTheRemainingBound(string range, string omitted, string remaining, double rejectingLimit, bool explicitNull)
    {
        Action<JsonObject> omitEndpoint = entry =>
        {
            if (explicitNull) entry[range]![omitted] = null;
            else entry[range]!.AsObject().Remove(omitted);
        };
        var runner = new PreflightRunner { ConfigureCompatibility = omitEndpoint };
        await Create(runner).RunAsync(TestContext.Current.CancellationToken);

        var restricted = new PreflightRunner { ConfigureCompatibility = entry =>
        {
            omitEndpoint(entry);
            entry[range]![remaining] = rejectingLimit;
        } };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(restricted).RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("image's shape constraints", error.Message);
    }

    [Theory]
    [DataInlineUnit(false, CpuOptions, Minimum, 4, 5)]
    [DataInlineUnit(false, CpuOptions, Maximum, 4, 3)]
    [DataInlineUnit(false, MemoryOptions, ShapeMemoryMinimum, 24, 25)]
    [DataInlineUnit(false, MemoryOptions, ShapeMemoryMaximum, 24, 23)]
    [DataInlineUnit(false, MemoryOptions, MemoryPerCpuMinimum, 6, 6.25)]
    [DataInlineUnit(false, MemoryOptions, MemoryPerCpuMaximum, 6, 5.75)]
    [DataInlineUnit(true, CpuConstraints, Minimum, 4, 5)]
    [DataInlineUnit(true, CpuConstraints, Maximum, 4, 3)]
    [DataInlineUnit(true, MemoryConstraints, ImageMemoryMinimum, 24, 25)]
    [DataInlineUnit(true, MemoryConstraints, ImageMemoryMaximum, 24, 23)]
    public async Task CpuAndMemoryLimitsAreInclusive(bool imageConstraint, string range, string bound, double exact, double outside)
    {
        PreflightRunner CreateRunner(double boundary)
        {
            Action<JsonObject> configure = metadata => metadata[range]![bound] = boundary;
            return new PreflightRunner
            {
                ConfigureShape = imageConstraint ? null : configure,
                ConfigureCompatibility = imageConstraint ? configure : null
            };
        }

        var runner = CreateRunner(exact);
        await Create(runner).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, runner.Calls.Count);

        var restricted = CreateRunner(outside);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(restricted).RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains(imageConstraint ? "image's shape constraints" : "shape's limits", error.Message);
    }

    [Theory]
    [DataInlineUnit(1, 2, 24, true)]
    [DataInlineUnit(0.99, 2, 24, false)]
    [DataInlineUnit(0.5, 4, 24, true)]
    [DataInlineUnit(2, 2, 4, true)]
    [DataInlineUnit(2, 2, 3, false)]
    public async Task FlexVnicCapacityHonorsRoundingAndClampedLimits(double perOcpu, int minimum, int maximum, bool compatible)
    {
        var runner = new PreflightRunner { ConfigureShape = shape =>
            shape[VnicOptions] = new JsonObject
            { [Minimum] = minimum, [Maximum] = maximum, [VnicsPerCpu] = perOcpu } };
        if (compatible)
            await Create(runner).RunAsync(TestContext.Current.CancellationToken);
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
            Assert.Contains("enough VNICs", error.Message);
        }
    }

    [Fact]
    public async Task SelectedFaultDomainsMustExistInTheConfiguredAd()
    {
        var selected = Settings with { FaultDomains = ["FAULT-DOMAIN-2"] };
        var runner = new PreflightRunner { SelectedSettings = selected };
        await new CloudPreflight(runner, new Dictionary<string, string>(), selected).RunAsync(TestContext.Current.CancellationToken);
        var invalid = new PreflightRunner { SelectedSettings = selected, Scenario = PreflightScenario.MissingFaultDomain };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudPreflight(invalid, new Dictionary<string, string>(), selected)
            .RunAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [DataInlineUnit(PreflightScenario.SourceImageMismatch)]
    [DataInlineUnit(PreflightScenario.UnsupportedKubernetesVersion)]
    [DataInlineUnit(PreflightScenario.UnsupportedShape)]
    [DataInlineUnit(PreflightScenario.UnsupportedImageOs)]
    [DataInlineUnit(PreflightScenario.WrongImageArchitecture)]
    [DataInlineUnit(PreflightScenario.WrongImagePatch)]
    public async Task IncompatibleImagesFailClosed(PreflightScenario scenario)
    {
        var runner = new PreflightRunner { Scenario = scenario };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletedOkeNodesAreExcludedButDeletingComputeInstancesRemainRecorded()
    {
        var runner = new PreflightRunner();
        Assert.Equal(new[] { OracleCloudInstanceId }, await Create(runner).ReadWorkerInstancesAsync(OracleCloudNodePoolId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownOkeNodeStateFailsBeforeWorkerListCanBeUsed()
    {
        var runner = new PreflightRunner { Scenario = PreflightScenario.UnknownNodeState };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).ReadWorkerInstancesAsync(OracleCloudNodePoolId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DetachmentInspectsAllVolumesInTheInstanceCompartmentAndAllPages()
    {
        var runner = new PreflightRunner();
        await Create(runner).RequireDetachedAsync(Volumes, [OracleCloudInstanceId], TestContext.Current.CancellationToken);
        Assert.Equal(8, runner.Calls.Count);
        var attachments = runner.Calls.Skip(1).ToArray();
        Assert.All(attachments, call =>
        {
            Assert.Equal(Settings.ClusterCompartmentId, Argument(call, "--compartment-id"));
            Assert.Contains("--all", call);
        });
        Assert.Equal(Volumes, attachments.Select(call => Argument(call, CommandArguments.OracleCloudVolumeId)));
    }

    [Theory]
    [DataInlineUnit(PreflightScenario.ForeignAttachment)]
    [DataInlineUnit(PreflightScenario.UnknownAttachmentState)]
    [DataInlineUnit(PreflightScenario.ForeignInstance)]
    [DataInlineUnit(PreflightScenario.UnknownInstanceState)]
    public async Task UnexpectedAttachmentOrWorkerEvidenceFailsClosed(PreflightScenario scenario)
    {
        var runner = new PreflightRunner { Scenario = scenario };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RequireDetachedAsync(Volumes, [OracleCloudInstanceId], TestContext.Current.CancellationToken));
    }

    [Theory]
    [DataInlineUnit(PreflightScenario.RunningInstance)]
    [DataInlineUnit(PreflightScenario.PendingAttachment)]
    public async Task ZeroNodePoolSizeCannotSubstituteForTerminatedInstancesAndDetachedVolumes(PreflightScenario scenario)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new PreflightRunner { Scenario = scenario, CancelAfterRead = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(runner).RequireDetachedAsync(Volumes, [OracleCloudInstanceId], cancellation.Token));
    }

    [Fact]
    public async Task IncompleteVolumeSetIsRejectedBeforeAnyCloudCall()
    {
        var runner = new PreflightRunner();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RequireDetachedAsync(Volumes[..6], [OracleCloudInstanceId], TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [DataInlineUnit("null")]
    [DataInlineUnit("\"4\"")]
    [DataInlineUnit("true")]
    [DataInlineUnit("0")]
    [DataInlineUnit("-1")]
    public async Task MalformedRequiredShapeNumbersFailBeforeImageCompatibility(string encodedValue)
    {
        var runner = new PreflightRunner { ConfigureShape = shape => shape[CpuOptions]![Minimum] = JsonNode.Parse(encodedValue) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("positive min value", error.Message);
        Assert.Equal(3, runner.Calls.Count);
    }

    [Theory]
    [DataInlineUnit(65536, true)]
    [DataInlineUnit(65537, false)]
    public async Task BootDiskAcceptsTheExactImageSizeAndRejectsOneExtraMegabyte(int imageSize, bool accepted)
    {
        var runner = new PreflightRunner { ConfigureImage = image => image["size-in-mbs"] = imageSize };
        if (accepted)
        {
            await Create(runner).RunAsync(TestContext.Current.CancellationToken);
            Assert.Equal(4, runner.Calls.Count);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(runner).RunAsync(TestContext.Current.CancellationToken));
            Assert.Contains("boot disk is smaller", error.Message);
            Assert.Equal(2, runner.Calls.Count);
        }
    }

    private static CloudPreflight Create(PreflightRunner runner) => new(runner, new Dictionary<string, string>(), Settings);
    private static string Argument(IReadOnlyList<string> args, string name) => CommandArguments.Required(args, name);

    private sealed class PreflightRunner : ICommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public PreflightScenario Scenario { get; init; } = PreflightScenario.Valid;
        public InfrastructureSettings SelectedSettings { get; init; } = Settings;
        public Action<JsonObject>? ConfigureShape { get; init; }
        public Action<JsonObject>? ConfigureImage { get; init; }
        public Action<JsonObject>? ConfigureCompatibility { get; init; }
        public CancellationTokenSource? CancelAfterRead { get; init; }
        public Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, bool displayOutput, CancellationToken cancellationToken,
            string? standardInput = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(CommandArguments.OracleCloudCli, executable);
            Assert.False(displayOutput);
            Assert.Null(standardInput);
            Assert.Equal(SelectedSettings.Region, Argument(arguments, CommandArguments.OracleCloudRegion));
            Assert.Equal(CommandArguments.OracleCloudJsonOutput, Argument(arguments, CommandArguments.OracleCloudOutput));
            Assert.Contains(CommandArguments.OracleCloudNoRetry, arguments);
            CommandArguments.OracleCloudRead(arguments);
            Calls.Add(arguments.ToArray());
            var imageName = SelectedSettings.NodeArchitecture == "AARCH64"
                ? "Oracle-Linux-8.10-aarch64-2026.10.01-0-OKE-1.36.4-1"
                : "Oracle-Linux-8.10-2026.10.01-0-OKE-1.36.4-1";
            JsonNode data;
            if (arguments[1] == "node-pool-options")
                data = new JsonObject
                {
                    ["kubernetes-versions"] = new JsonArray(Scenario == PreflightScenario.UnsupportedKubernetesVersion ? "v1.36.3" : Settings.KubernetesVersion),
                    ["shapes"] = new JsonArray(Scenario == PreflightScenario.UnsupportedShape ? "VM.Standard.E5.Flex" : SelectedSettings.NodeShape),
                    ["sources"] = new JsonArray(new JsonObject
                    {
                        ["source-type"] = "IMAGE", ["source-name"] = imageName,
                        ["image-id"] = Scenario == PreflightScenario.SourceImageMismatch ? "ocid1.image.oc1.test.other" : Settings.NodeImageId
                    })
                };
            else if (arguments[1] == "image")
            {
                data = new JsonObject
                {
                    ["id"] = Settings.NodeImageId, ["lifecycle-state"] = "AVAILABLE", ["size-in-mbs"] = Scenario == PreflightScenario.BootDiskTooSmall ? 100 * 1024 : 50 * 1024,
                    ["operating-system"] = Scenario == PreflightScenario.UnsupportedImageOs ? "Ubuntu" : "Oracle Linux", ["operating-system-version"] = "8",
                    ["display-name"] = Scenario switch
                    {
                        PreflightScenario.WrongImageArchitecture => imageName.Replace("aarch64", "x86_64", StringComparison.Ordinal),
                        PreflightScenario.WrongImagePatch => imageName.Replace("1.36.4", "1.36.40", StringComparison.Ordinal),
                        _ => imageName
                    }
                };
                ConfigureImage?.Invoke(data.AsObject());
            }
            else if (arguments[1] == "shape")
            {
                var shape = Shape(SelectedSettings);
                if (Scenario == PreflightScenario.ShapeCpuLimit) shape[CpuOptions]![Maximum] = 2;
                if (Scenario == PreflightScenario.ShapeMemoryLimit) shape[MemoryOptions]![MemoryPerCpuMaximum] = 2;
                if (Scenario == PreflightScenario.ShapeVnicLimit) shape[VnicOptions]![VnicsPerCpu] = 0.5;
                ConfigureShape?.Invoke(shape);
                data = new JsonArray(shape);
            }
            else if (arguments[1] == "image-shape-compatibility-entry")
            {
                data = ImageCompatibility(SelectedSettings);
                if (Scenario == PreflightScenario.ImageCpuLimit) data[CpuConstraints]![Maximum] = 2;
                if (Scenario == PreflightScenario.ImageMemoryLimit) data[MemoryConstraints]![ImageMemoryMaximum] = 16;
                if (Scenario == PreflightScenario.CompatibilityIdentityMismatch) data["image-id"] = "another-image";
                ConfigureCompatibility?.Invoke(data.AsObject());
            }
            else if (arguments[1] == "fault-domain")
                data = new JsonArray(new JsonObject { ["name"] = Scenario == PreflightScenario.MissingFaultDomain ? "FAULT-DOMAIN-1" : "FAULT-DOMAIN-2" });
            else if (arguments[1] == "node-pool")
                data = new JsonObject
                {
                    ["id"] = OracleCloudNodePoolId, ["compartment-id"] = Settings.ClusterCompartmentId,
                    ["nodes"] = new JsonArray(
                        new JsonObject { ["id"] = OracleCloudInstanceId, ["node-pool-id"] = OracleCloudNodePoolId,
                            ["lifecycle-state"] = Scenario == PreflightScenario.UnknownNodeState ? "UNKNOWN_ENUM_VALUE" : "DELETING" },
                        new JsonObject { ["id"] = "ocid1.instance.oc1.test.old", ["lifecycle-state"] = "DELETED" })
                };
            else if (arguments[1] == "instance")
                data = new JsonObject
                {
                    ["id"] = OracleCloudInstanceId,
                    ["compartment-id"] = Scenario == PreflightScenario.ForeignInstance ? "another-compartment" : Settings.ClusterCompartmentId,
                    ["lifecycle-state"] = Scenario switch
                    {
                        PreflightScenario.RunningInstance => "RUNNING",
                        PreflightScenario.UnknownInstanceState => "UNKNOWN",
                        _ => "TERMINATED"
                    }
                };
            else if (arguments[1] == "volume-attachment")
                data = new JsonArray(new JsonObject
                {
                    ["volume-id"] = Argument(arguments, CommandArguments.OracleCloudVolumeId), ["compartment-id"] = Settings.ClusterCompartmentId,
                    ["instance-id"] = Scenario == PreflightScenario.ForeignAttachment ? "ocid1.instance.oc1.test.foreign" : OracleCloudInstanceId,
                    ["lifecycle-state"] = Scenario switch
                    {
                        PreflightScenario.ForeignAttachment => "ATTACHED",
                        PreflightScenario.PendingAttachment => "DETACHING",
                        PreflightScenario.UnknownAttachmentState => "UNKNOWN",
                        _ => "DETACHED"
                    }
                });
            else throw new Xunit.Sdk.XunitException($"Unexpected OCI command: {string.Join(' ', arguments)}");
            if (arguments[1] == "volume-attachment" && Argument(arguments, CommandArguments.OracleCloudVolumeId) == Volumes[^1])
                CancelAfterRead?.Cancel();
            return Task.FromResult(new JsonObject { ["data"] = data }.ToJsonString());
        }
    }

    public enum PreflightScenario
    {
        Valid,
        BootDiskTooSmall,
        ShapeCpuLimit,
        ShapeMemoryLimit,
        ShapeVnicLimit,
        ImageCpuLimit,
        ImageMemoryLimit,
        CompatibilityIdentityMismatch,
        SourceImageMismatch,
        UnsupportedKubernetesVersion,
        UnsupportedShape,
        UnsupportedImageOs,
        WrongImageArchitecture,
        WrongImagePatch,
        MissingFaultDomain,
        UnknownNodeState,
        ForeignAttachment,
        UnknownAttachmentState,
        ForeignInstance,
        UnknownInstanceState,
        RunningInstance,
        PendingAttachment
    }
}
