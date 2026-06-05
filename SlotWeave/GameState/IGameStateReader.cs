// IGameStateReader — mod-facing interface for registering data readers
// Mods implement this to have their data gathered each frame during idle()

using SlotWeave.NativeInterop;

namespace SlotWeave.GameState;

/// <summary>
/// Implement this interface in your mod to register a game state reader.
/// The reader's Read() method is called every frame at the SceneTree::idle() return point,
/// after all _process()/_physics_process() callbacks have completed.
///
/// Mods receive an EngineObjectReader and the scene tree pointer — they can
/// traverse the node tree and read any property they need.
///
/// Example:
/// <code>
/// public class MyStateReader : IGameStateReader
/// {
///     public void Read(EngineObjectReader reader, IntPtr sceneTree, GameStateSnapshot snapshot)
///     {
///         var itemsNode = reader.FindNode("Main/Items");
///         snapshot.Extra["item_count"] = reader.ReadInt(itemsNode, "item_count");
///     }
/// }
/// </code>
/// </summary>
public interface IGameStateReader
{
    /// <summary>
    /// Called once per frame at the SceneTree::idle() return point.
    /// All game state is consistent — _process and _physics_process have completed.
    ///
    /// Use the provided EngineObjectReader to traverse nodes and read properties.
    /// Populate the snapshot's Extra dictionary with your data.
    ///
    /// IMPORTANT: This runs on the game's main thread. Keep it fast —
    /// avoid expensive allocations, file I/O, or lock contention.
    /// </summary>
    /// <param name="reader">EngineObjectReader for node traversal and property reading.</param>
    /// <param name="sceneTree">SceneTree* pointer for direct access.</param>
    /// <param name="snapshot">Mutable snapshot being built for this frame — write to it.</param>
    void Read(EngineObjectReader reader, IntPtr sceneTree, GameStateSnapshot snapshot);
}

/// <summary>
/// Simplified reader using declarative property bindings.
/// Override Bindings to declare what to read; the bus handles the details.
/// </summary>
public abstract class DeclarativeStateReader : IGameStateReader
{
    /// <summary>Declare the property bindings this reader needs.</summary>
    public abstract IReadOnlyList<PropertyBinding> Bindings { get; }

    /// <summary>
    /// Called with the results of reading all declared bindings.
    /// Override this to process the values into the snapshot.
    /// </summary>
    public abstract void OnPropertiesRead(
        EngineObjectReader reader,
        IntPtr sceneTree,
        GameStateSnapshot snapshot,
        IReadOnlyList<PropertyValue> values);

    /// <summary>Default implementation walks all Bindings and calls OnPropertiesRead.</summary>
    public virtual void Read(EngineObjectReader reader, IntPtr sceneTree, GameStateSnapshot snapshot)
    {
        var bindings = Bindings;
        var values = new List<PropertyValue>(bindings.Count);

        foreach (var binding in bindings)
        {
            var node = reader.FindNode(binding.NodePath);
            if (node == IntPtr.Zero)
            {
                values.Add(new PropertyValue
                {
                    Key = binding.TargetKey ?? binding.Property,
                    Type = VariantType.Nil,
                    Value = null,
                    Success = false
                });
                continue;
            }

            try
            {
                using var v = new NativeVariant(node, new NativeStringName(binding.Property));
                var val = ReadVariantValue(v);
                values.Add(new PropertyValue
                {
                    Key = binding.TargetKey ?? binding.Property,
                    Type = v.Type,
                    Value = val,
                    Success = true
                });
            }
            catch
            {
                values.Add(new PropertyValue
                {
                    Key = binding.TargetKey ?? binding.Property,
                    Type = VariantType.Nil,
                    Value = null,
                    Success = false
                });
            }
        }

        OnPropertiesRead(reader, sceneTree, snapshot, values);
    }

    private static object? ReadVariantValue(NativeVariant v)
    {
        return v.Type switch
        {
            VariantType.Nil => null,
            VariantType.Bool => v.AsBool(),
            VariantType.Int => v.AsInt(),
            VariantType.Real => v.AsReal(),
            VariantType.String => v.AsString(),
            VariantType.Object => v.AsObject(),
            _ => null // Complex types need custom marshaling in OnPropertiesRead
        };
    }
}
