using System.Security.Cryptography;
using System.Text;

namespace NCUT_Market.Infrastructure.Security;

/// <summary>
/// Mints and checks the one-time codes that let a user who has forgotten their password set a new one.
/// </summary>
/// <remarks>
/// <para>
/// The code is not a login credential and never becomes one. An administrator mints it, hands it over
/// in person after checking a student card, and the user exchanges it at an anonymous endpoint for a
/// password of their own choosing. Nothing is signed, so there is no token lifetime to police and no
/// scope to enforce.
/// </para>
/// <para>
/// The alphabet omits <c>I</c>, <c>O</c>, <c>0</c> and <c>1</c>, which are the pairs people misread
/// when copying a code off a screen or reading it down a phone.
/// </para>
/// </remarks>
internal static class ResetCode
{
    /// <summary>32 characters, so the code carries five bits each.</summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Eight characters, so forty bits.</summary>
    private const int Length = 8;

    /// <summary>
    /// A fresh code, formatted for transcription as <c>XXXX-XXXX</c>.
    /// </summary>
    /// <remarks>
    /// Eight characters of this alphabet is 1.1e12 possibilities. With no rate limiting anywhere in this
    /// API that is still not guessable over HTTP, and a code only lives for a day.
    /// </remarks>
    public static string Generate()
    {
        var characters = new char[Length];

        for (var index = 0; index < Length; index++)
        {
            // GetInt32 rather than a random byte taken modulo 32: it is free of modulo bias already, so
            // changing the alphabet's length would not quietly skew the distribution.
            characters[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var body = new string(characters);
        var half = Length / 2;

        return $"{body[..half]}-{body[half..]}";
    }

    /// <summary>
    /// Reduces what the user typed to the form the code was generated in.
    /// </summary>
    /// <remarks>
    /// The dash in the displayed code is punctuation, not data, and people copying it by hand drop it,
    /// retype it as a space, or leave caps lock on. All three arrive here and land on the same string.
    /// </remarks>
    public static string Normalize(string code)
    {
        var builder = new StringBuilder(code.Length);

        foreach (var character in code)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>The stored digest of a normalized code.</summary>
    /// <remarks>
    /// Unsalted SHA-256 rather than the password hasher. The input is forty random bits, so there is no
    /// dictionary to attack it with and nothing for a per-row salt to defend; what the digest buys is
    /// that reading the <c>users</c> table does not hand over a live account.
    /// </remarks>
    public static string Hash(string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    /// <summary>
    /// Whether a code the user supplied is the one behind a stored digest.
    /// </summary>
    /// <param name="code">What the user typed, already normalized.</param>
    /// <param name="storedHash">The <c>users.password_reset_code</c> value, or null when none is pending.</param>
    public static bool Matches(string code, string? storedHash) =>
        storedHash is not null
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(code)),
            Encoding.UTF8.GetBytes(storedHash));
}
