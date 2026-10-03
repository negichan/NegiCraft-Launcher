using Avalonia.Controls;

namespace NegiCraftLauncher.Pet.Services;

/// <summary>
/// Makes a pet window behave like a desktop companion instead of an ordinary app window.
///
/// Avalonia has no ToolWindow concept, and <c>ShowInTaskbar=false</c> only clears the taskbar
/// button (it re-parents the window to a hidden offscreen owner and strips WS_EX_APPWINDOW).
/// The window still shows up in Alt+Tab / Task view and still takes foreground on click.
/// Neither of the two ex-style bits that fix that has a corresponding Avalonia property, so we
/// OR them in through the one hook the Win32 backend re-runs on every style update:
/// <see cref="Win32Properties.AddWindowStylesCallback"/>.
///
///   - WS_EX_TOOLWINDOW (0x00000080) — removes the window from Alt+Tab and Task view.
///   - WS_EX_NOACTIVATE (0x08000000) — a click no longer makes it the foreground window.
///
/// Both are additive: the incoming style/ex-style is preserved and Topmost still applies, so the
/// pet keeps floating above everything (including full-screen games) exactly as before.
/// </summary>
public static class PetShellStyle
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    // A single stateless instance so Add/Remove always refer to the same delegate.
    private static readonly Win32Properties.CustomWindowStylesCallback Callback =
        static (style, exStyle) => (style, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

    /// <summary>
    /// Registers the tool-window/no-activate styles for the lifetime of <paramref name="topLevel"/>.
    /// Must be called while the window still has a live platform impl (i.e. before it is closed);
    /// the styles land the next time the backend updates window properties, which <c>Show()</c>
    /// always triggers. On non-Win32 backends the callback is simply ignored.
    /// </summary>
    public static void ApplyToolWindow(TopLevel topLevel) =>
        Win32Properties.AddWindowStylesCallback(topLevel, Callback);

    /// <summary>Removes a callback previously added by <see cref="ApplyToolWindow"/>.</summary>
    public static void RemoveToolWindow(TopLevel topLevel) =>
        Win32Properties.RemoveWindowStylesCallback(topLevel, Callback);
}
