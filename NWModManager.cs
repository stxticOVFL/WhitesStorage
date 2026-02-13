using System.Net;
using System.Text.RegularExpressions;
using MelonLoader;
using MelonLoader.Utils;
using static MelonLoader.MelonLogger;
using Mono.Cecil;

namespace NWModManager
{
    public class NWModManager : MelonPlugin
    {
        static string updates = Resources.Updates;
        static readonly Regex updateMatch = new(@"^(?<name>.*) +(?<ver>\d+\.\d+.*?) +(?<link>http.*?)(?: +(?<deps>.*))?$");
        static readonly Dictionary<string, Tuple<Semver.SemVersion, Match>> updateDict = [];

        static readonly List<string> disableTemp = [];

        static readonly Dictionary<string, MelonInfoAttribute> cachedAttr = [];

        public override void OnApplicationEarlyStart()
        {

            Settings.Register();
            if (Settings.UpdateEnabled.Value)
            {
                Msg("------------------------------");
                Msg("Fetching updates...");
                try
                {
                    using var client = new WebClient();
                    updates = client.DownloadString("https://raw.githubusercontent.com/stxticOVFL/WhitesStorage/master/Updates.txt");
                }
                catch (Exception e)
                {
                    Warning("  Failed to pull latest updates:");
                    Error($"  {e}");
                    Warning("  Using backup list from build date!!! This may be *EXTREMELY* outdated!!");
                }

                using var reader = new StringReader(updates);
                for (string line = reader.ReadLine(); line != null; line = reader.ReadLine())
                {
                    if (line == "") continue;
                    try
                    {
                        var m = updateMatch.Match(line.Trim());
                        updateDict[m.Groups["name"].Value] = new(m.Groups["ver"].Value, m);
                    }
                    catch (Exception e)
                    {
                        Warning($"  Error parsing line: {line}");
                        Error($"  {e}");
                    }
                }
                Settings.RegisterMods();

                Dictionary<string, string> toUpdate = [];
                List<string> check = [];

                Msg("------------------------------");
                Msg("Checking for updates...");
                foreach (var file in Directory.EnumerateFiles(MelonEnvironment.ModsDirectory, "*.dll"))
                {
                    var fstream = File.OpenRead(file);
                    var module = ModuleDefinition.ReadModule(fstream);
                    fstream.Close();
                    MelonInfoAttribute attribute = null;
                    // we move
                    foreach (var item in module.Assembly.CustomAttributes)
                    {
                        if (item.Constructor.DeclaringType.FullName == typeof(MelonInfoAttribute).FullName)
                        {
                            attribute = new MelonInfoAttribute(typeof(MelonBase), item.ConstructorArguments[1].Value as string, item.ConstructorArguments[2].Value as string, null);
                            break;
                        }
                    }

                    if (attribute == null) continue;
                    cachedAttr.Add(file, attribute);
                    check.Add(attribute.Name);
                    if (!updateDict.ContainsKey(attribute.Name) || !(bool)Settings.UpdateCategory.GetEntry($"{attribute.Name}_U").BoxedValue)
                        continue;

                    Msg($"  Checking {attribute.Name} {attribute.Version}...");
                    var verDL = updateDict[attribute.Name];

                    if (attribute.SemanticVersion > verDL.Item1)
                    {
                        Warning("    Version is ahead? Skipping...");
                        continue;
                    }
                    else if (attribute.SemanticVersion == verDL.Item1)
                    {
                        Msg("    Version is equal. Skipping.");
                        continue;
                    }
                    toUpdate[attribute.Name] = file;
                    Msg($"    Marked for download! New version: {verDL.Item1}");
                }

                Msg("------------------------------");

                HashSet<string> deps = [];

                foreach (var mod in toUpdate.Keys)
                {
                    Msg($"  Downloading new version of {mod}...");

                    try
                    {
                        if (File.Exists(toUpdate[mod] + ".old"))
                            File.Delete(toUpdate[mod] + ".old");
                        File.Move(toUpdate[mod], toUpdate[mod] + ".old");

                        using var client = new WebClient();
                        var url = updateDict[mod].Item2.Groups["link"].Value;
                        File.WriteAllBytes(toUpdate[mod], client.DownloadData(url));
                        Msg($"    Successfully updated {mod}!");

                        var depSplit = updateDict[mod].Item2.Groups["deps"]?.Value.Split();
                        depSplit?.ToList().ForEach(dep => { deps.Add(dep); });
                    }
                    catch (Exception e)
                    {
                        if (File.Exists(toUpdate[mod] + ".old"))
                            File.Move(toUpdate[mod] + ".old", toUpdate[mod]);
                        Error($"    Failed to update mod: {e}");
                    }
                }


                foreach (var mod in updateDict.Keys.Except(check))
                {
                    if (!(bool)Settings.UpdateCategory.GetEntry($"{mod}_U").BoxedValue)
                        continue;
                    Msg($"  Missing {mod}! Downloading...");
                    if (mod != "MelonPreferencesManager")
                        Settings.EnableCategory.GetEntry($"{mod}_E").BoxedValue = false;

                    try
                    {
                        var url = updateDict[mod].Item2.Groups["link"].Value;
                        var filename = Path.GetFileName(url);
                        using var client = new WebClient();
                        File.WriteAllBytes(MelonEnvironment.ModsDirectory + "/" + filename, client.DownloadData(url));
                        Msg($"    Successfully downloaded {mod}!");

                        var depSplit = updateDict[mod].Item2.Groups["deps"]?.Value.Split();
                        depSplit?.ToList().ForEach(dep => { deps.Add(dep); });
                    }
                    catch (Exception e)
                    {
                        Error($"    Failed to download mod: {e}");
                    }
                }

                foreach (var dep in deps)
                {
                    if (string.IsNullOrEmpty(dep)) continue;
                    var filename = Path.GetFileNameWithoutExtension(dep);
                    Msg($"    Downloading/updating dependency {Path.GetFileNameWithoutExtension(dep)}...");

                    try
                    {
                        using var client = new WebClient();
                        File.WriteAllBytes(MelonEnvironment.ModsDirectory + "/" + filename + ".dll", client.DownloadData(dep));
                        Msg($"    Successfully downloaded {filename}!");
                    }
                    catch (Exception e)
                    {
                        Error($"    Failed to download dependency: {e}");
                    }

                }
            }

            Msg("------------------------------");
            Msg("Checking for disabled mods...");
            foreach (var file in Directory.EnumerateFiles(MelonEnvironment.ModsDirectory, "*.dll"))
            {
                if (!cachedAttr.TryGetValue(file, out var attribute) || attribute.Name == "MelonPreferencesManager") continue;
                if (!Settings.EnableCategory.HasEntry($"{attribute.Name}_E"))
                    Settings.EnableCategory.CreateEntry($"{attribute.Name}_E", true, display_name: $"Enable {attribute.Name}", description: $"Whether or not to enable {attribute.Name}");
                if (!(bool)Settings.EnableCategory.GetEntry($"{attribute.Name}_E")?.BoxedValue)
                {
                    Msg($"  Disabling {attribute.Name}.");
                    disableTemp.Add(file);
                    File.Move(file, file + ".NWMMD");
                }
            }
            MelonPreferences.Save();
        }

        public override void OnPreSupportModule() => disableTemp.ForEach(file => { File.Move(file + ".NWMMD", file); });

        static class Settings
        {
            public static MelonPreferences_Category EnableCategory;
            public static MelonPreferences_Category UpdateCategory;
            public static MelonPreferences_Entry<bool> UpdateEnabled;

            public static void Register()
            {
                // can't break tradition!
                EnableCategory = MelonPreferences.CreateCategory("NWModManager", "White's Storage");
                UpdateCategory = MelonPreferences.CreateCategory("NWModManagerU", "White's Storage (Updates)");
                UpdateEnabled = UpdateCategory.CreateEntry("Enable auto-updates", true, description: "Whether or not to enable the auto-updater (enabling and disabling mods still works)");
            }
            public static void RegisterMods()
            {
                foreach (var mod in updateDict.Keys)
                {
                    if (mod != "MelonPreferencesManager") // LOL
                        EnableCategory.CreateEntry($"{mod}_E", true, display_name: $"Enable {mod}", description: $"Whether or not to enable {mod}");
                    UpdateCategory.CreateEntry($"{mod}_U", true, display_name: $"Auto-update {mod}", description: $"Whether or not to update {mod} automatically");
                }
            }
        }
    }
}
