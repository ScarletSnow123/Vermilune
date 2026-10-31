using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Vermilune.Core;

/// <summary>把 Mojang 默认日志配置的 LegacyXMLLayout 控制台流转换成可读的 [时间] [线程/级别]: 消息。</summary>
public sealed class LegacyLogParser
{
    private readonly List<string> _buffer = new();
    private bool _inEvent;
    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);
    private string _message = "";
    private string _throwable = "";

    public IEnumerable<string> Feed(string line)
    {
        if (line.Contains("<log4j:Event", StringComparison.Ordinal))
        {
            FlushBuffer();
            _inEvent = true;
            _fields.Clear();
            _message = "";
            _throwable = "";
            ParseAttributes(line);
            return Array.Empty<string>();
        }

        if (!_inEvent)
        {
            if (line.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || line.StartsWith("<log4j:", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();
            return new[] { line };
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith("</log4j:Event>", StringComparison.Ordinal))
        {
            _inEvent = false;
            return new[] { Format() };
        }
        if (trimmed.StartsWith("<log4j:Message", StringComparison.Ordinal))
        {
            _message = ExtractCData(trimmed);
            return Array.Empty<string>();
        }
        if (trimmed.StartsWith("<log4j:Throwable", StringComparison.Ordinal))
        {
            _throwable = ExtractCData(trimmed);
            return Array.Empty<string>();
        }
        if (trimmed.StartsWith("<log4j:", StringComparison.Ordinal))
            return Array.Empty<string>();

        if (_buffer.Count < 40) _buffer.Add(line);
        return Array.Empty<string>();
    }

    public IEnumerable<string> Flush()
    {
        var result = new List<string>();
        if (_inEvent && (!string.IsNullOrEmpty(_message) || _fields.Count > 0)) result.Add(Format());
        FlushBuffer();
        _inEvent = false;
        return result;
    }

    private void FlushBuffer()
    {
        _buffer.Clear();
    }

    private void ParseAttributes(string line)
    {
        foreach (Match m in Regex.Matches(line, @"([A-Za-z:]+)\s*=\s*""([^""]*)"""))
            _fields[m.Groups[1].Value] = WebUtility.HtmlDecode(m.Groups[2].Value);
    }

    private string Format()
    {
        var time = "";
        if (long.TryParse(_fields.GetValueOrDefault("timestamp"), out var ms))
        {
            try { time = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss"); }
            catch { }
        }
        var level = _fields.GetValueOrDefault("level", "INFO");
        var thread = _fields.GetValueOrDefault("thread", "");
        var logger = _fields.GetValueOrDefault("logger", "");
        var msg = _message;
        if (!string.IsNullOrEmpty(_throwable)) msg += Environment.NewLine + _throwable;
        var prefix = string.IsNullOrEmpty(thread) ? $"[{time}] [{level}]" : $"[{time}] [{thread}/{level}]:";
        var text = $"{prefix} {msg}";
        return text.TrimEnd();
    }

    private static string ExtractCData(string line)
    {
        var start = line.IndexOf("<![CDATA[", StringComparison.Ordinal);
        if (start >= 0)
        {
            start += "<![CDATA[".Length;
            var end = line.IndexOf("]]>", start, StringComparison.Ordinal);
            if (end >= 0) return line[start..end];
        }
        // <log4j:Message>text</log4j:Message>
        var m = Regex.Match(line, @">([\s\S]*)</log4j:Message>", RegexOptions.IgnoreCase);
        if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value);
        return line;
    }
}
