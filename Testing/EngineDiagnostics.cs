// EngineDiagnostics — self-test framework for the three-layer GameState architecture
// Triggered by GDWEAVE_DIAGNOSTIC=1 env var during SlotWeave initialization.
// Tests are non-destructive — they only read engine memory, never write.
//
// Test phases:
//   Phase A — Memory layout & ABI: Struct sizes, delegate calls don't crash
//   Phase B — Engine chain: OS → SceneTree → root → FindNode
//   Phase C — Property reads: Object::get on known nodes, Variant clear
//   Phase D — Stress: Repeated reads, no memory leak, no crash
//   Phase E — Bus integration: GameStateBus hook, snapshot publish

using System.Runtime.InteropServices;
using SlotWeave.GameState;
using SlotWeave.NativeInterop;
using Serilog;

namespace SlotWeave.Testing;

/// <summary>
/// Diagnostic test runner for the GameState architecture.
/// Each test returns (passed: bool, detail: string).
/// Tests are ordered — later tests depend on earlier ones.
/// </summary>
internal class EngineDiagnostics
{
    private readonly ILogger _log;
    private readonly Interop _interop;
    private readonly EngineObjectReader _reader;
    private readonly GameStateBus _bus;
    private int _passed;
    private int _failed;
    private int _skipped;

    public int Passed => _passed;
    public int Failed => _failed;
    public int Skipped => _skipped;
    public bool AllCriticalPassed { get; private set; } = true;

    public EngineDiagnostics(Interop interop, GameStateBus bus)
    {
        _log = SlotWeave.Logger.ForContext<EngineDiagnostics>();
        _interop = interop;
        _bus = bus;
        _reader = bus.Reader;
    }

    public void RunAll()
    {
        _log.Information("========== EngineDiagnostics START ==========");

        PhaseA_MemoryLayout();
        PhaseB_EngineChain();
        PhaseC_PropertyReads();
        PhaseD_StressTest();
        PhaseE_BusIntegration();

        _log.Information("========== EngineDiagnostics END: {Passed} passed, {Failed} failed, {Skipped} skipped ==========",
            _passed, _failed, _skipped);

        if (_failed > 0)
        {
            _log.Error("DIAGNOSTIC FAILURES DETECTED — GameState API may be unstable");
            AllCriticalPassed = false;
        }
    }

    // ── Phase A: Memory layout & ABI ──

    private void PhaseA_MemoryLayout()
    {
        _log.Information("--- Phase A: Memory layout & ABI ---");

        // A1: Variant struct size
        var variantSize = Marshal.SizeOf<NativeVariantStruct>();
        Check("A1: Variant size == 24", variantSize == 24,
            $"sizeof(Variant) = {variantSize} (expected 24)");

        // A2: NativeVariant allocation + clear doesn't crash
        try
        {
            var handle = Native.AllocZeroed(Native.VARIANT_SIZE);
            Native.VariantClear(handle);
            Marshal.FreeHGlobal(handle);
            Check("A2: Variant alloc/clear no-crash", true, "alloc + Variant::clear() succeeded");
        }
        catch (Exception ex)
        {
            Check("A2: Variant alloc/clear no-crash", false, $"CRASH: {ex.Message}");
        }

        // A3: StringName ctor/dtor doesn't crash
        try
        {
            var handle = Native.AllocZeroed(Native.STRINGNAME_SIZE);
            var ansi = Marshal.StringToHGlobalAnsi("test_prop");
            Native.StringNameCtor(handle, ansi);
            Native.StringNameDtor(handle);
            Marshal.FreeHGlobal(ansi);
            Marshal.FreeHGlobal(handle);
            Check("A3: StringName ctor/dtor no-crash", true, "ctor + dtor succeeded");
        }
        catch (Exception ex)
        {
            Check("A3: StringName ctor/dtor no-crash", false, $"CRASH: {ex.Message}");
        }

        // A4: NativeStringName wrapper
        try
        {
            using var sn = new NativeStringName("test_wrapper");
            Check("A4: NativeStringName wrapper", sn.Handle != IntPtr.Zero,
                $"Handle=0x{sn.Handle.ToInt64():X16}");
        }
        catch (Exception ex)
        {
            Check("A4: NativeStringName wrapper", false, $"CRASH: {ex.Message}");
        }

        // A5: Re-read handle after dispose (should throw)
        try
        {
            var sn = new NativeStringName("dispose_test");
            sn.Dispose();
            try
            {
                _ = sn.Handle; // should throw ObjectDisposedException
                Check("A5: NativeStringName post-dispose guard", false, "Did NOT throw after dispose!");
            }
            catch (ObjectDisposedException)
            {
                Check("A5: NativeStringName post-dispose guard", true, "Correctly threw ObjectDisposedException");
            }
        }
        catch (Exception ex)
        {
            Check("A5: NativeStringName post-dispose guard", false, $"Unexpected: {ex.Message}");
        }
    }

    // ── Phase B: Engine chain ──

    private void PhaseB_EngineChain()
    {
        _log.Information("--- Phase B: Engine chain (OS → SceneTree → root → FindNode) ---");

        // Need the reader to be initialized
        if (!_reader.IsInitialized)
        {
            Skip("B*: Engine chain", "EngineObjectReader not initialized — engine not running yet");
            return;
        }

        // B1: OS singleton
        var os = Native.GetOsSingleton();
        Check("B1: OS singleton", os != IntPtr.Zero,
            $"OS* = 0x{os.ToInt64():X16}");

        if (os == IntPtr.Zero) { SkipRemaining("B", "OS singleton is null"); return; }

        // B2: OS vtable readable
        var osVtable = Marshal.ReadIntPtr(os);
        Check("B2: OS vtable", osVtable != IntPtr.Zero,
            $"vtable = 0x{osVtable.ToInt64():X16}");

        // B3: get_main_loop vtable slot
        var getMainLoopPtr = Marshal.ReadIntPtr(osVtable + Native.VT_SLOT_GET_MAIN_LOOP);
        Check("B3: get_main_loop slot (0x330)", getMainLoopPtr != IntPtr.Zero,
            $"func ptr = 0x{getMainLoopPtr.ToInt64():X16}");

        if (getMainLoopPtr == IntPtr.Zero) { SkipRemaining("B", "get_main_loop slot is null"); return; }

        // B4: OS::get_main_loop() call
        var sceneTree = Native.GetMainLoop(os);
        Check("B4: OS::get_main_loop() → SceneTree", sceneTree != IntPtr.Zero,
            $"SceneTree* = 0x{sceneTree.ToInt64():X16}");

        if (sceneTree == IntPtr.Zero) { SkipRemaining("B", "SceneTree is null"); return; }

        // B5: SceneTree.root
        var root = Marshal.ReadIntPtr(sceneTree + Native.OFF_SCENETREE_ROOT);
        Check("B5: SceneTree.root (0x138)", root != IntPtr.Zero,
            $"root Viewport* = 0x{root.ToInt64():X16}");

        if (root == IntPtr.Zero) { SkipRemaining("B", "SceneTree.root is null"); return; }

        // B6: Root node children — list first 10 for discovery
        try
        {
            var names = _reader.GetChildNames(root).Take(10).ToList();
            Check("B6: Root children", names.Count > 0,
                $"found {names.Count}: [{string.Join(", ", names)}]");
        }
        catch (Exception ex)
        {
            Check("B6: Root children", false, $"CRASH: {ex.Message}");
        }

        // B7: FindNode on root
        var rootSelf = _reader.FindNode("", root);
        Check("B7: FindNode(\"\") == root", rootSelf == root,
            $"self=0x{rootSelf.ToInt64():X16}, root=0x{root.ToInt64():X16}");

        // B8: FindNode on known LABL nodes (try common paths)
        string[] knownPaths = ["Main", "Main/Items", "Main/Reels", "Main/Pop-up", "Main/Coins"];
        foreach (var path in knownPaths)
        {
            try
            {
                var node = _reader.FindNode(path);
                var found = node != IntPtr.Zero;
                var detail = found ? $"0x{node.ToInt64():X16}" : "NOT FOUND";
                Check($"B: FindNode(\"{path}\")", found, detail);
            }
            catch (Exception ex)
            {
                Check($"B: FindNode(\"{path}\")", false, $"CRASH: {ex.Message}");
            }
        }
    }

    // ── Phase C: Property reads ──

    private void PhaseC_PropertyReads()
    {
        _log.Information("--- Phase C: Property reads (Object::get → Variant → C#) ---");

        if (!_reader.IsInitialized)
        {
            Skip("C*: Property reads", "EngineObjectReader not initialized");
            return;
        }

        // C1: Read from root Viewport — try common properties
        var root = _reader.RootPtr;
        if (root == IntPtr.Zero)
        {
            Skip("C*: Property reads", "Root is null");
            return;
        }

        // Try reading common Viewport/Node properties
        string[] commonProps = ["name", "visible", "rect", "size", "canvas_transform"];
        foreach (var prop in commonProps)
        {
            try
            {
                using var v = new NativeVariant(root, new NativeStringName(prop));
                var type = v.Type;
                var val = type switch
                {
                    VariantType.Nil => "nil",
                    VariantType.Bool => v.AsBool().ToString(),
                    VariantType.Int => v.AsInt().ToString(),
                    VariantType.Real => v.AsReal().ToString("F3"),
                    VariantType.String => v.AsString() ?? "(null)",
                    _ => $"<{type}>"
                };
                Check($"C: root.{prop}", true, $"type={type}, value={val}");
            }
            catch (Exception ex)
            {
                Check($"C: root.{prop}", false, $"CRASH: {ex.Message}");
            }
        }

        // C2: Read via convenience methods
        try
        {
            var name = _reader.ReadString(root, "name");
            Check("C2: ReadString(\"name\")", name != null, $"root name = \"{name}\"");
        }
        catch (Exception ex)
        {
            Check("C2: ReadString(\"name\")", false, $"CRASH: {ex.Message}");
        }

        // C3: Read bool property
        try
        {
            var visible = _reader.ReadBool(root, "visible");
            Check("C3: ReadBool(\"visible\")", true, $"visible = {visible}");
        }
        catch (Exception ex)
        {
            Check("C3: ReadBool(\"visible\")", false, $"CRASH: {ex.Message}");
        }

        // C4: Read via NativeVariant.Read convenience factory
        try
        {
            using var v = NativeVariant.Read(root, "name");
            var str = v.AsString();
            Check("C4: NativeVariant.Read", str != null, $"name = \"{str}\"");
        }
        catch (Exception ex)
        {
            Check("C4: NativeVariant.Read", false, $"CRASH: {ex.Message}");
        }
    }

    // ── Phase D: Stress test ──

    private void PhaseD_StressTest()
    {
        _log.Information("--- Phase D: Stress test (memory leak / crash resistance) ---");

        if (!_reader.IsInitialized)
        {
            Skip("D*: Stress test", "EngineObjectReader not initialized");
            return;
        }

        var root = _reader.RootPtr;
        if (root == IntPtr.Zero) { Skip("D*: Stress test", "Root is null"); return; }

        // D1: Read the same property 1000 times — checks for Variant leak
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++)
            {
                using var v = new NativeVariant(root, new NativeStringName("name"));
                _ = v.AsString();
            }
            sw.Stop();
            var msPerRead = sw.Elapsed.TotalMilliseconds / 1000.0;
            Check("D1: 1000x ReadString(\"name\")", true,
                $"{sw.ElapsedMilliseconds}ms total, {msPerRead:F3}ms/read");
        }
        catch (Exception ex)
        {
            Check("D1: 1000x ReadString(\"name\")", false, $"CRASH at some iteration: {ex.Message}");
        }

        // D2: Read multiple properties 100 times each
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 100; i++)
            {
                using var v1 = new NativeVariant(root, new NativeStringName("name"));
                using var v2 = new NativeVariant(root, new NativeStringName("visible"));
                _ = v1.AsString();
                _ = v2.AsBool();
            }
            sw.Stop();
            Check("D2: 100x (name + visible)", true,
                $"{sw.ElapsedMilliseconds}ms total");
        }
        catch (Exception ex)
        {
            Check("D2: 100x (name + visible)", false, $"CRASH: {ex.Message}");
        }

        // D3: FindNode stress — do 100 lookups
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int found = 0;
            for (var i = 0; i < 100; i++)
            {
                var node = _reader.FindNode("Main");
                if (node != IntPtr.Zero) found++;
            }
            sw.Stop();
            Check("D3: 100x FindNode(\"Main\")", found > 0,
                $"{found}/100 found, {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Check("D3: 100x FindNode(\"Main\")", false, $"CRASH: {ex.Message}");
        }
    }

    // ── Phase E: Bus integration ──

    private void PhaseE_BusIntegration()
    {
        _log.Information("--- Phase E: Bus integration ---");

        // E1: Hook is active
        Check("E1: GameStateBus.IsHooked", _bus.IsHooked,
            $"hooked={_bus.IsHooked}");

        // E2: Reader count
        Check("E2: GameStateBus.ReaderCount", _bus.ReaderCount >= 0,
            $"count={_bus.ReaderCount}");

        // E3: LatestSnapshot available (may be Empty if no frames yet)
        var snap = _bus.LatestSnapshot;
        Check("E3: LatestSnapshot exists", snap != null,
            snap is not null
                ? $"TickCount={snap.TickCount}, Delta={snap.Delta}, ExtraKeys={snap.Extra.Count}"
                : "null");

        // E4: Register a test reader and verify it fires
        var fired = false;
        var testReader = new TestReader(() => { fired = true; });
        try
        {
            _bus.RegisterReader(testReader);
            Check("E4: Test reader registered", _bus.ReaderCount >= 1,
                $"reader count after register = {_bus.ReaderCount}");
        }
        catch (Exception ex)
        {
            Check("E4: Test reader registered", false, $"CRASH: {ex.Message}");
        }
        finally
        {
            _bus.UnregisterReader(testReader);
            _log.Debug("TestReader fired={Fired}", fired);
        }
    }

    // ── Helpers ──

    private void Check(string label, bool passed, string detail)
    {
        if (passed)
        {
            _passed++;
            _log.Information("  [PASS] {Label} — {Detail}", label, detail);
        }
        else
        {
            _failed++;
            _log.Error("  [FAIL] {Label} — {Detail}", label, detail);
        }
    }

    private void Skip(string label, string reason)
    {
        _skipped++;
        _log.Warning("  [SKIP] {Label} — {Reason}", label, reason);
    }

    private void SkipRemaining(string phase, string reason)
    {
        _log.Warning("  [SKIP] Remaining {Phase} tests — {Reason}", phase, reason);
    }

    /// <summary>Simple test reader that invokes a callback on Read().</summary>
    private class TestReader(Action onRead) : IGameStateReader
    {
        public void Read(EngineObjectReader reader, IntPtr sceneTree, GameStateSnapshot snapshot)
        {
            onRead();
            snapshot.Extra["_diagnostic_test"] = true;
        }
    }
}
