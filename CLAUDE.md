# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

必ず日本語で回答してください。

## Development Commands

### Build and Run
```bash
# Restore dependencies
dotnet restore

# Build the project
dotnet build --configuration Release

# Run the application
dotnet run --project FtpTransferAgent

# Format code
dotnet format --no-restore --verify-no-changes
```

### Testing
```bash
# Install Python test dependencies first (required for FTP server simulation)
python3 -m pip install pyftpdlib

# Run all tests
dotnet test FtpTransferAgent.Tests/FtpTransferAgent.Tests.csproj --no-build --configuration Release --verbosity normal

# Run tests with debug output
dotnet test --verbosity diagnostic

# Run a specific test class
dotnet test --filter "ClassName=NetworkFailureSimulationTests"

# Run a specific test method
dotnet test --filter "TestMethodName"

# Run tests matching a pattern
dotnet test --filter "DisplayName~Integration"
```

### Publishing
```bash
# Publish for specific runtime
dotnet publish -c Release -r win-x64 --self-contained
dotnet publish -c Release -r linux-x64 --self-contained
dotnet publish -c Release -r osx-x64 --self-contained
```

## Architecture Overview

This is a .NET 8 console application that performs batch FTP/SFTP file transfers. It's designed as a **one-shot batch processor** that runs once and exits, not a continuous service.

### Core Components

- **Program.cs**: Entry point that configures DI, logging, validates configuration, and starts the Worker service
- **Worker.cs**: Main background service that orchestrates the transfer process using a Channel-based queue with performance monitoring
- **Services/**: Contains transfer clients and utilities
  - `AsyncFtpClientWrapper` (FtpClient.cs) / `SftpClientWrapper.cs`: Protocol-specific implementations
  - `TransferQueue.cs`: Manages parallel transfers with intelligent retry logic and real-time statistics
  - `HashUtil.cs`: File integrity verification (MD5/SHA256) with stream support
  - `RetryableExceptionClassifier.cs`: Smart exception classification for retry decisions
- **Configuration/**: Strongly-typed configuration classes with comprehensive validation
  - `ConfigurationValidator.cs`: Cross-setting validation and change impact assessment
- **Logging/**: Custom logging providers for rolling file logs and email notifications

### Key Design Patterns

1. **Channel-based Queue**: Uses `System.Threading.Channels` for producer-consumer pattern with bounded capacity
2. **Options Pattern**: Configuration is injected via `IOptions<T>` with comprehensive validation at startup
3. **Dependency Injection**: Uses `ActivatorUtilities.CreateInstance` for factory pattern instead of direct instantiation
4. **Background Service**: Implements `BackgroundService` but designed for one-time execution with performance monitoring
5. **Strategy Pattern**: Different transfer clients (`IFileTransferClient`) for FTP vs SFTP
6. **Smart Retry**: Exception classification determines retryable vs non-retryable failures
7. **Real-time Monitoring**: Statistics tracking with memory usage and long-running transfer detection

### Transfer Flow

1. **Startup Validation**: Comprehensive configuration validation with error/warning reporting
2. **Initialization**: Load config, create transfer client (FTP/SFTP), start transfer queue with monitoring
3. **File Enumeration**: Based on `Direction` setting:
   - "put": Enumerate local files from `Watch.Path` with extension filtering
   - "get": List remote files from `RemotePath`
   - "both": Do get first, then put
4. **Queue Processing**: Files are processed in parallel (1-16 concurrent workers) with duplicate prevention
5. **Transfer**: Each file is uploaded/downloaded with temp name, then atomically renamed
6. **Hash Verification**: Always uses local calculation for reliability, with fail-fast on mismatch
7. **Smart Retry**: Network/temporary errors retry with exponential backoff; configuration/security errors fail immediately
8. **Cleanup**: Optional deletion of source files after successful verification
9. **Statistics**: Real-time progress logging with memory monitoring and final completion report
10. **Exit**: Application terminates after all transfers complete

### Configuration Structure

The application uses `appsettings.json` with these main sections:
- `Watch`: Local folder settings and file filters
- `Transfer`: FTP/SFTP connection and transfer settings  
- `Retry`: Error retry behavior using Polly
- `Hash`: Integrity verification settings (MD5/SHA256)
- `Cleanup`: Post-transfer file deletion options
- `Smtp`: Email notification for errors
- `Logging`: File logging with rolling behavior

### Testing Strategy

- **Integration Tests**: Use `pyftpdlib` to spin up test FTP servers for realistic network testing
- **Network Simulation Tests**: `NetworkFailureSimulationTests` - timeout, connection failures, concurrent processing
- **File Lock Tests**: `FileLockingTests` - file system concurrency, large file handling, memory efficiency
- **Configuration Tests**: `ConfigurationValidationAdvancedTests` - setting combinations, security warnings, impact assessment
- **Unit Tests**: Mock-based testing of individual components with comprehensive error scenarios
- **Test Framework**: xUnit with Moq for mocking
- **CI/CD**: GitHub Actions runs tests with Python FTP server setup, includes format verification

## Important Notes

- **Batch Processing Only**: One-shot execution that processes files present at startup and exits
- **Configuration Validation**: Comprehensive startup validation with cross-setting compatibility checks using `ConfigurationValidator`
- **Smart Error Handling**: Distinguishes between retryable network errors and non-retryable configuration/security errors using `RetryableExceptionClassifier`
- **Parallel Processing**: Thread-safe concurrent execution with proper exception isolation - one worker failure doesn't stop others
- **Performance Monitoring**: Real-time statistics with memory usage tracking, critical error tracking, and long-running transfer detection
- **Security Features**: Path traversal protection, FTP security warnings, private key validation
- **Reliable Transfers**: Atomic file operations with temporary names, fail-fast hash verification
- **Testing Coverage**: Extensive tests including `ParallelProcessingIntegrationTests`, network simulation, file locking, and configuration validation
- **Use Task Scheduler**: For continuous operation, schedule this batch processor at regular intervals

## Build and Testing Status
- **Build**: Successfully compiles with .NET 8
- **Dependencies**: All external library dependencies properly resolved (FluentFTP, SSH.NET, Polly)
- **Warnings**: Async/await warnings resolved in test code