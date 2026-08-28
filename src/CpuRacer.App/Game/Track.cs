namespace CpuRacer.App.Game;

// The track's height comes from live CPU% samples, but a smoothed random-walk wobble is
// layered on top (scaled by difficulty) so a near-idle, nearly-flat CPU trace still produces
// a drivable, varied track instead of the flat/boring line that motivated this whole project.
public sealed class Track
{
    // Meters, matching Box2D's expected physics scale (shapes/gravity tuned for ~1-10 unit sizes).
    public const double SampleSpacing = 1.0;
    public const double MaxHeight = 13.0;

    private const double NoiseAmplitudeBase = 2.2;
    private const double NoiseSmoothing = 0.85;

    private readonly List<double> _heights = new();
    private readonly Random _rng = new();
    private double _noiseState;

    public IReadOnlyList<double> Heights => _heights;
    public double FrontierX => Math.Max(0, (_heights.Count - 1) * SampleSpacing);

    public void AddSample(double cpuPercent, double difficultyFactor)
    {
        double cpuHeight = Math.Clamp(cpuPercent, 0, 100) / 100.0 * MaxHeight;

        _noiseState = _noiseState * NoiseSmoothing + (_rng.NextDouble() * 2 - 1) * (1 - NoiseSmoothing);
        double noiseHeight = _noiseState * NoiseAmplitudeBase * difficultyFactor;

        _heights.Add(Math.Clamp(cpuHeight + noiseHeight, 0, MaxHeight));
    }

    public double HeightAt(double worldX)
    {
        if (_heights.Count == 0) return 0;

        double index = Math.Max(0, worldX) / SampleSpacing;
        int i0 = Math.Clamp((int)Math.Floor(index), 0, _heights.Count - 1);
        int i1 = Math.Clamp(i0 + 1, 0, _heights.Count - 1);
        double t = Math.Clamp(index - i0, 0, 1);
        return _heights[i0] + (_heights[i1] - _heights[i0]) * t;
    }
}
