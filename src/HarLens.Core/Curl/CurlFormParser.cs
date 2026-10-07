using System.Text;

namespace HarLens.Core.Curl;

/// <summary>One multipart part described by a <c>-F</c> or <c>--form-string</c> argument.</summary>
internal sealed class CurlFormField
{
    public string Name { get; set; } = "";

    public string? Value { get; set; }

    /// <summary>Path after <c>@</c> (upload) or <c>&lt;</c> (content as value), as written.</summary>
    public string? FilePath { get; set; }

    /// <summary>True for <c>name=&lt;file</c>.</summary>
    public bool FileContentAsValue { get; set; }

    public string? FileName { get; set; }

    public string? ContentType { get; set; }
}

/// <summary>
/// Parses curl's <c>-F</c> syntax the way curl's tool_formparse.c does: <c>name=value</c>, <c>name=@file</c>,
/// <c>name=&lt;file</c>, the attributes <c>;type=</c> and <c>;filename=</c>, and double-quoted words with
/// <c>\"</c> and <c>\\</c> escapes. Unquoted words end at <c>;</c> (and at <c>,</c> for file names) and lose
/// surrounding blanks.
/// </summary>
internal static class CurlFormParser
{
    public static List<CurlFormField> Parse(string input, bool literal, WarningSink warnings)
    {
        var fields = new List<CurlFormField>();
        var eq = input.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            warnings.Add($"The form field '{input}' has no '=' and was ignored.");
            return fields;
        }

        var name = input[..eq];
        var content = input[(eq + 1)..];
        if (literal)
        {
            fields.Add(new CurlFormField { Name = name, Value = content });
            return fields;
        }

        if (content.StartsWith('(') || (name.Length == 0 && content == ")"))
        {
            warnings.Add($"Nested multipart form syntax '{input}' is not supported and was ignored.");
            return fields;
        }

        var p = 0;
        if (content.StartsWith('@'))
        {
            p = 1;
            while (true)
            {
                var part = ParsePart(content, ref p, ',', allowFileName: true, warnings);
                fields.Add(new CurlFormField
                {
                    Name = name,
                    FilePath = part.Data,
                    FileName = part.FileName,
                    ContentType = part.Type,
                });
                if (part.Separator != ',')
                {
                    WarnGarbage(part.Separator, content, p, warnings);
                    break;
                }

                p++;
            }

            if (fields.Count > 1)
            {
                warnings.Add($"The form field '{name}' lists {fields.Count} files; curl sends them as one multipart/mixed part, HarLens imported them as separate parts.");
            }

            return fields;
        }

        if (content.StartsWith('<'))
        {
            p = 1;
            var part = ParsePart(content, ref p, '\0', allowFileName: false, warnings);
            fields.Add(new CurlFormField
            {
                Name = name,
                FilePath = part.Data,
                FileContentAsValue = true,
                ContentType = part.Type,
            });
            WarnGarbage(part.Separator, content, p, warnings);
            return fields;
        }

        var text = ParsePart(content, ref p, '\0', allowFileName: true, warnings);
        fields.Add(new CurlFormField
        {
            Name = name,
            Value = text.Data,
            FileName = text.FileName,
            ContentType = text.Type,
        });
        WarnGarbage(text.Separator, content, p, warnings);
        return fields;
    }

    private static void WarnGarbage(char separator, string content, int p, WarningSink warnings)
    {
        if (separator != '\0' && p < content.Length)
        {
            warnings.Add($"Text at the end of a form field was ignored: '{content[p..]}'.");
        }
    }

    private readonly record struct Part(string Data, string? Type, string? FileName, char Separator);

    private static bool IsCurlSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary>Port of curl's get_param_part.</summary>
    private static Part ParsePart(string s, ref int p, char endChar, bool allowFileName, WarningSink warnings)
    {
        while (p < s.Length && IsCurlSpace(s[p]))
        {
            p++;
        }

        var data = ReadWord(s, ref p, endChar, warnings, out var quoted);
        if (!quoted)
        {
            data = data.TrimEnd(' ', '\t', '\n', '\v', '\f', '\r');
        }

        string? type = null;
        string? fileName = null;
        var typeStart = -1;
        var typeEnd = -1;
        var sep = At(s, p);
        while (sep == ';')
        {
            p++;
            while (p < s.Length && IsCurlSpace(s[p]))
            {
                p++;
            }

            var rest = s.AsSpan(p);
            if (typeEnd < 0 && rest.StartsWith("type=", StringComparison.OrdinalIgnoreCase))
            {
                p += 5;
                while (p < s.Length && IsCurlSpace(s[p]))
                {
                    p++;
                }

                typeStart = p;
                while (p < s.Length && "()<>@,;:\\\"[]?=\r\n ".IndexOf(s[p], StringComparison.Ordinal) < 0)
                {
                    p++;
                }

                typeEnd = p;
                type = s[typeStart..typeEnd];
                sep = At(s, p);
            }
            else if (rest.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
            {
                typeEnd = -1;
                p += 9;
                while (p < s.Length && IsCurlSpace(s[p]))
                {
                    p++;
                }

                var name = ReadWord(s, ref p, endChar, warnings, out var nameQuoted);
                if (!nameQuoted)
                {
                    name = name.TrimEnd(' ', '\t', '\n', '\v', '\f', '\r');
                }

                if (allowFileName)
                {
                    fileName = name;
                }
                else
                {
                    warnings.Add($"A filename is not allowed for a form field read with '<'; '{name}' was ignored.");
                }

                sep = At(s, p);
            }
            else if (rest.StartsWith("headers=", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("encoder=", StringComparison.OrdinalIgnoreCase))
            {
                typeEnd = -1;
                var attribute = s[p..(p + 7)];
                p += 8;
                var value = ReadWord(s, ref p, endChar, warnings, out _);
                warnings.Add($"The form attribute {attribute}={value} is not supported and was ignored.");
                sep = At(s, p);
            }
            else if (typeEnd >= 0)
            {
                // More content-type parameters, for example ";charset=utf-8".
                var end = p;
                while (p < s.Length && s[p] != ';' && s[p] != endChar)
                {
                    if (!IsCurlSpace(s[p]))
                    {
                        end = p + 1;
                    }

                    p++;
                }

                typeEnd = end;
                type = s[typeStart..typeEnd];
                sep = At(s, p);
            }
            else
            {
                var unknown = ReadWord(s, ref p, endChar, warnings, out _);
                if (unknown.Length > 0)
                {
                    warnings.Add($"The unknown form attribute '{unknown}' was ignored.");
                }

                sep = At(s, p);
            }
        }

        return new Part(data, type, fileName, sep);
    }

    /// <summary>Port of curl's get_param_word.</summary>
    private static string ReadWord(string s, ref int p, char endChar, WarningSink warnings, out bool quoted)
    {
        var start = p;
        if (At(s, p) == '"')
        {
            var sb = new StringBuilder();
            var q = p + 1;
            while (q < s.Length)
            {
                if (s[q] == '\\' && q + 1 < s.Length && s[q + 1] is '\\' or '"')
                {
                    sb.Append(s[q + 1]);
                    q += 2;
                    continue;
                }

                if (s[q] == '"')
                {
                    q++;
                    var trailing = false;
                    while (q < s.Length && s[q] != ';' && s[q] != endChar)
                    {
                        trailing |= !IsCurlSpace(s[q]);
                        q++;
                    }

                    if (trailing)
                    {
                        warnings.Add("Text after a quoted form value was ignored.");
                    }

                    p = q;
                    quoted = true;
                    return sb.ToString();
                }

                sb.Append(s[q]);
                q++;
            }

            // No closing quote: curl reads the word as unquoted.
        }

        while (p < s.Length && s[p] != ';' && s[p] != endChar)
        {
            p++;
        }

        quoted = false;
        return s[start..p];
    }
}
