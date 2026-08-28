namespace CpuRacer.App.Game;

public sealed class Coin
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public bool Collected { get; set; }
}
