using System.Collections.Immutable;

namespace Drn.Pulumi.UnitTests.Support;

internal static class InputMaps
{
    internal static ImmutableDictionary<string, object> Dictionary(params (string Name, object Value)[] values) =>
        values.ToImmutableDictionary(value => value.Name, value => value.Value);

    internal static ImmutableDictionary<string, object> Map(ImmutableDictionary<string, object> value, string key)
    {
        value.Should().ContainKey(key, $"input map must contain '{key}'");
        return value[key].Should().BeOfType<ImmutableDictionary<string, object>>($"input '{key}' must be an object").Subject;
    }

    internal static IEnumerable<ImmutableDictionary<string, object>> Maps(ImmutableDictionary<string, object> value, string key)
    {
        value.Should().ContainKey(key, $"input map must contain '{key}'");
        var items = value[key].Should().BeAssignableTo<IEnumerable<object>>($"input '{key}' must be an array").Subject;
        return items.Select((item, index) => item.Should().BeOfType<ImmutableDictionary<string, object>>(
            $"input '{key}[{index}]' must be an object").Subject);
    }
}
