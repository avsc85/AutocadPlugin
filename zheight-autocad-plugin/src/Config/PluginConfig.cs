// PluginConfig.cs
// Loads API key and base URL from encrypted local config.
// API key is NEVER stored in plain text or read from environment variables.
// Uses Windows DPAPI — decryptable only by the same Windows user account.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace zHeight.Plugin.Config
{
    public class PluginConfig
    {
        public string ApiBaseUrl  { get; set; } = "";
        public string ApiKey      { get; set; } = "";
        public string ArchitectId { get; set; } = "";

        private static readonly string ConfigDir =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "zHeightPlugin");

        private static readonly string ConfigPath =
            Path.Combine(ConfigDir, "config.dat");

        public static PluginConfig Load()
        {
            if (!File.Exists(ConfigPath))
                return new PluginConfig
                {
                    ApiBaseUrl = "https://rag-api-583598998751.us-central1.run.app",
                    ApiKey     = "REPLACE_WITH_ACTUAL_KEY",
                };

            try
            {
                var encrypted = File.ReadAllBytes(ConfigPath);
                var decrypted = ProtectedData.Unprotect(
                    encrypted, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(decrypted);
                return JsonConvert.DeserializeObject<PluginConfig>(json)
                       ?? new PluginConfig();
            }
            catch
            {
                return new PluginConfig();
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDir);
            var json      = JsonConvert.SerializeObject(this);
            var bytes     = Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(
                bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(ConfigPath, encrypted);
        }

        /// <summary>
        /// Call once from the ZHEIGHT_SETUP command to store credentials securely.
        /// </summary>
        public static void Configure(string apiUrl, string apiKey, string architectId = "")
        {
            new PluginConfig
            {
                ApiBaseUrl  = apiUrl,
                ApiKey      = apiKey,
                ArchitectId = architectId,
            }.Save();
        }
    }
}
