namespace NCUT_Market.Core.Enums;

/// <summary>
/// Listing lifecycle. Draft listings that see no activity for 7 days are hard-deleted by the
/// draft cleanup job, so there is deliberately no "Deleted" member here.
/// </summary>
public enum ProductStatus : byte
{
    Draft = 1,

    Published = 2,

    Sold = 3,

    Offline = 4,

    /// <summary>
    /// A trade agreed in the message thread has been accepted and is waiting on both sides to
    /// confirm. Appended rather than inserted, so the values already on the wire do not move.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public and listed, exactly like <see cref="Published"/> — the whole point of the state is that
    /// the people already talking about the listing do not watch it vanish. The old design hid it
    /// from everyone but the two parties, which turns a chat into a dead end the moment somebody
    /// agrees to buy.
    /// </para>
    /// <para>
    /// Not terminal, unlike <see cref="Sold"/>: a trade nobody confirms within a day rolls back to
    /// <see cref="Published"/>. Anything that treats <see cref="Sold"/> as "the listing is finished"
    /// has to decide what it means for this one, and most things mean "still live".
    /// </para>
    /// </remarks>
    InTransaction = 5
}
