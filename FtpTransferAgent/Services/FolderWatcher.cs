using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;

namespace FtpTransferAgent.Services;

/// <summary>
/// 指定フォルダを監視して新しいファイルを転送キューへ登録する
/// </summary>
public class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ChannelWriter<TransferItem> _writer;
    private readonly string[] _extensions;

    public FolderWatcher(WatchOptions options, ChannelWriter<TransferItem> writer)
    {
        _writer = writer;
        // 許可する拡張子を正規化
        _extensions = options.AllowedExtensions.Select(e => e.StartsWith(".") ? e : $".{e}").ToArray();
        _watcher = new FileSystemWatcher(options.Path)
        {
            IncludeSubdirectories = options.IncludeSubfolders,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnCreated;
        _watcher.Renamed += OnCreated;
    }

    // ファイル作成/リネーム時に呼び出される
    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (_extensions.Length > 0 && !_extensions.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        // 転送キューに追加
        _writer.TryWrite(new TransferItem(e.FullPath, TransferAction.Upload));
    }

    public void Dispose()
    {
        _watcher.Dispose();
    }
}
