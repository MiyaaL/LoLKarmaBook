using System.IO;
using System.Text;
using LoLKarmaBook.Scoring;
using LoLKarmaBook.Scoring.Performance;

namespace LoLKarmaBook.Desktop.Services;

/// <summary>Bounds bytes read before decoding, even if an input grows during the read.</summary>
internal static class BoundedJsonFile
{
    internal static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadText(stream);
    }

    internal static string ReadText(Stream stream)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            int count = (int)Math.Min(buffer.Length, PerformanceJson.MaxBytes - bytes.Length + 1);
            int read = stream.Read(buffer, 0, count);
            if (read == 0) break;
            bytes.Write(buffer, 0, read);
            if (bytes.Length > PerformanceJson.MaxBytes)
            {
                throw new ScoringValidationException("scoring.file-too-large", "文件超过 5 MiB 限制。");
            }
        }

        bytes.Position = 0;
        using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
