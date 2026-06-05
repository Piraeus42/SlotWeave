// Godot 3.4.4 (Luck be a Landlord, 419e713a2) — native interop definitions
// All offsets verified against IDA session e88c0cb6, see docs/ida_analysis_results.md

using System.Runtime.InteropServices;

namespace SlotWeave.NativeInterop;

/// <summary>
/// Low-level native delegate definitions and RVA constants.
/// Calling conventions verified against MSVC x64 disassembly.
/// </summary>
internal static class Native
{
    // ── Image base ──
    public static nint BaseAddress => System.Diagnostics.Process.GetCurrentProcess().MainModule!.BaseAddress;

    // ── RVA offsets (from IDA analysis) ──

    /// <summary>Object::get(Variant* ret, Object* this, StringName* prop, bool* valid) — sret</summary>
    public const int RVA_OBJECT_GET = 0x81F980;

    /// <summary>Variant::clear(this: Variant*)</summary>
    public const int RVA_VARIANT_CLEAR = 0x1513D20;

    /// <summary>StringName::StringName(this: StringName*, cstr: const char*) — ANSI</summary>
    public const int RVA_STRINGNAME_CTOR = 0x14AA130;

    /// <summary>StringName::~StringName(this: StringName*)</summary>
    public const int RVA_STRINGNAME_DTOR = 0x14A9DB0;

    /// <summary>SceneTree::idle(float) entry point</summary>
    public const int RVA_SCENETREE_IDLE = 0x787930;

    /// <summary>GDScriptInstance::get — extracted at runtime from vtable[1]</summary>
    public const int RVA_GDSCRIPT_INSTANCE_GET = 0x1A1D30;

    // ── Global variable offsets ──

    /// <summary>OS singleton global variable (absolute: 0x142048AB8)</summary>
    public const int RVA_OS_SINGLETON = 0x2048AB8;

    // ── Vtable slots ──

    /// <summary>OS::get_main_loop() vtable slot (index 102)</summary>
    public const int VT_SLOT_GET_MAIN_LOOP = 0x330;

    // ── Struct member offsets ──

    /// <summary>SceneTree::root (Viewport*)</summary>
    public const int OFF_SCENETREE_ROOT = 0x138;

    /// <summary>SceneTree::_quit (bool)</summary>
    public const int OFF_SCENETREE_QUIT = 0x170;

    /// <summary>Node::data.parent (Node*)</summary>
    public const int OFF_NODE_PARENT = 0xF0;

    /// <summary>Node::data.children (CowData&lt;Node*&gt;)</summary>
    public const int OFF_NODE_CHILDREN = 0x108;

    /// <summary>Node::data.name (StringName::_Data*)</summary>
    public const int OFF_NODE_NAME = 0x120;

    /// <summary>Node → script_instance (GDScriptInstance*)</summary>
    public const int OFF_NODE_SCRIPT_INSTANCE = 0x58;

    /// <summary>GDScriptInstance → members (Vector&lt;Variant&gt; = CowData pointer). Verified via hex dump: offset 0x20, not 0x18.</summary>
    public const int OFF_SCRIPT_INSTANCE_MEMBERS = 0x20;

    // ── Constants ──

    /// <summary>Size of Godot Variant on x64 Godot 3.4.4</summary>
    public const int VARIANT_SIZE = 24;

    /// <summary>Size of Godot StringName on x64 (just a _Data* pointer)</summary>
    public const int STRINGNAME_SIZE = 8;

    // ── Delegate types ──

    /// <summary>
    /// Object::get(const StringName &p_name, bool *r_valid = nullptr) const → Variant
    /// Returns Variant (24 bytes) BY VALUE → MSVC x64 sret mandatory.
    ///   RCX = Variant* ret (sret buffer), RDX = Object* this, R8 = StringName* prop, R9 = bool* valid
    /// RVA currently UNKNOWN — probe extracts from Object vtable at runtime.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    public delegate void ObjectGetDelegate(IntPtr ret, IntPtr obj, IntPtr prop, IntPtr valid);

    /// <summary>
    /// GDScriptInstance::get(this: GDScriptInstance*, p_name: StringName*, r_ret: Variant*) → bool
    /// Godot 3.x: bool GDScriptInstance::get(const StringName &, Variant &) const
    /// No sret — returns bool, Variant is a reference parameter.
    ///   RCX = instance (this)
    ///   RDX = &p_name (StringName*)
    ///   R8  = &r_ret (Variant*)
    ///   AL  = bool return
    /// RVA 0x1A1D30 extracted at runtime from GDScriptInstance vtable[1].
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    public delegate bool GDScriptInstanceGetDelegate(IntPtr instance, IntPtr propName, IntPtr rRet);

    /// <summary>
    /// Variant::clear(this: Variant*)
    /// Frees any heap-allocated resources (String, Array, Dictionary, Object refs, etc.)
    /// and resets type to NIL. MUST be called for every Variant returned by Object::get().
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    public delegate void VariantClearDelegate(IntPtr variant);

    /// <summary>
    /// StringName::StringName(this: StringName*, cstr: const char*)
    /// Constructs a StringName from an ANSI C string.
    /// Critical: input MUST be ANSI (Marshal.StringToHGlobalAnsi).
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    public delegate void StringNameCtorDelegate(IntPtr pThis, IntPtr pCString);

    /// <summary>
    /// StringName::~StringName(this: StringName*)
    /// Destroys a StringName, decrementing refcount on the interned _Data.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    public delegate void StringNameDtorDelegate(IntPtr pThis);

    /// <summary>
    /// SceneTree::idle(this: SceneTree*, delta: float) → bool
    /// rcx=this, xmm1=delta, return=al (bool whether to quit)
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    public delegate bool SceneTreeIdleDelegate(IntPtr sceneTree, float delta);

    /// <summary>
    /// OS::get_main_loop(this: OS*) → MainLoop*
    /// Virtual method at vtable slot 0x330.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    public delegate IntPtr GetMainLoopDelegate(IntPtr os);

    // ── Delegate instances (lazy-init) ──

    private static VariantClearDelegate? _variantClear;
    private static StringNameCtorDelegate? _stringNameCtor;
    private static StringNameDtorDelegate? _stringNameDtor;
    private static GDScriptInstanceGetDelegate? _gdscriptInstanceGet;

    public static VariantClearDelegate VariantClear =>
        _variantClear ??= Marshal.GetDelegateForFunctionPointer<VariantClearDelegate>(BaseAddress + RVA_VARIANT_CLEAR);

    public static StringNameCtorDelegate StringNameCtor =>
        _stringNameCtor ??= Marshal.GetDelegateForFunctionPointer<StringNameCtorDelegate>(BaseAddress + RVA_STRINGNAME_CTOR);

    public static StringNameDtorDelegate StringNameDtor =>
        _stringNameDtor ??= Marshal.GetDelegateForFunctionPointer<StringNameDtorDelegate>(BaseAddress + RVA_STRINGNAME_DTOR);

    /// <summary>GDScriptInstance::get — 3-param, no sret. Safe to call.</summary>
    public static GDScriptInstanceGetDelegate GDScriptInstanceGet =>
        _gdscriptInstanceGet ??= Marshal.GetDelegateForFunctionPointer<GDScriptInstanceGetDelegate>(BaseAddress + RVA_GDSCRIPT_INSTANCE_GET);

    // ── Helper methods ──

    /// <summary>Read the OS singleton pointer directly from the global variable.</summary>
    public static IntPtr GetOsSingleton() =>
        Marshal.ReadIntPtr(BaseAddress + RVA_OS_SINGLETON);

    /// <summary>
    /// Call OS::get_main_loop() through the vtable.
    /// Returns the MainLoop* (which is actually a SceneTree* in LABL).
    /// </summary>
    public static IntPtr GetMainLoop(IntPtr os)
    {
        var vtable = Marshal.ReadIntPtr(os);
        var funcPtr = Marshal.ReadIntPtr(vtable + VT_SLOT_GET_MAIN_LOOP);
        var getMainLoop = Marshal.GetDelegateForFunctionPointer<GetMainLoopDelegate>(funcPtr);
        return getMainLoop(os);
    }

    /// <summary>Call a virtual method by vtable slot index (8 bytes per slot).</summary>
    public static IntPtr CallVirtual(IntPtr obj, int vtableSlot)
    {
        var vtable = Marshal.ReadIntPtr(obj);
        var funcPtr = Marshal.ReadIntPtr(vtable + vtableSlot);
        // Generic: return-type-only delegate for simple vtable calls
        var fn = Marshal.GetDelegateForFunctionPointer<GetMainLoopDelegate>(funcPtr);
        return fn(obj);
    }

    /// <summary>Allocate zeroed memory on the process heap via Marshal.</summary>
    public static IntPtr AllocZeroed(int size)
    {
        var ptr = Marshal.AllocHGlobal(size);
        unsafe
        {
            var span = new Span<byte>((void*)ptr, size);
            span.Clear();
        }
        return ptr;
    }

    /// <summary>Check if a memory range is readable.</summary>
    public static bool IsReadable(nint addr, int len)
    {
        var mbi = new MemoryBasicInformation();
        if (VirtualQuery(addr, ref mbi, (nint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            return false;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD = 0x100;
        if ((mbi.State & MEM_COMMIT) == 0) return false;
        if ((mbi.Protect & PAGE_NOACCESS) != 0) return false;
        if ((mbi.Protect & PAGE_GUARD) != 0) return false;
        return addr + len <= mbi.BaseAddress + mbi.RegionSize;
    }

    /// <summary>
    /// Safely read a Godot String (CowData&lt;char32_t&gt;) from a pointer to the String field.
    /// The String is a CowData pointer. Returns null if unreadable.
    /// </summary>
    public static string? SafeReadGodotString(IntPtr stringFieldPtr)
    {
        if (stringFieldPtr == IntPtr.Zero) return null;
        if (!IsReadable(stringFieldPtr, 8)) return null;
        var cowDataPtr = Marshal.ReadIntPtr(stringFieldPtr);
        if (cowDataPtr == IntPtr.Zero) return null;
        if (!IsReadable(cowDataPtr - 4, 4)) return null;
        var size = Marshal.ReadInt32(cowDataPtr - 4);
        if (size <= 0 || size > 1_000_000) return null;
        if (!IsReadable(cowDataPtr, size * 2)) return null;
        var str = Marshal.PtrToStringUni(cowDataPtr, size);
        if (str is not null) MemoryUtils.TrimNullTerminator(ref str);
        return str;
    }

    // ── P/Invoke for VirtualQuery ──

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualQuery(nint lpAddress, ref MemoryBasicInformation lpBuffer, nint dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}

/// <summary>
/// Godot 3.4 Variant type enum.
/// Only types with heap allocations (marked with *) need Variant::clear().
/// </summary>
public enum VariantType : int
{
    Nil = 0,
    Bool = 1,
    Int = 2,
    Real = 3,
    String = 4,          // * heap (CowData<char32_t>)
    Vector2 = 5,
    Rect2 = 6,
    Vector3 = 7,
    Transform2D = 8,     // * memdelete
    Plane = 9,
    Quat = 10,
    Aabb = 11,           // * memdelete
    Basis = 12,          // * memdelete
    Transform = 13,      // * memdelete
    Color = 14,
    NodePath = 15,       // * NodePath dtor
    Rid = 16,
    Object = 17,         // * ObjectRC::unref
    Dictionary = 18,     // * Dictionary dtor
    Array = 19,          // * Array dtor
    PoolByteArray = 20,  // * PoolVector dtor
    PoolIntArray = 21,   // * PoolVector dtor
    PoolRealArray = 22,  // * PoolVector dtor
    PoolStringArray = 23,// * PoolVector dtor
    PoolVector2Array = 24,// * PoolVector dtor
    PoolVector3Array = 25,// * PoolVector dtor
    PoolColorArray = 26, // * PoolVector dtor
    VariantMax = 27
}

/// <summary>
/// Memory layout of Godot 3.4.4 Variant on MSVC x64 (24 bytes).
/// [StructLayout(LayoutKind.Explicit)] is used because the union at +0x08
/// has different interpretations depending on the type tag.
///
/// Layout:
///   +0x00: Type type (4 bytes)
///   +0x04: padding  (4 bytes, MSVC alignment)
///   +0x08: union _data (16 bytes)
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24, Pack = 1)]
public struct NativeVariantStruct
{
    /// <summary>Variant type tag (VariantType enum).</summary>
    [FieldOffset(0x00)]
    public int Type;

    // ── Union members at +0x08 (16 bytes) ──

    /// <summary>BOOL: byte at [0x08] is 0 or 1.</summary>
    [FieldOffset(0x08)]
    public byte BoolByte;

    /// <summary>INT: Godot stores 64-bit signed int.</summary>
    [FieldOffset(0x08)]
    public long Int64;

    /// <summary>REAL: IEEE 754 double.</summary>
    [FieldOffset(0x08)]
    public double Real64;

    /// <summary>STRING: pointer to Godot String (CowData&lt;char32_t&gt;).</summary>
    [FieldOffset(0x08)]
    public IntPtr StringPtr;

    /// <summary>OBJECT: pointer to Object* (with ref held).</summary>
    [FieldOffset(0x08)]
    public IntPtr ObjectPtr;

    /// <summary>Raw access to the data union.</summary>
    [FieldOffset(0x08)]
    public long Data0;

    [FieldOffset(0x10)]
    public long Data1;
}
