using SlotWeave.Scripting;

// [Patch] 类自动被发现并应用，无需在 Mod.cs 中注册
// path: 脚本路径，数字 ID 可通过 GDRE Tools 查看
// function: 目标函数名

[Patch("res://Main.tscn::1", "_ready")]
class ReadyPrefix
{
    [Prefix]
    static string Code() => """
        print("_ready() begins")
        """;
}
