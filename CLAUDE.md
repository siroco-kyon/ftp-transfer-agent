# CLAUDE.md
必ず日本語で回答してください。
This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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
# Install Python test dependencies first
python -m pip install pyftpdlib

# Run all tests
dotnet test FtpTransferAgent.Tests/FtpTransferAgent.Tests.csproj --no-build --configuration Release --verbosity normal

# Run tests with debug output
dotnet test --verbosity diagnostic

# Run a specific test
dotnet test --filter "TestMethodName"
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

- **Program.cs**: Entry point that configures DI, logging, and starts the Worker service
- **Worker.cs**: Main background service that orchestrates the transfer process using a Channel-based queue
- **Services/**: Contains transfer clients and utilities
  - `FtpClient.cs` / `SftpClientWrapper.cs`: Protocol-specific implementations
  - `TransferQueue.cs`: Manages parallel transfers with retry logic using Polly
  - `HashUtil.cs`: File integrity verification (MD5/SHA256)
- **Configuration/**: Strongly-typed configuration classes bound from appsettings.json
- **Logging/**: Custom logging providers for rolling file logs and email notifications

### Key Design Patterns

1. **Channel-based Queue**: Uses `System.Threading.Channels` for producer-consumer pattern
2. **Options Pattern**: Configuration is injected via `IOptions<T>` with validation
3. **Dependency Injection**: Standard .NET DI container for service resolution
4. **Background Service**: Implements `BackgroundService` but designed for one-time execution
5. **Strategy Pattern**: Different transfer clients (`IFileTransferClient`) for FTP vs SFTP

### Transfer Flow

1. **Initialization**: Load config, create transfer client (FTP/SFTP), start transfer queue
2. **File Enumeration**: Based on `Direction` setting:
   - "put": Enumerate local files from `Watch.Path`
   - "get": List remote files from `RemotePath`
   - "both": Do get first, then put
3. **Queue Processing**: Files are processed in parallel (configurable concurrency)
4. **Transfer**: Each file is uploaded/downloaded with temp name, then renamed
5. **Verification**: Hash comparison between local and remote files
6. **Cleanup**: Optional deletion of source files after successful verification
7. **Exit**: Application terminates after all transfers complete

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

- **Integration Tests**: Use `pyftpdlib` to spin up test FTP servers
- **Unit Tests**: Mock-based testing of individual components
- **Test Framework**: xUnit with Moq for mocking
- **CI/CD**: GitHub Actions runs tests with Python FTP server setup

## Important Notes

- **Not a Service**: This is a batch processor, not a continuous monitoring service
- **No Folder Watching**: FolderWatcher has been completely removed - use task scheduler for regular execution
- **One-Shot Processing**: Only processes files present at startup time
- **Reliable Parallel Transfers**: Supports 1-16 concurrent transfers with duplicate prevention
- **Local Hash Verification**: Always calculates hashes locally for maximum reliability, never depends on server commands
- **Exponential Backoff Retry**: Uses Polly with exponential backoff for robust retry behavior
- **Fail-Fast on Hash Mismatch**: Throws exceptions immediately on hash verification failure to ensure data integrity
- **Logging**: Supports console, rolling file, and email notification logging