# IDA Analysis Results — Luck be a Landlord (Godot 3.4.4)

> **目标二进制**: `D:\steam\steamapps\common\Luck be a Landlord\Luck be a Landlord.exe`
> **IDA 会话**: `e88c0cb6`
> **镜像基址**: `0x140000000`
> **架构**: x64, MSVC 编译, stripped
> **分析日期**: 2026-05-31

---

## ✅ 全部 9 个目标的 RVA/偏移 (最终成果)

| # | 目标 | 绝对地址 | RVA | 用途 |
|---|------|---------|-----|------|
| 1 | **SceneTree::idle()** | `0x140787930` | `0x787930` | GameStateBus hook 注入点 |
| 2 | **OS singleton (全局变量)** | `0x142048AB8` | `0x2048AB8` | 获取 OS* → get_main_loop() → SceneTree* |
| 3 | **SceneTree.root** (Viewport*) | — | `0x138` | 获取场景根节点 |
| 3 | **SceneTree._quit** (bool) | — | `0x170` | idle() 返回值 |
| 4 | **Node.data.parent** (Node*) | — | `0xF0` | 遍历父节点链 |
| 4 | **Node.data.children** (Vector) | — | `0x108` | 读取子节点列表 (CowData ptr) |
| 4 | **Node.data.name** (StringName) | — | `0x120` | 匹配节点名称 |
| 5 | **OS::get_main_loop() vtable slot** | — | **slot 0x330** (index 102) | 从 OS* 获取 MainLoop* (即 SceneTree*) |
| 6 | **Object::get()** | `0x14081F980` | `0x81F980` | 读属性 Variant (**sret**: rcx=ret,rdx=this,r8=&name,r9=valid) |
| 7 | **StringName::StringName(const char*)** | `0x1414AA130` | `0x14AA130` | 构造属性名 (djb2 hash + StringName pool) |
| 8 | **StringName::~StringName()** | `0x1414A9DB0` | `0x14A9DB0` | 释放属性名 (refcount-- → 可能 free) |
| 9 | **Variant::clear()** | `0x141513D20` | `0x1513D20` | 释放读出的 Variant (23-case switch → type=NIL) |
| + | **Node::get_node_or_null()** | `0x1408086A0` | `0x8086A0` | 路径解析后备 (C# 直接遍历更好) |
| + | **OS::get_singleton()** | **内联** | — | MSVC release 内联为直接读全局变量 `[0x2048AB8]` |
| + | **StringName 比较** | — | **指针相等** | `cmp [node+0x120], searchName` — 比较 _Data* |

### Hook 插入点
- **idle() hook**: `0x140788068` — 在 `_call_idle_callbacks` 循环之后, `_quit` 读取之前
  ```asm
  140788068: movzx eax, byte ptr [r14+170h]  ; ← 替换为 JMP hook (8字节需trampoline)
  ```
- 此时所有 `_process()` / `_physics_process()` 已完成, game state 一致

---

## 📐 结构体布局 (MSVC x64)

### Variant (24 bytes = GODOT_VARIANT_SIZE)
```
+0x00: Type type (4 bytes, 0=NIL..27=VARIANT_MAX)
+0x04: (padding, 4 bytes)
+0x08: union _data (16 bytes, GCC_ALIGNED_8)
```

### CowData<T> (仅 8 bytes = T* _ptr)
```
_ptr == null → 空 Vector
_ptr != null:
  [ptr - 8]: refcount (uint32_t)
  [ptr - 4]: size     (uint32_t)
  [ptr]:     data[0..size-1]
```

### SceneTree 关键成员
| 偏移 | 类型 | 用途 |
|------|------|------|
| `0x138` | Viewport* | **root** (get_root) |
| `0x14C` | float | idle_process_time |
| `0x154` | int | **root_lock** |
| `0x158` | Map | group_map |
| `0x170` | bool | **_quit** (idle返回) |
| `0x174` | Size2 | last_screen_size |
| `0x288` | List | idle callbacks / timers |

### Node 关键成员 (从 Node* 指针开始)
| 偏移 | 类型 | 用途 |
|------|------|------|
| `0xF0` | Node* | **data.parent** |
| `0x108` | CowData<Node*> | **data.children** (Vector) |
| `0x120` | _Data* | **data.name** (StringName) |

---

## 🔩 C# Delegate 签名

```csharp
// Variant::clear(this: Variant*) — RVA 0x1513D20
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
delegate void VariantClearDelegate(IntPtr variant);

// Object::get(ret: Variant*, this: Object*, prop: StringName*, valid: bool*)
// sret: rcx=ret_ptr, rdx=this, r8=&prop, r9=valid — RVA 0x81F980
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
delegate void ObjectGetDelegate(IntPtr ret, IntPtr obj, IntPtr prop, IntPtr valid);

// StringName::StringName(this: StringName*, cstr: const char*)
// 2 params: rcx=this, rdx=cstr — RVA 0x14AA130
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
delegate void StringNameCtorDelegate(IntPtr pThis, IntPtr pCString);

// StringName::~StringName(this: StringName*) — RVA 0x14A9DB0
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
delegate void StringNameDtorDelegate(IntPtr pThis);

// 直接读取 OS singleton (无需 delegate)
// nint osSingleton = ReadIntPtr(baseAddr + 0x2048AB8);
// nint sceneTree = vtable_call(osSingleton, <get_main_loop_slot>);
```

### 从 SceneTree 获取 root 路径的完整调用链 (纯 C# 内存读取)

```csharp
// Step 0: 获取基址
nint baseAddr = Process.GetCurrentProcess().MainModule.BaseAddress;

// Step 1: 读取 OS singleton
nint osSingleton = Marshal.ReadIntPtr(baseAddr + 0x2048AB8);

// Step 2: OS::get_main_loop() → vtable call
nint osVtable = Marshal.ReadIntPtr(osSingleton);
nint getMainLoop = Marshal.ReadIntPtr(osVtable + MAIN_LOOP_VT_SLOT);
nint mainLoop = call_virtual(osSingleton, getMainLoop);  // 返回 MainLoop*

// Step 3: 直接强转为 SceneTree* (LABL 中 main loop 必定是 SceneTree)
// Step 4: 读取 SceneTree.root
nint root = Marshal.ReadIntPtr(mainLoop + 0x138);

// Step 5: 遍历子节点
nint childrenPtr = Marshal.ReadIntPtr(root + 0x108);  // CowData<Node*>::_ptr
if (childrenPtr != IntPtr.Zero) {
    uint size = ReadUInt32(childrenPtr - 4);           // CowData size
    for (int i = 0; i < size; i++) {
        nint child = Marshal.ReadIntPtr(childrenPtr + i * 8);
        nint nameData = Marshal.ReadIntPtr(child + 0x120); // child->data.name
        // ... compare StringName _Data* or dereference for name string
    }
}
```

---

## 🔑 关键全局变量

| 变量 | 绝对地址 | RVA | 用途 |
|------|---------|-----|------|
| OS singleton | `0x142048AB8` | `0x2048AB8` | OS::singleton → OS* |
| StringName _table | `0x142048B20` | `0x2048B20` | StringName 全局 hash 表 |
| StringName mutex | `0x1420516D0` | `0x20516D0` | StringName 线程安全锁 |
| configured flag | `0x142047B77` | `0x2047B77` | StringName 系统是否已初始化 |

---

## 🔧 Variant 类型枚举 (Godot 3.4)

| 值 | 符号 | 在 clear() 中 |
|----|------|-------------|
| 0 | NIL | default → 无清理 |
| 1 | BOOL | default → 无清理 |
| 2 | INT | default → 无清理 |
| 3 | REAL | default → 无清理 |
| **4** | **STRING** | String 析构 |
| 5 | VECTOR2 | default |
| 6 | RECT2 | default |
| 7 | VECTOR3 | default |
| **8** | **TRANSFORM2D** | memdelete |
| 9 | PLANE | default |
| 10 | QUAT | default |
| **11** | **AABB** | memdelete |
| **12** | **BASIS** | memdelete |
| **13** | **TRANSFORM** | memdelete |
| 14 | COLOR | default |
| **15** | **NODE_PATH** | NodePath 析构 |
| 16 | _RID | default |
| **17** | **OBJECT** | ObjectRC::unref |
| **18** | **DICTIONARY** | Dictionary 析构 |
| **19** | **ARRAY** | Array 析构 |
| **20-26** | **POOL_*_ARRAY** | PoolVector 析构 |
| 27 | VARIANT_MAX | — |

**关键**: 只有加粗的类型在 `clear()` 中有堆清理逻辑。调用 `Object::get()` 获取属性后, 必须调用 `Variant::clear()` 释放, 否则会泄漏引用计数。

---

## 验证记录

| 步骤 | 目标 | 方法 | 状态 |
|------|------|------|------|
| 1 | 打开二进制 | `idalib_open` | ✅ |
| 2 | 预热 | `idalib_warmup` (Hex-Rays + caches) | ✅ |
| 3 | 二进制概览 | `survey_binary` → 71131 funcs, Godot引擎确认 | ✅ |
| 4 | idle() 定位 | `"idle_frame"` xref → `sub_140787930` | ✅ |
| 5 | idle() 验证 | 包含 "idle_process_internal" / "screen_resized" / "timeout" | ✅ |
| 6 | SceneTree 构造函数 | `"node_added"` xref → `sub_140790AE0` | ✅ |
| 7 | root 偏移 | 构造函数汇编: `mov [r14+138h], rbx` + `"root"` string | ✅ |
| 8 | _quit 偏移 | 构造函数: `mov word [r14+170h], 0`; idle: `movzx [r14+170h]` | ✅ |
| 9 | Node 偏移 | `"Can't use get_node"` xref → `sub_1408086A0` (get_node_or_null) | ✅ |
| 10 | Variant::clear() | switch 23 cases + `mov [rbx], edi` (type=NIL) → `sub_141513D20` | ✅ |
| 11 | StringName ctor | `_scs_create` with 5381 hash + mutex → `sub_1414AA130` | ✅ |
| 12 | StringName dtor | Post-construction cleanup → `sub_1414A9DB0` | ✅ |
| 13 | Object::get() | `"__meta__"` xref → `sub_14081F980` (sret 由 ABI 保证) | ✅ |
| 14 | get_main_loop() slot | `call [rax+330h]` → vtable index 102 | ✅ |
| 15 | StringName 比较 | `cmp [rax+120h], rdi` → 指针相等 | ✅ |
| 16 | OS::get_singleton() | 被 MSVC release 完全内联为直接读全局变量 | ✅ |
| 17 | Object::script_instance 偏移 | `[rbx+58h]` = ScriptInstance* → Object 源码确证 | ✅ |
| 18 | ScriptInstance::get vtable slot | **slot 1 (offset +0x08)** — set=slot0, get=slot1 | ✅ |
| 19 | GDScriptInstance 布局 | owner@0x08, script@0x10, members@0x18(DBG off)/0x28(DBG on) | ⚠️ 待运行时验证 |

---

## 🔗 最终 C# 调用链

```csharp
// === 直接从全局变量获取 OS*（无函数调用） ===
nint baseAddr = Process.GetCurrentProcess().MainModule.BaseAddress;
nint os = Marshal.ReadIntPtr(baseAddr + 0x2048AB8);

// === OS::get_main_loop() via vtable slot 0x330 ===
nint osVtable = Marshal.ReadIntPtr(os);
nint getMainLoopPtr = Marshal.ReadIntPtr(osVtable + 0x330);
// [call getMainLoopPtr(os)] → 返回 MainLoop* = SceneTree*

// === 读取 SceneTree.root ===
nint root = Marshal.ReadIntPtr(sceneTree + 0x138);

// === 纯 C# 路径遍历（不需要 get_node_or_null） ===
nint FindNode(nint from, string path) {
    foreach (string part in path.Split('/')) {
        nint childrenPtr = Marshal.ReadIntPtr(from + 0x108);
        if (childrenPtr == IntPtr.Zero) return 0;
        uint count = ReadUInt32(childrenPtr - 4);  // CowData size
        
        // 构造 StringName
        nint sn = AllocStack(8);
        stringNameCtor(sn, Marshal.StringToHGlobalAnsi(part));
        nint targetData = Marshal.ReadIntPtr(sn);
        
        bool found = false;
        for (uint i = 0; i < count; i++) {
            nint child = Marshal.ReadIntPtr(childrenPtr + i * 8);
            // StringName 比较 = 指针相等!
            if (Marshal.ReadIntPtr(child + 0x120) == targetData) {
                from = child; found = true; break;
            }
        }
        stringNameDtor(sn);
        if (!found) return 0;
    }
    return from;
}

// === 读属性 ===
// Object::get(Variant* ret, Object* this, StringName* prop, bool* valid)
// sret: rcx=ret, rdx=this, r8=&prop, r9=valid
Variant ReadProp(nint node, string name) {
    nint sn = AllocStack(8);
    stringNameCtor(sn, namePtr);
    Variant v;
    objectGet(&v, node, sn, IntPtr.Zero);  // r_valid = null
    stringNameDtor(sn);
    return v;
}
// 用完后必须: variantClear(&v);
```

---

## 🔬 GDScriptInstance 直接内存读取（Plan B/C）

### 不从函数调用，纯 C# 偏移读 Variant 数组

```
Node* → [+0x58] → ScriptInstance* (GDScriptInstance*)
      → [+0x18] → members (CowData<Variant> ptr) — 仅 8 字节!
      → *(uint32_t*)(_ptr - 4) = members.size()
      → _ptr[i * 24] = Variant #i (24 bytes each)
```

### GDScriptInstance 布局 (两种可能)

**如果 DEBUG_ENABLED=OFF（Release 构建）**:
```
+0x00: ScriptInstance vtable    (8 bytes)
+0x08: Object *owner            (8 bytes)
+0x10: Ref<GDScript> script     (8 bytes = RefPtr)
+0x18: Vector<Variant> members  (8 bytes = CowData<Variant>::_ptr)
+0x20: bool base_ref            (1 byte)
```

**如果 DEBUG_ENABLED=ON**:
```
+0x00: ScriptInstance vtable           (8 bytes)
+0x08: Object *owner                   (8 bytes)
+0x10: Ref<GDScript> script            (8 bytes)
+0x18: Map<StringName,int> cache       (~16 bytes: root ptr + size)
+0x28: Vector<Variant> members         (8 bytes)
+0x30: bool base_ref                   (1 byte)
```

### 运行时验证 members 偏移的方法

不依赖静态分析猜测——直接用已知的 GDScriptInstance 对象做探针：

```csharp
// 已知: node = SceneTree* (已拿到) 或任意 Node*
nint si = Marshal.ReadIntPtr(node + 0x58);  // script_instance

// 探测 members 偏移: 读几个候选位置
// 如果 members 在 +0x18, Vector<Variant> ptr 应非空且 size > 0
nint ptr18 = Marshal.ReadIntPtr(si + 0x18);  // 候选1
nint ptr28 = Marshal.ReadIntPtr(si + 0x28);  // 候选2

// 哪一个 ptr 非空且 *(uint32_t*)(ptr - 4) > 0?
// SceneTree (底层 C++ 对象) 无 GDScript → si == null → 跳过
// 找一个 GDScript 控制的节点 (如 /root/Main) 来测试

// 获取 GDScriptInstance*:
nint mainNode = FindNode(root, "Main");
nint si2 = Marshal.ReadIntPtr(mainNode + 0x58); // 应该非空!
// 然后:
nint membersPtr = Marshal.ReadIntPtr(si2 + 0x18);
uint count = ReadUInt32(membersPtr - 4); // 应该是 Main.gd 的成员变量数

// 如果 count 看起来合理 (>0且<100)，那 0x18 就是对的
// 如果不对，试 0x28
```

### 名称→索引映射

要读 `members[i]`，需要知道属性名对应的 index。可以从 GDScript 对象的 `member_indices` 获取：

```
GDScriptInstance → [+0x10] → Ref<GDScript> script
                 → ref.ptr() → GDScript*
                 → [+??] → member_indices (Map<StringName, MemberInfo>)
                 → find(propName) → MemberInfo.index
```

Or alternatively: 已知 `GDScriptInstance::get()` 会自动查 member_indices。
若找到函数 RVA，直接用 C# delegate 调用它（3参数，无 sret）:
```csharp
// GDScriptInstance::get(const StringName&, Variant&) → bool
// RCX=this, RDX=&p_name, R8=&r_ret, AL=return
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
delegate bool GDScriptInstanceGet(IntPtr instance, IntPtr propName, IntPtr rRet);
```

### ScriptInstance vtable 布局

```
+0x00: set            (const StringName&, const Variant&) → bool
+0x08: get            (const StringName&, Variant&) → bool  ← 目标!
+0x10: get_property_list (List<PropertyInfo>*) → void
+0x18: get_property_type (const StringName&, bool*) → Variant::Type
+0x20: get_owner      () → Object*
+0x28: get_property_state (List<Pair<StringName,Variant>>&) → void
+0x30: get_method_list (List<MethodInfo>*) → void
+0x38: has_method     (const StringName&) → bool
+0x40: call(StringName, VARGS) → Variant
+0x48: call(StringName, const Variant**, int, CallError&) → Variant
...
```
