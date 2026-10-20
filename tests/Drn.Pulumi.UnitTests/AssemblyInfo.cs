using Xunit.Sdk;
using Xunit.v3;

// PulumiDeploymentCollection opts shared deployment callbacks out of parallel execution.
[assembly: Parallelization(Mode = ParallelMode.All)]
