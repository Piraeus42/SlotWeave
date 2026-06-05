using SlotWeave.Scripting;

[Patch("res://Main.tscn::1", "get_coins")]
class CoinsReplace
{
    [Replace]
    static string Code(string originalBody) =>
        originalBody.Replace("coins", "coins + 999");
}
