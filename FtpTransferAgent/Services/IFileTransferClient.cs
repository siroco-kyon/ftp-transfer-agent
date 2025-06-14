namespace FtpTransferAgent.Services;

public interface IFileTransferClient : IDisposable
{
    Task UploadAsync(string localPath, string remotePath, CancellationToken ct);
    Task DownloadAsync(string remotePath, string localPath, CancellationToken ct);
    Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct);
}
