using System.Text.Json;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Pins enums to numbers on the wire.
/// </summary>
/// <remarks>
/// <para>
/// This is a decision, not an accident, so it gets a test rather than a comment. The columns store
/// enums as <c>tinyint</c> and the API mirrors that: a client sees <c>2</c> for a published listing.
/// The alternative — <c>JsonStringEnumConverter</c> — is one line in Program.cs and would flip every
/// enum in the API at once, which is precisely why it is worth noticing when it happens.
/// </para>
/// <para>
/// Read as raw JSON rather than through a DTO. Deserialising into a DTO with an enum member accepts
/// either representation, so a round trip through the typed model cannot tell the two apart — it
/// would pass whether the wire carried <c>2</c> or <c>"Published"</c>.
/// </para>
/// </remarks>
public sealed class EnumSerializationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Listing_status_and_condition_are_numbers_on_the_detail_endpoint()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var listing = await client.PublishListingAsync(fixture, "枚举-详情");

        var json = await client.GetStringAsync($"/api/products/{listing.Id}");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Number, root.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("condition").ValueKind);

        // Published is 2 in the database, so the wire carries the same value the column holds. If the
        // enum members were ever renumbered, the stored rows and the API would disagree — this catches
        // the API half.
        Assert.Equal(2, root.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Listing_condition_is_a_number_on_the_list_endpoint()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();

        // A token unique to this test, so the assertion lands on a listing this test created rather
        // than on whatever else the shared database happens to hold.
        var token = "e" + Guid.NewGuid().ToString("N")[..12];

        await client.PublishListingAsync(fixture, $"枚举-列表-{token}");

        var json = await anonymous.GetStringAsync($"/api/products?q={token}");

        using var document = JsonDocument.Parse(json);
        var items = document.RootElement.GetProperty("items");

        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(JsonValueKind.Number, items[0].GetProperty("condition").ValueKind);
    }

    [Fact]
    public async Task The_image_list_is_an_empty_array_rather_than_null_on_a_draft()
    {
        // Not about enums, but the same class of decision: the DTO promises a list, so a client can
        // iterate it without a null check. A null here would be a rendering bug in every gallery.
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var draft = await client.CreateDraftAsync(fixture, "枚举-空图列表");

        var json = await client.GetStringAsync($"/api/products/{draft.Id}");

        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("images").ValueKind);
        Assert.Equal(0, document.RootElement.GetProperty("images").GetArrayLength());
    }
}
