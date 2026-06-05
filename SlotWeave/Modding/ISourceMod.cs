namespace SlotWeave.Modding;

public interface ISourceMod {
    bool ShouldRun(string path);
    string Modify(string path, string source);

    /// <summary>
    /// Idempotent guard token. If the source already contains this string,
    /// the modder skips this mod to prevent double-injection.
    /// Return null (default) to disable auto-guarding.
    /// </summary>
    /// <example>
    /// <code>
    /// public string? Sentinel => "func _my_rng_helper";
    /// </code>
    /// </example>
    string? Sentinel => null;

    /// <summary>
    /// Execution priority. Higher values run earlier in the pipeline.
    /// Use this when your mod needs to run before or after specific other mods.
    /// Default is 0. [Patch] always runs first regardless of priority.
    /// </summary>
    int Priority => 0;
}
