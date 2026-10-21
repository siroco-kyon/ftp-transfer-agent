namespace FtpTransferAgent.Tests;

// SMTP の実受信とプロセス終了待機は、他のテストの同期ブロックや負荷から分離する。
[CollectionDefinition("SMTP integration", DisableParallelization = true)]
public sealed class SmtpIntegrationCollection;
