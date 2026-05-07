using MudBlazor;
using MudBlazor.Utilities;

namespace Magpilot.UI;

/// <summary>
/// Magpilot's MudBlazor theme. Light + dark palettes share a single brand
/// identity:
///   - Primary: deep midnight blue (the magpie's overall plumage tone)
///   - Secondary: iridescent teal/violet (the magpie's wing+tail flash)
///   - Surface: clean off-white in light mode, soft black in dark mode
/// Typography is a system stack so we don't ship a webfont; magpie favicon
/// remains the brand mark in the app bar.
/// </summary>
public static class MagpilotTheme
{
    public static readonly MudTheme Default = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1A2B4A",
            PrimaryDarken = "#0F1A30",
            PrimaryLighten = "#2C4373",
            Secondary = "#2DD4BF",
            SecondaryDarken = "#14B8A6",
            Tertiary = "#A78BFA",
            AppbarBackground = "#1A2B4A",
            AppbarText = "#FAF6EE",
            Background = "#FAF6EE",
            Surface = "#FFFFFF",
            DrawerBackground = "#F4EFE3",
            DrawerText = "#1A2B4A",
            DrawerIcon = "#1A2B4A",
            TextPrimary = "#1A1A1A",
            TextSecondary = "#4A4A4A",
            ActionDefault = "#4A4A4A",
            Divider = "#E5DFD0",
            Success = "#10B981",
            Warning = "#F59E0B",
            Error = "#DC2626",
            Info = "#3B82F6",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#7E9DD2",
            PrimaryDarken = "#5B7FBF",
            PrimaryLighten = "#A1BAE0",
            Secondary = "#5EEAD4",
            SecondaryDarken = "#2DD4BF",
            Tertiary = "#C4B5FD",
            AppbarBackground = "#1E2A44",
            AppbarText = "#F2EEE0",
            Background = "#1A2238",
            Surface = "#222D48",
            DrawerBackground = "#1E2A44",
            DrawerText = "#E5DFD0",
            DrawerIcon = "#E5DFD0",
            TextPrimary = "#F2EEE0",
            TextSecondary = "#C5BFB1",
            ActionDefault = "#C5BFB1",
            Divider = "#2D3A56",
            DividerLight = "#39476A",
            Success = "#34D399",
            Warning = "#FBBF24",
            Error = "#F87171",
            Info = "#60A5FA",
            LinesDefault = "#39476A",
            LinesInputs = "#39476A",
            TableLines = "#2D3A56",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "-apple-system", "BlinkMacSystemFont", "Segoe UI", "Roboto", "system-ui", "sans-serif"],
                FontSize = "0.95rem",
                LineHeight = "1.5",
            },
            H1 = new H1Typography { FontFamily = ["Inter", "system-ui", "sans-serif"], FontWeight = "700", FontSize = "2rem", LineHeight = "1.2" },
            H2 = new H2Typography { FontFamily = ["Inter", "system-ui", "sans-serif"], FontWeight = "700", FontSize = "1.5rem", LineHeight = "1.25" },
            H3 = new H3Typography { FontFamily = ["Inter", "system-ui", "sans-serif"], FontWeight = "600", FontSize = "1.2rem", LineHeight = "1.3" },
            H6 = new H6Typography { FontFamily = ["Inter", "system-ui", "sans-serif"], FontWeight = "600", FontSize = "1rem" },
            Button = new ButtonTypography { FontFamily = ["Inter", "system-ui", "sans-serif"], FontWeight = "500", TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            AppbarHeight = "56px",
            DrawerWidthLeft = "300px",
        },
    };
}
