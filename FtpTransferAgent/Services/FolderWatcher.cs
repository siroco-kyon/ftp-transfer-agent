using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;

namespace FtpTransferAgent.Services;

public class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ChannelWriter<TransferItem> _writer;
    private readonly string[] _extensions;

    public FolderWatcher(WatchOptions options, ChannelWriter<TransferItem> writer)
    {
        _writer = writer;
        _extensions = options.AllowedExtensions.Select(e => e.StartsWith(".") ? e : $".{e}").ToArray();
        _watcher = new FileSystemWatcher(options.Path)
        {
            IncludeSubdirectories = options.IncludeSubfolders,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnCreated;
        _watcher.Renamed += OnCreated;
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (_extensions.Length > 0 && !_extensions.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        _writer.TryWrite(new TransferItem(e.FullPath));
    }

    public void Dispose()
    {
        _watcher.Dispose();
    }
}
