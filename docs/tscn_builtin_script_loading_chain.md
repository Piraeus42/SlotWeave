<!-- AI-assisted documentation | SlotWeave LBAL -->

# Godot 3.4.4 TSCN 内嵌脚本加载链路完整分析

本文档详尽追踪 `.tscn` 场景文件中内嵌 GDScript（即直接写在 tscn 文件中的脚本）从启动加载到最终实例化的完整调用链。每个关键函数都标注了源文件路径和行号。

---

## 阶段 0: 加载器注册

**启动时**（引擎初始化阶段），Godot 会注册多个 ResourceFormatLoader：

| 加载器 | 处理类型 | 源文件 |
|--------|----------|--------|
| `ResourceFormatLoaderText` | `.tscn`, `.tres` | `scene/resources/resource_format_text.cpp` |
| `ResourceFormatLoaderBinary` | `.scn`, `.res` | `core/io/resource_format_binary.cpp` |
| `ResourceFormatLoaderGDScript` | `.gd`, `.gdc`, `.gde` | `modules/gdscript/gdscript.cpp:2167` |

> 注意: `ResourceFormatLoaderGDScript` 只处理独立 `.gd` 文件。内嵌在 tscn 中的脚本**不会**经过这个加载器。

---

## 阶段 1: 加载入口 — ResourceLoader::load()

**源文件**: `core/io/resource_loader.cpp`

```
ResourceLoader::load("res://Main.tscn", "PackedScene")
    │  (line 325-411)
    ├─► 路径本地化和缓存检查
    ├─► 遍历所有 loader，识别路径匹配的加载器
    └─► loader[i]->load(path, local_path, ...)
```

由于 `.tscn` 扩展名匹配 `ResourceFormatLoaderText`，会调用后者的 `load()` 方法。但它**重写了 `load_interactive()`**，所以实际的同步 `load()` 走的是基类实现：

```
ResourceFormatLoader::load()            // core/io/resource_loader.cpp:167-208
    │
    └─► load_interactive(path)          // 多态调用，实际到达 ↓
        ResourceFormatLoaderText::load_interactive()   // resource_format_text.cpp:1175-1193
            │
            ├─► FileAccess::open(path)              // 打开 tscn 文件
            ├─► 创建 ResourceInteractiveLoaderText 实例
            └─► ria->open(f)                        // 解析文件头
```

---

## 阶段 2: 文件打开 — ResourceInteractiveLoaderText::open()

**源文件**: `scene/resources/resource_format_text.cpp:788-857`

```cpp
void ResourceInteractiveLoaderText::open(FileAccess *p_f, ...) {
    // 1. 解析文件头标签
    VariantParser::parse_tag(&stream, lines, error_text, tag);
    
    // 2. 识别文件类型
    if (tag.name == "gd_scene") {
        is_scene = true;   // tscn 分支
    } else if (tag.name == "gd_resource") {
        res_type = tag.fields["type"];  // tres 分支
    }
    
    // 3. 设置 load_steps（资源总数，用于进度追踪）
    resources_total = tag.fields["load_steps"];
    
    // 4. 解析第一个子标签（通常是 ext_resource 或 sub_resource）
    VariantParser::parse_tag(&stream, ..., next_tag, &rp);
    
    // 5. ★ 关键：设置资源解析回调
    rp.ext_func = _parse_ext_resources;   // 解析 ExtResource(id) 引用
    rp.sub_func = _parse_sub_resources;   // 解析 SubResource(id) 引用
    rp.userdata = this;
}
```

**文件头示例**:
```
[gd_scene load_steps=15 format=2]
```

---

## 阶段 3: 轮询解析 — poll()

**源文件**: `scene/resources/resource_format_text.cpp:366-619`

`ResourceFormatLoader::load()` 在获得 `ResourceInteractiveLoader` 后循环调用 `poll()`，直到返回 `ERR_FILE_EOF`。每次 `poll()` 处理一个顶级标签。

```cpp
Error ResourceInteractiveLoaderText::poll() {
    // ★ 按顺序处理四种标签类型
    
    if (next_tag.name == "ext_resource") {
        // ...[外部资源处理]...
    }
    else if (next_tag.name == "sub_resource") {
        // ★★★ 内嵌脚本的核心入口！见阶段 4
    }
    else if (next_tag.name == "resource") {
        // tres 文件的主资源
    }
    else if (next_tag.name == "node") {
        // ★ 场景节点图入口！见阶段 5
    }
}
```

---

## 阶段 4: 子资源（内嵌脚本）的创建与编译 ★ 核心链路 ★

**源文件**: `scene/resources/resource_format_text.cpp:439-524`

当 `.tscn` 文件中出现如下标签时：
```
[sub_resource type="GDScript" id=1]
script/source = "extends Node2D
class_name MyNode
func _ready():
    print('hello')
"
instance_base_type = "Node2D"
```

### 步骤 4.1: 创建 GDScript 对象

```cpp
// resource_format_text.cpp:468-486
Object *obj = ClassDB::instance(type);   // type = "GDScript"
                                          // → 创建一个新的 GDScript 实例
Resource *r = Object::cast_to<Resource>(obj);
res = Ref<Resource>(r);

// 存储在内部资源映射表中
int_resources[id] = res;                  // 后续通过 SubResource(id) 引用
res->set_path(local_path + "::" + itos(id));
```

### 步骤 4.2: 设置属性 → 触发编译

```cpp
// resource_format_text.cpp:497-521
while (true) {
    // 逐个解析属性赋值
    VariantParser::parse_tag_assign_eof(&stream, ..., assign, value, &rp);
    
    if (assign != String()) {
        res->set(assign, value);           // ★ 调用 GDScript::_set()
    }
}
```

当 `assign == "script/source"` 时:

```
Resource::set("script/source", source_code)
    │
    └─► GDScript::_set("script/source", source_code)
        // modules/gdscript/gdscript.cpp:683-692
        │
        ├─► set_source_code(source_code)   // 将源码字符串存入 source 字段
        └─► reload()                       // ★ 立即编译！
```

### 步骤 4.3: GDScript::reload() — 解析与编译

```cpp
// modules/gdscript/gdscript.cpp:547-614
Error GDScript::reload(bool p_keep_state) {
    valid = false;
    
    // ★ 步骤 4.3.1: 解析源码
    GDScriptParser parser;
    Error err = parser.parse(source, basedir, false, path);
    // → 将源码字符串解析为 AST（语法树）
    
    if (err) { /* 报错 */ }
    
    // ★ 步骤 4.3.2: 编译为字节码
    GDScriptCompiler compiler;
    err = compiler.compile(&parser, this, p_keep_state);
    // → 将 AST 编译为 GDScriptFunction 对象
    // → 填充 member_functions, member_indices, constants, _signals 等
    // → 设置 initializer (指向 _init 函数)
    // → 设置 native (instance_base_type)
    
    if (err) { /* 报错 */ }
    
    valid = true;  // ★ 脚本标记为可用
    
    // 处理子类（内建类型）
    for (Map<StringName, Ref<GDScript>>::Element *E = subclasses.front(); ...) {
        _set_subclass_path(E->get(), path);
    }
    
    return OK;
}
```

### 步骤 4.4: 编译结果

编译完成后，`GDScript` 对象的关键字段被填充：

| 字段 | 内容 | 后续用途 |
|------|------|---------|
| `valid` | `true` | 标记脚本可用 |
| `native` | `GDScriptNativeClass("Node2D")` | 实例化时的基类 |
| `initializer` | `GDScriptFunction*`（指向 `_init`） | `instance_create` 时调用 |
| `member_functions` | Map\<StringName, GDScriptFunction*>\ | 方法查找 |
| `member_indices` | Map\<StringName, MemberInfo>\ | 属性访问 |
| `source` | 源码字符串 | 调试/热重载 |

---

## 阶段 5: 场景节点图解析

**源文件**: `scene/resources/resource_format_text.cpp:172-364`

当 `poll()` 遇到 `[node]` 标签时，调用 `_parse_node_tag()`:

```cpp
Ref<PackedScene> ResourceInteractiveLoaderText::_parse_node_tag(parser) {
    Ref<PackedScene> packed_scene;
    packed_scene.instance();  // 创建带 SceneState 的 PackedScene
    
    while (true) {
        if (next_tag.name == "node") {
            // ★ 解析节点元数据
            int name = packed_scene->get_state()->add_name(next_tag.fields["name"]);
            int type = packed_scene->get_state()->add_name(next_tag.fields["type"]);
            int parent = packed_scene->get_state()->add_node_path(...);
            int node_id = packed_scene->get_state()->add_node(parent, owner, type, name, ...);
            
            // ★ 解析节点属性
            while (true) {
                VariantParser::parse_tag_assign_eof(&stream, ..., assign, value, &parser);
                
                if (assign != String()) {
                    int nameidx = packed_scene->get_state()->add_name(assign);
                    int valueidx = packed_scene->get_state()->add_value(value);
                    packed_scene->get_state()->add_node_property(node_id, nameidx, valueidx);
                }
            }
        }
    }
}
```

### 步骤 5.1: 子资源引用的解析

当节点属性值包含 `SubResource(1)` 时，VariantParser 在解析过程中会调用之前设置的回调：

```
VariantParser::parse_tag_assign_eof(...)
    │
    └─► 遇到 "SubResource( 1 )" 文本
        └─► rp.sub_func(this, stream, r_res, ...)
            └─► ResourceInteractiveLoaderText::_parse_sub_resource()
                // resource_format_text.cpp:108-128
                │
                ├─► int index = 读取括号中的数字  // e.g. 1
                └─► r_res = int_resources[index]   // 返回已编译的 GDScript 对象
```

**这就是 SubResource(id) 引用如何被解析为实际 GDScript 对象的**。

### 步骤 5.2: SceneState 的数据结构

此时，场景数据被存储为索引数组：

```
SceneState 内部结构:
├── names:       Vector<StringName>    // 所有字符串（属性名、节点名、类型名）
├── variants:    Vector<Variant>      // 所有属性值（包括 GDScript 对象引用）
├── nodes:       Vector<NodeData>     // 节点数据
│   └── NodeData:
│       ├── parent, owner, type, name, instance  (都是 names[] 或 variants[] 的索引)
│       └── properties: Vector<Property>  (name, value 都是索引)
└── connections: Vector<ConnectionData>
```

内嵌 GDScript 对象此时已是 `variants[]` 中的一个元素，它是**已编译完成**的 `Ref<GDScript>`。

---

## 阶段 6: 场景实例化 — PackedScene::instance()

**源文件**: `scene/resources/packed_scene.cpp:1640-1661`

当需要实际创建场景节点树时（例如 `get_tree()->change_scene("res://Main.tscn")` 的内部处理完成后）：

```cpp
Node *PackedScene::instance(GenEditState p_edit_state) {
    Node *s = state->instance((SceneState::GenEditState)p_edit_state);
    // ...
    s->notification(Node::NOTIFICATION_INSTANCED);
    return s;
}
```

### 步骤 6.1: SceneState::instance() — 创建节点树

**源文件**: `scene/resources/packed_scene.cpp:48-348`

```cpp
Node *SceneState::instance(GenEditState p_edit_state) const {
    // 遍历所有节点
    for (int i = 0; i < nc; i++) {
        const NodeData &n = nd[i];
        
        // ★ 创建节点对象
        if (n.type == TYPE_INSTANCED) {
            // 来自继承/实例化的节点
        } else {
            obj = ClassDB::instance(snames[n.type]);  // e.g. ClassDB::instance("Node2D")
        }
        node = Object::cast_to<Node>(obj);
        
        // ★ 设置节点属性（包括脚本）
        for (int j = 0; j < nprop_count; j++) {
            if (snames[nprops[j].name] == CoreStringNames::get_singleton()->_script) {
                // ★★★ 脚本属性的特殊处理！进入阶段 7
                node->set("script", props[nprops[j].value], &valid);
            } else {
                node->set(snames[nprops[j].name], props[nprops[j].value], &valid);
            }
        }
    }
}
```

---

## 阶段 7: 节点绑定脚本 — Object::set_script() ★ 最终环节 ★

**源文件**: `core/object.cpp:1002-1027`

当 `node->set("script", script_res)` 被调用时：

```
Node::set("script", Ref<GDScript>)
    │
    └─► ClassDB 查找到 "script" 属性的 setter → set_script()
        └─► Object::set_script(p_script)
            // core/object.cpp:1002
            │
            ├─► script = p_script           // 保存脚本引用
            ├─► s->can_instance()            // GDScript::can_instance() → 检查 valid && (tool || scripting_enabled)
            │   └─► 返回 true（对于已编译的有效脚本）
            │
            └─► script_instance = s->instance_create(this)  // ★
                └─► GDScript::instance_create(p_this)
                    // modules/gdscript/gdscript.cpp:301-318
```

### 步骤 7.1: GDScript::instance_create()

```cpp
ScriptInstance *GDScript::instance_create(Object *p_this) {
    GDScript *top = this;
    while (top->_base) {
        top = top->_base;  // 找到继承链顶层
    }
    
    // 验证基类兼容性
    if (!ClassDB::is_parent_class(p_this->get_class_name(), top->native->get_name())) {
        ERR_FAIL_V(nullptr);  // 例如: Node2D 脚本不能赋给 Node
    }
    
    Variant::CallError unchecked_error;
    return _create_instance(nullptr, 0, p_this,
                            Object::cast_to<Reference>(p_this) != nullptr,
                            unchecked_error);
}
```

### 步骤 7.2: GDScript::_create_instance() — 最终创建实例

```cpp
GDScriptInstance *GDScript::_create_instance(
    const Variant **p_args, int p_argcount,
    Object *p_owner, bool p_isref, Variant::CallError &r_error)
{
    // 1. 创建 GDScriptInstance
    GDScriptInstance *instance = memnew(GDScriptInstance);
    instance->base_ref = p_isref;
    instance->members.resize(member_indices.size());
    instance->script = Ref<GDScript>(this);
    instance->owner = p_owner;
    
    // 2. 绑定到 Node → Object::set_script_instance()
    p_owner->set_script_instance(instance);
    
    // 3. 注册到全局实例列表
    instances.insert(instance->owner);
    
    // 4. ★ 调用 _init() 方法
    initializer->call(instance, p_args, p_argcount, r_error);
    //       ↑
    //       └── 执行用户在 GDScript 中写的 _init() 函数
    
    if (r_error.error != Variant::CallError::CALL_OK) {
        // _init() 调用失败，清理
        instance->script = Ref<GDScript>();
        instance->owner->set_script_instance(nullptr);
        instances.erase(p_owner);
        return nullptr;
    }
    
    return instance;
}
```

---

## 完整链路总图

```
游戏启动 / 场景切换
│
├─► ResourceLoader::load("res://Main.tscn", "PackedScene")
│   └─► ResourceFormatLoader::load()  [base class, resource_loader.cpp:167]
│       └─► load_interactive("res://Main.tscn")  [virtual dispatch]
│           └─► ResourceFormatLoaderText::load_interactive()  [resource_format_text.cpp:1175]
│               ├─► FileAccess::open("res://Main.tscn")
│               └─► ResourceInteractiveLoaderText::open(f)  [line 788]
│                   ├─► 解析 [gd_scene ...] 文件头
│                   ├─► 设置 rp.sub_func = _parse_sub_resources
│                   └─► 读取第一个子标签 → next_tag
│
├─► [循环] ResourceFormatLoader::poll()
│   └─► ResourceInteractiveLoaderText::poll()  [line 366]
│       │
│       ├─► [标签: ext_resource]
│       │   └─► ResourceLoader::load(path, type)  // 递归加载外部依赖
│       │
│       ├─► [标签: sub_resource type="GDScript" id=N]  ★ 内嵌脚本入口
│       │   ├─► ClassDB::instance("GDScript")         // 创建 GDScript
│       │   ├─► int_resources[id] = res                 // 存入资源映射
│       │   └─► 对每个属性:
│       │       └─► res->set("script/source", source_string)
│       │           └─► GDScript::_set()  [gdscript.cpp:683]
│       │               ├─► set_source_code(source_string)
│       │               └─► reload()  [gdscript.cpp:547]
│       │                   ├─► GDScriptParser::parse(source, ...)
│       │                   │   → 生成 AST（解析树）
│       │                   └─► GDScriptCompiler::compile(parser, this)
│       │                       → 生成 GDScriptFunction 对象
│       │                       → valid = true  ★
│       │
│       ├─► [标签: sub_resource type="其他资源类型" id=N]
│       │   └─► 类似创建过程（Texture, Material 等）
│       │
│       └─► [标签: node ...]  ★ 场景节点入口
│           └─► _parse_node_tag(rp)  [line 172]
│               ├─► 创建 PackedScene + SceneState
│               ├─► 解析 [node name="..." type="..."] 子标签
│               │   ├─► add_node(...)
│               │   └─► 解析属性赋值:
│               │       ├─► "position" = ...
│               │       ├─► "script" = SubResource( 1 )
│               │       │   └─► VariantParser 解析时遇到 SubResource(id)
│               │       │       └─► rp.sub_func() → _parse_sub_resource()
│               │       │           └─► 返回 int_resources[id] (=已编译的GDScript)
│               │       └─► 存入 SceneState
│               └─► 解析 connections, editable
│
└─► PackedScene::instance()  ★ 运行时实例化
    └─► SceneState::instance()  [packed_scene.cpp:48]
        ├─► 遍历所有节点:
        │   ├─► ClassDB::instance(type) → 创建 Node
        │   └─► 对每个属性:
        │       └─► node->set(property_name, property_value)
        │           │
        │           └─► 当 property_name == "script":
        │               └─► Object::set_script(ref<GDScript>)  [object.cpp:1002]
        │                   ├─► script->can_instance() → true (valid==true)
        │                   └─► GDScript::instance_create(this)  [gdscript.cpp:301]
        │                       └─► _create_instance(...)  [gdscript.cpp:91]
        │                           ├─► new GDScriptInstance
        │                           ├─► owner->set_script_instance(instance)
        │                           └─► initializer->call(instance, ...)
        │                               └─► 执行 _init() 方法 ★
        │
        └─► 建立信号连接, 设置节点父子关系, 触发 NOTIFICATION_INSTANCED
```

---

## 关键注入点总结

对于 SlotWeave 而言，可拦截的点：

### 点 1: `GDScript::reload()` (gdscript.cpp:547)
- 所有内嵌脚本的源码都会经过这里被解析和编译
- 可在此捕获 `this->source`（源码字符串）和 `this->path`（形如 `res://Main.tscn::1`）
- **你可以在这里拿到所有被编译的脚本源码，无论来源是 .gd 文件还是 tscn 内嵌**

### 点 2: `GDScriptParser::parse()` — 源码转 AST
- 被 `reload()` 调用
- 在此处拦截可获得完整的解析树

### 点 3: `GDScriptCompiler::compile()` — AST 转字节码
- 被 `reload()` 调用
- 在此处拦截可修改生成的函数、变量等

### 点 4: `ResourceInteractiveLoaderText::poll()` (resource_format_text.cpp:366)
- 每个 `sub_resource type="GDScript"` 的创建入口在此
- 可在 `res->set(assign, value)` 循环中拦截每个属性的设置

### 点 5: `GDScript::_set()` (gdscript.cpp:683)
- `script/source` 属性的设置入口
- 任何通过 `set("script/source", code)` 加载的源码都会经过这里

### 点 6: `GDScript::load_source_code()` (gdscript.cpp:782)
- 仅用于**独立 .gd 文件**，不适用 tscn 内嵌脚本
- 这就是为什么基于 load_byte_code / load_source_code hook 对于幸运房东不生效的原因

### ★ 推荐方案: Hook `GDScript::reload()`
对于 SlotWeave 移植到幸运房东，最推荐的拦截点是 **`GDScript::reload()`**，因为:
1. 所有脚本（无论 .gd 还是内嵌）最终都会走到这里
2. 此时 `this->source` 已经包含了完整的源码
3. 此时 `this->path` 可以标识脚本来源（path like `res://Main.tscn::1`）
4. 在 `parser.parse()` 之前和 `compiler.compile()` 之后做修改都非常方便
