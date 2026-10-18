using System.ComponentModel;
using System.Configuration;
using System.Xml.Linq;
using OmenFanPlus.Properties;

namespace OmenFanPlus.Services
{
    /// <summary>
    /// .NET stores user.config in a folder named after a hash of the exe path, so a rebuilt or moved copy of the
    /// app starts with empty settings. On the first start of a copy, take over the settings of the most recently
    /// used other copy, including copies from before the rename to OmenFanPlus (OmenHubLighterPlus).
    /// </summary>
    internal static class SettingsMigration
    {
        // Folder under %LOCALAPPDATA% and settings section name of the version before the rename.
        private const string LegacyName = "OmenHubLighterPlus";

        public static bool TryImportFromOtherCopy()
        {
            try
            {
                var current = Path.GetFullPath(ConfigurationManager
                    .OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath);

                // ...\<company>\<exe>_Url_<hash>\<version>\user.config: search every copy under both names.
                var roots = new[]
                {
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, "..", "..")),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyName),
                };

                var source = roots.Where(Directory.Exists)
                    .SelectMany(root => Directory.EnumerateFiles(root, "user.config", SearchOption.AllDirectories))
                    .Where(f => !string.Equals(Path.GetFullPath(f), current, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (source == null)
                    return false;

                var userSettings = XDocument.Load(source.FullName).Root?.Element("userSettings");
                var section = userSettings?.Element("OmenFanPlus.Properties.Settings")
                              ?? userSettings?.Element(LegacyName + ".Properties.Settings");
                if (section == null)
                    return false;

                var imported = new List<string>();
                foreach (var setting in section.Elements("setting"))
                {
                    var name = (string?)setting.Attribute("name");
                    var value = setting.Element("value")?.Value;
                    if (name == null || value == null || name == nameof(Settings.FirstStart))
                        continue;

                    var property = Settings.Default.Properties[name];
                    if (property == null)
                        continue;

                    try
                    {
                        var converter = TypeDescriptor.GetConverter(property.PropertyType);
                        Settings.Default[name] = converter.ConvertFromInvariantString(value);
                        imported.Add($"{name}={value}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"Skipped setting {name}='{value}' from {source.FullName}: {ex.Message}");
                    }
                }

                AppLog.Info($"Imported settings from {source.FullName}: {string.Join(", ", imported)}");
                return imported.Count > 0;
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not import settings from another copy of the app", ex);
                return false;
            }
        }
    }
}
