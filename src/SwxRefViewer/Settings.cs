using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwxRefViewer
{
    // User settings in %APPDATA%\SwxRefViewer\settings.ini (key=value).
    public class Settings
    {
        public string LicenseKey = "";
        public bool ForceRawScan;
        public List<string> SearchFolders = new List<string>();
        public string LastFolder = "";

        static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwxRefViewer", "settings.ini");

        // The license key can also come from the environment variable SWX_DM_LICENSE_KEY.
        public string EffectiveLicenseKey =>
            string.IsNullOrWhiteSpace(LicenseKey) ? Environment.GetEnvironmentVariable("SWX_DM_LICENSE_KEY") ?? "" : LicenseKey;

        public static Settings Load()
        {
            var settings = new Settings();
            if (!File.Exists(FilePath)) return settings;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var key = line.Substring(0, i).Trim();
                var value = line.Substring(i + 1).Trim();
                if (key == "LicenseKey") settings.LicenseKey = value;
                else if (key == "ForceRawScan") settings.ForceRawScan = value == "true";
                else if (key == "SearchFolder") settings.SearchFolders.Add(value);
                else if (key == "LastFolder") settings.LastFolder = value;
            }
            return settings;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var lines = new List<string>
            {
                "LicenseKey=" + LicenseKey,
                "ForceRawScan=" + (ForceRawScan ? "true" : "false"),
                "LastFolder=" + LastFolder
            };
            lines.AddRange(SearchFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => "SearchFolder=" + f.Trim()));
            File.WriteAllLines(FilePath, lines);
        }
    }
}
