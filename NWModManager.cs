using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MelonLoader;
using MelonLoader.Utils;
using static MelonLoader.MelonLogger;
using Mono.Cecil;

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

        // this is ICKY.
        // but we have no choice :(
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr MessageBox(int hWnd, string text, string caption, uint type);

        public void DownloadVerifyDLL(string url, string output, RSAParameters key)
        {
            rsa.ImportParameters(key);
            byte[] data = client.DownloadData(url);
            Msg("  Verifying signature...");
            if (!rsa.VerifyData(data, SHA256.Create(), client.DownloadData(url + ".sig")))
                throw new Exception("The DLL did not pass the signature check. Please contact the mod developer immediately.");
            File.WriteAllBytes(output, data);
        }

        public override void OnApplicationEarlyStart()
        {
            client = new();
            rsa = new();

            Settings.Register();

            foreach (var file in Directory.EnumerateFiles(MelonEnvironment.ModsDirectory, "*.NWMMD"))
            {
                var name = file.Substring(0, file.LastIndexOf('.'));
                File.Move(name + ".NWMMD", name);
            }

            if (Settings.UpdateEnabled.Value)
            {
                Msg("------------------------------");
                Msg("Fetching updates...");
                try
                {
                    updates = client.DownloadString("https://raw.githubusercontent.com/stxticOVFL/WhitesStorage/encrypt-dev/Updates.txt");
                }
                catch (Exception e)
                {
                    if (Settings.ShowMessages.Value)
                        MessageBox(0, "The latest update list was not able to be fetched!\nUsing backup list from the compiled build, which could be *EXTREMELY OUTDATED!*", "White's Storage Auto-updater", 0x30);
                    Error($"  Failed to pull latest updates: {e.Message}");
                    Warning("  Using backup list from build date!!! This may be *EXTREMELY* outdated!!");
                }

                var reader = new StringReader(updates);
                string line = reader.ReadLine();
                var parsedUVer = Semver.SemVersion.Parse(line);
                if (parsedUVer > Info.SemanticVersion && Info.SemanticVersion > Semver.SemVersion.Parse(Settings.LastChecked.Value))
                {
                    Settings.LastChecked.Value = Info.SemanticVersion.ToString();
                    MelonPreferences.Save();
                    var result = MessageBox(0, """
                        White's Storage has a new update! Would you like to open the release and plugin folder?
                        This will immediately close the game.
                        
                        (This notice will only show once.)
                        """, "White's Storage Auto-updater", 0x44);
                    if (result == new IntPtr(6))
                    {
                        Process.Start("https://github.com/stxticOVFL/WhitesStorage/releases/latest");
                        Process.Start("file://" + MelonEnvironment.PluginsDirectory);
                        Environment.Exit(0);
                        return;
                    }
                }

                for (line = reader.ReadLine(); line != null; line = reader.ReadLine())
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

                var missing = updateDict.Keys.Except(check).Where(x => Settings.DownloadOptionals.Value || updateDict[x].Item2.Groups["deps"].Value.Split().Last() != "X");

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
                        if (!rsa.VerifyData(data, SHA256.Create(), client.DownloadData("https://raw.githubusercontent.com/stxticOVFL/WhitesStorage/encrypt-dev/Keys/PublicKeys.txt.sig")))
                            throw new Exception("The public keys did not pass the signature check.");

                        // BOM markings are showing up so this doens't work
                        // var str = new UTF8Encoding(true).GetString(data);
                        var str = client.DownloadString("https://raw.githubusercontent.com/stxticOVFL/WhitesStorage/encrypt-dev/Keys/PublicKeys.txt");

                        reader = new(str);
                        for (line = reader.ReadLine(); line != null; line = reader.ReadLine())
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
                        if (Settings.ShowMessages.Value)
                            MessageBox(0, "Failed to download/verify public keys.\nDownloads/updating will be skipped.", "White's Storage Auto-updater", 0x10); // 0x10 == ICONERROR
                        Error($"  Failed to download/verify public keys: {e.Message}");
                        Error($"  Downloads will have to be skipped this time around.");
                        toUpdate.Clear();
                        missing = Enumerable.Empty<string>();
                    }
                }

                // filter out anything that doesn't have a key
                // list of ones that don't have keys
                var keyGone = missing.Where(x => !keys.ContainsKey(x)).Concat(toUpdate.Keys.Where(x => !keys.ContainsKey(x)));
                // warn if that's the case
                if (keyGone.Count() > 0)
                {
                    // we have some keys missing
                    Msg("  The following mods are missing from the public key list:");
                    foreach (var item in keyGone)
                        Msg($"    - {item}");

                    if (Settings.ShowMessages.Value)
                    {
                        StringBuilder builder = new("The following queued mods are missing from the public key list:\n");
                        foreach (var item in keyGone)
                            builder.AppendLine($"- {item}");

                        builder.AppendLine();
                        builder.AppendLine("These mods will not be downloaded. Please let either the mod developers or stxtic know.");
                        MessageBox(0, builder.ToString().Trim(), "White's Storage Auto-updater", 0x30); // ICONWARNING
                    }
                }

                toUpdate = toUpdate.Where(pair => keys.ContainsKey(pair.Key)).ToDictionary(x => x.Key, x => x.Value);
                missing = missing.Where(keys.ContainsKey);

                if (toUpdate.Count + missing.Count() > 0 && !Settings.SkipConfirm.Value) // we have some stuff to update
                {
                    StringBuilder builder = new();

                    if (toUpdate.Count > 0)
                    {
                        builder.AppendLine("The following mods have an update available:");
                        foreach (var kv in toUpdate)
                        {
                            builder.AppendLine($"- {kv.Key}");
                        }
                    }

                    if (missing.Count() > 0)
                    {
                        builder.AppendLine();
                        builder.AppendLine("The following mods are currently missing:");
                        foreach (var kv in missing)
                        {
                            builder.AppendLine($"- {kv}");
                        }
                    }

                    builder.AppendLine();
                    builder.AppendLine("Would you like to download/update these mods and their dependencies?");
                    builder.AppendLine("Their signatures will be verified.");
                    if (missing.Count() > 0)
                        builder.AppendLine("(Newly downloaded mods will be initially disabled)");

                    var result = MessageBox(0, builder.ToString().Trim(), "White's Storage Auto-updater", 4 | 0x40); // 4 == MB_YESNO, 0x40 == MB_ICONINFORMATION
                    if (result != new IntPtr(6))
                    { // 6 == IDYES
                        toUpdate.Clear();
                        missing = Enumerable.Empty<string>();
                    }
                }

                HashSet<string> failed = [];

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
                            try
                            {
                                if (File.Exists(toUpdate[mod] + ".old"))
                                    File.Move(toUpdate[mod] + ".old", toUpdate[mod]);
                            }
                            catch { }
                            failed.Add(mod);
                            Error($"  Failed to update mod: {e.Message}");
                        }
                    }
                }

                if (missing.Count() > 0)
                {
                    Msg("------------------------------");

                    foreach (var mod in missing)
                    {
                        if (!Settings.UpdateCategory.GetEntry<bool>($"{mod}_U").Value)
                            continue;
                        Msg($"Missing {mod}! Downloading...");
                        if (mod != "MelonPreferencesManager")
                            Settings.EnableCategory.GetEntry<bool>($"{mod}_E").Value = false;

                        try
                        {
                            var url = updateDict[mod].Item2.Groups["link"].Value;
                            var filename = Path.GetFileName(url);
                            DownloadVerifyDLL(url, MelonEnvironment.ModsDirectory + "/" + filename, keys[mod]);
                            Msg($"  Successfully downloaded {mod}!");

                            var depSplit = updateDict[mod].Item2.Groups["deps"]?.Value.Split();
                            depSplit?.ToList().ForEach(dep => { if (dep != "X") deps.Add(dep); });
                        }
                        catch (Exception e)
                        {
                            failed.Add(mod);
                            Error($"  Failed to download mod: {e.Message}");
                        }
                    }
                }

                deps = new(deps.Where(x => keys.ContainsKey(Path.GetFileNameWithoutExtension(x))));

                if (deps.Count() > 0)
                {
                    Msg("------------------------------");

                    foreach (var dep in deps)
                    {
                        if (string.IsNullOrEmpty(dep)) continue;
                        var filename = Path.GetFileNameWithoutExtension(dep);
                        Msg($"Downloading/updating dependency {filename}...");

                        try
                        {
                            DownloadVerifyDLL(dep, MelonEnvironment.ModsDirectory + "/" + Path.GetFileName(dep), keys[filename]);
                            Msg($"  Successfully downloaded {filename}!");
                        }
                        catch (Exception e)
                        {
                            failed.Add(filename);
                            Error($"  Failed to download dependency: {e.Message}");
                        }
                    }
                }

                if (failed.Count() > 0 && Settings.ShowMessages.Value)
                {
                    StringBuilder builder = new("The following mods/dependencies failed to download:\n");

                    foreach (var item in failed)
                        builder.AppendLine($"- {item}");
                    builder.AppendLine();
                    builder.AppendLine("Please check with the mod developers or stxtic and ensure everything is correct, and check Latest.log for more details.");
                    MessageBox(0, builder.ToString(), "White's Storage Auto-updater", 0x30);
                }

                reader.Dispose();
                rsa.Dispose();
                client.Dispose();
            }

            Msg("------------------------------");
            Msg("Checking for disabled mods...");
            foreach (var file in Directory.EnumerateFiles(MelonEnvironment.ModsDirectory, "*.dll"))
            {
                if (!cachedAttr.TryGetValue(file, out var attribute) || attribute.Name == "MelonPreferencesManager") continue;
                if (!Settings.EnableCategory.HasEntry($"{attribute.Name}_E"))
                    Settings.EnableCategory.CreateEntry($"{attribute.Name}_E", true, display_name: $"Enable {attribute.Name}", description: $"Whether or not to enable {attribute.Name}");
                if (!(bool)Settings.EnableCategory.GetEntry<bool>($"{attribute.Name}_E")?.Value)
                {
                    Msg($"  Disabling {attribute.Name}...");
                    disableTemp.Add(file);
                    File.Move(file, file + ".NWMMD");
                }
            }
            MelonPreferences.Save();
            Msg("------------------------------");
        }

        public override void OnPreSupportModule() => disableTemp.ForEach(file => { File.Move(file + ".NWMMD", file); });

        static class Settings
        {
            public static MelonPreferences_Category EnableCategory;
            public static MelonPreferences_Category UpdateCategory;
            public static MelonPreferences_Entry<bool> ShowMessages;
            public static MelonPreferences_Entry<bool> SkipConfirm;
            public static MelonPreferences_Entry<bool> UpdateEnabled;
            public static MelonPreferences_Entry<bool> DownloadOptionals;

            public static MelonPreferences_Entry<string> LastChecked;

            public static void Register()
            {
                // can't break tradition!
                EnableCategory = MelonPreferences.CreateCategory("NWModManager", "White's Storage");
                UpdateCategory = MelonPreferences.CreateCategory("NWModManagerU", "White's Storage (Updates)");
                UpdateEnabled = UpdateCategory.CreateEntry("Enable auto-updates", true, description: "Whether or not to enable the auto-updater (enabling and disabling mods still works)");
                ShowMessages = UpdateCategory.CreateEntry("Show popup messages", true, description: "Whether or not to show popups before actions like updating and some errors");
                SkipConfirm = UpdateCategory.CreateEntry("Skip download confirmation", false, description: "Whether or not to skip the download confirmation popup specifically");
                DownloadOptionals = UpdateCategory.CreateEntry("Download optional mods", false, description: "Whether or not to download some optional mods like YourStory");
                LastChecked = UpdateCategory.CreateEntry("Last version checked for self-update", "0.0.0", is_hidden: true);
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
