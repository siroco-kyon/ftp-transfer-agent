using System.Net;
using System.Net.Sockets;
using System.Security;
using FluentFTP.Exceptions;
using Renci.SshNet.Common;
using Renci.SshNet;

namespace FtpTransferAgent.Services;

/// <summary>
/// 例外がリトライ可能かどうかを判定するユーティリティクラス
/// </summary>
public static class RetryableExceptionClassifier
{
    /// <summary>
    /// 指定された例外がリトライ可能かどうかを判定
    /// </summary>
    /// <param name="exception">判定対象の例外</param>
    /// <returns>リトライ可能な場合true</returns>
    public static bool IsRetryable(Exception exception)
    {
        return exception switch
        {
            // ネットワーク関連の例外（リトライ可能）
            SocketException => true,
            TimeoutException => true,
            HttpRequestException => true,
            SshConnectionException => true,
            // SshOperationTimeoutException は SshException を継承しており TimeoutException ではないため個別に指定
            SshOperationTimeoutException => true,
            // ハッシュ不一致は転送中の一過性破損で起きるため再転送で回復し得る
            HashMismatchException => true,
            FtpException ftpEx when IsRetryableFtpException(ftpEx) => true,

            // ファイルシステム関連の一時的な例外（リトライ可能）
            IOException ioEx when IsRetryableIOException(ioEx) => true,
            UnauthorizedAccessException => true, // ファイルロック等の一時的な問題の可能性

            // 設定やセキュリティ関連の例外（リトライ不可）
            ArgumentNullException => false, // より具体的な例外を先に配置
            ArgumentException => false,
            InvalidOperationException => false,
            DirectoryNotFoundException => false,
            SecurityException => false,

            // その他の例外は基底クラスをチェック
            _ => IsRetryableByInnerException(exception)
        };
    }

    /// <summary>
    /// FTP例外がリトライ可能かどうかを判定
    /// </summary>
    private static bool IsRetryableFtpException(FtpException ftpException)
    {
        // FluentFTPの例外メッセージやタイプに基づいて判定
        var message = ftpException.Message;

        // 一時的なエラーの可能性が高いメッセージパターン
        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("busy", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 設定や認証エラーの可能性が高いメッセージパターン
        if (message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("syntax", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 不明な場合は安全のためリトライする
        return true;
    }

    /// <summary>
    /// IO例外がリトライ可能かどうかを判定。
    /// HResult は Windows では Win32 エラーコード (0x8007xxxx)、Unix では .NET ランタイムが
    /// errno を設定する。両者の値域は重ならないため、例外がどちらの体系で生成されても
    /// 判定できるよう常に両方の集合をチェックする。
    /// </summary>
    private static bool IsRetryableIOException(IOException ioException)
    {
        return ioException.HResult switch
        {
            // Windows (Win32 エラーコード)
            unchecked((int)0x80070020) => true, // ERROR_SHARING_VIOLATION (ファイルが他のプロセスで使用中)
            unchecked((int)0x80070021) => true, // ERROR_LOCK_VIOLATION (ファイルがロックされている)
            unchecked((int)0x80070070) => true, // ERROR_DISK_FULL (ディスク容量不足)
            unchecked((int)0x8007006E) => true, // ERROR_OPEN_FAILED (ファイルオープン失敗)
            // Unix (errno)
            11 => true, // EAGAIN (リソースが一時的に利用不可)
            16 => true, // EBUSY (デバイス/リソースがビジー)
            26 => true, // ETXTBSY (テキストファイルがビジー)
            28 => true, // ENOSPC (空き容量なし。Windows 側の DISK_FULL と整合)
            _ => false
        };
    }

    /// <summary>
    /// 内部例外を再帰的にチェックしてリトライ可能性を判定
    /// </summary>
    private static bool IsRetryableByInnerException(Exception exception)
    {
        var innerException = exception.InnerException;
        if (innerException == null)
        {
            return false;
        }

        return IsRetryable(innerException);
    }

    /// <summary>
    /// 例外が「接続そのものが壊れた」ことを示すかを判定する。
    /// true の場合、その接続は再利用せず破棄して張り直すべき。
    /// ハッシュ不一致やローカルファイル系エラーは接続が生きているため false。
    /// </summary>
    /// <param name="exception">判定対象の例外</param>
    /// <returns>接続を破棄すべき場合 true</returns>
    public static bool IsConnectionBroken(Exception exception)
    {
        return exception switch
        {
            // ネットワーク/セッションが切れたことを示す例外
            SocketException => true,
            SshConnectionException => true,
            SshOperationTimeoutException => true,
            TimeoutException => true,
            ObjectDisposedException => true,
            FtpException ftpEx => IsConnectionRelatedFtpException(ftpEx),

            // 接続は生きている前提のエラーは接続を維持する
            HashMismatchException => false,
            // IOException はローカルファイル起因なら接続は生きているが、転送路の切断が
            // inner の SocketException 等としてラップされることもあるため inner を辿って判定する
            IOException ioEx => ioEx.InnerException is { } ioInner && IsConnectionBroken(ioInner),
            UnauthorizedAccessException => false,

            // その他は内部例外を辿って判定する
            _ => exception.InnerException is { } inner && IsConnectionBroken(inner)
        };
    }

    /// <summary>
    /// FTP 例外が接続断に起因するかをメッセージから判定する
    /// </summary>
    private static bool IsConnectionRelatedFtpException(FtpException ftpException)
    {
        var message = ftpException.Message;
        return message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("broken", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("reset", StringComparison.OrdinalIgnoreCase);
    }
}