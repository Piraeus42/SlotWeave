# SlotWeave Mod Development Guide

## Patching Mechanisms

SlotWeave provides two mechanisms for modifying GDScript at load time:

| Mechanism | Granularity | Sees | Best for |
|-----------|-------------|------|----------|
| `[Patch]` attribute | Function-level (Prefix, Postfix, Replace) | Original source only | Targeted function modifications, insertions |
| `ISourceMod` | Full source string | Output of previous ISourceMods | Broad string replacements, helper injection |

**Execution order**: `[Patch]` always runs before all `ISourceMod` instances.

**Critical rule**: All `[Patch]` Replace methods see the **original** source — they cannot compose on each other's output. If you need sequential transformations, use `ISourceMod` with dependency ordering.

---

## ISourceMod Best Practices

### 1. Use Framework Sentinel for Idempotent Guards

The framework now provides automatic double-injection protection via the `Sentinel` property.

❌ **Before** (manual guard, easy to forget):
```csharp
public class MyMod : ISourceMod {
    public string Modify(string path, string source) {
        if (source.Contains("func _my_helper")) return source;  // fragile
        // ... do work ...
    }
}
```

✅ **After** (framework-managed):
```csharp
public class MyMod : ISourceMod {
    public string? Sentinel => "func _my_helper";  // framework checks automatically

    public string Modify(string path, string source) {
        // No manual guard needed — SourceModder skips if sentinel is present
        // ... do work ...
    }
}
```

**Choosing a sentinel**: Pick a string your mod injects that would not appear in the vanilla game source. A unique helper function name is ideal (e.g. `"func _mymod_rng_shuffle"`).

### 2. Use ReplaceHelper for Safe Function Renaming

Plain `String.Replace` is unsafe because the replacement output can contain the original pattern as a substring:

```csharp
// ❌ BUG: _my_rand_range contains "rand_range" → double-replaced
source = source.Replace("rand_range(", "_my_rand_range(");
source = source.Replace("shuffle(",   "_my_shuffle(");    // corrupts the first replacement!
```

✅ Use `ReplaceHelper.ReplaceCall` which uses regex `\b` word boundaries:

```csharp
// ✅ SAFE: \b ensures _my_rand_range is NOT re-matched
source = ReplaceHelper.ReplaceCall(source, "rand_range", "_my_rng_range");
source = ReplaceHelper.ReplaceCall(source, "shuffle",    "_my_shuffle");
```

**How it works**: The regex `\b` (word boundary) treats `_` as a word character. `\brand_range\(` matches `rand_range(` but NOT the `rand_range(` substring inside `_my_rand_range(` — because there is no word boundary between `_` and `r`.

### 3. Order: Specific First, Then Generic

When you need both specific and generic replacements, do the specific ones first:

```csharp
public string Modify(string path, string source) {
    // ① Specific replacements first
    source = source.Replace("floor(rand_range(-1, 2))", "floor(_my_specific_rng(-1, 2))");

    // ② Generic replacements second (safe because specific already matched)
    source = ReplaceHelper.ReplaceCall(source, "rand_range", "_my_rng_range");

    // ③ Append helpers at the end
    source = ReplaceHelper.AppendCode(source, HelperFunctions);
    return source;
}
```

Even better: prefer `ReplaceCall` over `String.Replace` for all function-call renames.

### 4. Append Code Cleanly

```csharp
source = ReplaceHelper.AppendCode(source, @"
# === MyMod RNG Helpers ===
func _my_rng_shuffle(arr):
    var n = arr.size()
    for i in range(n - 1, 0, -1):
        var j = _my_rng_randi() % (i + 1)
        var temp = arr[i]
        arr[i] = arr[j]
        arr[j] = temp

func _my_rng_range(from_val, to_val):
    return from_val + _my_rng_randf() * (to_val - from_val)
");
```

### 5. Declare Priority When Order Matters

```csharp
public class MyMod : ISourceMod {
    public int Priority => 10;  // runs before default-priority (0) mods

    // If your mod depends on helpers injected by another mod,
    // use a lower priority (or negative) to run after them:
    // public int Priority => -5;
}
```

Higher values run earlier. `[Patch]` always runs first, regardless of priority.

---

## [Patch] and ISourceMod Interaction

**Do not mix the two mechanisms for the same transformation in a single mod.**

❌ **Anti-pattern** — [Patch] and ISourceMod doing overlapping work:
```csharp
// [Patch] replaces rand_range → _lr_rand_range in get_fine_print()
// ISourceMod ALSO replaces rand_range → _lr_rand_range globally
// AND injects the helper function
// → The helper may not exist yet when [Patch] runs!
```

✅ **Pattern A** — ISourceMod only:
```csharp
public class RngRedirectMod : ISourceMod {
    public string? Sentinel => "func _lr_rng_shuffle";
    public bool ShouldRun(string path) => path == "res://Landlord.tscn::9";

    public string Modify(string path, string source) {
        source = ReplaceHelper.ReplaceCall(source, "rand_range", "_lr_rng_range");
        source = ReplaceHelper.ReplaceCall(source, "shuffle",    "_lr_rng_shuffle");
        source = ReplaceHelper.AppendCode(source, RngHelpers.GetCode());
        return source;
    }
}
```

✅ **Pattern B** — [Patch] only (for targeted function modifications):
```csharp
[Patch("res://Main.tscn::4", "_process")]
static class ProcessPatch {
    [Prefix]
    static string Code() => "if frame_count % 60 == 0: _my_diagnostic()";
}
```

✅ **Pattern C** — Both, with clean separation:
```csharp
// [Patch] does structured insertion (Prefix/Postfix on specific functions)
// ISourceMod does global string replacement (RNG redirect, helper injection)
// They handle DIFFERENT concerns, not the same one.
```

---

## Pipeline Diagnostics

### Tokenizer validation

Each mod's output is checked with GdTokenizer. If an intermediate state produces a syntax error, a **warning** is logged (but execution continues — the intermediate state may be intentionally incomplete and fixed by a later mod). Final output validation in Hooks.cs remains the hard enforcement point.

### Debug environment variables

| Variable | Effect |
|----------|--------|
| `GDWEAVE_DEBUG` | Verbose logging |
| `GDWEAVE_DUMP_SOURCE` | Write original scripts to `SlotWeave/scripts/` |
| `GDWEAVE_DUMP_PATCHED` | Write patched scripts with provenance annotations |
| `GDWEAVE_STRICT_SANDBOX` | Refuse to write scripts with syntax errors |
| `GDWEAVE_NO_CACHE` | Disable the patch cache |

---

## Migration Checklist (for existing mods)

- [ ] Remove manual `if (source.Contains(...)) return source;` guards
- [ ] Add `public string? Sentinel => "...";` with a unique marker string
- [ ] Replace `source.Replace("func_name(", "_prefix_func_name(")` with `ReplaceHelper.ReplaceCall(source, "func_name", "_prefix_func_name")`
- [ ] Add `public int Priority => N;` if ordering relative to other mods matters
- [ ] Verify your mod still works with `GDWEAVE_DEBUG=1 GDWEAVE_NO_CACHE=1`
