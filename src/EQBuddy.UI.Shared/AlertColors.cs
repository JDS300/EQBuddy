namespace EQBuddy.UI.Shared;

/// <summary>
/// The per-rule banner tint palette (Chaosrah, 2026-08-06: on quiet-sound or migraine
/// days, color is what identifies an alert at a glance — "mez purple, heals green,
/// enemy red"). Deliberately a short named list, not a color picker: these hexes are
/// legible on every theme's dark background, which a free choice can't promise.
/// "Default" is the theme accent, whatever theme is active.
/// </summary>
public static class AlertColors
{
    public static readonly (string Name, string Hex)[] Choices =
    [
        ("Default", ""),
        ("Purple", "#B48CDE"),
        ("Green", "#7FBF5F"),
        ("Red", "#D9634F"),
        ("Blue", "#5FA8D3"),
        ("Amber", "#E0A030"),
        ("White", "#EDE4D3"),
    ];

    /// <summary>Hex for a stored color name; "" (theme accent) for unknown/empty.</summary>
    public static string Hex(string? name) =>
        Choices.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Hex ?? "";

    public static int IndexOf(string? name)
    {
        for (var i = 0; i < Choices.Length; i++)
            if (Choices[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}
