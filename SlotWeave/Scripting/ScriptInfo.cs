namespace SlotWeave.Scripting;

/// <summary>
/// GDScript structure parser. Pass 1: character-level state machine classifies
/// lines and finds function declarations. Pass 2: resolves function body boundaries
/// via indentation. Handles strings, multi-line strings, comments, default params,
/// and multi-line signatures.
/// </summary>
public class ScriptInfo
{
    public string Path { get; init; } = "";
    public string Source { get; init; } = "";
    public List<FunctionInfo> Functions { get; } = [];
    public List<VariableInfo> Variables { get; } = [];
    public string? Extends { get; private set; }
    public string? ClassName { get; private set; }

    internal List<string> Lines { get; } = [];
    internal List<string?> Provenance { get; } = []; // null=original, "Type.Method"=patched

    /// <summary>Parse a GDScript source string into structured info.</summary>
    public static ScriptInfo Parse(string path, string source)
    {
        var info = new ScriptInfo { Path = path, Source = source };
        var raw = source.Replace("\r\n", "\n");
        info.Lines.AddRange(raw.Split('\n'));
        // Initialize provenance: all lines start as "original" (null)
        for (var i = 0; i < info.Lines.Count; i++)
            info.Provenance.Add(null);

        // ── Pass 1: classify lines, find declarations ──
        var lineKinds = new LineKind[info.Lines.Count];
        var inMultiString = false;
        var multiDelim = "";

        for (var i = 0; i < info.Lines.Count; i++)
        {
            var line = info.Lines[i];

            // Continue multi-line string
            if (inMultiString)
            {
                var end = line.IndexOf(multiDelim, StringComparison.Ordinal);
                if (end >= 0)
                {
                    inMultiString = false;
                    line = line[(end + multiDelim.Length)..];
                }
                else
                {
                    lineKinds[i] = LineKind.String;
                    continue;
                }
            }

            // Scan line character by character
            var col = 0;
            var kind = LineKind.Code;
            var inString = false;
            var inComment = false;

            while (col < line.Length)
            {
                var c = line[col];

                if (inComment) break; // rest of line is comment

                if (inString)
                {
                    if (c == '"') inString = false;
                    else if (c == '\\') col++; // skip escaped
                    col++;
                    continue;
                }

                if (c == '#') { inComment = true; break; }

                if (c == '"')
                {
                    if (col + 2 < line.Length && line[col + 1] == '"' && line[col + 2] == '"')
                    {
                        // Multi-line string
                        multiDelim = "\"\"\"";
                        var close = line.IndexOf(multiDelim, col + 3, StringComparison.Ordinal);
                        if (close >= 0)
                        {
                            // Single-line multi-string: "..."..."..."
                            col = close + 3;
                            continue;
                        }
                        else
                        {
                            inMultiString = true;
                            break;
                        }
                    }
                    else
                    {
                        inString = true;
                        col++;
                        continue;
                    }
                }

                col++;
            }

            if (inMultiString) { lineKinds[i] = LineKind.String; continue; }
            if (inComment) kind = LineKind.Comment;

            var trimmed = line.TrimStart();
            if (string.IsNullOrEmpty(trimmed)) kind = LineKind.Blank;

            lineKinds[i] = kind;

            // Skip non-code lines for declaration scanning
            if (kind != LineKind.Code) continue;

            var indent = line.Length - trimmed.Length;

            // extends / class_name / var (top-level only, before first function)
            if (info.Functions.Count == 0)
            {
                if (trimmed.StartsWith("extends "))
                {
                    info.Extends = trimmed[8..].Trim();
                    continue;
                }
                if (trimmed.StartsWith("class_name "))
                {
                    var rest = trimmed[11..].Trim();
                    var sp = rest.IndexOf(' ');
                    info.ClassName = sp > 0 ? rest[..sp] : rest;
                    continue;
                }
                if (trimmed.StartsWith("var ") && indent == 0)
                {
                    var rest = trimmed[4..].Trim();
                    var eq = rest.IndexOf('=');
                    info.Variables.Add(new VariableInfo
                    {
                        Name = eq > 0 ? rest[..eq].Trim() : rest,
                        Line = i
                    });
                    continue;
                }
            }

            // Function declaration: "func Name(" or "static func Name("
            var funcStart = trimmed.StartsWith("static func ") ? 12 : 0;
            if (funcStart == 0 && trimmed.StartsWith("func "))
                funcStart = 5;

            if (funcStart > 0)
            {
                var func = ParseFunctionSignature(info.Lines, i, indent, trimmed, funcStart);
                if (func != null)
                {
                    info.Functions.Add(func);
                }
            }
        }

        // ── Pass 2: resolve function body boundaries ──
        for (var fi = 0; fi < info.Functions.Count; fi++)
        {
            var func = info.Functions[fi];
            var funcIndent = func.Indent;
            var bodyEnd = info.Lines.Count - 1;

            // Next function's start (if any) is the upper bound
            if (fi + 1 < info.Functions.Count)
                bodyEnd = info.Functions[fi + 1].StartLine - 1;

            // Tighten: find last indented body line
            for (var i = func.StartLine + 1; i <= bodyEnd; i++)
            {
                var l = info.Lines[i];
                var t = l.TrimStart();
                if (string.IsNullOrEmpty(t) || t.StartsWith('#'))
                    continue;
                var ind = l.Length - t.Length;
                if (ind <= funcIndent)
                {
                    func.EndLine = i - 1;
                    break;
                }
            }
            if (func.EndLine < 0)
                func.EndLine = bodyEnd;
        }

        return info;
    }

    private static FunctionInfo? ParseFunctionSignature(
        List<string> lines, int lineIdx, int indent, string trimmed, int funcStart)
    {
        // Collect signature across multiple lines (handle multi-line params)
        var sig = trimmed[funcStart..];
        var sigLine = lineIdx;

        // Check if signature spans multiple lines
        while (!HasClosingColon(sig) && sigLine + 1 < lines.Count)
        {
            sigLine++;
            sig += " " + lines[sigLine].Trim();
        }

        if (!HasClosingColon(sig)) return null; // malformed

        // Split at the final ':'
        var colonIdx = FindSignatureColon(sig);
        var decl = sig[..colonIdx]; // "name(arg1, arg2)"
        var parenIdx = decl.IndexOf('(');

        var name = parenIdx > 0 ? decl[..parenIdx].Trim() : decl.Trim();
        var args = "";
        if (parenIdx > 0)
        {
            var closeParen = FindMatchingParen(decl, parenIdx);
            if (closeParen > parenIdx)
                args = decl[(parenIdx + 1)..closeParen];
        }

        return new FunctionInfo
        {
            Name = name,
            Parameters = args.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => CleanParam(p))
                .Where(p => p.Length > 0)
                .ToList(),
            StartLine = lineIdx,
            Indent = indent,
            EndLine = -1
        };
    }

    /// <summary>Rebuild source text from internal line buffer.</summary>
    public string Emit() => string.Join("\n", this.Lines);

    // ── Helpers ──

    private static bool HasClosingColon(string sig)
    {
        var depth = 0;
        for (var i = 0; i < sig.Length; i++)
        {
            var c = sig[i];
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            else if (c == ':' && depth == 0) return true;
        }
        return false;
    }

    private static int FindSignatureColon(string sig)
    {
        var depth = 0;
        for (var i = 0; i < sig.Length; i++)
        {
            var c = sig[i];
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            else if (c == ':' && depth == 0) return i;
        }
        return sig.Length - 1;
    }

    private static int FindMatchingParen(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            if (s[i] == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private static string CleanParam(string p)
    {
        p = p.Trim();
        // Strip type hints: "a: int" → "a", "b = 5" → "b"
        // Find ':' not inside quotes/parens
        var depth = 0;
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == '(') depth++;
            if (p[i] == ')') depth--;
            if (p[i] == ':' && depth == 0)
            {
                p = p[..i].Trim();
                break;
            }
        }
        // Strip default value: "b = 5" → "b"
        var eq = p.IndexOf('=');
        if (eq > 0 && depth == 0) p = p[..eq].Trim();
        return p;
    }

    private enum LineKind { Code, Comment, Blank, String }
}

public class FunctionInfo
{
    public string Name { get; init; } = "";
    public List<string> Parameters { get; init; } = [];
    public int StartLine { get; set; }
    public int EndLine { get; set; } = -1;
    internal int Indent;

    public IEnumerable<string> GetBodyLines(List<string> sourceLines)
    {
        for (var i = StartLine + 1; i <= EndLine && i < sourceLines.Count; i++)
            yield return sourceLines[i];
    }
}

public class VariableInfo
{
    public string Name { get; init; } = "";
    public int Line { get; init; }
}
