using System.Text.Json;
using SlotWeave.GameState;
using SlotWeave.Modding;
using Serilog;

namespace SlotWeave;

internal class ModInterface(string modId, ModLoader modLoader, GameStateBus? gameStateBus = null) : IModInterface {
    public ILogger Logger { get; } = SlotWeave.Logger.ForContext("SourceContext", modId);

    public string GameDir => SlotWeave.GameDir;
    public string SlotWeaveDir => SlotWeave.SlotWeaveDir;

    public string[] LoadedMods => modLoader.LoadedMods.Select(x => x.Manifest.Id).ToArray();

    private string GetConfigPath() => Path.Combine(SlotWeave.SlotWeaveDir, "configs", $"{modId}.json");

    public T ReadConfig<T>() where T : class, new() {
        var path = this.GetConfigPath();

        if (!File.Exists(path)) {
            var @default = new T();
            this.WriteConfig(@default);
            return @default;
        }

        var json = File.ReadAllText(path);
        var obj = JsonSerializer.Deserialize<T>(json, SlotWeave.JsonSerializerOptions)!;
        this.WriteConfig(obj); // apply new fields
        return obj;
    }

    public void WriteConfig<T>(T config) where T : class {
        var path = this.GetConfigPath();
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, SlotWeave.JsonSerializerOptions);
        File.WriteAllText(path, json);
    }

    public void RegisterSourceMod(ISourceMod mod) {
        modLoader.RegisterSourceMod(modId, mod);
    }

    public void Subscribe<T>(Action<T> handler) where T : notnull {
        EventBus.Subscribe(handler);
    }

    public void ClearCache() {
        SlotWeave.Cache.Clear();
    }

    public void RegisterGameStateReader(IGameStateReader reader) {
        gameStateBus?.RegisterReader(reader);
    }

    public void UnregisterGameStateReader(IGameStateReader reader) {
        gameStateBus?.UnregisterReader(reader);
    }
}
