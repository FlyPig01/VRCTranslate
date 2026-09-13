using VrcTranslate.Application.Abstractions;
using VrcTranslate.Core.Translation;

namespace VrcTranslate.Application.Translation;

public sealed class InMemoryTranslationRouteStore : ITranslationRouteStore
{
    private TranslationRoute? _current;

    public TranslationRoute? GetCurrent() => Volatile.Read(ref _current);

    public void SetCurrent(TranslationRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        Volatile.Write(ref _current, route);
    }
}
