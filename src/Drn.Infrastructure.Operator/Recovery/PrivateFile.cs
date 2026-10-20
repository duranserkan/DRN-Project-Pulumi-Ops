namespace Drn.Infrastructure.Operator.Recovery;

/// <summary>A private scratch file for OCI ciphertext payloads, removed even after a failed request.</summary>
public sealed class PrivateFile : IDisposable
{
    private readonly string directory;
    public string Path { get; }

    public PrivateFile(ReadOnlySpan<byte> contents = default)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Recovery requires a Unix host with private file permissions.");
        directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"drn-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Path = System.IO.Path.Combine(directory, "payload");
        using var stream = new FileStream(Path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        stream.Write(contents);
    }

    public void Dispose()
    {
        File.Delete(Path);
        Directory.Delete(directory);
    }
}
