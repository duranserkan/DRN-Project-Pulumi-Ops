using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Drn.Infrastructure.Operator.Recovery;

/// <summary>Loopback-only kubectl tunnel; TLS still validates the original DataNode DNS identity.</summary>
public sealed class KubernetesTunnel : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> errors;
    private readonly Task<string> output;
    public int Port { get; }

    private KubernetesTunnel(Process process, Task<string> errors, Task<string> output, int port)
    {
        this.process = process;
        this.errors = errors;
        this.output = output;
        Port = port;
    }

    public static async Task<KubernetesTunnel> OpenAsync(IReadOnlyDictionary<string, string> environment,
        string namespaceName, string pod, int remotePort, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("kubectl")
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var value in new[] { "port-forward", "--address", "127.0.0.1", "--namespace", namespaceName, $"pod/{pod}", $":{remotePort}" })
            info.ArgumentList.Add(value);
        foreach (var entry in environment) info.Environment[entry.Key] = entry.Value;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the private Kubernetes tunnel.");
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                var match = Regex.Match(line, @"^Forwarding from 127\.0\.0\.1:(\d+) -> ");
                if (!match.Success) continue;
                var port = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return new KubernetesTunnel(process, errors, process.StandardOutput.ReadToEndAsync(), port);
            }
            throw new InvalidOperationException("Kubernetes tunnel exited before becoming ready.");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await errors;
            process.Dispose();
            throw;
        }
    }

    public HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    { BaseAddress = new Uri($"http://127.0.0.1:{Port}/"), Timeout = TimeSpan.FromMinutes(5) };

    public HttpClient CreateTlsClient(string hostname, X509Certificate2 ca)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            VerificationFlags = X509VerificationFlags.NoFlag
        };
        policy.CustomTrustStore.Add(ca);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                CertificateChainPolicy = policy,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            },
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, hostname, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected DataNode tunnel destination.");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { BaseAddress = new Uri($"https://{hostname}:9200/"), Timeout = TimeSpan.FromHours(2) };
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(output, errors);
        process.Dispose();
    }
}
