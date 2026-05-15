namespace PratfallModFramework;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class ModPatchAttribute : Attribute
{
    public Type TargetType { get; }
    public string MethodName { get; }
    public PatchType Type { get; }

    public ModPatchAttribute(Type targetType, string methodName, PatchType type = PatchType.Prefix)
    {
        TargetType = targetType;
        MethodName = methodName;
        Type = type;
    }
}

public enum PatchType { Prefix, Postfix, Transpiler }
