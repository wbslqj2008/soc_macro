namespace SocMacro.Core;

/// <summary>Rectangle in base coordinates (the 1274x717 reference client area).</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double CX => X + W / 2;
    public double CY => Y + H / 2;

    public bool ContainsCenterOf(Box other) =>
        other.CX >= X && other.CX <= X + W && other.CY >= Y && other.CY <= Y + H;

    public override string ToString() => $"({X:0},{Y:0} {W:0}x{H:0})";
}
