using System;
using System.Collections.Generic;
using System.Linq;

namespace CommandCenter.Model
{
    // Every icon a tab can show. The 6 built-in .ico files are the exact embedded img/*.ico
    // resources GMS/CMS/Live/Server Status/Settings/the app itself already use - referenced by
    // their pack-relative "img/…ico" string, same as MainWindow.xaml's own Image sources. Only 4
    // of those 6 (GMS/CMS/Live/App Icon) are offered as user-selectable Presets below - Servers.ico
    // and Settings.ico stay reserved for their own one-of-a-kind tabs, but DefaultIconFor still
    // uses both internally for those tabs' own default. A tab can also point at a custom image the
    // user picked via Settings' "Change Icon" (see DraftTabViewModel.ChooseIconCommand /
    // ChooseIconDialog) - TabSettings.CustomIconPath, when set, always wins over the Kind/Category
    // default below.
    public static class TabIconCatalog
    {
        public sealed record IconChoice(string Label, string Path);

        // Offered as ready-made presets in ChooseIconDialog, alongside "Browse for image..." -
        // reuses the exact same files each permanent tab already defaults to, so picking "Maple"
        // here looks identical to what GMS already shows. Servers/Settings are deliberately left
        // out (unlike Maple/Classic/Live/App Icon) - those two belong to the Server Status and
        // Settings tabs specifically, which are always-one-of-a-kind singletons (see TabKind), so
        // letting an extra build tab also wear one of those icons would make it look like a second
        // Server Status or Settings tab in the tray. DefaultIconFor below still uses both files for
        // their actual owning tabs; only the picker excludes them. OpTool.ico is the same story -
        // reserved for the OpTool singleton tab, never offered as a preset.
        //
        // img/Server Up.gif and img/Server Down.gif (see AppPaths.ServerUpGif/ServerDownGif) live
        // in this same img/ source folder (though unlike the .ico files above, they're copied to
        // the output directory as loose files, not embedded - see the csproj) - they are Server
        // Status' animated up/down indicators, not tab icons, and must never be offered here either
        // - excluded today simply because Presets is a hand-picked list (not a scan of img/) and
        // ChooseIconDialog.Browse_Click's file filter ("*.ico;*.png;*.jpg;*.jpeg") already omits
        // .gif, but call this out explicitly so anyone later turning Presets or Browse into a scan
        // of img/ knows to keep excluding both gifs.
        public static IReadOnlyList<IconChoice> Presets { get; } = new List<IconChoice>
        {
            new("Maple", "img/Maple.ico"),
            new("Classic", "img/Classic.ico"),
            new("Live", "img/Live.ico"),
            new("App Icon", "img/AppIcon.ico"),
        };

        // A custom upload has to be a square image at least this many pixels on a side - matches
        // the 5 built-in tab icons, which are all exactly 256x256 .ico files, so a custom pick can
        // never end up blurrier in the tab strip than its neighbors (shown at 16x16, but stored at
        // full size the same way the built-ins are).
        public const int RequiredCustomIconSize = 256;

        // What TabInfo.IconSource actually renders for a given tab - a custom pick (if any) always
        // wins; otherwise falls back to the same Kind/Category lookup DraftTabViewModel.
        // IconPreviewSource mirrors for the Settings-tab preview.
        public static string IconFor(TabSettings settings) =>
            !string.IsNullOrWhiteSpace(settings.CustomIconPath)
                ? settings.CustomIconPath!
                : DefaultIconFor(settings.Kind, settings.Category);

        // Display name for whatever IconFor/DefaultIconFor returned - "Custom image" for anything
        // that isn't one of the 4 preset paths (i.e. a user upload copied under
        // AppPaths.TabIconsFolder). In practice this only ever runs for a non-permanent tab (see
        // DraftTabViewModel.CanCustomizeIcon), whose Kind/Category default is always one of the 4
        // presets in the first place, so Servers.ico/Settings.ico never reach here even though
        // they're excluded from Presets. Used by DraftTabViewModel.IconPreviewLabel (next to
        // Settings' "Change Icon" button) and ChooseIconDialog (pre-selecting/labeling the current
        // icon when the dialog opens).
        public static string LabelFor(string path) =>
            Presets.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))?.Label
                ?? "Custom image";

        public static string DefaultIconFor(TabKind kind, SectionCategory category) => kind switch
        {
            TabKind.ServerStatus => "img/Servers.ico",
            TabKind.OpTool => "img/OpTool.ico",
            TabKind.Settings => "img/Settings.ico",
            TabKind.BuildSection => category switch
            {
                SectionCategory.Gms => "img/Maple.ico",
                SectionCategory.Cms => "img/Classic.ico",
                SectionCategory.Live => "img/Live.ico",
                _ => "img/AppIcon.ico"
            },
            _ => "img/AppIcon.ico"
        };
    }
}
