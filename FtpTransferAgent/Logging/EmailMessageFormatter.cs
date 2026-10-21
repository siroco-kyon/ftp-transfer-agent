using System.Text;
using System.Text.RegularExpressions;
using FtpTransferAgent.Configuration;

namespace FtpTransferAgent.Logging;

internal sealed class EmailMessageFormatter
{
    internal const string DefaultTemplate = "[{JobName}][実行元: {SourceServerName}] {Summary}";
    private const int MaxSubjectLength = 200;
    private static readonly Regex Tokens = new(@"\{(JobName|SourceServerName|Summary)\}", RegexOptions.CultureInvariant);
    private readonly SmtpOptions _options;
    private readonly string _template;
    private readonly string _server;

    public EmailMessageFormatter(SmtpOptions options)
    {
        _options = options;
        _server = string.IsNullOrWhiteSpace(options.SourceServerName) ? Environment.MachineName : options.SourceServerName.Trim();
        _template = string.IsNullOrWhiteSpace(options.SubjectTemplate) ? DefaultTemplate : options.SubjectTemplate;
        // 値を埋める前に検査する。値自体に含まれる {Summary} 等は再展開しない。
        var remainder = Tokens.Replace(_template, string.Empty);
        if (_template.Length > 1000 || _template.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029')
            || remainder.Contains('{') || remainder.Contains('}'))
        {
            _template = DefaultTemplate;
            if (options.Enabled)
                Console.Error.WriteLine("WARNING: Invalid Smtp.SubjectTemplate. Using the default email subject template.");
        }
    }

    public (string Subject, string Body) Format(string category, string message, Exception? exception, EmailTransferContext? context)
    {
        var originalBody = message + (exception is null ? string.Empty : "\n" + exception);
        if (!_options.UsesCustomSubject)
            return ($"[{category}] Error", originalBody);

        var job = string.IsNullOrWhiteSpace(_options.JobName) ? "FtpTransferAgent" : _options.JobName.Trim();
        var summary = context?.Summary ?? "転送処理でエラー";
        var subject = Tokens.Replace(_template, match => match.Groups[1].Value switch
        {
            "JobName" => job,
            "SourceServerName" => _server,
            _ => summary
        });
        if (!string.IsNullOrWhiteSpace(_options.SubjectPrefix))
            subject = $"[{_options.SubjectPrefix.Trim()}]" + subject;

        var body = $"業務: {SingleLine(job)}\n実行元: {SingleLine(_server)}\n"
            + (context is null ? string.Empty : $"転送先・取得元: {SingleLine(context.Peer)}\n接続先: {SingleLine(context.Endpoint)}\n")
            + $"概要: {SingleLine(summary)}\n\n{originalBody}";
        return (LimitSubject(SingleLine(subject)), body);
    }

    private static string SingleLine(string value) => string.Concat(value.EnumerateRunes()
        .Select(rune => Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029 ? " " : rune.ToString())).Trim();

    private static string LimitSubject(string subject)
    {
        var runes = subject.EnumerateRunes().ToArray();
        return runes.Length <= MaxSubjectLength ? subject
            : string.Concat(runes.Take(MaxSubjectLength - 1).Select(rune => rune.ToString())) + "…";
    }
}
