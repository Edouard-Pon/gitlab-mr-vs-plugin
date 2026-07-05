using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace GitLabMr.Vsix
{
    public class ToolSettings
    {
        public string GitLabBaseUrl { get; set; } = string.Empty;
        public bool IgnoreTlsErrors { get; set; }
    }

    /// <summary>
    /// Persists settings under %LOCALAPPDATA%\GitLabMr.
    /// - settings.json : base URL + flags (not secret)
    /// - token.bin     : the PAT, DPAPI-encrypted with CurrentUser scope
    ///   (only the same Windows user on the same machine can decrypt it).
    /// </summary>
    public static class SettingsStore
    {
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitLabMr");
        private static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
        private static readonly string TokenPath = Path.Combine(Dir, "token.bin");

        // Not a secret, just binds the ciphertext to this app.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GitLabMr.Vsix.v1");

        public static ToolSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return JsonConvert.DeserializeObject<ToolSettings>(File.ReadAllText(SettingsPath)) ?? new ToolSettings();
            }
            catch { /* corrupted file -> start fresh */ }
            return new ToolSettings();
        }

        public static void Save(ToolSettings settings)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }

        public static void SaveToken(string token)
        {
            Directory.CreateDirectory(Dir);
            if (string.IsNullOrEmpty(token))
            {
                if (File.Exists(TokenPath)) File.Delete(TokenPath);
                return;
            }
            byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenPath, cipher);
        }

        /// <summary>Returns null when no token is stored (or it can't be decrypted).</summary>
        public static string LoadToken()
        {
            try
            {
                if (!File.Exists(TokenPath)) return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }
    }
}
