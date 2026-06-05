using System.Text.Json;
using SlotWeave.GameState;
using SlotWeave.Modding;
using SlotWeave.Scripting;
using Serilog;

namespace SlotWeave;

internal class ModLoader {
    public List<LoadedMod> LoadedMods = new();
    public List<ISourceMod> SourceMods => this.sourceMods.Values.SelectMany(x => x).ToList();

    private readonly ILogger logger = SlotWeave.Logger.ForContext<ModLoader>();
    private readonly Dictionary<string, List<ISourceMod>> sourceMods = new();
    private readonly PatchManager patchManager;
    private readonly GameStateBus? gameStateBus;

    /// <summary>Active mods as (Id, Version) tuples for cache key computation.</summary>
    public List<(string Id, string Version)> ActiveModVersions =>
        this.LoadedMods.Select(m => (m.Manifest.Id, m.Manifest.Metadata?.Version ?? "0.0.0")).ToList();

    public ModLoader(PatchManager patchManager, GameStateBus? gameStateBus = null) {
        this.patchManager = patchManager;
        this.gameStateBus = gameStateBus;
        EventBus.Publish(new ModEvents.LoaderPhase("Starting"));

        this.Register();
        this.Sort();
        this.LoadAssemblies();
        this.InitializeMods();

        this.logger.Information("Loaded {Count} mods: {ModIds}", this.LoadedMods.Count,
            this.LoadedMods.Select(x => x.Manifest.Id));
        EventBus.Publish(new ModEvents.LoaderPhase("Ready"));
    }

    private void Register() {
        var modsDir = Path.Combine(SlotWeave.SlotWeaveDir, "mods");
        if (!Directory.Exists(modsDir)) return;

        foreach (var modDir in Directory.GetDirectories(modsDir)) {
            try {
                var manifestPath = Path.Combine(modDir, "manifest.json");
                if (!File.Exists(manifestPath)) {
                    this.logger.Warning("Mod at {ModDir} does not have a manifest.json", modDir);
                    continue;
                }

                var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath))!;
                if (this.LoadedMods.Any(x => x.Manifest.Id == manifest.Id)) {
                    this.logger.Warning("Duplicate mod ID: {ModId}", manifest.Id);
                    continue;
                }

                this.logger.Debug("Loading mod {ModId} from {ModDir}", manifest.Id, modDir);

                var loadedMod = new LoadedMod {
                    Manifest = manifest,
                    Directory = modDir,
                    AssemblyMod = null,
                    AssemblyPath = manifest.AssemblyPath is { } assemblyPath
                                       ? Path.Combine(modDir, assemblyPath)
                                       : null,
                    PackPath = manifest.PackPath is { } packPath
                                   ? Path.Combine(modDir, packPath)
                                   : null
                };

                if (loadedMod.AssemblyPath != null && !File.Exists(loadedMod.AssemblyPath)) {
                    this.logger.Warning("Assembly at {AssemblyPath} does not exist", loadedMod.AssemblyPath);
                    continue;
                }

                if (loadedMod.PackPath != null && !File.Exists(loadedMod.PackPath)) {
                    this.logger.Warning("Pack file at {PackPath} does not exist", loadedMod.PackPath);
                    continue;
                }

                this.LoadedMods.Add(loadedMod);
            } catch (Exception e) {
                this.logger.Error(e, "Failed to load mod at {ModDir}", modDir);
            }
        }
    }

    private void Sort() {
        while (true) {
            var invalidMods = this.LoadedMods
                .Where(x => x.Manifest.Dependencies.Any(d => !this.LoadedMods.Any(m => m.Manifest.Id == d))).ToList();
            if (invalidMods.Count == 0) break;

            foreach (var invalidMod in invalidMods) {
                this.logger.Error("Mod {ModId} has missing/invalid dependencies: {InvalidDependencies}",
                    invalidMod.Manifest.Id, invalidMod.Manifest.Dependencies);
                this.LoadedMods.Remove(invalidMod);
            }
        }

        var dependencyGraph = this.LoadedMods.ToDictionary(x => x.Manifest.Id, x => x.Manifest.Dependencies);
        var resolvedOrder = new List<string>();
        while (dependencyGraph.Count > 0) {
            var noDependencies = dependencyGraph.Where(x => x.Value.Count == 0).ToList();
            foreach (var (modId, _) in noDependencies) {
                resolvedOrder.Add(modId);
                dependencyGraph.Remove(modId);
            }

            foreach (var (_, dependencies) in dependencyGraph) {
                foreach (var noDependency in noDependencies) {
                    dependencies.Remove(noDependency.Key);
                }
            }

            if (noDependencies.Count == 0) {
                this.logger.Error("Circular dependency detected: {CircularDependency}", dependencyGraph.Keys);
                break;
            }
        }

        this.logger.Debug("Resolved mod load order: {ResolvedOrder}", resolvedOrder);
        this.LoadedMods = this.LoadedMods.OrderBy(x => resolvedOrder.IndexOf(x.Manifest.Id)).ToList();
    }

    private void LoadAssemblies() {
        var invalidMods = new List<LoadedMod>();

        foreach (var loadedMod in this.LoadedMods) {
            if (loadedMod.AssemblyPath is not { } assemblyPath) continue;

            try {
                this.logger.Debug("Loading assembly for mod {ModId} from {AssemblyPath}", loadedMod.Manifest.Id,
                    assemblyPath);
                var assemblyMod = this.LoadAssembly(loadedMod.Manifest.Id, assemblyPath);
                loadedMod.AssemblyMod = assemblyMod;
            } catch (Exception e) {
                this.logger.Error(e, "Failed to load assembly for mod {ModId}", loadedMod.Manifest.Id);
                invalidMods.Add(loadedMod);
            }
        }

        foreach (var invalidMod in invalidMods) {
            this.LoadedMods.Remove(invalidMod);
        }
    }

    private IMod? LoadAssembly(string id, string assemblyPath) {
        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        var context = new ModLoadContext(fullAssemblyPath);
        var assembly = context.LoadFromAssemblyPath(fullAssemblyPath);
        var modType = assembly.GetTypes().FirstOrDefault(t =>
            t.GetInterfaces().FirstOrDefault(t => t.FullName == typeof(IMod).FullName) != null);

        if (modType == null) {
            this.logger.Error("Assembly at {AssemblyPath} does not contain a mod", assemblyPath);
            return null;
        }

        var ctor = modType.GetConstructor([typeof(IModInterface)]);

        IMod? mod = ctor is not null
            ? ctor.Invoke([new ModInterface(id, this, this.gameStateBus)]) as IMod
            : Activator.CreateInstance(modType) as IMod;

        if (mod != null) {
            try {
                mod.OnLoad();
                var version = this.LoadedMods.First(m => m.Manifest.Id == id)
                                  .Manifest.Metadata?.Version ?? "0.0.0";
                EventBus.Publish(new ModEvents.ModLoaded(id, version));
            } catch (Exception e) {
                this.logger.Error(e, "Mod {ModId} OnLoad threw", id);
            }
        } else {
            this.logger.Error("Mod {ModId} constructor returned null or incompatible type", id);
        }

        // Auto-discover [Patch] classes in the mod assembly
        try { this.patchManager.ScanAssembly(assembly); }
        catch (Exception e) { this.logger.Error(e, "Patch scan failed for {ModId}", id); }

        return mod;
    }

    public void RegisterSourceMod(string modId, ISourceMod mod) {
        if (!this.sourceMods.ContainsKey(modId)) this.sourceMods[modId] = new();
        this.sourceMods[modId].Add(mod);
    }

    private void InitializeMods() {
        foreach (var mod in this.LoadedMods) {
            if (mod.AssemblyMod == null) continue;
            try {
                mod.AssemblyMod.OnInitialize();
            } catch (Exception e) {
                this.logger.Error(e, "Mod {ModId} OnInitialize threw", mod.Manifest.Id);
            }
        }
    }

    /// <summary>Shut down all mods: OnUnload then Dispose.</summary>
    public void Shutdown() {
        foreach (var mod in this.LoadedMods) {
            if (mod.AssemblyMod == null) continue;
            try { mod.AssemblyMod.OnUnload(); }
            catch (Exception e) { this.logger.Warning(e, "Mod {ModId} OnUnload threw", mod.Manifest.Id); }
            try { mod.AssemblyMod.Dispose(); }
            catch (Exception e) { this.logger.Warning(e, "Mod {ModId} Dispose threw", mod.Manifest.Id); }
        }
    }
}
