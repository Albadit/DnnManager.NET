using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <param name="SwitchedOff">Rules switched off just now.</param>
/// <param name="AlreadyOff">Rules DNN Manager had switched off before.</param>
public sealed record HttpsRedirectRules(IReadOnlyList<string> SwitchedOff, IReadOnlyList<string> AlreadyOff)
{
    public static readonly HttpsRedirectRules None = new(Array.Empty<string>(), Array.Empty<string>());
    public IReadOnlyList<string> All => SwitchedOff.Concat(AlreadyOff).ToList();
}
