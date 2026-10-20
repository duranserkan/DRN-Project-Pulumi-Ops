using System.Collections.Immutable;
using Drn.Pulumi;
using Xunit;

namespace Drn.Pulumi.UnitTests;

public class StorageBindingTests
{
    [Fact]
    public void PvCapacityMustMatchTheDurableOutputAndSelectedGeneration()
    {
        var generation = new StorageGeneration { Volumes = new() { ["sample"] = new VolumeSettings { SizeInGbs = 100 } } };
        var sizes = StorageClaims.All.ToImmutableDictionary(c => c.Key, c => (object)(double)generation.Volume(c.Key).SizeInGbs);
        Assert.Equal(100, Bootstrap.ReadVolumeSizes(sizes, generation)["sample"]);
        Assert.Equal(50, Bootstrap.ReadVolumeSizes(null, new StorageGeneration())["sample"]);
        Assert.Throws<InvalidOperationException>(() => Bootstrap.ReadVolumeSizes(null, generation));
        Assert.Throws<InvalidOperationException>(() => Bootstrap.ReadVolumeSizes(sizes.SetItem("sample", 99.5), generation));
        Assert.Throws<InvalidOperationException>(() => Bootstrap.ReadVolumeSizes(sizes.Remove("sample"), generation));
        Assert.Throws<InvalidOperationException>(() => Bootstrap.ReadVolumeSizes(sizes.Add("foreign", 50), generation));
    }

    [Theory]
    [DataMemberUnit(nameof(InvalidVolumeSizes))]
    public void CapacityRejectsMalformedSerializedValues(object? size)
    {
        var sizes = StorageClaims.All.ToImmutableDictionary(claim => claim.Key, _ => (object)50);
        var error = Assert.Throws<InvalidOperationException>(() => Bootstrap.ReadVolumeSizes(sizes.SetItem("sample", size!), new StorageGeneration()));
        Assert.Contains("sample", error.Message);
    }

    public static IEnumerable<object?[]> InvalidVolumeSizes =>
    [
        [null], ["50"], [true], [50.5], [double.NaN], [double.PositiveInfinity], [0], [-1]
    ];

    [Theory]
    [DataMemberUnit(nameof(ValidVolumeSizes))]
    public void CapacityAcceptsSupportedIntegralNumericRepresentations(object size)
    {
        var sizes = StorageClaims.All.ToImmutableDictionary(claim => claim.Key, _ => size);
        var actual = Bootstrap.ReadVolumeSizes(sizes, new StorageGeneration());
        Assert.Equal(7, actual.Count);
        Assert.All(actual.Values, value => Assert.Equal(50, value));
    }

    public static IEnumerable<object[]> ValidVolumeSizes => [[50], [50L], [50d]];

    [Fact]
    public void StaticReservationsPreserveTheRetainedVolumeBindingContract()
    {
        Assert.Equal(new[]
        {
            "drn-project/sample-data", "drn-project/nexus-data", "drn-project/postgresql-1",
            "graylog/data-volume-mongodb-0", "graylog/logs-volume-mongodb-0",
            "graylog/graylog-data-graylog-0", "graylog/data-graylog-datanode-0"
        }, StorageClaims.All.Select(claim => $"{claim.Namespace}/{claim.ClaimName}"));
    }
}
