using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CommandCenter.Model
{
    // Persists AppSettings (every tab's build path/version number/title/visibility/order) to a
    // JSON file under %AppData%\CommandCenter so it survives an app restart.
    public class SettingsService
    {
        private readonly string _settingsPath;

        public SettingsService()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CommandCenter");
            Directory.CreateDirectory(folder);
            _settingsPath = Path.Combine(folder, "settings.json");
        }

        public AppSettings Load()
        {
            if (!File.Exists(_settingsPath))
            {
                return CreateDefaultSettings();
            }

            try
            {
                string json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefaultSettings();
                MigrateIfNeeded(settings);
                MigrateServersIfNeeded(settings);
                EnsurePermanentLiveSupportsPushedToLive(settings);
                return settings;
            }
            catch (Exception)
            {
                return CreateDefaultSettings();
            }
        }

        public void Save(AppSettings settings)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(_settingsPath, json);
        }

        // The 5 tabs every install starts with: GMS, CMS, Live Service Builds, Server Status,
        // Settings - all permanent, all visible, in that order.
        private static AppSettings CreateDefaultSettings() => new AppSettings { Tabs = BuildDefaultPermanentTabs() };

        private static List<TabSettings> BuildDefaultPermanentTabs() => new()
        {
            new TabSettings { Kind = TabKind.BuildSection, Category = SectionCategory.Gms, IsPermanent = true, Title = "GMS", Order = 0,
                Servers = LaunchServerCatalog.BuiltInEntries(SectionCategory.Gms).ToList() },
            new TabSettings { Kind = TabKind.BuildSection, Category = SectionCategory.Cms, IsPermanent = true, Title = "CMS", Order = 1,
                Servers = LaunchServerCatalog.BuiltInEntries(SectionCategory.Cms).ToList() },
            new TabSettings { Kind = TabKind.BuildSection, Category = SectionCategory.Live, IsPermanent = true, Title = "Live Service Builds", Order = 2,
                SupportsPushedToLive = true, Servers = LaunchServerCatalog.BuiltInEntries(SectionCategory.Live).ToList() },
            new TabSettings { Kind = TabKind.ServerStatus, IsPermanent = true, Title = "Server Status", Order = 3 },
            new TabSettings { Kind = TabKind.Settings, IsPermanent = true, Title = "Settings", Order = 4 },
        };

        // Backfills TabSettings.Servers for every BuildSection tab in a settings.json saved before
        // per-tab servers existed - null there means "never migrated" (see TabSettings.Servers),
        // never "user emptied this tab's list on purpose", since an intentionally-emptied list is
        // still a real (non-null) empty array once it's been through this once and gets persisted.
        // Gms/Cms/Live get seeded from the built-in registry so existing installs don't lose their
        // launch dropdown; General tabs (every "+ Add Tab" tab) just get an empty list - they never
        // had built-ins to begin with, but can now have custom servers added via Settings.
        private static void MigrateServersIfNeeded(AppSettings settings)
        {
            foreach (var tab in settings.Tabs)
            {
                if (tab.Kind == TabKind.BuildSection && tab.Servers == null)
                {
                    tab.Servers = LaunchServerCatalog.BuiltInEntries(tab.Category).ToList();
                }
            }
        }

        // Before SupportsPushedToLive existed, the permanent Live tab's Pushed to Live support was
        // derived purely from Category == Live (see MainViewModel.CreateBuildSectionViewModel's old
        // code). A settings.json saved by that version already has Tabs populated, so
        // MigrateIfNeeded below is a no-op for it and SupportsPushedToLive just deserializes to its
        // JSON default (false) on the permanent Live tab - this fixes that up on every load,
        // unconditionally, rather than trying to version-detect it once. Idempotent and cheap
        // (single LINQ scan), so it's safe to run on every Load regardless of whether anything
        // actually needed fixing. Never touches extra tabs - those never had this derived from
        // Category to begin with (see SettingsViewModel.AddTab), so there's nothing to backfill.
        private static void EnsurePermanentLiveSupportsPushedToLive(AppSettings settings)
        {
            var permanentLive = settings.Tabs.FirstOrDefault(t =>
                t.IsPermanent && t.Kind == TabKind.BuildSection && t.Category == SectionCategory.Live);

            if (permanentLive != null)
            {
                permanentLive.SupportsPushedToLive = true;
            }
        }

        // A settings.json saved before tabs existed has Tabs empty (the type's default) but its
        // legacy Gms/Cms/Live/ExtraSections fields populated - migrate those into Tabs exactly
        // once, preserving every build path/version number/extra section (keeping each extra's
        // original Id so nothing about it looks "new"), then reset the legacy fields so they
        // don't sit around duplicated in the persisted file going forward. Safe to call on an
        // already-current file too (Tabs.Count > 0 short-circuits immediately) and produces the
        // same 5 default tabs as a fresh install if the legacy fields turn out to be empty too.
        private static void MigrateIfNeeded(AppSettings settings)
        {
            if (settings.Tabs.Count > 0)
            {
                return;
            }

            int order = 0;
            settings.Tabs.Add(new TabSettings
            {
                Kind = TabKind.BuildSection, Category = SectionCategory.Gms, IsPermanent = true, Title = "GMS", Order = order++,
                BuildPath = settings.Gms.BuildPath, VersionNumber = settings.Gms.VersionNumber
            });
            settings.Tabs.Add(new TabSettings
            {
                Kind = TabKind.BuildSection, Category = SectionCategory.Cms, IsPermanent = true, Title = "CMS", Order = order++,
                BuildPath = settings.Cms.BuildPath, VersionNumber = settings.Cms.VersionNumber
            });
            settings.Tabs.Add(new TabSettings
            {
                Kind = TabKind.BuildSection, Category = SectionCategory.Live, IsPermanent = true, Title = "Live Service Builds", Order = order++,
                SupportsPushedToLive = true, BuildPath = settings.Live.BuildPath, VersionNumber = settings.Live.VersionNumber
            });

            foreach (var extra in settings.ExtraSections)
            {
                settings.Tabs.Add(new TabSettings
                {
                    Id = extra.Id,
                    Kind = TabKind.BuildSection,
                    Category = extra.Category,
                    IsPermanent = false,
                    Title = extra.Label,
                    Order = order++,
                    BuildPath = extra.BuildPath,
                    VersionNumber = extra.VersionNumber
                });
            }

            settings.Tabs.Add(new TabSettings { Kind = TabKind.ServerStatus, IsPermanent = true, Title = "Server Status", Order = order++ });
            settings.Tabs.Add(new TabSettings { Kind = TabKind.Settings, IsPermanent = true, Title = "Settings", Order = order++ });

            // Nothing reads these again after this point - reset so the persisted file doesn't
            // carry stale, now-duplicated data forward.
            settings.Gms = new SectionSettings();
            settings.Cms = new SectionSettings();
            settings.Live = new SectionSettings();
            settings.ExtraSections = new List<ExtraSectionSettings>();
        }
    }
}
