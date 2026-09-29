namespace NCUT_Market.Core.Enums;

/// <summary>
/// Account lifecycle. Users are never physically deleted — disabling is the only removal path,
/// which is why every FK pointing at a user is ON DELETE RESTRICT.
/// </summary>
public enum UserStatus : byte
{
    Active = 1,
    Disabled = 2
}
