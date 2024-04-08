using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MelonLoader;
using MelonLoader.Utils;
using static MelonLoader.MelonLogger;
using Mono.Cecil;
using System.Text;

namespace NWModManager
{
    public class NWModManager : MelonPlugin
    {
        static WebClient client;
        static RSACryptoServiceProvider rsa;
        static string updates = Resources.Updates;
        static readonly Regex updateMatch = new(@"^(?<name>.*) +(?<ver>\d+\.\d+.*?) +(?<link>http.*?)(?: +(?<deps>.*))?$");
        static readonly Dictionary<string, Tuple<Semver.SemVersion, Match>> updateDict = [];

        static readonly List<string> disableTemp = [];

        static readonly Dictionary<string, MelonInfoAttribute> cachedAttr = [];

        public void DownloadVerifyDLL(string url, string output, RSAParameters key)
        {
            rsa.ImportParameters(key);
            byte[] data = client.DownloadData(url);
            if (!rsa.VerifyData(data, SHA256.Create(), client.DownloadData(url + ".sig")))
            {
                throw new Exception("The DLL did not pass the signature check. Please contact the mod developer immediately.");
            }
            File.WriteAllBytes(output, data);
        }

        public override void OnApplicationEarlyStart()
        {
            client = new();
            rsa = new();

            Settings.Register();
            if (Settings.UpdateEnabled.Value)
            {
                Msg("------------------------------");
                Msg("Fetching updates...");
                try
                {
                    updates = client.DownloadString("https://raw.githubusercontent.com/stxticOVFL/WhitesStorage/master/Updates.txt");
                }
                catch (Exception e)
                {
                    Error($"  Failed to pull latest updates: {e.Message}");
                    Warning("  Using backup list from build date!!! This may be *EXTREMELY* outdated!!");
                }

                var reader = new StringReader(updates);
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
                        Error($"  {e.Message}");
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
                        Msg("    Version is up-to-date.");
                        continue;
                    }
                    toUpdate[attribute.Name] = file;
                    Msg($"    Marked for download! New version: {verDL.Item1}");
                }

                HashSet<string> deps = [];
                Dictionary<string, RSAParameters> keys = [];

                var missing = updateDict.Keys.Except(check);

                if (toUpdate.Count + missing.Count() > 0)
                {
                    Msg("------------------------------");
                    Msg("Downloading and verifying the public keys...");

                    try
                    {
                        var split = Resources.PublicKeys.Split('|');
                        static byte[] db64(string str) => Convert.FromBase64String(str);

                        rsa.ImportParameters(new RSAParameters
                        {
                            Modulus = db64(split[0]),
                            Exponent = db64(split[1])
                        });
                        byte[] data = client.DownloadData("https://raw.githubusercontent.com/stxticOVFL/WhitesStorage/encrypt-dev/Keys/PublicKeys.txt");
                        if (!rsa.VerifyData(data, SHA256.Create(), client.DownloadData("https://raw.githubusercontent.com/stxticOVFL/WhitesStorage/encrypt-dev/Keys/PublicKeys.txt.sig"))) {
                            throw new Exception("The public keys did not pass the signature check.");
                        }

                        var str = Encoding.UTF8.GetString(data);
                        reader = new(str);
                        for (string line = reader.ReadLine(); line != null; line = reader.ReadLine())
                        {
                            if (line == "") continue;
                            try
                            {
                                split = line.Substring(line.LastIndexOf(' ')).Split('|');
                                keys.Add(line.Substring(0, line.LastIndexOf(' ')), new RSAParameters
                                {
                                    Modulus = db64(split[0]),
                                    Exponent = db64(split[1])
                                });
                            }
                            catch (Exception e)
                            {
                                Warning($"  Error parsing line: {line}");
                                Error($"  {e.Message}");
                            }
                        }

                    }
                    catch (Exception e)
                    {
                        Error($"  Failed to download/verify public keys: {e.Message}");
                        Error($"  Downloads will have to be skipped this time around.");
                        toUpdate.Clear();
                        missing = Enumerable.Empty<string>();
                    }
                }
                
                // filter out anything that doesn't have a key
                toUpdate = toUpdate.Where(pair => keys.ContainsKey(pair.Key)).ToDictionary(x => x.Key, x => x.Value);
                missing = missing.Where(keys.ContainsKey);

                if (toUpdate.Count > 0)
                {
                    Msg("------------------------------");

                    foreach (var mod in toUpdate.Keys)
                    {
                        Msg($"Downloading new version of {mod}...");

                        try
                        {
                            if (File.Exists(toUpdate[mod] + ".old"))
                                File.Delete(toUpdate[mod] + ".old");
                            File.Move(toUpdate[mod], toUpdate[mod] + ".old");

                            var url = updateDict[mod].Item2.Groups["link"].Value;
                            DownloadVerifyDLL(url, toUpdate[mod], keys[mod]);
                            Msg($"  Successfully updated {mod}!");

                            var depSplit = updateDict[mod].Item2.Groups["deps"]?.Value.Split();
                            depSplit?.ToList().ForEach(dep => { deps.Add(dep); });
                        }
                        catch (Exception e)
                        {
                            if (File.Exists(toUpdate[mod] + ".old"))
                                File.Move(toUpdate[mod] + ".old", toUpdate[mod]);
                            Error($"  Failed to update mod: {e.Message}");
                        }
                    }
                }

                if (missing.Count() > 0)
                {
                    Msg("------------------------------");

                    foreach (var mod in missing)
                    {
                        if (!(bool)Settings.UpdateCategory.GetEntry($"{mod}_U").BoxedValue)
                            continue;
                        Msg($"Missing {mod}! Downloading...");
                        if (mod != "MelonPreferencesManager")
                            Settings.EnableCategory.GetEntry($"{mod}_E").BoxedValue = false;

                        try
                        {
                            var url = updateDict[mod].Item2.Groups["link"].Value;
                            var filename = Path.GetFileName(url);
                            DownloadVerifyDLL(url, MelonEnvironment.ModsDirectory + "/" + filename, keys[mod]);
                            Msg($"  Successfully downloaded {mod}!");

                            var depSplit = updateDict[mod].Item2.Groups["deps"]?.Value.Split();
                            depSplit?.ToList().ForEach(dep => { deps.Add(dep); });
                        }
                        catch (Exception e)
                        {
                            Error($"  Failed to download mod: {e.Message}");
                        }
                    }
                }

                if (deps.Count() > 0) {
                    Msg("------------------------------");

                    foreach (var dep in deps)
                    {
                        if (string.IsNullOrEmpty(dep)) continue;
                        var filename = Path.GetFileNameWithoutExtension(dep);
                        Msg($"Downloading/updating dependency {Path.GetFileNameWithoutExtension(dep)}...");

                        try
                        {
                            File.WriteAllBytes(MelonEnvironment.ModsDirectory + "/" + filename + ".dll", client.DownloadData(dep));
                            Msg($"  Successfully downloaded {filename}!");
                        }
                        catch (Exception e)
                        {
                            Error($"  Failed to download dependency: {e}");
                        }
                    }
                }

                reader.Dispose();
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
                    Msg($"  Disabling {attribute.Name}...");
                    disableTemp.Add(file);
                    File.Move(file, file + ".NWMMD");
                }
            }
            MelonPreferences.Save();

            client.Dispose();
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
