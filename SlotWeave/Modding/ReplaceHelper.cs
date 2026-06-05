using System.Text.RegularExpressions;

namespace SlotWeave.Modding;

/// <summary>
/// Safe string-replacement helpers for ISourceMod implementations.
/// Uses regex word-boundary matching to prevent substring collisions
/// where a replacement output is re-matched by a later rule.
///
/// Example of the problem this solves:
/// <code>
///   source = source.Replace("rand_range(", "_my_rand_range(");
///   // "_my_rand_range(" contains "rand_range(" as a substring, so a
///   // subsequent generic .Replace("rand_range(", ...) would corrupt it.
/// </code>
/// </summary>
public static class ReplaceHelper
{
    /// <summary>
    /// Safely rename a GDScript function call using \b word-boundary matching.
    /// The underscore (_) is a word character, so <c>\brand_range\(</c> will NOT
    /// match inside identifiers like <c>_my_rand_range(</c> or <c>_scr_rand_range(</c>.
    /// </summary>
    /// <param name="source">GDScript source text</param>
    /// <param name="oldFunc">The function name to replace (e.g. "rand_range")</param>
    /// <param name="newFunc">The replacement function name (e.g. "_my_rng_range")</param>
    /// <returns>Transformed source</returns>
    public static string ReplaceCall(string source, string oldFunc, string newFunc)
    {
        // \b = word boundary.  Since '_' is \w, there is no boundary between
        // '_' and 'r' in "_scr_rand_range(", so the substring "rand_range(" is
        // never matched inside a longer identifier.
        return Regex.Replace(
            source,
            $@"\b{Regex.Escape(oldFunc)}\(",
            $"{newFunc}("
        );
    }

    /// <summary>
    /// Append a block of code at the end of a GDScript source file,
    /// ensuring a clean newline separation.
    /// </summary>
    public static string AppendCode(string source, string code)
    {
        if (!source.EndsWith('\n'))
            source += '\n';
        return source + '\n' + code;
    }
}
