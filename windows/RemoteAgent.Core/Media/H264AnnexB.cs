namespace RemoteAgent.Core.Media;

/// <summary>
/// Minimal H.264 Annex-B inspection: finding NAL units and recognising the ones the pipeline
/// cares about.
/// </summary>
/// <remarks>
/// This is not a parser in any meaningful sense — it reads start codes and the one-byte NAL header
/// and nothing else. That is all the pipeline needs: to know whether an access unit is a keyframe,
/// and whether the parameter sets a decoder needs to start are in-band.
/// </remarks>
public static class H264AnnexB
{
    /// <summary>NAL unit type of a non-IDR slice.</summary>
    public const int NalSlice = 1;

    /// <summary>NAL unit type of an IDR slice: a keyframe.</summary>
    public const int NalIdr = 5;

    /// <summary>NAL unit type of a sequence parameter set.</summary>
    public const int NalSps = 7;

    /// <summary>NAL unit type of a picture parameter set.</summary>
    public const int NalPps = 8;

    /// <summary>
    /// Enumerates NAL units as (offset of the NAL header, length excluding the start code).
    /// </summary>
    /// <remarks>
    /// Accepts both three- and four-byte start codes, since encoders mix them. Data before the
    /// first start code is ignored, which is the correct reading of a stream that does not begin
    /// on a NAL boundary.
    /// </remarks>
    public static List<(int Offset, int Length)> FindNalUnits(ReadOnlySpan<byte> data)
    {
        var units = new List<(int Offset, int Length)>();

        int start = NextStartCode(data, 0, out int prefix);
        while (start >= 0)
        {
            int payload = start + prefix;
            int next = NextStartCode(data, payload, out int nextPrefix);
            int end = next < 0 ? data.Length : next;

            // Trailing zero bytes belong to the next start code (a four-byte code is 00 00 00 01),
            // not to this NAL's payload.
            while (end > payload && data[end - 1] == 0)
            {
                end--;
            }

            if (end > payload)
            {
                units.Add((payload, end - payload));
            }

            start = next;
            prefix = nextPrefix;
        }

        return units;
    }

    /// <summary>The NAL unit type of the unit whose header is at <paramref name="offset"/>.</summary>
    public static int NalType(ReadOnlySpan<byte> data, int offset) => data[offset] & 0x1F;

    /// <summary>Whether the access unit contains an IDR slice.</summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> data) => Contains(data, NalIdr);

    /// <summary>Whether the access unit contains a NAL unit of <paramref name="nalType"/>.</summary>
    public static bool Contains(ReadOnlySpan<byte> data, int nalType)
    {
        foreach ((int offset, _) in FindNalUnits(data))
        {
            if (NalType(data, offset) == nalType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the access unit with <paramref name="parameterSets"/> prepended if it is a keyframe
    /// that lacks an in-band SPS.
    /// </summary>
    /// <remarks>
    /// A WebRTC receiver cannot start decoding without SPS and PPS, and it only gets them in-band —
    /// there is no out-of-band <c>sprop-parameter-sets</c> in this signalling. Some encoders put the
    /// parameter sets only in their output media type, so every keyframe is checked.
    /// </remarks>
    public static byte[] EnsureParameterSets(byte[] accessUnit, ReadOnlySpan<byte> parameterSets)
    {
        ArgumentNullException.ThrowIfNull(accessUnit);

        if (parameterSets.IsEmpty || !IsKeyFrame(accessUnit) || Contains(accessUnit, NalSps))
        {
            return accessUnit;
        }

        byte[] combined = new byte[parameterSets.Length + accessUnit.Length];
        parameterSets.CopyTo(combined);
        accessUnit.CopyTo(combined.AsSpan(parameterSets.Length));
        return combined;
    }

    private static int NextStartCode(ReadOnlySpan<byte> data, int from, out int prefixLength)
    {
        for (int i = from; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
            {
                continue;
            }

            if (data[i + 2] == 1)
            {
                // Report a four-byte code as starting at its first zero, so the byte is not
                // mistaken for the tail of the previous NAL.
                if (i > from && data[i - 1] == 0)
                {
                    prefixLength = 4;
                    return i - 1;
                }

                prefixLength = 3;
                return i;
            }
        }

        prefixLength = 0;
        return -1;
    }
}
