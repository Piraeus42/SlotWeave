# GDScript Patched 源码语法验证方案

> 2026-06-01 | 提交评定

---

## 问题现状

Mod 开发者通过 `[Prefix]`/`[Postfix]`/`[Replace]` 返回 GDScript 代码字符串时，
需要穿越三层转义：

```
GDScript 源码  →  C# raw string (@"...")  →  Tabify 缩进处理  →  写入引擎内存
```

任一环节出错（少一个引号、括号不匹配、缩进不对齐），结果是：
- `C0000005` Access Violation crash
- **无行号、无错误提示、无堆栈**
- 开发者只能二分注释排查，90% 的 debug 时间在追语法错误

## 方案对比

| 方案 | 准确度 | 实现成本 | 维护成本 | 时间 |
|------|--------|---------|---------|------|
| A. C# 轻量括号/引号检查 | 60% | 极低 | 零 | 已完成 ✅ |
| B. C# GDScript Tokenizer | 95% | 中 | 低 | 2-3 天 |
| C. 调引擎内 Tokenizer (RVA) | 100% | 高 | 中（依赖引擎版本） | 3-5 天 |
| D. EmbeddedResource .gd 文件 | 100% | 低 | 零 | 1 天（框架侧） |

### 推荐：B (短期) + D (长期)

---

## 方案 B: C# GDScript Tokenizer

### 原理

Godot 3.4.4 的 `gdscript_tokenizer.cpp` 约 800 行，是一个确定性的状态机。
将核心逻辑移植到 C# 中，不依赖任何引擎 RVA。

### Token 类型

```csharp
enum GdTokenType {
    // 字面量
    Integer, Float, String, Name,
    // 关键字
    If, Elif, Else, For, While, Match, Break, Continue,
    Return, Pass, Func, Class, Extends, Is, As,  
    Var, Const, Enum, Signal, Static, Tool, Onready,
    // 运算符
    Plus, Minus, Star, Slash, Percent, 
    Equal, NotEqual, Less, Greater, LessEqual, GreaterEqual,
    Assign, AssignPlus, AssignMinus, // ...
    // 分隔符
    ParenOpen, ParenClose, BracketOpen, BracketClose,
    BraceOpen, BraceClose, Comma, Colon, Semicolon, Period,
    // 特殊
    Newline, Indent, Dedent, Eof, Error, Comment, Annotation
}
```

### 检查项

| 检查 | 来源 | 示例 |
|------|------|------|
| 非法字符 | Tokenizer | Line 42: unexpected character '\' |
| 未闭合字符串 | Tokenizer | Line 15: unclosed string literal |
| 多行字符串未闭合 | Tokenizer | Line 8: unclosed multi-line string ("""...""") |
| 缩进不一致 | Tokenizer | Line 33: mixed tabs and spaces for indentation |
| 意外 EOF | Tokenizer | Line 120: unexpected end of file in function body |
| 关键字拼写 | Tokenizer | Line 56: unknown token 'funtcion' (did you mean 'func'?) |

### 集成方式

```csharp
// Hooks.cs — 在 SourceModder 管线之后、WriteGodotString 之前
var tokenizer = new GdTokenizer(modified);
var errors = tokenizer.Validate();

if (errors.Count > 0) {
    foreach (var (line, msg) in errors)
        logger.Warning("#{N} [{Path}] Line {Line}: {Msg}", n, path, line, msg);
}
```

### 文件

| 文件 | 内容 |
|------|------|
| `SlotWeave/Modding/GdTokenizer.cs` | Tokenizer 状态机 + Token 枚举 |
| `SlotWeave/Modding/GdValidator.cs` | 对 token 流做语法检查（括号匹配、结构完整性） |

### 效果

```
# 修改前（当前状态）:
Fatal error. System.AccessViolationException: Attempted to read or write protected memory.
   at ...

# 修改后:
[WARN] #42 [res://Slot Icon.tscn::1] Line 128: unclosed string literal
[WARN] #42 [res://Slot Icon.tscn::1] Line 215: unexpected end of file in function body
  → 不写入引擎内存，回退到原始源码，游戏正常运行
  → 开发者直接看到哪一行有什么问题
```

---

## 方案 D: EmbeddedResource .gd 文件

### 原理

Mod 开发者在项目中创建 `.gd` 文件，设置为 `EmbeddedResource`。
编译时嵌入 DLL，运行时读出内容传给 Patch API。彻底消灭字符串转义。

### Mod 项目结构

```
MyMod/
├── MyMod.csproj
├── Mod.cs
└── Patches/
    ├── Patch_Damage.cs
    ├── gd/
    │   ├── damage_prefix.gd      ← EmbeddedResource
    │   ├── coins_replace.gd      ← EmbeddedResource
    │   └── spinner_postfix.gd    ← EmbeddedResource
    └── ...
```

### GDScript 文件（纯文本，无转义）

```gdscript
# Patches/gd/coins_replace.gd
var coins = get_node("/root/Main/Coins")
if coins.coins >= 999:
    coins.coins = 999
    print("Max coins reached!")
}
```

### C# 侧 API

```csharp
using SlotWeave.Scripting;

[Patch("res://Main.tscn::1", "get_coins")]
static class CoinsPatch
{
    [Replace]
    static string ReplaceCode(string original)
        => EmbeddedGd.Read("Patches.gd.coins_replace.gd");  // 一行，零转义
}
```

### 框架侧实现

```csharp
// SlotWeave/Scripting/EmbeddedGd.cs
public static class EmbeddedGd
{
    public static string Read(string resourceName)
    {
        var assembly = Assembly.GetCallingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException($"Embedded .gd not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

就 12 行代码。

### 优势

- **零转义**: .gd 文件就是纯 GDScript，VS Code / Rider 自带语法高亮
- **可测试**: 可以直接用 Godot 编辑器打开 .gd 文件验证语法
- **可 Diff**: Git 里看到的是 GDScript 而不是 C# string
- **向后兼容**: 和现有的 `static string Code()` 方法并存，不影响旧 Mod

---

## 实施计划

| 阶段 | 内容 | 时间 | 依赖 |
|------|------|------|------|
| ✅ Phase 0 | 括号/引号轻量检查 + 重载循环断路器 | 已完成 | — |
| 🔲 Phase 1 | C# GDScript Tokenizer (GdTokenizer.cs) | 2-3 天 | — |
| 🔲 Phase 2 | EmbeddedGd API + Mod 模板 | 1 天 | — |
| 🔲 Phase 3 | 调引擎内 GDScriptParser (可选) | 3-5 天 | 找到 Parser RVA |

## 建议

**Phase 1 + 2 同时推进。** Tokenizer 解决"出了错能立刻看到行号"的痛点，
EmbeddedResource 解决"永远不会再写出错代码"的根因。
两者互补，覆盖所有场景。
