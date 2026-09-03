using System.Net.Sockets;
using FluentFTP.Exceptions;
using FtpTransferAgent.Services;
using Renci.SshNet.Common;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="RetryableExceptionClassifier.IsConnectionBroken"/> の判定を検証する。
/// 特に「接続断が IOException の inner にラップされる」典型ケースで、
/// 壊れた接続を誤って再利用しないことを担保する。
/// </summary>
public class RetryableExceptionClassifierTests
{
    [Fact]
    public void IsConnectionBroken_SocketException_True()
        => Assert.True(RetryableExceptionClassifier.IsConnectionBroken(new SocketException()));

    [Fact]
    public void IsConnectionBroken_SshConnectionException_True()
        => Assert.True(RetryableExceptionClassifier.IsConnectionBroken(new SshConnectionException("disconnected")));

    [Fact]
    public void IsConnectionBroken_TimeoutException_True()
        => Assert.True(RetryableExceptionClassifier.IsConnectionBroken(new TimeoutException()));

    [Fact]
    public void IsConnectionBroken_ObjectDisposedException_True()
        => Assert.True(RetryableExceptionClassifier.IsConnectionBroken(new ObjectDisposedException("client")));

    // 指摘の核心: SocketException が IOException の inner に入る典型的な転送路切断
    [Fact]
    public void IsConnectionBroken_IOExceptionWithSocketInner_True()
        => Assert.True(RetryableExceptionClassifier.IsConnectionBroken(
            new IOException("Unable to read data from the transport connection", new SocketException())));

    [Fact]
    public void IsConnectionBroken_PlainIOException_False()
        => Assert.False(RetryableExceptionClassifier.IsConnectionBroken(new IOException("disk full")));

    [Fact]
    public void IsConnectionBroken_IOExceptionWithNonConnectionInner_False()
        => Assert.False(RetryableExceptionClassifier.IsConnectionBroken(
            new IOException("local file error", new ArgumentException("bad path"))));

    [Fact]
    public void IsRetryable_TransferFailed_True()
        => Assert.True(RetryableExceptionClassifier.IsRetryable(
            new TransferFailedException("FTP upload did not complete successfully (status=Failed)")));

    [Fact]
    public void IsConnectionBroken_TransferFailed_False()
        => Assert.False(RetryableExceptionClassifier.IsConnectionBroken(
            new TransferFailedException("upload size mismatch")));

    [Fact]
    public void IsConnectionBroken_HashMismatch_False()
        => Assert.False(RetryableExceptionClassifier.IsConnectionBroken(new HashMismatchException("hash mismatch")));

    [Fact]
    public void IsConnectionBroken_UnauthorizedAccess_False()
        => Assert.False(RetryableExceptionClassifier.IsConnectionBroken(new UnauthorizedAccessException()));

    [Fact]
    public void IsConnectionBroken_ArgumentException_False()
        => Assert.False(RetryableExceptionClassifier.IsConnectionBroken(new ArgumentException("config error")));

    [Fact]
    public void IsConnectionBroken_WrappedSocketException_True()
        => Assert.True(RetryableExceptionClassifier.IsConnectionBroken(new Exception("wrapper", new SocketException())));

    // --- IsRetryable: SSH/SFTP 例外の分類 ---
    // 汎用 SshException (SSH_FX_FAILURE 等) はサーバ側の一時要因の可能性があるためリトライする。
    // 恒久的な要因 (認証・権限・パス不在) はリトライしない。

    [Fact]
    public void IsRetryable_GenericSshException_True()
        => Assert.True(RetryableExceptionClassifier.IsRetryable(new SshException("Failure")));

    [Fact]
    public void IsRetryable_SshConnectionException_True()
        => Assert.True(RetryableExceptionClassifier.IsRetryable(new SshConnectionException("disconnected")));

    [Fact]
    public void IsRetryable_SshOperationTimeoutException_True()
        => Assert.True(RetryableExceptionClassifier.IsRetryable(new SshOperationTimeoutException("timeout")));

    [Fact]
    public void IsRetryable_SshAuthenticationException_False()
        => Assert.False(RetryableExceptionClassifier.IsRetryable(new SshAuthenticationException("bad credentials")));

    [Fact]
    public void IsRetryable_SftpPermissionDeniedException_False()
        => Assert.False(RetryableExceptionClassifier.IsRetryable(new SftpPermissionDeniedException("denied")));

    [Fact]
    public void IsRetryable_SftpPathNotFoundException_False()
        => Assert.False(RetryableExceptionClassifier.IsRetryable(new SftpPathNotFoundException("no such file")));

    // 汎用 SshException は接続自体は生きている扱い (プールの接続は再利用してよい)
    [Fact]
    public void IsConnectionBroken_GenericSshException_False()
        => Assert.False(RetryableExceptionClassifier.IsConnectionBroken(new SshException("Failure")));

    [Fact]
    public void IsConnectionBroken_FtpConnectionMessage_True()
        => Assert.True(RetryableExceptionClassifier.IsConnectionBroken(new FtpException("connection reset by peer")));

    [Fact]
    public void IsConnectionBroken_FtpNonConnectionMessage_False()
        => Assert.False(RetryableExceptionClassifier.IsConnectionBroken(new FtpException("login incorrect")));
}
