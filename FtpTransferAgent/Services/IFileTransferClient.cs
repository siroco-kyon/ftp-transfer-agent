using System.Collections.Generic;

namespace FtpTransferAgent.Services;

public interface IFileTransferClient : IDisposable
{
    Task UploadAsync(string localPath, string remotePath, CancellationToken ct);
    Task DownloadAsync(string remotePath, string localPath, CancellationToken ct);
    Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false);
    Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct);
    Task DeleteAsync(string remotePath, CancellationToken ct);
}
