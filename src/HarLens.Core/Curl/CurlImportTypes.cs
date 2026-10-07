using HarLens.Core.Http;

namespace HarLens.Core.Curl;

/// <summary>Shell quoting rules used to split a pasted curl command into arguments.</summary>
public enum CurlDialect
{
    /// <summary>Detect the dialect from the command text.</summary>
    Auto,

    /// <summary>POSIX shell (bash, zsh): Chrome and Firefox "Copy as cURL" on macOS and Linux, and "Copy as cURL (bash)" on Windows.</summary>
    Bash,

    /// <summary>Windows cmd.exe followed by the C runtime argument splitter: Chrome "Copy as cURL (cmd)".</summary>
    Cmd,

    /// <summary>PowerShell calling <c>curl.exe</c>.</summary>
    PowerShell,
}

public sealed class CurlParseOptions
{
    public CurlDialect Dialect { get; set; } = CurlDialect.Auto;

    /// <summary>
    /// Directory that relative <c>@file</c> references resolve against. When null, no referenced file is read
    /// or checked: each reference is reported as unresolved with a warning.
    /// </summary>
    public string? FileBaseDirectory { get; set; }
}

/// <summary>A file named by the command, for example <c>-d @body.json</c> or <c>-F file=@photo.png</c>.</summary>
public sealed class CurlFileReference
{
    /// <summary>The option that named the file, as written (for example <c>-d</c> or <c>--data-binary</c>).</summary>
    public string Option { get; set; } = "";

    /// <summary>The path as written in the command.</summary>
    public string Path { get; set; } = "";

    /// <summary>Full path after resolving against <see cref="CurlParseOptions.FileBaseDirectory"/>. Null when no base directory was given.</summary>
    public string? ResolvedPath { get; set; }

    /// <summary>True when <see cref="ResolvedPath"/> exists and, for references read during import, was read successfully.</summary>
    public bool Resolved { get; set; }

    /// <summary>
    /// True when the file is not read during import but by the request engine at send time
    /// (multipart file parts, <c>--data-binary @file</c>, client certificates). The request model holds the path.
    /// </summary>
    public bool ReadAtSendTime { get; set; }

    public override string ToString() => $"{Option} {Path}{(ResolvedPath is null ? " (unresolved)" : $" -> {ResolvedPath}{(Resolved ? "" : " (missing)")}")}";
}

public sealed class CurlImportResult
{
    public HttpRequestSpec Request { get; set; } = new();

    /// <summary>The dialect actually used. Never <see cref="CurlDialect.Auto"/>.</summary>
    public CurlDialect Dialect { get; set; }

    /// <summary>Messages for the user: unsupported options, unresolved files, shell constructs that were not evaluated.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Every option that was accepted but had no effect on the request, as written in the command.</summary>
    public List<string> IgnoredOptions { get; } = [];

    public List<CurlFileReference> FileReferences { get; } = [];
}
