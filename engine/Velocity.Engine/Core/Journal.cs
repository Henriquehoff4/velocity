using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Velocity.Engine.Core;

public readonly record struct Extent(long Start, long Length, ulong Hash)
{
    public long End => Start + Length;
}

public sealed record JournalHeader(long Size, string Etag, string LastModified);

public sealed class Journal : IDisposable
{
    private const uint Magic = 0x314C4456;
    private const int RecordSize = 28;
    private readonly string _path;
    private FileStream? _stream;
    private readonly byte[] _record = new byte[RecordSize];

    private Journal(string path, FileStream stream)
    {
        _path = path;
        _stream = stream;
    }

    public static Journal Create(string path, JournalHeader header)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096);
        stream.Write(EncodeHeader(header));
        stream.Flush(true);
        return new Journal(path, stream);
    }

    public static (Journal Journal, List<Extent> Extents)? Open(string path, JournalHeader expected)
    {
        if (!File.Exists(path)) return null;
        byte[] all;
        try
        {
            all = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        if (!TryDecodeHeader(all, out var header, out var offset)) return null;
        if (header!.Size != expected.Size || header.Etag != expected.Etag || header.LastModified != expected.LastModified) return null;
        var extents = new List<Extent>();
        var valid = offset;
        while (offset + RecordSize <= all.Length)
        {
            var span = all.AsSpan(offset, RecordSize);
            if (Crc32.HashToUInt32(span[..24]) != BinaryPrimitives.ReadUInt32LittleEndian(span[24..])) break;
            var extent = new Extent(BinaryPrimitives.ReadInt64LittleEndian(span), BinaryPrimitives.ReadInt64LittleEndian(span[8..]), BinaryPrimitives.ReadUInt64LittleEndian(span[16..]));
            if (extent.Start < 0 || extent.Length <= 0 || extent.End > header.Size) break;
            extents.Add(extent);
            offset += RecordSize;
            valid = offset;
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096);
        stream.SetLength(valid);
        stream.Seek(valid, SeekOrigin.Begin);
        return (new Journal(path, stream), extents);
    }

    public void Append(IReadOnlyList<Extent> extents, bool durable = true)
    {
        if (_stream == null || extents.Count == 0) return;
        foreach (var extent in extents)
        {
            var span = _record.AsSpan();
            BinaryPrimitives.WriteInt64LittleEndian(span, extent.Start);
            BinaryPrimitives.WriteInt64LittleEndian(span[8..], extent.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(span[16..], extent.Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..], Crc32.HashToUInt32(span[..24]));
            _stream.Write(span);
        }
        _stream.Flush(durable);
    }

    public void Rewrite(string path, JournalHeader header, IReadOnlyList<Extent> extents)
    {
        _stream?.Dispose();
        _stream = null;
        var temp = path + ".tmp";
        using (var fresh = Create(temp, header)) fresh.Append(extents);
        File.Move(temp, path, true);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 4096);
        stream.Seek(0, SeekOrigin.End);
        _stream = stream;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    private static byte[] EncodeHeader(JournalHeader header)
    {
        var etag = Encoding.UTF8.GetBytes(header.Etag);
        var modified = Encoding.UTF8.GetBytes(header.LastModified);
        var bytes = new byte[4 + 8 + 2 + etag.Length + 2 + modified.Length + 4];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteInt64LittleEndian(span[4..], header.Size);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)etag.Length);
        etag.CopyTo(span[14..]);
        var at = 14 + etag.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(span[at..], (ushort)modified.Length);
        modified.CopyTo(span[(at + 2)..]);
        at += 2 + modified.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], Crc32.HashToUInt32(span[..at]));
        return bytes;
    }

    private static bool TryDecodeHeader(byte[] all, out JournalHeader? header, out int offset)
    {
        header = null;
        offset = 0;
        var span = all.AsSpan();
        if (span.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic) return false;
        var size = BinaryPrimitives.ReadInt64LittleEndian(span[4..]);
        int etagLength = BinaryPrimitives.ReadUInt16LittleEndian(span[12..]);
        if (14 + etagLength + 2 > span.Length) return false;
        var etag = Encoding.UTF8.GetString(span.Slice(14, etagLength));
        var at = 14 + etagLength;
        int modifiedLength = BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);
        if (at + 2 + modifiedLength + 4 > span.Length) return false;
        var modified = Encoding.UTF8.GetString(span.Slice(at + 2, modifiedLength));
        at += 2 + modifiedLength;
        if (Crc32.HashToUInt32(span[..at]) != BinaryPrimitives.ReadUInt32LittleEndian(span[at..])) return false;
        header = new JournalHeader(size, etag, modified);
        offset = at + 4;
        return true;
    }
}

public sealed class ExtentWriter
{
    private readonly object _gate = new();
    private readonly List<Extent> _closed = new();
    private readonly XxHash3 _hash = new();
    private long _start = -1;
    private long _length;

    public void Append(long position, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_length > 0 && position != _start + _length) Close();
            if (_length == 0) _start = position;
            _hash.Append(data);
            _length += data.Length;
        }
    }

    private void Close()
    {
        if (_length <= 0) return;
        _closed.Add(new Extent(_start, _length, _hash.GetCurrentHashAsUInt64()));
        _hash.Reset();
        _length = 0;
    }

    public void Drain(List<Extent> into)
    {
        lock (_gate)
        {
            Close();
            into.AddRange(_closed);
            _closed.Clear();
        }
    }
}
