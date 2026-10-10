namespace NCUT_Market.Core.DTOs.Ai;

/// <summary>
/// One photo on its way to the model: the bytes, and the media type they arrived as.
/// </summary>
/// <remarks>
/// Bytes rather than a stream because the caller has already read the upload to size-check it, and the
/// model request needs the whole thing base64-encoded in one piece anyway — a stream would only buy a
/// second copy. Deliberately not <c>IFormFile</c>: <c>Core</c> has no reference to ASP.NET Core, and
/// nothing here should learn about multipart.
/// </remarks>
public sealed record AiImage(byte[] Content, string ContentType);
