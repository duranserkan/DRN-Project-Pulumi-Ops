using System.Diagnostics;

namespace Drn.Infrastructure.Operator;

public interface ICommandRunner
{
    Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, bool displayOutput, CancellationToken cancellationToken,
        string? standardInput = null);
}

public sealed class ProcessRunner : ICommandRunner
{
    public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, bool displayOutput, CancellationToken cancellationToken,
        string? standardInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = !displayOutput,
            RedirectStandardError = !displayOutput
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var entry in environment) info.Environment[entry.Key] = entry.Value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var output = displayOutput ? Task.FromResult("") : process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = displayOutput ? Task.FromResult("") : process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        if (process.ExitCode != 0)
            // Captured output may include kubeconfig, private configuration or credential material.
            throw new InvalidOperationException($"{executable} failed (exit {process.ExitCode}). Check authentication, permissions and the selected stack. No later phase was started.");
        return await output;
    }
}
