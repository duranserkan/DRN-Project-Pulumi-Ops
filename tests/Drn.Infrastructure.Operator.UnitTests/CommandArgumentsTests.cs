using Xunit;
using Xunit.Sdk;

namespace Drn.Infrastructure.Operator.UnitTests;

public sealed class CommandArgumentsTests
{
    [Theory]
    [DataInlineUnit("")]
    [DataInlineUnit("--output|json")]
    [DataInlineUnit("--file")]
    [DataInlineUnit("--file|")]
    [DataInlineUnit("--file|   ")]
    [DataInlineUnit("--file|--output|json")]
    [DataInlineUnit("--file|-value")]
    [DataInlineUnit("--file|one|--file|two")]
    public void RequiredRejectsMissingDuplicatedAndInvalidOptionValues(string command) =>
        Assert.Throws<XunitException>(() => CommandArguments.Required(command.Split('|'), "--file"));

    [Fact]
    public void RequiredRejectsANullValue() =>
        Assert.Throws<XunitException>(() => CommandArguments.Required(["--file", null!], "--file"));

    [Theory]
    [DataInlineUnit("--file|path with spaces|--output|json")]
    [DataInlineUnit("--output|json|--file|path with spaces")]
    public void RequiredReturnsTheExactValueWithoutConsumingOtherOptions(string command) =>
        Assert.Equal("path with spaces", CommandArguments.Required(command.Split('|'), "--file"));

    [Theory]
    [DataInlineUnit("")]
    [DataInlineUnit("os")]
    [DataInlineUnit("os|bucket")]
    [DataInlineUnit("OS|object")]
    public void PrefixRejectsIncompleteOrDifferentCommands(string command) =>
        Assert.ThrowsAny<XunitException>(() => CommandArguments.Prefix(command.Split('|'), "os", "object"));

    [Fact]
    public void PrefixAcceptsTheExactPrefixWithAdditionalArguments() =>
        CommandArguments.Prefix(["os", "object", "get", "--name", "state"], "os", "object");
}
