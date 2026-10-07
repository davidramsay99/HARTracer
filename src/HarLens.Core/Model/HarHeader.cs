namespace HarLens.Core.Model;

/// <summary>A name/value pair as it appears in a HAR <c>headers</c>, <c>queryString</c> or <c>params</c> array.</summary>
public readonly record struct HarHeader(string Name, string Value)
{
    public override string ToString() => $"{Name}: {Value}";
}
