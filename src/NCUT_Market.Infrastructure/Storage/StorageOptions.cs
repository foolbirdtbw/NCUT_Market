namespace NCUT_Market.Infrastructure.Storage;

/// <summary>
/// Where uploaded product images live and what URL prefix they are served under.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UploadRoot"/> defaults to a path relative to the working directory, but the API
/// overrides it with an absolute path under its content root before registering services. That one
/// rule resolves correctly in both layouts — under <c>dotnet run</c> the content root is the project
/// directory, and after <c>dotnet publish</c> it is the publish output — which is why this does not
/// need the candidate-searching that <c>StaticWebRoot</c> does for the frontend. The difference is
/// that the frontend lives outside the project and uploads do not.
/// </para>
/// <para>
/// Overridable via <c>Storage__UploadRoot</c> for an operator who wants uploads on a separate
/// volume.
/// </para>
/// </remarks>
public sealed class StorageOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Storage";

    /// <summary>Absolute or working-directory-relative directory holding the image files.</summary>
    public string UploadRoot { get; set; } = Path.Combine("data", "uploads");

    /// <summary>
    /// Request path the upload directory is mounted at. Must match the <c>RequestPath</c> the API
    /// passes to <c>UseStaticFiles</c>, since this value is what turns a storage key into a URL a
    /// client can fetch.
    /// </summary>
    public string PublicBasePath { get; set; } = "/media";
}
