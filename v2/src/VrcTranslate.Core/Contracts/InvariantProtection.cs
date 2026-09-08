using System.Collections.Frozen;

namespace VrcTranslate.Core.Translation;

public enum InvariantKind
{
    Url,
    Email,
    Mention,
    Number,
    Path,
    Code
}

public sealed record InvariantProtectionPolicy
{
    public InvariantProtectionPolicy(IReadOnlySet<InvariantKind> protectedKinds)
    {
        ArgumentNullException.ThrowIfNull(protectedKinds);
        ProtectedKinds = protectedKinds.ToFrozenSet();
    }

    public IReadOnlySet<InvariantKind> ProtectedKinds { get; }

    public static InvariantProtectionPolicy Default { get; } = new(
        new HashSet<InvariantKind>
        {
            InvariantKind.Url,
            InvariantKind.Email,
            InvariantKind.Mention,
            InvariantKind.Number,
            InvariantKind.Path,
            InvariantKind.Code
        }.ToFrozenSet());
}

public sealed record InvariantToken
{
    public InvariantToken(InvariantKind kind, string value, int ordinal)
    {
        Kind = kind;
        Value = Required(value);
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        Ordinal = ordinal;
    }

    public InvariantKind Kind { get; }
    public string Value { get; }
    public int Ordinal { get; }

    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("An invariant value is required.", nameof(value))
        : value;
}

public sealed record ProtectedText
{
    public ProtectedText(string text, IReadOnlyList<InvariantToken> tokens)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text is required.", nameof(text));
        }

        if (tokens is null)
        {
            throw new ArgumentNullException(nameof(tokens));
        }

        Text = text;
        Tokens = tokens;
    }

    public string Text { get; }
    public IReadOnlyList<InvariantToken> Tokens { get; }
}

public sealed record InvariantViolation(InvariantKind Kind, string Expected, string? Actual, string Reason);

public sealed record InvariantValidationResult(bool IsValid, IReadOnlyList<InvariantViolation> Violations)
{
    public static InvariantValidationResult Valid { get; } = new(true, Array.Empty<InvariantViolation>());

    public static InvariantValidationResult Invalid(IReadOnlyList<InvariantViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        return new InvariantValidationResult(violations.Count == 0, violations);
    }
}

public interface IInvariantProtectionGuard
{
    ProtectedText Protect(string sourceText, InvariantProtectionPolicy policy);

    InvariantValidationResult Validate(ProtectedText protectedText, string translatedText);
}
