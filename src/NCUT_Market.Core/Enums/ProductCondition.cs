namespace NCUT_Market.Core.Enums;

/// <summary>
/// Item condition (成色). Four coarse buckets so sellers do not stall on the listing form.
/// </summary>
public enum ProductCondition : byte
{
    /// <summary>全新</summary>
    New = 1,

    /// <summary>几乎全新</summary>
    LikeNew = 2,

    /// <summary>轻微使用痕迹</summary>
    Good = 3,

    /// <summary>明显使用痕迹</summary>
    Fair = 4
}
