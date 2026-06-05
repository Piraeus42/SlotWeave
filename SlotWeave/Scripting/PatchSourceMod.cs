using SlotWeave.Modding;

namespace SlotWeave.Scripting;

/// <summary>
/// Adapter that bridges the PatchManager to the ISourceMod pipeline.
/// Runs first in the chain so [Prefix]/[Postfix]/[Replace] patches apply
/// before any raw ISourceMod modifications.
/// </summary>
internal class PatchSourceMod(PatchManager patches) : ISourceMod
{
    public bool ShouldRun(string path) => true;

    public string Modify(string path, string source)
    {
        return patches.Apply(path, source) ?? source;
    }

    /// <summary>Fine-grained provenance from the last Modify() call.</summary>
    public List<string?>? LastProvenance => patches.LastProvenance;
}
