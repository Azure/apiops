namespace common;

/// <summary>
/// Represents the absence of a meaningful value. Used as a functional equivalent 
/// of void that can be used in generic contexts.
/// </summary>
public readonly record struct Unit
{
    public static Unit Instance { get; }
    
    public override string ToString() => "()";

    public override int GetHashCode() => 0;
}
