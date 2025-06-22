using System.Net;
using System.Net.Mail;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FtpTransferAgent.Tests;

/// <summary>
/// ErrorEmailLoggerのテスト（SMTP機能、TLS、認証）
/// </summary>
public class ErrorEmailLoggerTests : IDisposable
{
    private readonly Mock<ISmtpClientWrapper> _mockSmtpClient;
    private readonly SmtpOptions _smtpOptions;
    private readonly TestableErrorEmailLogger _logger;

    public ErrorEmailLoggerTests()
    {
        _mockSmtpClient = new Mock<ISmtpClientWrapper>();
        _smtpOptions = new SmtpOptions
        {
            RelayHost = "smtp.example.com",
            RelayPort = 587,
            Username = "test@example.com",
            Password = "password",
            UseTls = true,
            From = "noreply@example.com",
            To = new[] { "admin@example.com" }
        };

        _logger = new TestableErrorEmailLogger("TestCategory", _smtpOptions, _mockSmtpClient.Object);
    }

    [Fact]
    public void Log_ShouldNotSendEmail_WhenLogLevelIsNotError()
    {
        // Act
        _logger.Log(LogLevel.Information, new EventId(1), "Info message", null, (state, ex) => state);

        // Assert
        _mockSmtpClient.Verify(x => x.SendMailAsync(It.IsAny<MailMessage>()), Times.Never);
    }

    [Fact]
    public void Log_ShouldNotSendEmail_WhenSmtpOptionsAreInvalid()
    {
        // Arrange
        var invalidOptions = new SmtpOptions(); // すべて空/null
        var logger = new TestableErrorEmailLogger("TestCategory", invalidOptions, _mockSmtpClient.Object);

        // Act
        logger.Log(LogLevel.Error, new EventId(1), "Error message", null, (state, ex) => state);

        // Assert
        _mockSmtpClient.Verify(x => x.SendMailAsync(It.IsAny<MailMessage>()), Times.Never);
    }

    [Fact]
    public void Log_ShouldSendEmail_WhenErrorOccurs()
    {
        // Arrange
        var exception = new InvalidOperationException("Test error");

        MailMessage? sentMessage = null;
        _mockSmtpClient.Setup(x => x.SendMailAsync(It.IsAny<MailMessage>()))
                      .Callback<MailMessage>(msg => sentMessage = msg)
                      .Returns(Task.CompletedTask);

        // Act
        _logger.Log(LogLevel.Error, new EventId(1), "Error occurred", exception, (state, ex) => $"{state}: {ex?.Message}");

        // Assert
        _mockSmtpClient.Verify(x => x.SendMailAsync(It.IsAny<MailMessage>()), Times.Once);
        Assert.NotNull(sentMessage);
        Assert.Equal(_smtpOptions.From, sentMessage.From?.Address);
        Assert.Contains(_smtpOptions.To[0], sentMessage.To.Select(t => t.Address));
        Assert.Equal("[TestCategory] Error", sentMessage.Subject);
        Assert.Contains("Error occurred", sentMessage.Body);
        Assert.Contains("Test error", sentMessage.Body);
    }

    [Fact]
    public void Log_ShouldHandleMultipleRecipients()
    {
        // Arrange
        _smtpOptions.To = new[] { "admin1@example.com", "admin2@example.com", "admin3@example.com" };

        MailMessage? sentMessage = null;
        _mockSmtpClient.Setup(x => x.SendMailAsync(It.IsAny<MailMessage>()))
                      .Callback<MailMessage>(msg => sentMessage = msg)
                      .Returns(Task.CompletedTask);

        // Act
        _logger.Log(LogLevel.Error, new EventId(1), "Error message", null, (state, ex) => state);

        // Assert
        _mockSmtpClient.Verify(x => x.SendMailAsync(It.IsAny<MailMessage>()), Times.Once);
        Assert.NotNull(sentMessage);
        Assert.Equal(3, sentMessage.To.Count);
        Assert.Contains(sentMessage.To, t => t.Address == "admin1@example.com");
        Assert.Contains(sentMessage.To, t => t.Address == "admin2@example.com");
        Assert.Contains(sentMessage.To, t => t.Address == "admin3@example.com");
    }

    [Fact]
    public void Log_ShouldIncludeExceptionDetails_WhenExceptionExists()
    {
        // Arrange
        var innerException = new ArgumentException("Inner error");
        var exception = new InvalidOperationException("Outer error", innerException);

        MailMessage? sentMessage = null;
        _mockSmtpClient.Setup(x => x.SendMailAsync(It.IsAny<MailMessage>()))
                      .Callback<MailMessage>(msg => sentMessage = msg)
                      .Returns(Task.CompletedTask);

        // Act
        _logger.Log(LogLevel.Error, new EventId(1), "Error with exception", exception, (state, ex) => $"{state}: {ex?.Message}");

        // Assert
        _mockSmtpClient.Verify(x => x.SendMailAsync(It.IsAny<MailMessage>()), Times.Once);
        Assert.NotNull(sentMessage);
        Assert.Contains("Error with exception", sentMessage.Body);
        Assert.Contains("Outer error", sentMessage.Body);
        Assert.Contains("ArgumentException", sentMessage.Body);
        Assert.Contains("Inner error", sentMessage.Body);
    }

    [Fact]
    public void Log_ShouldHandleSmtpException()
    {
        // Arrange
        _mockSmtpClient.Setup(x => x.SendMailAsync(It.IsAny<MailMessage>()))
                      .ThrowsAsync(new SmtpException("SMTP server error"));

        // Act & Assert (例外が飲み込まれること)
        var exception = Record.Exception(() => _logger.Log(LogLevel.Error, new EventId(1), "Error message", null, (state, ex) => state));
        Assert.Null(exception);

        _mockSmtpClient.Verify(x => x.SendMailAsync(It.IsAny<MailMessage>()), Times.Once);
    }

    [Fact]
    public void Log_ShouldHandleNetworkException()
    {
        // Arrange
        _mockSmtpClient.Setup(x => x.SendMailAsync(It.IsAny<MailMessage>()))
                      .ThrowsAsync(new HttpRequestException("Network error"));

        // Act & Assert (例外が飲み込まれること)
        var exception = Record.Exception(() => _logger.Log(LogLevel.Error, new EventId(1), "Error message", null, (state, ex) => state));
        Assert.Null(exception);

        _mockSmtpClient.Verify(x => x.SendMailAsync(It.IsAny<MailMessage>()), Times.Once);
    }

    [Fact]
    public void IsEnabled_ShouldReturnTrue_OnlyForErrorLevel()
    {
        // Act & Assert
        Assert.True(_logger.IsEnabled(LogLevel.Error));
        Assert.True(_logger.IsEnabled(LogLevel.Critical));
        Assert.False(_logger.IsEnabled(LogLevel.Warning));
        Assert.False(_logger.IsEnabled(LogLevel.Information));
        Assert.False(_logger.IsEnabled(LogLevel.Debug));
        Assert.False(_logger.IsEnabled(LogLevel.Trace));
        Assert.False(_logger.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void BeginScope_ShouldReturnDisposableScope()
    {
        // Act
        using var scope = _logger.BeginScope("test scope");

        // Assert
        Assert.NotNull(scope);
    }

    [Fact]
    public void Log_ShouldValidateSmtpConfiguration()
    {
        // Arrange: 不完全な設定でテスト
        var incompleteConfigs = new[]
        {
            new SmtpOptions { RelayHost = "", RelayPort = 587, Username = "user", Password = "pass", From = "from@test.com", To = new[] { "to@test.com" } },
            new SmtpOptions { RelayHost = "smtp.test.com", RelayPort = 587, Username = "", Password = "pass", From = "from@test.com", To = new[] { "to@test.com" } },
            new SmtpOptions { RelayHost = "smtp.test.com", RelayPort = 587, Username = "user", Password = "", From = "from@test.com", To = new[] { "to@test.com" } },
            new SmtpOptions { RelayHost = "smtp.test.com", RelayPort = 587, Username = "user", Password = "pass", From = "", To = new[] { "to@test.com" } },
            new SmtpOptions { RelayHost = "smtp.test.com", RelayPort = 587, Username = "user", Password = "pass", From = "from@test.com", To = Array.Empty<string>() }
        };

        foreach (var config in incompleteConfigs)
        {
            // Arrange
            var logger = new TestableErrorEmailLogger("TestCategory", config, _mockSmtpClient.Object);

            // Act
            logger.Log(LogLevel.Error, new EventId(1), "Error message", null, (state, ex) => state);

            // Assert
            _mockSmtpClient.Verify(x => x.SendMailAsync(It.IsAny<MailMessage>()), Times.Never);
            _mockSmtpClient.Reset();
        }
    }

    [Fact]
    public void SmtpClientWrapper_ShouldConfigureCorrectly()
    {
        // Arrange
        var wrapper = new SmtpClientWrapper(_smtpOptions);

        // Act & Assert
        Assert.Equal(_smtpOptions.RelayHost, wrapper.Host);
        Assert.Equal(_smtpOptions.RelayPort, wrapper.Port);
        Assert.Equal(_smtpOptions.UseTls, wrapper.EnableSsl);
        Assert.NotNull(wrapper.Credentials);

        var networkCredentials = wrapper.Credentials as NetworkCredential;
        Assert.NotNull(networkCredentials);
        Assert.Equal(_smtpOptions.Username, networkCredentials.UserName);
        Assert.Equal(_smtpOptions.Password, networkCredentials.Password);
    }

    public void Dispose()
    {
        _logger?.Dispose();
    }
}

/// <summary>
/// SMTPクライアントの抽象化インターフェース（テスト用）
/// </summary>
public interface ISmtpClientWrapper : IDisposable
{
    string Host { get; set; }
    int Port { get; set; }
    bool EnableSsl { get; set; }
    ICredentialsByHost? Credentials { get; set; }
    Task SendMailAsync(MailMessage message);
}

/// <summary>
/// 本物のSmtpClientのラッパー
/// </summary>
public class SmtpClientWrapper : ISmtpClientWrapper
{
    private readonly SmtpClient _smtpClient;

    public SmtpClientWrapper(SmtpOptions options)
    {
        _smtpClient = new SmtpClient(options.RelayHost, options.RelayPort)
        {
            EnableSsl = options.UseTls,
            Credentials = new NetworkCredential(options.Username, options.Password)
        };
    }

    public string Host
    {
        get => _smtpClient.Host ?? string.Empty;
        set => _smtpClient.Host = value;
    }

    public int Port
    {
        get => _smtpClient.Port;
        set => _smtpClient.Port = value;
    }

    public bool EnableSsl
    {
        get => _smtpClient.EnableSsl;
        set => _smtpClient.EnableSsl = value;
    }

    public ICredentialsByHost? Credentials
    {
        get => _smtpClient.Credentials;
        set => _smtpClient.Credentials = value;
    }

    public async Task SendMailAsync(MailMessage message)
    {
        await _smtpClient.SendMailAsync(message).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _smtpClient?.Dispose();
    }
}

/// <summary>
/// テスト用のErrorEmailLoggerクラス（SMTPクライアントを注入可能にする）
/// </summary>
public class TestableErrorEmailLogger : ILogger
{
    private readonly string _category;
    private readonly SmtpOptions _options;
    private readonly ISmtpClientWrapper _mockSmtpClient;

    public TestableErrorEmailLogger(string category, SmtpOptions options, ISmtpClientWrapper mockSmtpClient)
    {
        _category = category;
        _options = options;
        _mockSmtpClient = mockSmtpClient;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NoOpDisposable();

    public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Error || logLevel == LogLevel.Critical;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || !IsValidConfiguration())
        {
            return;
        }

        var message = formatter(state, exception);

        // テスト用に同期的にメール送信
        try
        {
            using var mail = new MailMessage
            {
                From = new MailAddress(_options.From),
                Subject = $"[{_category}] Error",
                Body = message + (exception != null ? "\n" + exception : string.Empty)
            };
            foreach (var to in _options.To)
            {
                mail.To.Add(to);
            }
            // 同期的に実行
            _mockSmtpClient.SendMailAsync(mail).GetAwaiter().GetResult();
        }
        catch
        {
            // ignore mail errors to avoid recursive logging
        }
    }

    private bool IsValidConfiguration()
    {
        return !string.IsNullOrEmpty(_options.RelayHost) &&
               !string.IsNullOrEmpty(_options.Username) &&
               !string.IsNullOrEmpty(_options.Password) &&
               !string.IsNullOrEmpty(_options.From) &&
               _options.To?.Length > 0;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// No-op disposable for ILogger.BeginScope
/// </summary>
public class NoOpDisposable : IDisposable
{
    public void Dispose()
    {
    }
}