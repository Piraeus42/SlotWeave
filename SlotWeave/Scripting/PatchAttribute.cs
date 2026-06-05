namespace SlotWeave.Scripting;

/// <summary>Declares a patch targeting a specific script path and function name.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class PatchAttribute : Attribute
{
    public string Path { get; }
    public string Function { get; }

    public PatchAttribute(string path, string function)
    {
        this.Path = path;
        this.Function = function;
    }
}

/// <summary>Method returns code to insert before the function body.</summary>
[AttributeUsage(AttributeTargets.Method)]
public class PrefixAttribute : Attribute { }

/// <summary>Method returns code to insert after the function body.</summary>
[AttributeUsage(AttributeTargets.Method)]
public class PostfixAttribute : Attribute { }

/// <summary>Method receives the original function body and returns replacement code.</summary>
[AttributeUsage(AttributeTargets.Method)]
public class ReplaceAttribute : Attribute { }
