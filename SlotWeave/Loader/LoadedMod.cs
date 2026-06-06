using System.Security.Cryptography;

namespace SlotWeave;

internal class LoadedMod {
    public required ModManifest Manifest;
    public required string Directory;
    public IMod? AssemblyMod;
    public string? AssemblyPath;
    public string? PackPath;

    private string? contentHash;

    /// <summary>
    /// SHA256 fingerprint of this mod's files (assembly + pack + manifest).
    /// Used in cache-key computation to detect mod file changes even when the
    /// developer forgets to bump the manifest version.
    /// </summary>
    public string ContentHash
    {
        get
        {
            if (this.contentHash != null) return this.contentHash;

            using var sha = SHA256.Create();
            var files = new List<string>();
            if (this.AssemblyPath != null && File.Exists(this.AssemblyPath))
                files.Add(this.AssemblyPath);
            if (this.PackPath != null && File.Exists(this.PackPath))
                files.Add(this.PackPath);
            var manifestPath = Path.Combine(this.Directory, "manifest.json");
            if (File.Exists(manifestPath))
                files.Add(manifestPath);

            foreach (var file in files.OrderBy(f => f))
            {
                var bytes = File.ReadAllBytes(file);
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            this.contentHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            return this.contentHash;
        }
    }
}
