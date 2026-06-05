using Serilog;

namespace SlotWeave.Modding;

public class SourceModder(List<ISourceMod>? mods = null) {
    private static readonly ILogger Logger = SlotWeave.Logger.ForContext("SourceContext", "SourceModder");

    /// <summary>Line-level provenance from the last Run() call (null = original).</summary>
    public List<string?>? Provenance { get; private set; }

    public string? Run(string path, string source) {
        if (mods == null) return null;

        var modified = source;
        var ran = false;
        List<string?>? provenance = null;

        foreach (var mod in mods) {
            if (!mod.ShouldRun(path)) continue;

            // Idempotent guard: skip if sentinel already present in source
            if (mod.Sentinel is { } sentinel && modified.Contains(sentinel)) {
                Logger.Information("Skipping {Mod} for {Path}: sentinel already present", mod.GetType().Name, path);
                continue;
            }

            ran = true;
            var before = modified;
            modified = mod.Modify(path, modified);

            // Soft validation: warn on intermediate syntax errors (may be intentional)
            if (modified != before) {
                var tok = new GdTokenizer(modified);
                if (!tok.Validate()) {
                    Logger.Warning("{Mod} produced potentially invalid intermediate GDScript at line {Line}: {Error}",
                        mod.GetType().Name, tok.ErrorLine, tok.LastError);
                    Logger.Verbose("Intermediate source around error:\n{Snippet}",
                        ExtractSnippet(modified, tok.ErrorLine, contextLines: 2));
                }
            }

            // Use fine-grained provenance from PatchSourceMod when available
            if (mod is Scripting.PatchSourceMod patchMod && patchMod.LastProvenance is { } patchProv) {
                provenance = patchProv;
            } else {
                // Generic diff: match output lines back to input, carry forward provenance
                var modName = mod.GetType().Name;
                provenance = MergeProvenance(before, provenance, modified, modName);
            }
        }

        this.Provenance = provenance;
        return ran ? modified : null;
    }

    /// <summary>
    /// Diff before→after strings. Lines that match a line in "before"
    /// carry forward its provenance; unmatched lines get newModName.
    /// </summary>
    private static List<string?> MergeProvenance(
        string before, List<string?>? beforeProv, string after, string newModName)
    {
        var beforeLines = before.Replace("\r\n", "\n").Split('\n');
        var afterLines = after.Replace("\r\n", "\n").Split('\n');

        // Build reverse stack per unique line value for stable duplicate matching
        var pool = new Dictionary<string, Stack<string?>>();
        for (var i = beforeLines.Length - 1; i >= 0; i--) {
            if (!pool.ContainsKey(beforeLines[i]))
                pool[beforeLines[i]] = new Stack<string?>();
            var prov = beforeProv is not null && i < beforeProv.Count ? beforeProv[i] : null;
            pool[beforeLines[i]].Push(prov);
        }

        var result = new List<string?>(afterLines.Length);
        foreach (var line in afterLines) {
            if (pool.TryGetValue(line, out var stack) && stack.Count > 0) {
                result.Add(stack.Pop());
            } else {
                result.Add(newModName);
            }
        }

        return result;
    }

    /// <summary>
    /// Extract a few lines around an error line for diagnostic logging.
    /// </summary>
    private static string ExtractSnippet(string source, int errorLine, int contextLines) {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var start = Math.Max(0, errorLine - 1 - contextLines);
        var end = Math.Min(lines.Length, errorLine + contextLines);
        var sb = new System.Text.StringBuilder();
        for (var i = start; i < end; i++) {
            var marker = i == errorLine - 1 ? ">>>" : "   ";
            sb.AppendLine($"{marker} L{i + 1}: {lines[i]}");
        }
        return sb.ToString();
    }
}
