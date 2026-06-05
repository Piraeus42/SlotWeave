using System.Reflection;

namespace SlotWeave.Scripting;

/// <summary>
/// Reads GDScript from embedded .gd resources. Eliminates C# string escaping.
///
/// Setup:
///   1. Add .gd files to your mod project (e.g. Patches/gd/my_patch.gd)
///   2. In .csproj: &lt;EmbeddedResource Include="Patches/gd/*.gd" /&gt;
///   3. Call: EmbeddedGd.Read(typeof(MyPatch), "gd.my_patch.gd")
/// </summary>
public static class EmbeddedGd
{
    /// <summary>
    /// Read an embedded .gd file.
    /// </summary>
    /// <param name="callerType">The calling patch type, used to resolve the assembly and namespace prefix.</param>
    /// <param name="relativePath">
    /// Path relative to the caller's namespace, with dots as separators.
    /// Example: "gd.coins_replace.gd" resolves to "MyMod.Patches.gd.coins_replace.gd"
    /// </param>
    public static string Read(Type callerType, string relativePath)
    {
        var assembly = callerType.Assembly;
        var prefixed = $"{callerType.Namespace}.{relativePath}";

        var stream = assembly.GetManifestResourceStream(prefixed)
                  ?? assembly.GetManifestResourceStream(relativePath);

        if (stream == null)
        {
            var available = string.Join("\n  ", assembly.GetManifestResourceNames());
            throw new FileNotFoundException(
                $"[SlotWeave] Embedded GDScript not found.\n" +
                $"  Tried: {prefixed}\n" +
                $"  Tried: {relativePath}\n" +
                $"  Available resources in {assembly.GetName().Name}:\n  {available}");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
