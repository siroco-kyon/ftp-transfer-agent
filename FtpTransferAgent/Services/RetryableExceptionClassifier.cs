using System.Net;
using System.Net.Sockets;
using FluentFTP.Exceptions;
using Renci.SshNet.Common;

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
            FtpException ftpEx when IsRetryableFtpException(ftpEx) => true,
            
            // ファイルシステム関連の一時的な例外（リトライ可能）
            IOException ioEx when IsRetryableIOException(ioEx) => true,
            UnauthorizedAccessException => true, // ファイルロック等の一時的な問題の可能性
            
            // 設定やセキュリティ関連の例外（リトライ不可）
            ArgumentException => false,
            ArgumentNullException => false,
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
        // FTPの応答コードに基づいて判定
        var code = ftpException.CompletionCode;
        return code switch
        {
            "421" => true,  // Service not available, closing control connection
            "425" => true,  // Can't open data connection
            "426" => true,  // Connection closed; transfer aborted
            "450" => true,  // Requested file action not taken. File unavailable (e.g., file busy)
            "451" => true,  // Requested action aborted: local error in processing
            "452" => true,  // Requested action not taken. Insufficient storage space in system
            "500" => false, // Syntax error, command unrecognized
            "501" => false, // Syntax error in parameters or arguments
            "502" => false, // Command not implemented
            "503" => false, // Bad sequence of commands
            "530" => false, // Not logged in
            "550" => false, // Requested action not taken. File unavailable (e.g., file not found, no access)
            "551" => false, // Requested action aborted: page type unknown
            "552" => false, // Requested file action aborted. Exceeded storage allocation
            "553" => false, // Requested action not taken. File name not allowed
            _ => true       // 不明なコードは安全のためリトライする
        };
    }
    
    /// <summary>
    /// IO例外がリトライ可能かどうかを判定
    /// </summary>
    private static bool IsRetryableIOException(IOException ioException)
    {
        // Win32エラーコードに基づいて判定
        var hResult = ioException.HResult;
        return hResult switch
        {
            unchecked((int)0x80070020) => true, // ERROR_SHARING_VIOLATION (ファイルが他のプロセスで使用中)
            unchecked((int)0x80070021) => true, // ERROR_LOCK_VIOLATION (ファイルがロックされている)
            unchecked((int)0x80070070) => true, // ERROR_DISK_FULL (ディスク容量不足)
            unchecked((int)0x8007006E) => true, // ERROR_OPEN_FAILED (ファイルオープン失敗)
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
}