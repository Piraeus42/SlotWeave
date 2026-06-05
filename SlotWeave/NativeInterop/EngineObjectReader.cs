// Layer 1: Safe engine object reading with IDisposable wrappers
// All Variant/StringName lifecycle managed via IDisposable → using blocks
// Implements the "shadow Godot DOM" — pure C# node tree traversal

using System.Runtime.InteropServices;
using Serilog;

namespace SlotWeave.NativeInterop;

/// <summary>
/// Safe wrapper around a Godot StringName (interned string identifier).
/// Allocates 8 bytes for the _Data* pointer, constructs via StringName::StringName(const char*),
/// and destroys via StringName::~StringName() on Dispose.
/// </summary>
public sealed class NativeStringName : IDisposable
{
    private IntPtr _handle;
    private IntPtr _ansiStr;
    private bool _disposed;

    /// <summary>Pointer to the StringName memory (8 bytes).</summary>
    public IntPtr Handle
    {
        get
        {
            ThrowIfDisposed();
            return _handle;
        }
    }

    /// <summary>Construct a StringName from a C# string (converted to ANSI).</summary>
    public NativeStringName(string name)
    {
        _handle = Native.AllocZeroed(Native.STRINGNAME_SIZE);
        _ansiStr = Marshal.StringToHGlobalAnsi(name);
        try
        {
            Native.StringNameCtor(_handle, _ansiStr);
        }
        catch
        {
            // Clean up on failure
            Marshal.FreeHGlobal(_handle);
            Marshal.FreeHGlobal(_ansiStr);
            _handle = IntPtr.Zero;
            _ansiStr = IntPtr.Zero;
            throw;
        }
    }

    /// <summary>
    /// Read the _Data* pointer from this StringName.
    /// Used for pointer-equality comparison when searching child nodes by name.
    /// </summary>
    public IntPtr DataPtr => _disposed ? IntPtr.Zero : Marshal.ReadIntPtr(_handle);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NativeStringName));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            try { Native.StringNameDtor(_handle); }
            catch (Exception ex)
            {
                SlotWeave.Logger.ForContext<NativeStringName>()
                    .Warning(ex, "StringNameDtor threw during Dispose");
            }
            Marshal.FreeHGlobal(_handle);
            _handle = IntPtr.Zero;
        }

        if (_ansiStr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_ansiStr);
            _ansiStr = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Safe wrapper around a Godot Variant (24 bytes).
/// Calls Object::get() to retrieve a property value, then Variant::clear()
/// on Dispose to release any heap-allocated resources (Strings, Arrays, Objects, etc.).
///
/// CRITICAL: Must always Dispose or use `using` — failing to call Variant::clear()
/// will leak reference-counted objects and heap memory.
/// </summary>
public sealed class NativeVariant : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>Pointer to the 24-byte Variant memory.</summary>
    public IntPtr Handle
    {
        get
        {
            ThrowIfDisposed();
            return _handle;
        }
    }

    /// <summary>Variant type tag (VariantType enum).</summary>
    public VariantType Type => _disposed
        ? VariantType.Nil
        : (VariantType)Marshal.ReadInt32(_handle);

    /// <summary>
    /// Call Object::get() on a node and store the result.
    /// The returned Variant MUST be disposed to free engine resources.
    /// </summary>
    /// <param name="nodePtr">Node* to read from.</param>
    /// <param name="propName">Property name as StringName.</param>
    public NativeVariant(IntPtr nodePtr, NativeStringName propName)
    {
        _handle = Native.AllocZeroed(Native.VARIANT_SIZE);
        // Resolve Node → GDScriptInstance, then call GDScriptInstance::get
        var instance = Marshal.ReadIntPtr(nodePtr + Native.OFF_NODE_SCRIPT_INSTANCE);
        if (instance != IntPtr.Zero)
            Native.GDScriptInstanceGet(instance, propName.Handle, _handle);
    }

    /// <summary>
    /// Call Object::get() with a string property name (auto-constructs StringName).
    /// Convenience overload — slightly more overhead than passing a pre-built NativeStringName.
    /// </summary>
    public static NativeVariant Read(IntPtr nodePtr, string propName)
    {
        using var sn = new NativeStringName(propName);
        return new NativeVariant(nodePtr, sn);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NativeVariant));
    }

    // ── Typed property accessors ──

    /// <summary>Read as bool. Returns false if type mismatch.</summary>
    public bool AsBool() =>
        !_disposed && (VariantType)Marshal.ReadInt32(_handle) == VariantType.Bool
            && Marshal.ReadByte(_handle + 8) != 0;

    /// <summary>Read as signed 64-bit int. Returns 0 if type mismatch.</summary>
    public long AsInt() =>
        !_disposed && (VariantType)Marshal.ReadInt32(_handle) == VariantType.Int
            ? Marshal.ReadInt64(_handle + 8)
            : 0L;

    /// <summary>Read as double. Returns 0.0 if type mismatch.</summary>
    public double AsReal()
    {
        if (_disposed || (VariantType)Marshal.ReadInt32(_handle) != VariantType.Real)
            return 0.0;
        var bits = Marshal.ReadInt64(_handle + 8);
        return BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>
    /// Read as C# string. Returns null if type mismatch or unreadable.
    /// Handles Godot's CowData&lt;char32_t&gt; (UTF-16 on Windows) layout.
    /// </summary>
    public string? AsString()
    {
        if (_disposed || (VariantType)Marshal.ReadInt32(_handle) != VariantType.String)
            return null;
        // Variant._data at +0x08 contains the Godot String, which starts with CowData::_ptr
        return Native.SafeReadGodotString(_handle + 8);
    }

    /// <summary>Read as Object* pointer. Returns IntPtr.Zero if type mismatch.</summary>
    public IntPtr AsObject() =>
        !_disposed && (VariantType)Marshal.ReadInt32(_handle) == VariantType.Object
            ? Marshal.ReadIntPtr(_handle + 8)
            : IntPtr.Zero;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            try { Native.VariantClear(_handle); }
            catch (Exception ex)
            {
                SlotWeave.Logger.ForContext<NativeVariant>()
                    .Warning(ex, "VariantClear threw during Dispose");
            }
            Marshal.FreeHGlobal(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Pure C# engine object reader — the "shadow Godot DOM."
///
/// Initializes by reading OS singleton → vtable get_main_loop → SceneTree → root.
/// Provides FindNode (path traversal via CowData children vectors) and
/// convenience ReadXxx methods for common property types.
///
/// All methods that return Variant data use IDisposable wrappers —
/// callers MUST dispose or use `using`.
/// </summary>
public class EngineObjectReader
{
    private static readonly ILogger Logger = SlotWeave.Logger.ForContext<EngineObjectReader>();

    private readonly IntPtr _baseAddr;
    private IntPtr _os;
    private IntPtr _sceneTree;
    private IntPtr _root;

    private bool _initialized;

    public IntPtr SceneTreePtr => _sceneTree;
    public IntPtr RootPtr => _root;
    public bool IsInitialized => _initialized;

    public EngineObjectReader()
    {
        _baseAddr = Native.BaseAddress;
    }

    /// <summary>
    /// Initialize the reader: OS singleton → get_main_loop → SceneTree → root.
    /// Safe to call multiple times; returns true on success.
    /// </summary>
    /// <param name="quiet">If true, suppresses error logging (used for lazy-init retries).</param>
    public bool Initialize(bool quiet = false)
    {
        if (_initialized) return true;

        try
        {
            // Step 1: Read OS singleton from global variable
            _os = Native.GetOsSingleton();
            if (_os == IntPtr.Zero)
            {
                if (!quiet)
                    Logger.Debug("OS singleton not yet available (engine not started)");
                else
                    Logger.Debug("OS singleton not yet available (lazy init retry)");
                return false;
            }
            Logger.Debug("OS singleton at 0x{Addr:X16}", _os.ToInt64());

            // Step 2: OS::get_main_loop() via vtable
            _sceneTree = Native.GetMainLoop(_os);
            if (_sceneTree == IntPtr.Zero)
            {
                if (!quiet)
                    Logger.Error("get_main_loop() returned null");
                return false;
            }
            Logger.Debug("SceneTree at 0x{Addr:X16}", _sceneTree.ToInt64());

            // Step 3: Read SceneTree.root
            _root = Marshal.ReadIntPtr(_sceneTree + Native.OFF_SCENETREE_ROOT);
            if (_root == IntPtr.Zero)
            {
                Logger.Error("SceneTree.root is null — scene not loaded yet?");
                return false;
            }
            Logger.Debug("Root Viewport at 0x{Addr:X16}", _root.ToInt64());

            _initialized = true;
            Logger.Information("EngineObjectReader initialized successfully");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "EngineObjectReader initialization failed");
            return false;
        }
    }

    /// <summary>
    /// Find a node by Godot path (e.g. "Main/Items" or "root/Main/Pop-up").
    /// Uses pure C# CowData traversal — no engine function calls for search.
    /// StringName comparison uses pointer equality (O(1) per child).
    /// </summary>
    /// <param name="path">Slash-separated node path.</param>
    /// <param name="from">Starting node (defaults to scene root).</param>
    /// <returns>Node* pointer, or IntPtr.Zero if not found.</returns>
    public IntPtr FindNode(string path, IntPtr? from = null)
    {
        if (!_initialized)
        {
            Logger.Warning("FindNode called before initialization");
            return IntPtr.Zero;
        }

        var current = from ?? _root;
        if (current == IntPtr.Zero) return IntPtr.Zero;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return current;

        foreach (var part in parts)
        {
            if (current == IntPtr.Zero) return IntPtr.Zero;

            using var searchName = new NativeStringName(part);
            var targetData = searchName.DataPtr;

            // Read children vector: CowData<Node*> at node+0x108
            var childrenCowData = Marshal.ReadIntPtr(current + Native.OFF_NODE_CHILDREN);
            if (childrenCowData == IntPtr.Zero) return IntPtr.Zero;

            var count = (uint)Marshal.ReadInt32(childrenCowData - 4);
            var found = false;

            for (uint i = 0; i < count; i++)
            {
                var child = Marshal.ReadIntPtr(childrenCowData + (int)(i * 8));
                if (child == IntPtr.Zero) continue;

                // StringName comparison = pointer equality (verified in IDA)
                var childNameData = Marshal.ReadIntPtr(child + Native.OFF_NODE_NAME);
                if (childNameData == targetData)
                {
                    current = child;
                    found = true;
                    break;
                }
            }

            if (!found) return IntPtr.Zero;
        }

        return current;
    }

    /// <summary>
    /// Enumerate the names of all direct children of a node.
    /// Useful for discovery/debugging.
    /// </summary>
    public IEnumerable<string> GetChildNames(IntPtr nodePtr)
    {
        if (nodePtr == IntPtr.Zero) yield break;

        var childrenCowData = Marshal.ReadIntPtr(nodePtr + Native.OFF_NODE_CHILDREN);
        if (childrenCowData == IntPtr.Zero) yield break;

        var count = (uint)Marshal.ReadInt32(childrenCowData - 4);
        for (uint i = 0; i < count; i++)
        {
            var child = Marshal.ReadIntPtr(childrenCowData + (int)(i * 8));
            if (child == IntPtr.Zero) continue;

            // Read the name via the StringName's interned _Data
            var nameData = Marshal.ReadIntPtr(child + Native.OFF_NODE_NAME);
            if (nameData == IntPtr.Zero) continue;

            // Dereference _Data to get the actual string
            // _Data layout: refcount (4) + cname (ptr) + name (ptr) + ...
            // The cname field is at _Data + 8 (after refcount)
            var cname = Marshal.ReadIntPtr(nameData + 8);
            if (cname != IntPtr.Zero)
            {
                string? str = null;
                try { str = Marshal.PtrToStringAnsi(cname); }
                catch { /* skip unreadable names */ }
                if (str != null) yield return str;
            }
        }
    }

    // ── Convenience typed property readers ──

    /// <summary>Read a bool property from a node.</summary>
    public bool ReadBool(IntPtr nodePtr, string property)
    {
        using var v = NativeVariant.Read(nodePtr, property);
        return v.AsBool();
    }

    /// <summary>Read an int property from a node.</summary>
    public long ReadInt(IntPtr nodePtr, string property)
    {
        using var v = NativeVariant.Read(nodePtr, property);
        return v.AsInt();
    }

    /// <summary>Read a float/double property from a node.</summary>
    public double ReadReal(IntPtr nodePtr, string property)
    {
        using var v = NativeVariant.Read(nodePtr, property);
        return v.AsReal();
    }

    /// <summary>Read a string property from a node.</summary>
    public string? ReadString(IntPtr nodePtr, string property)
    {
        using var v = NativeVariant.Read(nodePtr, property);
        return v.AsString();
    }

    /// <summary>Read an Object* property from a node (returns child/ref pointer).</summary>
    public IntPtr ReadObject(IntPtr nodePtr, string property)
    {
        using var v = NativeVariant.Read(nodePtr, property);
        return v.AsObject();
    }

    /// <summary>
    /// Read a property as a raw NativeVariant (caller must dispose).
    /// Use this for complex types (Array, Dictionary) that need custom marshaling.
    /// </summary>
    public NativeVariant ReadProperty(IntPtr nodePtr, string property)
    {
        return NativeVariant.Read(nodePtr, property);
    }

    // ── GDScriptInstance::get property reader (vtable-extracted RVA) ──

    /// <summary>
    /// Read a property from a node's GDScriptInstance using GDScriptInstance::get().
    /// This is the correct path — no sret, clean 3-param calling convention.
    /// Returns the Variant value as an object, or null if the read failed.
    /// Caller must check the type before casting.
    /// </summary>
    public static object? ReadScriptProp(IntPtr nodePtr, string property)
    {
        var instance = Marshal.ReadIntPtr(nodePtr + Native.OFF_NODE_SCRIPT_INSTANCE);
        if (instance == IntPtr.Zero) return null;

        using var sn = new NativeStringName(property);
        var ret = Native.AllocZeroed(Native.VARIANT_SIZE);
        try
        {
            var ok = Native.GDScriptInstanceGet(instance, sn.Handle, ret);
            if (!ok) return null;

            var type = Marshal.ReadInt32(ret);
            return type switch
            {
                0 => null,
                1 => Marshal.ReadByte(ret + 8) != 0,
                2 => Marshal.ReadInt64(ret + 8),
                3 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(ret + 8)),
                4 => Native.SafeReadGodotString(ret + 8),
                _ => null
            };
        }
        finally
        {
            Native.VariantClear(ret);
            Marshal.FreeHGlobal(ret);
        }
    }

}
