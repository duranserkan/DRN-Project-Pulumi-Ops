---
name: drn-testing
description: "DRN unit-test attributes, isolated contexts and MTP commands. Keywords: testing, dtt, mtp, xunit, unit-testing"
last-updated: 2026-10-20
difficulty: intermediate
---

# DRN Unit Testing

Use the repository profile for test boundaries and the installed DRN.Framework.Testing version for API compatibility.

## When to Apply

- Selecting `[Fact]`, `DataInlineUnit`, `DataMemberUnit`, or `DataSelfUnit` patterns
- Writing isolated tests with `DrnTestContextUnit`
- Explaining how DRN test projects run under Microsoft Testing Platform (MTP)

## DTT Attribute/Context Matrix

| Scenario | Attribute | Context parameter | Auto-generated params | Extra capabilities |
|---|---|---|---|---|
| No inline data, generated params, or context | `[Fact]` | none | no | simplest test shape |
| Unit rows or generated params | `[Theory]` + `[DataInlineUnit]` | optional `DrnTestContextUnit` first | yes | DI, config, data files, service validation |
| Unit member data | `[Theory]` + `[DataMemberUnit]` | optional `DrnTestContextUnit` first | yes | complex data from static member |
| Unit self-contained data | `[Theory]` + custom `DataSelfUnitAttribute` | optional `DrnTestContextUnit` first | yes | complex rows declared in an attribute class |

Rules:

- Request `DrnTestContextUnit` only when the test uses services, configuration, data files, temporary paths or service validation.
- Keep unit tests isolated. Do not use the integration `DrnTestContext` to start applications or containers.
- Put the context first when requested, followed by attribute data. AutoFixture/NSubstitute fill missing parameters.
- Interface/abstract parameters auto-mocked by NSubstitute can replace matching `ServiceCollection` registrations. Use those substitutes for dependencies, not for concrete convenience methods whose implementation is the behavior under test.
- Assertions use `AwesomeAssertions`.
- Unit self-data derives from `DataSelfUnitAttribute`.
- Use explicit test configuration. Do not weaken production validation for tests.

## Minimal Patterns

```csharp
[Fact]
public void Trim_Should_Remove_Outer_Whitespace()
{
    "  Duran  ".Trim().Should().Be("Duran");
}

[Theory]
[DataInlineUnit(2, 3, 5)]
[DataInlineUnit(-1, -2, -3)]
public void Add_Should_Return_Correct_Sum(int a, int b, int expected)
{
    (a + b).Should().Be(expected);
}

[Theory]
[DataInlineUnit]
public void Service_Should_Use_Mocked_Dependency(DrnTestContextUnit context, IDependency dependency)
{
    dependency.GetValue().Returns(42);
    context.ServiceCollection.AddScoped<IDependency>(_ => dependency);
    context.ServiceCollection.AddScoped<MyService>();

    context.GetRequiredService<MyService>().Calculate().Should().Be(42);
}

public sealed class CategoryRows : DataSelfUnitAttribute
{
    public CategoryRows()
    {
        AddRow("dotnet");
        AddRow("security");
    }
}
```

## Context Capabilities

`DrnTestContextUnit` provides `ServiceCollection`, `GetRequiredService<T>()`, `BuildConfigurationRoot()`, `GetData()` and `ValidateServicesAsync()`. Use `context.GetTempPath()` for a temporary directory owned by that test context. Keep mutable state local to each test.

## MTP Commands

Repository rule: do not build or run tests unless the user explicitly allows it. When allowed, prefer commands from `.agent/repository-profile.md`; otherwise discover test project paths from the filesystem or CI. Use project commands only; never use `.slnx` for test execution.

```bash
# Unit tests first
dotnet run --project <unit-test-csproj>

# Filter a unit class or method
dotnet run --project <unit-test-csproj> -- --filter-class Fully.Qualified.TestClass
dotnet run --project <unit-test-csproj> -- --filter-method Fully.Qualified.TestMethod
```

## Test Project Essentials

Follow the repository profile and existing `.csproj` files for SDK, package and runner settings. If a test uses `Settings/` or `Data/`, copy those files to output. Import only the namespaces used by the tests.

## Related Skills

- [test-unit](../test-unit/SKILL.md) - test boundaries, parameterization and decision coverage
