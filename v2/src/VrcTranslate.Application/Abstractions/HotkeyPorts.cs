using VrcTranslate.Core.Settings;

namespace VrcTranslate.Application.Abstractions;

public interface IHotkeyActionHandler
{
    Task HandleAsync(HotkeyAction action, CancellationToken cancellationToken = default);
}
