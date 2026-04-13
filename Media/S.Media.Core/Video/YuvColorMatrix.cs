namespace S.Media.Core.Video;

/// <summary>
/// Color matrix selection for YUV-to-RGB shader conversion paths.
/// </summary>
public enum YuvColorMatrix
{
    /// <summary>Automatically select based on resolution (BT.601 for SD, BT.709 for HD+).</summary>
    Auto,
    /// <summary>ITU-R BT.601 — standard definition SD video.</summary>
    Bt601,
    /// <summary>ITU-R BT.709 — high definition HD/FHD/4K SDR video.</summary>
    Bt709,
    /// <summary>ITU-R BT.2020 — wide color gamut UHD/HDR video.</summary>
    Bt2020
}

