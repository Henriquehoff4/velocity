using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Velocity.Engine.Core;

public static class NativeDisk
{
    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref byte input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    public static bool SetSparse(string path, bool sparse)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            byte flag = sparse ? (byte)1 : (byte)0;
            return DeviceIoControl(handle, FsctlSetSparse, ref flag, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static long FreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    public static void MarkFromInternet(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    public static string UniquePath(string folder, string name)
    {
        var candidate = Path.Combine(folder, name);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 1; i < 10000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }
}

public static class ExtentVerifier
{
    public static async Task<List<Extent>> FindBadAsync(SafeFileHandle handle, IEnumerable<Extent> extents, int parallelism, Action<long>? progress, CancellationToken ct)
    {
        var bad = new List<Extent>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };
        await Parallel.ForEachAsync(extents.OrderBy(e => e.Start), options, async (extent, token) =>
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                var hash = new XxHash3();
                var position = extent.Start;
                var remaining = extent.Length;
                while (remaining > 0)
                {
                    var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), position, token);
                    if (read <= 0) break;
                    hash.Append(buffer.AsSpan(0, read));
                    position += read;
                    remaining -= read;
                    progress?.Invoke(read);
                }
                if (remaining != 0 || hash.GetCurrentHashAsUInt64() != extent.Hash)
                    lock (bad) bad.Add(extent);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        });
        return bad;
    }
}
