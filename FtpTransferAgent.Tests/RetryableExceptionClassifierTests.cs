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

    [Fact]
    public void IsConnectionBroken_FtpConnectionMessage_True()
        => Assert.True(RetryableExceptionClassifier.IsConnectionBroken(new FtpException("connection reset by peer")));

    [Fact]
    public void IsConnectionBroken_FtpNonConnectionMessage_False()
        => Assert.False(RetryableExceptionClassifier.IsConnectionBroken(new FtpException("login incorrect")));
}
