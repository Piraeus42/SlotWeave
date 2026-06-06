// Godot 3.4.4 (Luck be a Landlord, 419e713a2) — RVA offsets are version-specific

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SlotWeave.Modding;
using Serilog;

namespace SlotWeave;

internal unsafe class Hooks {
    // GDScript::reload hook: RCX = GDScript*, RDX = bool keepState
    private delegate nint ReloadDelegate(nint gdscript, nint keepState);

    // String::resize(int): RCX=CowData*, RDX=int, returns Error
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int StringResizeDelegate(nint pCowData, int pSize);

    private delegate nint SetUnhandledExceptionFilterDelegate(nint filter);
    private delegate nint UnhandledExceptionFilterDelegate(ExceptionPointersStruct* exceptionInfo);

    private ITrackedHook<SetUnhandledExceptionFilterDelegate> setUnhandledExceptionFilterHook;
    private UnhandledExceptionFilterDelegate unhandledExceptionFilter;

    private ITrackedHook<ReloadDelegate> reloadHook;

    private readonly ILogger logger = SlotWeave.Logger.ForContext<Hooks>();
    private readonly SourceModder modder;
    private readonly Interop interop;
    private readonly CacheManager cache;
    private readonly List<(string Id, string Version, string ContentHash)> activeModVersions;

    private const int RVA_STRING_RESIZE = 0x14D10;

    private bool dumpSource = Environment.GetEnvironmentVariable("GDWEAVE_DUMP_SOURCE") is not null;
    private bool dumpPatched = Environment.GetEnvironmentVariable("GDWEAVE_DUMP_PATCHED") is not null;

    private enum PatternType {
        Reload
    }

    public Hooks(SourceModder modder, Interop interop, CacheManager cache,
                 List<(string Id, string Version, string ContentHash)> activeModVersions) {
        this.modder = modder;
        this.interop = interop;
        this.cache = cache;
        this.activeModVersions = activeModVersions;

        // We can't set an exception filter directly as the game overrides it, so we need to hook SetUnhandledExceptionFilter
        var kernel32 = LoadLibrary("kernel32.dll");
        var setUnhandledExceptionFilter = GetProcAddress(kernel32, "SetUnhandledExceptionFilter");
        this.unhandledExceptionFilter = this.UnhandledExceptionFilter;
        this.setUnhandledExceptionFilterHook = interop.CreateHook<SetUnhandledExceptionFilterDelegate>(
            setUnhandledExceptionFilter,
            this.SetUnhandledExceptionFilterDetour);
        this.setUnhandledExceptionFilterHook.Enable();

        var patterns = new Dictionary<PatternType, string[]> {
            [PatternType.Reload] = [
                // GDScript::reload prologue
                "48 89 5C 24 08 88 54 24 10 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 B0 FE FF FF 48 81 EC 50 02 00 00"
            ]
        };

        const string patternsJson = "patterns.json";
        if (File.Exists(patternsJson)) {
            patterns = JsonSerializer.Deserialize<Dictionary<PatternType, string[]>>(
                File.ReadAllText(patternsJson), SlotWeave.JsonSerializerOptions)!;
        }

        var reloadAddr = interop.ScanText(patterns[PatternType.Reload]);
        this.logger.Debug("GDScript::reload found at {Addr:X16}", reloadAddr);
        Console.WriteLine("[x64dbg] bp GDScript::reload = 0x{0:X16}", reloadAddr.ToInt64());
        Console.WriteLine("[x64dbg] bp crash_site (RVA 0x15F07DA) = 0x{0:X16}",
            this.interop.BaseAddress.ToInt64() + 0x15F07DA);
        Console.Out.Flush();
        this.reloadHook = interop.CreateHook<ReloadDelegate>(reloadAddr, this.ReloadDetour);
        this.reloadHook.Enable();
    }

    private nint SetUnhandledExceptionFilterDetour(nint filter) {
        return this.setUnhandledExceptionFilterHook.Original(
            Marshal.GetFunctionPointerForDelegate(this.unhandledExceptionFilter));
    }

    private nint UnhandledExceptionFilter(ExceptionPointersStruct* exceptionInfo) {
        this.logger.Error("========== UNHANDLED EXCEPTION!!!");
        this.logger.Error("Exception code: {Code:X8}", exceptionInfo->ExceptionRecord->ExceptionCode);
        this.logger.Error("Exception address: {Address:X8}", exceptionInfo->ExceptionRecord->ExceptionAddress);

        const string message = """
                               The game has crashed. Sorry! :(

                               Try disabling some mods and see if the problem persists. It is very likely this issue was not caused by SlotWeave itself but rather a mod.

                               When asking for support, provide the log file in the SlotWeave folder in your game install.
                               """;

        _ = MessageBox(IntPtr.Zero, message, "SlotWeave", 0x30);
        return 0;
    }

    private int callCount;
    // Track patched source contents to prevent re-patching already-modified scripts
    private readonly HashSet<string> knownPatchedSources = [];

    // One-shot patch: uses GDScript* pointer as key (NOT path string).
    // Godot may expose the same GDScript via multiple paths (res://Main.tscn::4
    // vs internal resource path). If we key by string, the same underlying script
    // gets processed twice → double injection. The gdscript pointer is unique.
    private readonly HashSet<IntPtr> patchedScripts = [];

    // Reload loop breaker: if the same path reloads >3 times in 2 seconds, blacklist.
    private readonly Dictionary<string, (long FirstTick, int Count)> reloadLoopTracker = new();
    private readonly HashSet<string> reloadBlacklist = [];
    private const int RELOAD_LOOP_THRESHOLD = 3;
    private const long RELOAD_LOOP_WINDOW_MS = 2000;

    private nint ReloadDetour(nint gdscript, nint keepState) {
        var n = Interlocked.Increment(ref this.callCount);

        const int SOURCE_OFFSET = 0x248;
        const int PATH_OFFSET = 0x250;
        const int RESOURCE_PATH_OFFSET = 0x108;

        var source = SafeReadString(gdscript + SOURCE_OFFSET);
        var gdscriptPath = SafeReadString(gdscript + PATH_OFFSET);
        var resourcePath = SafeReadString(gdscript + RESOURCE_PATH_OFFSET);
        // GDScript::path is empty for embedded scripts — fall back to Resource::path
        var path = !string.IsNullOrEmpty(gdscriptPath) ? gdscriptPath : resourcePath;

        this.logger.Debug(
            "[RELOAD] #{N} tick={Tick}ms path={Path} srcLen={SrcLen}",
            n, Environment.TickCount64, path ?? "(null)", source?.Length ?? -1);

        if (source != null)
            source = source.Replace("\r\n", "\n");

        // ── Reload loop detection ──
        if (path != null && source != null)
        {
            var now = Environment.TickCount64;
            lock (reloadLoopTracker)
            {
                if (reloadLoopTracker.TryGetValue(path, out var entry))
                {
                    if (now - entry.FirstTick < RELOAD_LOOP_WINDOW_MS)
                    {
                        entry.Count++;
                        reloadLoopTracker[path] = (entry.FirstTick, entry.Count);
                        if (entry.Count >= RELOAD_LOOP_THRESHOLD && !reloadBlacklist.Contains(path))
                        {
                            reloadBlacklist.Add(path);
                            this.logger.Warning(
                                "[LOOP-BREAK] {Path} reloaded {Count} times in {Ms}ms — " +
                                "dependency-chain recompile loop detected. " +
                                "Blacklisting: original source will be used. Game is protected.",
                                path, entry.Count, now - entry.FirstTick);
                        }
                    }
                    else
                    {
                        // Window expired — reset
                        reloadLoopTracker[path] = (now, 1);
                    }
                }
                else
                {
                    reloadLoopTracker[path] = (now, 1);
                }
            }
        }

        // One-shot patch guard: use GDScript* pointer (unique per object), not path string.
        // Fixes double-injection when Godot exposes same script via multiple path aliases.
        if (patchedScripts.Contains(gdscript))
        {
            this.logger.Debug("#{N} reload: gdscript=0x{Gdscript:X16} path={Path} already patched, skipping",
                n, gdscript.ToInt64(), path ?? "(null)");
            return this.reloadHook.Original(gdscript, keepState);
        }

        // Blacklisted paths: skip pipeline, use original source
        if (path != null && reloadBlacklist.Contains(path))
        {
            this.logger.Debug("#{N} reload: path={Path} is blacklisted, skipping pipeline", n, path);
            return this.reloadHook.Original(gdscript, keepState);
        }

        // Dump to scripts/ (only when GDWEAVE_DUMP_SOURCE is set)
        if (this.dumpSource && source != null && source.Length > 0) {
            var dumpDir = Path.Combine(SlotWeave.SlotWeaveDir, "scripts");
            Directory.CreateDirectory(dumpDir);
            var name = (path ?? $"unknown_{n}")
                .Replace("res://", "").Replace("/", "_").Replace("::", "__") + ".gd";
            try {
                File.WriteAllText(Path.Combine(dumpDir, name), source);
            } catch (Exception e) {
                this.logger.Warning(e, "Failed to dump script");
            }
        }

        // Run through SourceModder pipeline (with cache)
        var modified = source;
        var alreadyPatched = source != null && this.knownPatchedSources.Contains(source);

        if (source != null && source.Length > 0 && path != null && !alreadyPatched) {
            try {
                // Check cache first
                var cached = this.cache.Lookup(source, this.activeModVersions);
                if (cached != null) {
                    modified = cached;
                } else {
                    var result = this.modder.Run(path, source);
                    if (result != null) {
                        modified = result;
                        this.cache.Store(source, this.activeModVersions, modified);
                    }
                }

                if (modified != null && !string.Equals(modified, source, StringComparison.Ordinal)) {
                    // ── GDScript tokenizer validation ──
                    var tok = new Modding.GdTokenizer(modified);
                    if (!tok.Validate())
                    {
                        this.logger.Warning(
                            "#{N} [{Path}] Patched source has syntax error at line {Line}: {Error}",
                            n, path, tok.ErrorLine, tok.LastError);

                        if (Environment.GetEnvironmentVariable("GDWEAVE_STRICT_SANDBOX") is not null)
                        {
                            this.logger.Error(
                                "#{N} [{Path}] GDWEAVE_STRICT_SANDBOX is set — refusing to write. Falling back to original.",
                                n, path);
                            modified = source;
                        }
                    }

                    if (!string.Equals(modified, source, StringComparison.Ordinal))
                    {
                        if (!WriteGodotString(gdscript + SOURCE_OFFSET, modified))
                        {
                            this.logger.Error(
                                "#{N} FAILED to write patched source for {Path} — script may be corrupted", n, path);
                        }
                        else
                        {
                            this.logger.Debug("#{N} source modified, written back", n);
                            this.knownPatchedSources.Add(modified);
                            this.patchedScripts.Add(gdscript);
                        }
                    }
                }

                // Dump patched source with line-level provenance annotations
                if (this.dumpPatched && modified != null && modified.Length > 0) {
                    var patchedDir = Path.Combine(SlotWeave.SlotWeaveDir, "scripts_patched");
                    Directory.CreateDirectory(patchedDir);
                    var name = (path ?? $"unknown_{n}")
                        .Replace("res://", "").Replace("/", "_").Replace("::", "__") + ".gd";
                    try {
                        var prov = this.modder.Provenance;
                        var lines = modified.Replace("\r\n", "\n").Split('\n');
                        var sb = new StringBuilder();
                        string? lastProv = null;
                        for (var i = 0; i < lines.Length; i++) {
                            var curProv = prov is not null && i < prov.Count ? prov[i] : null;
                            if (curProv != lastProv) {
                                lastProv = curProv;
                                sb.AppendLine(curProv is null
                                    ? $"# --- original line {i + 1} ---"
                                    : $"# --- [{curProv}] line {i + 1} ---");
                            }
                            sb.AppendLine(lines[i]);
                        }
                        File.WriteAllText(Path.Combine(patchedDir, name), sb.ToString());
                    } catch (Exception e) {
                        this.logger.Warning(e, "Failed to dump patched script");
                    }
                }
            } catch (Exception e) {
                this.logger.Error(e, "#{N} Pipeline crashed for {Path} — falling back to original source. " +
                    "Check your [Patch] and ISourceMod code for errors.", n, path);
                modified = source; // fall back to original
            }
        } else if (alreadyPatched) {
            this.logger.Debug("#{N} reload: path={Path} already patched, skipping pipeline", n, path);
        }

        // Publish event
        if (source != null && path != null) {
            EventBus.Publish(new ModEvents.ScriptPatched(
                path, source.Length, modified?.Length ?? 0,
                modified != null && modified != source));
        }

        // Periodic cache stats
        if (n % 50 == 0) {
            this.logger.Debug("Cache stats: {Hits} hits / {Misses} misses",
                this.cache.HitCount, this.cache.MissCount);
        }

        return this.reloadHook.Original(gdscript, keepState);
    }

    private static string? SafeReadString(nint stringPtr) {
        if (stringPtr == 0) return null;
        if (!IsReadable(stringPtr, 8)) return null;
        var cowDataPtr = Marshal.ReadIntPtr(stringPtr);
        if (cowDataPtr == 0) return null;
        if (!IsReadable(cowDataPtr - 4, 4)) return null;
        var size = Marshal.ReadInt32(cowDataPtr - 4);
        if (size <= 0 || size > 1_000_000) return null;
        if (!IsReadable(cowDataPtr, size * 2)) return null;
        var str = Marshal.PtrToStringUni(cowDataPtr, size);
        if (str is not null) MemoryUtils.TrimNullTerminator(ref str);
        return str;
    }

    private static string TryReadString(nint addr) {
        if (addr == 0) return "(null)";
        if (!IsReadable(addr, 8)) return "(unreadable)";
        var cowDataPtr = Marshal.ReadIntPtr(addr);
        if (cowDataPtr == 0) return "(empty)";
        if (!IsReadable(cowDataPtr - 4, 4)) return "(no_size)";
        var size = Marshal.ReadInt32(cowDataPtr - 4);
        if (size <= 0 || size > 1_000_000) return $"(bad_size:{size})";
        if (!IsReadable(cowDataPtr, Math.Min(size * 2, 600))) return "(no_data)";
        var text = Marshal.PtrToStringUni(cowDataPtr, Math.Min(size, 300));
        return text ?? "(null_text)";
    }

    private static bool IsReadable(nint addr, int len) {
        var mbi = new MemoryBasicInformation();
        if (VirtualQuery(addr, ref mbi, (nint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            return false;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD = 0x100;
        if ((mbi.State & MEM_COMMIT) == 0) return false;
        if ((mbi.Protect & PAGE_NOACCESS) != 0) return false;
        if ((mbi.Protect & PAGE_GUARD) != 0) return false;
        // Check the entire range fits within this region
        return addr + len <= mbi.BaseAddress + mbi.RegionSize;
    }

    private static bool IsLikelyString(string text) {
        if (text.Length == 0) return false;
        var c = text[0];
        return c >= ' ' && c <= 0xFFFD && !char.IsControl(c);
    }

    /// <summary>
    /// Write a .NET string into a Godot String field. Prefers in-place for speed,
    /// delegates to engine's String::operator=(const char*) when expansion is needed.
    /// </summary>
    private bool WriteGodotString(nint stringFieldAddr, string value) {
        var cowDataPtr = Marshal.ReadIntPtr(stringFieldAddr);
        var newBytes = Encoding.Unicode.GetBytes(value);

        if (cowDataPtr != 0) {
            var oldSize = Marshal.ReadInt32(cowDataPtr - 4); // element count, not bytes
            var oldCapacity = oldSize * 2;                    // UTF-16 byte capacity
            if (newBytes.Length <= oldCapacity) {
                Marshal.Copy(newBytes, 0, cowDataPtr, newBytes.Length);
                Marshal.WriteInt32(cowDataPtr - 4, value.Length); // Godot stores char count
                for (var i = newBytes.Length; i < oldCapacity; i++)
                    Marshal.WriteByte(cowDataPtr + i, 0);
                return true;
            }
        }

        // Buffer too small — delegate to engine's own allocator
        return AssignEngineString(stringFieldAddr, value);
    }

    /// <summary>
    /// Engine-powered String resize: calls Godot's CowData::resize then writes
    /// UTF-16 directly into the newly allocated buffer. Returns false on failure.
    /// </summary>
    private bool AssignEngineString(nint stringFieldAddr, string value) {
        var resize = Marshal.GetDelegateForFunctionPointer<StringResizeDelegate>(
            this.interop.BaseAddress + RVA_STRING_RESIZE);

        var charCount = value.Length;
        logger.Debug("Engine resize: {Chars} chars at 0x{X:X16}", charCount, stringFieldAddr.ToInt64());

        // resize to len+1 matching copy_from semantics (includes null terminator)
        resize(stringFieldAddr, charCount + 1);

        // Read updated CowData pointer — bypasses ptrw(), avoids unverified function
        var dst = Marshal.ReadIntPtr(stringFieldAddr);
        if (dst == 0) {
            this.logger.Error("Engine resize returned null buffer at 0x{X:X16}", stringFieldAddr.ToInt64());
            return false;
        }

        var bytes = Encoding.Unicode.GetBytes(value);
        Marshal.Copy(bytes, 0, dst, bytes.Length);
        Marshal.WriteInt16(dst + bytes.Length, 0); // null terminator
        return true;
    }

    public static nint CowDataCtor(ReadOnlySpan<byte> buffer) {
        var cowData = Marshal.AllocHGlobal(8 + buffer.Length);
        *(int*) cowData = 1;
        *(int*) (cowData + 4) = buffer.Length;
        buffer.CopyTo(new Span<byte>((void*) (cowData + 8), buffer.Length));
        return cowData + 8;
    }

    public static void CowDataDtor(nint cowData) {
        Marshal.FreeHGlobal(cowData - 8);
    }

    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 8)]
    public struct CowData : IDisposable {
        [FieldOffset(0x0)] public nint Value;

        public int RefCount => this.Value != 0 ? *(int*) (this.Value - 8) : 0;
        public int Size => this.Value != 0 ? *(int*) (this.Value - 4) : 0;

        public static CowData Ctor(ReadOnlySpan<byte> buffer) {
            var cowData = Marshal.AllocHGlobal(8 + buffer.Length);
            *(int*) cowData = 1;                   // Ref count
            *(int*) (cowData + 4) = buffer.Length; // Size
            buffer.CopyTo(new Span<byte>((void*) (cowData + 8), buffer.Length));
            return new CowData { Value = cowData + 8 };
        }

        public void Dispose() {
            Marshal.FreeHGlobal(this.Value - 8);
        }
    }

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct GodotString {
        [FieldOffset(0x0)] public CowData CowData;

        public string Value {
            get {
                if (this.CowData.Value == 0) return "";
                var str = Marshal.PtrToStringUni(this.CowData.Value, this.CowData.Size);
                MemoryUtils.TrimNullTerminator(ref str);
                return str;
            }
        }

        public static GodotString* Ctor(string value) {
            var str = (GodotString*) Marshal.AllocHGlobal(sizeof(GodotString));
            str->CowData = CowData.Ctor(Encoding.Unicode.GetBytes(value + '\0'));
            return str;
        }

        public static void Dtor(GodotString* str) {
            str->CowData.Dispose();
            Marshal.FreeHGlobal((nint) str);
        }
    }

    public class GodotStringWrapper(string value) : IDisposable {
        public readonly GodotString* String = GodotString.Ctor(value);

        public void Dispose() {
            GodotString.Dtor(this.String);
        }
    }

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct GodotVector {
        [FieldOffset(0x8)] public CowData CowData;

        public ReadOnlySpan<byte> Data => this.CowData.Value != 0
            ? new ReadOnlySpan<byte>((void*) this.CowData.Value, this.CowData.Size)
            : ReadOnlySpan<byte>.Empty;

        public static GodotVector* Ctor(ReadOnlySpan<byte> buffer) {
            var vector = (GodotVector*) Marshal.AllocHGlobal(sizeof(GodotVector));
            vector->CowData = CowData.Ctor(buffer);
            return vector;
        }

        public static void Dtor(GodotVector* vector) {
            vector->CowData.Dispose();
            Marshal.FreeHGlobal((nint) vector);
        }
    }

    public class GodotVectorWrapper(ReadOnlySpan<byte> buffer) : IDisposable {
        public readonly GodotVector* Vector = GodotVector.Ctor(buffer);

        public void Dispose() {
            GodotVector.Dtor(this.Vector);
        }
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionPointersStruct {
        public ExceptionRecordStruct* ExceptionRecord;
        // We don't care about context here
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionRecordStruct {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public nint ExceptionRecord;
        public nint ExceptionAddress;
        public uint NumberParameters;
        public fixed uint ExceptionInformation[15];
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint hModule, string lpProcName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hwnd, string text, string caption, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualQuery(nint lpAddress, ref MemoryBasicInformation lpBuffer, nint dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

}
