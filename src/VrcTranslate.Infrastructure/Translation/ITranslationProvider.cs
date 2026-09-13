using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Translation;

/// <summary>Infrastructure uses the single provider port owned by Application.</summary>
public interface ITranslationProvider : VrcTranslate.Application.Abstractions.ITranslationProvider
{
}
