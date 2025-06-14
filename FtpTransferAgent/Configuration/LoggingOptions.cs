namespace FtpTransferAgent.Configuration;

public class LoggingOptions
{
    public string Level { get; set; } = "Information";
    public string RollingFilePath { get; set; } = string.Empty;
}
