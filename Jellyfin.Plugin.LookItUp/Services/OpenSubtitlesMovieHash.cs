using System.Buffers.Binary;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Classic OpenSubtitles moviehash (file size + first/last 64 KiB as little-endian ulongs).
/// </summary>
public static class OpenSubtitlesMovieHash
{
    private const int ChunkSize = 65536;

    /// <summary>
    /// Computes the OpenSubtitles moviehash for a video file.
    /// </summary>
    public static string? Compute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            var length = info.Length;
            if (length <= 0)
            {
                return null;
            }

            ulong hash = (ulong)length;
            using var stream = File.OpenRead(path);
            hash = AddChunk(hash, stream, Math.Min(ChunkSize, length));

            if (length > ChunkSize)
            {
                stream.Seek(Math.Max(0, length - ChunkSize), SeekOrigin.Begin);
                hash = AddChunk(hash, stream, ChunkSize);
            }

            return hash.ToString("x16");
        }
        catch
        {
            return null;
        }
    }

    private static ulong AddChunk(ulong hash, Stream stream, long bytesToRead)
    {
        var buffer = new byte[ChunkSize];
        var remaining = (int)bytesToRead;
        var offset = 0;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, offset, remaining);
            if (read <= 0)
            {
                break;
            }

            offset += read;
            remaining -= read;
        }

        var span = buffer.AsSpan(0, offset);
        for (var i = 0; i + 8 <= span.Length; i += 8)
        {
            hash += BinaryPrimitives.ReadUInt64LittleEndian(span[i..]);
        }

        return hash;
    }
}
