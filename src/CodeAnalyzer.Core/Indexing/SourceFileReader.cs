using System.IO;
using System.Security.Cryptography;
using System.Text;
using CodeAnalyzer.Core.Crawling;

namespace CodeAnalyzer.Core.Indexing;

/// <summary>File contents plus the hash that drives incremental re-indexing.</summary>
public sealed record SourceFileContent(string Text, byte[] ContentHash);

/// <summary>
/// Reads source files, hashing and binary-sniffing in one pass so a file is touched once.
/// </summary>
public static class SourceFileReader
{
    /// <summary>
    /// Returns the file's text and content hash, or null when it should not be parsed
    /// (unreadable, or binary despite having a source extension).
    /// </summary>
    public static SourceFileContent? TryRead(string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = ReadAllBytes(fullPath, cancellationToken);

            var sniffWindow = bytes.AsSpan(0, Math.Min(bytes.Length, IgnoreRules.BinarySniffWindowBytes));
            if (IgnoreRules.LooksBinary(sniffWindow))
            {
                return null;
            }

            // Hash the raw bytes, not the decoded text: encoding changes are real changes.
            var hash = SHA256.HashData(bytes);

            return new SourceFileContent(Decode(bytes), hash);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[] ReadAllBytes(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // FileShare.ReadWrite so indexing never blocks the user's editor from saving.
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        var length = (int)stream.Length;
        var buffer = new byte[length];
        stream.ReadExactly(buffer, 0, length);
        return buffer;
    }

    /// <summary>
    /// Decodes as UTF-8, honouring a BOM when present. Invalid sequences become
    /// replacement characters rather than throwing, because a single bad byte in a
    /// vendored file should not lose the whole file's symbols.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
