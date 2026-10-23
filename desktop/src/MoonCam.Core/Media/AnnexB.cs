namespace MoonCam.Core.Media;

/// <summary>
/// Helpers for H.264 Annex-B byte streams: NAL units separated by 00 00 01 or 00 00 00 01 start codes.
/// </summary>
public static class AnnexB
{
    public const int NalSps = 7;
    public const int NalPps = 8;
    public const int NalAccessUnitDelimiter = 9;
    public const int NalIdr = 5;

    /// <summary>Returns (offset, length) of every NAL unit, start codes excluded.</summary>
    public static List<(int Offset, int Length)> FindNalUnits(ReadOnlySpan<byte> data)
    {
        var units = new List<(int, int)>();
        var start = -1;
        var i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0)
                    units.Add((start, TrimTrailingZeros(data, start, i) - start));
                i += 3;
                start = i;
                continue;
            }
            i++;
        }
        if (start >= 0 && start < data.Length)
            units.Add((start, data.Length - start));
        return units;
    }

    /// <summary>The H.264 NAL unit type (low 5 bits of the first byte).</summary>
    public static int H264Type(ReadOnlySpan<byte> nal) => nal.IsEmpty ? -1 : nal[0] & 0x1F;

    /// <summary>True if the access unit contains an IDR slice (a key frame).</summary>
    public static bool ContainsIdr(ReadOnlySpan<byte> accessUnit)
    {
        foreach (var (offset, length) in FindNalUnits(accessUnit))
        {
            if (H264Type(accessUnit.Slice(offset, length)) == NalIdr)
                return true;
        }
        return false;
    }

    /// <summary>Joins NAL units back into an Annex-B buffer with 4-byte start codes.</summary>
    public static byte[] Join(IEnumerable<ReadOnlyMemory<byte>> nals)
    {
        using var output = new MemoryStream();
        foreach (var nal in nals)
        {
            output.Write([0, 0, 0, 1]);
            output.Write(nal.Span);
        }
        return output.ToArray();
    }

    // A 4-byte start code 00 00 00 01 leaves one zero at the end of the previous unit; drop it.
    private static int TrimTrailingZeros(ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0)
            end--;
        return end;
    }
}

/// <summary>
/// Splits a continuous H.264 Annex-B stream into access units (one per picture), using access unit
/// delimiters. Used by the FakePhone tool and by tests; the phone's encoder already outputs whole units.
/// </summary>
public sealed class AccessUnitSplitter
{
    private readonly List<byte> _pending = [];

    /// <summary>Feeds bytes and returns every access unit completed by them.</summary>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data)
    {
        _pending.AddRange(data);
        var buffer = _pending.ToArray();
        var units = AnnexB.FindNalUnits(buffer);
        var completed = new List<byte[]>();
        var auStart = -1;

        // The last NAL may still be growing, so only units followed by another start code are final.
        for (var n = 0; n < units.Count - 1; n++)
        {
            var (offset, length) = units[n];
            if (AnnexB.H264Type(buffer.AsSpan(offset, length)) != AnnexB.NalAccessUnitDelimiter)
                continue;
            var startCode = StartCodeOffset(buffer, offset);
            if (auStart >= 0)
                completed.Add(buffer[auStart..startCode]);
            auStart = startCode;
        }

        // Keep only the access unit still being received.
        if (auStart > 0)
            _pending.RemoveRange(0, auStart);
        return completed;
    }

    private static int StartCodeOffset(byte[] buffer, int nalOffset)
    {
        var start = nalOffset - 3;
        return start > 0 && buffer[start - 1] == 0 ? start - 1 : start;
    }
}
