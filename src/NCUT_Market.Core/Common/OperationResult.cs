using System.Diagnostics.CodeAnalysis;

namespace NCUT_Market.Core.Common;

/// <summary>
/// A service outcome that is never serialised: controllers destructure it into either the bare DTO
/// or a ProblemDetails. Expected failures travel here; exceptions are reserved for the unexpected.
/// </summary>
/// <remarks>
/// <see cref="Succeeded"/> carries <see cref="MemberNotNullWhenAttribute"/> so controllers can read
/// <c>result.ErrorCode</c> without a null-forgiving operator. It is a compile-time hint only — the
/// wire shape and runtime behaviour are unchanged from the plain <c>ErrorCode is null</c> test.
/// </remarks>
public sealed record OperationResult<T>(T? Value, string? ErrorCode, string? ErrorMessage)
{
    [MemberNotNullWhen(false, nameof(ErrorCode))]
    public bool Succeeded => ErrorCode is null;

    public static OperationResult<T> Success(T value) => new(value, null, null);

    public static OperationResult<T> Failure(string errorCode, string errorMessage) =>
        new(default, errorCode, errorMessage);
}
