using System.Reflection;
using Serilog;

namespace SlotWeave.Scripting;

/// <summary>
/// Collects [Patch] classes and applies them to ScriptInfo objects.
/// Processes operations in reverse line order to keep line numbers stable
/// across insertions/deletions.
/// </summary>
internal class PatchManager
{
    private readonly ILogger logger = SlotWeave.Logger.ForContext("SourceContext", "PatchManager");
    private readonly List<PatchEntry> patches = [];

    /// <summary>Scan a mod assembly for [Patch] classes and register them.</summary>
    public void ScanAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            var attrs = type.GetCustomAttributes<PatchAttribute>().ToList();
            if (attrs.Count == 0) continue;

            var prefixMethods = GetMethods<PrefixAttribute>(type);
            var postfixMethods = GetMethods<PostfixAttribute>(type);
            var replaceMethods = GetMethods<ReplaceAttribute>(type);

            if (prefixMethods.Count == 0 && postfixMethods.Count == 0 && replaceMethods.Count == 0)
            {
                logger.Warning("[Patch] class {Type} has no [Prefix]/[Postfix]/[Replace] methods", type.Name);
                continue;
            }

            foreach (var attr in attrs)
            {
                var entry = new PatchEntry(attr.Path, attr.Function, type.Name,
                    prefixMethods, postfixMethods, replaceMethods);
                this.patches.Add(entry);
                logger.Debug("Registered [Patch] {Path}::{Func} from {Type}",
                    attr.Path, attr.Function, type.Name);
            }
        }
    }

    private static List<MethodInfo> GetMethods<TAttr>(Type type) where TAttr : Attribute =>
        type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetCustomAttribute<TAttr>() != null)
            .ToList();

    /// <summary>Apply all matching patches. Returns modified source or null.</summary>
    public string? Apply(string path, string source)
    {
        var matching = this.patches.Where(p => p.Matches(path)).ToList();
        if (matching.Count == 0) return null;

        var script = ScriptInfo.Parse(path, source);
        var operations = new List<PatchOp>();
        var matchedEntries = new HashSet<string>();

        foreach (var entry in matching)
        {
            foreach (var func in script.Functions)
            {
                if (func.Name != entry.Function) continue;
                matchedEntries.Add($"{entry.SourceType}::{entry.Function}");

                // Collect operations with line numbers from original script
                if (entry.ReplaceMethods.Count > 0)
                {
                    var originalBody = string.Join("\n", func.GetBodyLines(script.Lines));
                    var replacement = InvokeCodeMethods(entry.ReplaceMethods, originalBody);
                    if (replacement != null)
                    {
                        var prov = $"{entry.SourceType}.{entry.ReplaceMethods[0].Name}";
                        operations.Add(new PatchOp(func.StartLine, func.EndLine, OpType.Replace, replacement, prov));
                    }
                }

                if (entry.PrefixMethods.Count > 0)
                {
                    var code = InvokeCodeMethods(entry.PrefixMethods, null);
                    if (code != null)
                    {
                        var prov = $"{entry.SourceType}.{entry.PrefixMethods[0].Name}";
                        operations.Add(new PatchOp(func.StartLine, func.StartLine, OpType.Prefix, code, prov));
                    }
                }

                if (entry.PostfixMethods.Count > 0)
                {
                    var code = InvokeCodeMethods(entry.PostfixMethods, null);
                    if (code != null)
                    {
                        var prov = $"{entry.SourceType}.{entry.PostfixMethods[0].Name}";
                        operations.Add(new PatchOp(func.EndLine, func.EndLine, OpType.Postfix, code, prov));
                    }
                }
            }
        }

        // Warn about patches that matched the path but can't find their target function
        foreach (var entry in matching)
        {
            var key = $"{entry.SourceType}::{entry.Function}";
            if (!matchedEntries.Contains(key))
            {
                var available = script.Functions.Select(f => f.Name).ToList();
                logger.Warning(
                    "[Patch] {Source} targets function '{Func}' which does not exist in {Path}. " +
                    "Available functions: {Available}",
                    entry.SourceType, entry.Function, path,
                    available.Count > 0 ? string.Join(", ", available) : "(none)");
            }
        }

        if (operations.Count == 0) return null;

        // ── Conflict detection ──
        // Multiple [Replace] targeting the same function: both want to own the body,
        // and since all patches see the original source, the later one silently wins.
        // This is almost certainly a mistake — warn the developer.
        var replaceConflicts = operations
            .Where(o => o.Type == OpType.Replace)
            .GroupBy(o => o.TargetLine)
            .Where(g => g.Count() > 1);
        foreach (var group in replaceConflicts) {
            var names = group.Select(o => o.Provenance).ToList();
            logger.Warning(
                "Replace conflict in {Path}: multiple patches target the same function: {Patches}",
                path, string.Join(", ", names));
            logger.Warning(
                "All [Patch] classes see the original source — they cannot compose on each other's output. " +
                "Use ISourceMod with manifest Dependencies for sequential transforms.");
        }

        // Apply in reverse line order so earlier line numbers stay valid
        operations.Sort((a, b) => b.TargetLine.CompareTo(a.TargetLine));

        foreach (var op in operations)
        {
            switch (op.Type)
            {
                case OpType.Replace:
                    ReplaceFunctionBody(script, op.TargetLine, op.EndLine, op.Code, op.Provenance);
                    break;
                case OpType.Prefix:
                    InsertAfterLine(script, op.TargetLine, op.Code, op.Provenance);
                    break;
                case OpType.Postfix:
                    InsertAfterLine(script, op.EndLine, op.Code, op.Provenance);
                    break;
            }
        }

        var result = script.Emit();
        if (result == source) return null;

        // Set provenance for caller to read
        this.LastProvenance = script.Provenance;

        // PatchDump: log original vs patched when debug is enabled
        SlotWeave.Logger.ForContext("SourceContext", "PatchDump")
            .Debug("===== {Path} ({Ops} ops) =====", path, operations.Count);
        SlotWeave.Logger.ForContext("SourceContext", "PatchDump")
            .Debug("Original {OrigLen} -> Patched {NewLen} chars", source.Length, result.Length);

        return result;
    }

    private static string? InvokeCodeMethods(List<MethodInfo> methods, string? input)
    {
        string? result = input;
        foreach (var method in methods)
        {
            try
            {
                object?[]? args = method.GetParameters().Length > 0 ? [result] : null;
                var ret = method.Invoke(null, args);
                result = ret as string ?? result;
            }
            catch (Exception e)
            {
                SlotWeave.Logger.ForContext("SourceContext", "PatchManager")
                    .Error(e, "Patch method {Method} threw — this patch is skipped", method.Name);
            }
        }
        return result;
    }

    // Body removal + replacement (startLine = func declaration, endLine = body end)
    private static void ReplaceFunctionBody(ScriptInfo script, int funcLine, int bodyEnd, string newBody, string provenance)
    {
        var bodyIndent = DetectBodyIndent(script, funcLine);

        // Remove old body lines between funcLine+1 and bodyEnd
        var removeCount = bodyEnd - funcLine;
        for (var i = 0; i < removeCount && funcLine + 1 < script.Lines.Count; i++)
        {
            script.Lines.RemoveAt(funcLine + 1);
            script.Provenance.RemoveAt(funcLine + 1);
        }

        // Insert new body lines
        var bodyLines = newBody.Replace("\r\n", "\n").Split('\n');

        for (var i = bodyLines.Length - 1; i >= 0; i--)
        {
            var content = bodyLines[i];
            if (string.IsNullOrWhiteSpace(content))
            {
                script.Lines.Insert(funcLine + 1, "");
                script.Provenance.Insert(funcLine + 1, provenance);
            }
            else
            {
                script.Lines.Insert(funcLine + 1, bodyIndent + StripBaseIndent(content, bodyIndent));
                script.Provenance.Insert(funcLine + 1, provenance);
            }
        }
    }

    // Insert code block after targetLine
    private static void InsertAfterLine(ScriptInfo script, int line, string code, string provenance)
    {
        if (line + 1 > script.Lines.Count) {
            SlotWeave.Logger.ForContext("SourceContext", "PatchManager")
                .Warning("Patch target line {Line} is out of range ({Total} lines) — patch skipped", line, script.Lines.Count);
            return;
        }

        var bodyIndent = DetectBodyIndent(script, line);
        var codeLines = code.Replace("\r\n", "\n").Split('\n');

        for (var i = codeLines.Length - 1; i >= 0; i--)
        {
            var content = codeLines[i];
            if (string.IsNullOrWhiteSpace(content))
            {
                script.Lines.Insert(line + 1, "");
                script.Provenance.Insert(line + 1, provenance);
            }
            else
            {
                script.Lines.Insert(line + 1, bodyIndent + StripBaseIndent(content, bodyIndent));
                script.Provenance.Insert(line + 1, provenance);
            }
        }
    }

    /// <summary>
    /// Strip only the base indent prefix from a line, preserving nested indentation.
    /// e.g. if baseIndent="\t" and line="\t\tdo_something()", returns "\tdo_something()".
    /// </summary>
    private static string StripBaseIndent(string line, string baseIndent)
    {
        if (baseIndent.Length == 0) return line;
        if (line.StartsWith(baseIndent)) return line[baseIndent.Length..];
        return line.TrimStart(); // fallback: line doesn't match expected indent
    }

    /// <summary>
    /// Detect the indentation of the function body by looking at the first
    /// non-empty line after the function declaration. Falls back to declaration
    /// indent + "\t" if no body line is found.
    /// </summary>
    private static string DetectBodyIndent(ScriptInfo script, int funcDeclLine)
    {
        for (var i = funcDeclLine + 1; i < script.Lines.Count; i++)
        {
            var l = script.Lines[i];
            var t = l.TrimStart();
            if (string.IsNullOrEmpty(t) || t.StartsWith('#'))
                continue;
            return l[..(l.Length - t.Length)];
        }
        // Fallback: declaration indent + one tab
        return DetectIndent(script.Lines[funcDeclLine]) + "\t";
    }

    private static string DetectIndent(string line)
    {
        var trimmed = line.TrimStart();
        return line[..(line.Length - trimmed.Length)];
    }

    public int Count => this.patches.Count;

    /// <summary>Line-level provenance from the last Apply() call (null = original).</summary>
    public List<string?>? LastProvenance { get; private set; }

    private enum OpType { Prefix, Postfix, Replace }

    private record struct PatchOp(int TargetLine, int EndLine, OpType Type, string Code, string Provenance);

    private class PatchEntry
    {
        public string Path { get; }
        public string Function { get; }
        public string SourceType { get; }
        public List<MethodInfo> PrefixMethods { get; }
        public List<MethodInfo> PostfixMethods { get; }
        public List<MethodInfo> ReplaceMethods { get; }

        public PatchEntry(string path, string function, string sourceType,
            List<MethodInfo> prefix, List<MethodInfo> postfix, List<MethodInfo> replace)
        {
            this.Path = path;
            this.Function = function;
            this.SourceType = sourceType;
            this.PrefixMethods = prefix;
            this.PostfixMethods = postfix;
            this.ReplaceMethods = replace;
        }

        public bool Matches(string scriptPath) =>
            this.Path == "*" || scriptPath == this.Path || scriptPath.EndsWith(this.Path) ||
            (this.Path.Contains('*') && WildcardMatch(this.Path, scriptPath));

        private static bool WildcardMatch(string pattern, string input)
        {
            // Simple glob: * matches any sequence of characters
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(input, regex);
        }
    }
}
