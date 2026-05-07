using Microsoft.AspNetCore.Components;

namespace Magpilot.UI;

/// <summary>
/// Cascaded by the app's MainLayout so any descendant component (e.g.
/// the app bar's dark-mode toggle) can read the current theme mode and
/// flip it without taking a circular reference on the Web project.
/// </summary>
public sealed record ThemeState(bool IsDark, EventCallback<bool> Set);
