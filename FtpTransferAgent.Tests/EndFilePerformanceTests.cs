using System.Diagnostics;
using System.IO;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace FtpTransferAgent.Tests;

/// <summary>
/// ENDファイル機能のパフォーマンステスト
/// </summary>
public class EndFilePerformanceTests
{
    [Fact]
    public async Task EndFileCheck_WithManyFiles_ShouldCompleteReasonablyFast()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            // 1000ファイル作成（半分にENDファイルを作成）
            const int fileCount = 1000;
            var tasks = new List<Task>();
            
            for (int i = 0; i < fileCount; i++)
            {
                var fileName = $"test{i:D4}.txt";
                var filePath = Path.Combine(dir, fileName);
                tasks.Add(File.WriteAllTextAsync(filePath, $"data{i}"));

                // 半分のファイルにENDファイルを作成
                if (i % 2 == 0)
                {
                    var endFilePath = Path.Combine(dir, $"test{i:D4}.END");
                    tasks.Add(File.WriteAllTextAsync(endFilePath, ""));
                }
            }
            
            await Task.WhenAll(tasks);

            var watch = Options.Create(new WatchOptions 
            { 
                Path = dir,
                RequireEndFile = true,
                EndFileExtensions = new[] { ".END", ".end" },
                AllowedExtensions = new[] { ".txt" } // .txtファイルのみを転送対象にする
            });
            var transfer = Options.Create(new TransferOptions
            {
                Mode = "ftp",
                Direction = "put",
                Host = "host",
                Username = "user",
                Password = "pass",
                RemotePath = "/remote",
                Concurrency = 1
            });
            var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
            var hash = Options.Create(new HashOptions { Algorithm = "MD5" });
            var cleanup = Options.Create(new CleanupOptions());

            var mock = new Mock<IFileTransferClient>();
            // ENDファイルがあるファイル（500個）のみアップロードされることを期待
            mock.Setup(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.GetRemoteHashAsync(It.IsAny<string>(), "MD5", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync("d41d8cd98f00b204e9800998ecf8427e"); // 空文字のMD5
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();

            var lifetime = new DummyLifetime();
            var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));

            // パフォーマンス測定
            var stopwatch = Stopwatch.StartNew();
            await worker.RunAsync(CancellationToken.None);
            stopwatch.Stop();

            // 1000ファイルの処理が10秒以内に完了することを確認
            Assert.True(stopwatch.ElapsedMilliseconds < 10000, 
                $"Processing took too long: {stopwatch.ElapsedMilliseconds}ms");

            // ENDファイルがあるファイル（500個）のみアップロードされたことを確認
            mock.Verify(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), 
                Times.Exactly(500));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task EndFileCheck_WithManyExtensions_ShouldNotDegrade()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            var file = Path.Combine(dir, "test.txt");
            var endFile = Path.Combine(dir, "test.END");
            await File.WriteAllTextAsync(file, "data");
            await File.WriteAllTextAsync(endFile, "");

            // 100個の拡張子を設定（最後にマッチする.ENDを配置）
            var extensions = new List<string>();
            for (int i = 0; i < 99; i++)
            {
                extensions.Add($".EXT{i:D2}");
            }
            extensions.Add(".END");

            var watch = Options.Create(new WatchOptions 
            { 
                Path = dir,
                RequireEndFile = true,
                EndFileExtensions = extensions.ToArray(),
                AllowedExtensions = new[] { ".txt" } // .txtファイルのみを転送対象にする
            });
            var transfer = Options.Create(new TransferOptions
            {
                Mode = "ftp",
                Direction = "put",
                Host = "host",
                Username = "user",
                Password = "pass",
                RemotePath = "/remote",
                Concurrency = 1
            });
            var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
            var hash = Options.Create(new HashOptions { Algorithm = "MD5" });
            var cleanup = Options.Create(new CleanupOptions());

            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.GetRemoteHashAsync(It.IsAny<string>(), "MD5", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync("d41d8cd98f00b204e9800998ecf8427e");
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();

            var lifetime = new DummyLifetime();
            var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));

            // パフォーマンス測定
            var stopwatch = Stopwatch.StartNew();
            await worker.RunAsync(CancellationToken.None);
            stopwatch.Stop();

            // 100個の拡張子があっても処理が1秒以内に完了することを確認
            Assert.True(stopwatch.ElapsedMilliseconds < 1000, 
                $"Processing with many extensions took too long: {stopwatch.ElapsedMilliseconds}ms");

            // ファイルが正しく検出されアップロードされたことを確認
            mock.Verify(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), 
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    private class TestWorker : Worker
    {
        private readonly IFileTransferClient _client;
        public TestWorker(IOptions<WatchOptions> w, IOptions<TransferOptions> t, IOptions<RetryOptions> r, IOptions<HashOptions> h, IOptions<CleanupOptions> c, IServiceProvider sp, ILogger<Worker> l, IHostApplicationLifetime lifetime, IFileTransferClient client)
            : base(w, t, r, h, c, sp, l, lifetime)
        {
            _client = client;
        }

        protected override IFileTransferClient CreateClient() => _client;

        public Task RunAsync(CancellationToken token) => base.ExecuteAsync(token);
    }

    private class NoDisposeClient : IFileTransferClient
    {
        private readonly IFileTransferClient _inner;
        public NoDisposeClient(IFileTransferClient inner) => _inner = inner;
        public void Dispose() { }
        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => _inner.UploadAsync(localPath, remotePath, ct);
        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => _inner.DownloadAsync(remotePath, localPath, ct);
        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false) => _inner.GetRemoteHashAsync(remotePath, algorithm, ct, useServerCommand);
        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct) => _inner.ListFilesAsync(remotePath, ct);
        public Task DeleteAsync(string remotePath, CancellationToken ct) => _inner.DeleteAsync(remotePath, ct);
    }

    private class DummyLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}