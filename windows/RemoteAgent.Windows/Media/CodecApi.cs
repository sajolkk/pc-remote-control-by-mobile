using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteAgent.Windows.Media;

/// <summary>
/// The handful of <c>ICodecAPI</c> calls the encoder needs.
/// </summary>
/// <remarks>
/// <para>Called through the raw vtable because Vortice does not project <c>ICodecAPI</c>. Only
/// <c>SetValue</c> is used, so the surface is one method and one <c>VARIANT</c> layout — small
/// enough to read in full, which is the standard every other P/Invoke in this project is held
/// to.</para>
///
/// <para>Every setting is best-effort. Encoders differ in which properties they honour, and a
/// property an encoder rejects is logged and skipped rather than treated as fatal: the stream
/// still works, just without that tuning (§0).</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    private nint _pointer;

    private CodecApi(nint pointer)
    {
        _pointer = pointer;
    }

    /// <summary>Queries <paramref name="unknown"/> for ICodecAPI, or returns null if it has none.</summary>
    public static CodecApi? From(nint unknown)
    {
        if (unknown == 0)
        {
            return null;
        }

        Guid iid = IidCodecApi;
        int hr = Marshal.QueryInterface(unknown, in iid, out nint codecApi);
        return hr >= 0 && codecApi != 0 ? new CodecApi(codecApi) : null;
    }

    /// <summary>Sets a 32-bit unsigned property. Returns the HRESULT.</summary>
    public int SetUInt32(Guid api, uint value)
    {
        var variant = new Variant { Type = VtUi4, UInt64 = value };
        return SetValue(api, ref variant);
    }

    /// <summary>Sets a boolean property. Returns the HRESULT.</summary>
    public int SetBool(Guid api, bool value)
    {
        var variant = new Variant { Type = VtBool, UInt64 = value ? 0xFFFFu : 0u };
        return SetValue(api, ref variant);
    }

    private int SetValue(Guid api, ref Variant value)
    {
        ObjectDisposedException.ThrowIf(_pointer == 0, this);

        // ICodecAPI vtable: IUnknown (0-2), IsSupported, IsModifiable, GetParameterRange,
        // GetParameterValues, GetDefaultValue, GetValue, SetValue (9).
        var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)(*(nint**)_pointer)[9];

        fixed (Variant* variant = &value)
        {
            return setValue(_pointer, &api, variant);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_pointer != 0)
        {
            Marshal.Release(_pointer);
            _pointer = 0;
        }
    }

    private const ushort VtBool = 11;
    private const ushort VtUi4 = 19;

    /// <summary>The 64-bit VARIANT layout: a type tag, three reserved words, then a 16-byte union.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public ulong UInt64;
    }
}

/// <summary>Property GUIDs from codecapi.h.</summary>
internal static class CodecApiProperties
{
    /// <summary>CODECAPI_AVLowLatencyMode: one frame in, one frame out, no lookahead.</summary>
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    /// <summary>CODECAPI_AVEncCommonRateControlMode.</summary>
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");

    /// <summary>CODECAPI_AVEncCommonMeanBitRate, in bits per second.</summary>
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");

    /// <summary>CODECAPI_AVEncCommonQualityVsSpeed: 0 is fastest, 100 is best quality.</summary>
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    /// <summary>CODECAPI_AVEncMPVGOPSize: frames between forced keyframes.</summary>
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    /// <summary>CODECAPI_AVEncMPVDefaultBPictureCount.</summary>
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    /// <summary>CODECAPI_AVEncVideoForceKeyFrame.</summary>
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    /// <summary>eAVEncCommonRateControlMode_CBR.</summary>
    public const uint RateControlCbr = 0;
}
