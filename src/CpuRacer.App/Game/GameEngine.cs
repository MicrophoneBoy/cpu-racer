namespace CpuRacer.App.Game;

public enum RunState { Playing, Flipped, GameOver }

public sealed class GameEngine
{
    private const double FlipTimeout = 6.0; // seconds stuck upside down (post-grace) before game over
    private const double ImmobileGraceSeconds = 5.0; // how long a settled-upside-down car gets before it counts as a real flip
    private const double CoinCollectRadius = 0.7;
    private const double ThrottleRate = 2.5; // per second, how fast the throttle lever moves
    private const double ThrottleMin = -0.4;
    private const double ThrottleMax = 1.0;

    // The camera never stops advancing, like the real Task Manager graph scrolling in real time —
    // standing still or reversing just means falling behind it, not revisiting old track.
    private const double CameraScrollSpeed = 1.4; // m/s baseline, scales with DifficultyFactor
    private const double JumpCooldownSeconds = 1.0;
    private const double CameraLeadOffset = 6.0;  // camera trails a fast car by at least this much
    private const double LeftBehindLimit = 12.0;  // meters behind the camera before it's a fail

    private readonly List<Coin> _coinMarkers = new();
    private readonly Random _rng = new();
    private double _elapsed;
    private double _lastCoinRollX;
    private double _runStartX;
    private double _cameraWorldX;
    private double _immobileTime;
    private double _jumpCooldownRemaining;
    private CarPhysics _physics;

    public Track Track { get; } = new();
    public IReadOnlyList<Coin> CoinMarkers => _coinMarkers;

    public double DistanceMeters => Math.Max(0, _physics.ChassisPosition.X - _runStartX);
    public double BestDistanceMeters { get; private set; }
    public int BestScore { get; private set; }
    public int CoinCount { get; private set; }
    public int Score => (int)DistanceMeters + CoinCount * 10;

    public RunState State { get; private set; } = RunState.Playing;
    public double FlippedElapsed { get; private set; }
    public string StatusMessage { get; private set; } = "";
    public bool CanJump => _jumpCooldownRemaining <= 0 && State != RunState.GameOver;

    public void TryJump()
    {
        if (!CanJump) return;
        _physics.Jump();
        _jumpCooldownRemaining = JumpCooldownSeconds;
    }

    public double DifficultyFactor => 1.0 + Math.Min(_elapsed / 60.0, 4.0) * 0.35;

    public CarPhysics Physics => _physics;
    public double ThrottleLevel { get; private set; }
    public double ThrottleFraction => (ThrottleLevel - ThrottleMin) / (ThrottleMax - ThrottleMin);
    public double CameraWorldX => _cameraWorldX;

    // A fresh (or respawned) car drops in just behind the live edge of the track rather than at the
    // beginning of the whole session's CPU history — the trace is "real time", so getting a new life
    // should feel like rejoining it now, not rewinding to the start. MinSpawnX is only a floor for the
    // very first spawn, when the frontier hasn't moved far past 0 yet; SpawnMargin keeps a stable
    // stretch of already-built ground under the whole wheelbase when it lands.
    private const double MinSpawnX = 1.5;
    private const double SpawnMargin = 1.5;
    private const double FallOutOfBoundsY = -8.0;

    private double SafeSpawnX() => Math.Max(MinSpawnX, Track.FrontierX - SpawnMargin);

    public GameEngine()
    {
        var best = LeaderboardStore.Load();
        if (best.Count > 0)
        {
            BestDistanceMeters = best.Max(e => e.DistanceMeters);
            BestScore = best.Max(e => e.Score);
        }

        for (int i = 0; i < 5; i++) Track.AddSample(0, 1.0);
        _runStartX = SafeSpawnX();
        _cameraWorldX = _runStartX;
        _physics = new CarPhysics(Track, _runStartX);
    }

    public void SampleCpu(double cpuPercent)
    {
        Track.AddSample(cpuPercent, DifficultyFactor);
        _physics.ExtendGround();
        MaybeSpawnCoin();
    }

    private void MaybeSpawnCoin()
    {
        double frontier = Track.FrontierX;
        if (frontier - _lastCoinRollX < Track.SampleSpacing * 3) return;
        _lastCoinRollX = frontier;

        double chance = Math.Clamp(0.35 * DifficultyFactor, 0, 0.85);
        if (_rng.NextDouble() >= chance) return;

        // Find the highest point in the last few meters so a coin never has to spawn deep in a dip
        // between two close peaks — either it sits on that peak outright, or its height gets pulled
        // up toward it instead of sinking to whatever the ground happens to be right here.
        IReadOnlyList<double> heights = Track.Heights;
        int frontierIndex = heights.Count - 1;
        int windowStart = Math.Max(0, frontierIndex - 6);
        int peakIndex = frontierIndex;
        double peakHeight = heights[frontierIndex];
        for (int i = windowStart; i <= frontierIndex; i++)
        {
            if (heights[i] > peakHeight)
            {
                peakHeight = heights[i];
                peakIndex = i;
            }
        }

        double x, y;
        if (_rng.NextDouble() < 0.5)
        {
            x = peakIndex * Track.SampleSpacing;
            y = peakHeight + 0.9;
        }
        else
        {
            x = frontier;
            y = Math.Max(Track.HeightAt(frontier) + 0.9, peakHeight - 2.5);
        }

        _coinMarkers.Add(new Coin { X = x, Y = y });
    }

    public void Update(double dt, bool throttleUp, bool throttleDown, int rotateInput, bool recoverPressed)
    {
        _elapsed += dt;

        if (State == RunState.GameOver) return;

        if (_jumpCooldownRemaining > 0) _jumpCooldownRemaining -= dt;

        // Throttle is a lever, not a pedal: it holds its setting until nudged again, even through a flip.
        if (throttleUp) ThrottleLevel = Math.Clamp(ThrottleLevel + ThrottleRate * dt, ThrottleMin, ThrottleMax);
        else if (throttleDown) ThrottleLevel = Math.Clamp(ThrottleLevel - ThrottleRate * dt, ThrottleMin, ThrottleMax);

        // The camera creeps forward on its own, like the real graph scrolling in real time; it only
        // ever speeds up to keep a fast car in view, never slows down or waits.
        _cameraWorldX = Math.Max(_cameraWorldX + CameraScrollSpeed * DifficultyFactor * dt, _physics.ChassisPosition.X - CameraLeadOffset);

        if (State == RunState.Flipped)
        {
            FlippedElapsed += dt;
            StatusMessage = $"Flipped — {DistanceMeters:0.0}m — press R to recover";
            _physics.SetDrive(ThrottleLevel, DifficultyFactor);
            _physics.ApplyRotationInput(rotateInput);
            _physics.Step((float)dt);

            if (CheckLeftBehind()) return;

            if (recoverPressed)
            {
                _physics.Recover();
                State = RunState.Playing;
                FlippedElapsed = 0;
                _immobileTime = 0;
                StatusMessage = "";
            }
            else if (FlippedElapsed >= FlipTimeout)
            {
                EndRun();
            }
            return;
        }

        _physics.SetDrive(ThrottleLevel, DifficultyFactor);
        _physics.ApplyRotationInput(rotateInput);
        _physics.Step((float)dt);

        if (_physics.ChassisPosition.Y < FallOutOfBoundsY)
        {
            // Safety net: driving off the edge of not-yet-generated track, or any other physics
            // edge case that sends the car into the void, ends the run instead of stranding the player.
            EndRun();
            return;
        }

        if (CheckLeftBehind()) return;

        // A brief upside-down bounce that rights itself shouldn't count as a real flip — only a car
        // that stays stuck and settled for a few seconds is actually treated as failed.
        if (_physics.IsSettledUpsideDown)
        {
            _immobileTime += dt;
            if (_immobileTime >= ImmobileGraceSeconds)
            {
                State = RunState.Flipped;
                FlippedElapsed = 0;
                _immobileTime = 0;
                return;
            }
        }
        else
        {
            _immobileTime = 0;
        }

        CollectCoins();
        StatusMessage = "";
    }

    private bool CheckLeftBehind()
    {
        if (_cameraWorldX - _physics.ChassisPosition.X <= LeftBehindLimit) return false;
        EndRun();
        return true;
    }

    private void CollectCoins()
    {
        var carPos = _physics.ChassisPosition;
        var rearWheelPos = _physics.RearWheelPosition;
        var frontWheelPos = _physics.FrontWheelPosition;

        foreach (var coin in _coinMarkers)
        {
            if (coin.Collected) continue;
            if (IsWithinCollectRadius(coin, carPos.X, carPos.Y) ||
                IsWithinCollectRadius(coin, rearWheelPos.X, rearWheelPos.Y) ||
                IsWithinCollectRadius(coin, frontWheelPos.X, frontWheelPos.Y))
            {
                coin.Collected = true;
                CoinCount++;
            }
        }
    }

    private static bool IsWithinCollectRadius(Coin coin, double x, double y)
    {
        double dx = coin.X - x;
        double dy = coin.Y - y;
        return dx * dx + dy * dy < CoinCollectRadius * CoinCollectRadius;
    }

    private void EndRun()
    {
        State = RunState.GameOver;
        if (DistanceMeters > BestDistanceMeters) BestDistanceMeters = DistanceMeters;
        if (Score > BestScore) BestScore = Score;
        LeaderboardStore.RecordRun(DistanceMeters, CoinCount, Score);
        StatusMessage = $"GAME OVER — {DistanceMeters:0.0}m — press Space to retry";
    }

    public void Restart()
    {
        CoinCount = 0;
        FlippedElapsed = 0;
        _elapsed = 0;
        ThrottleLevel = 0;
        State = RunState.Playing;
        StatusMessage = "";
        _coinMarkers.Clear();
        _runStartX = SafeSpawnX();
        _cameraWorldX = _runStartX;
        _immobileTime = 0;
        _jumpCooldownRemaining = 0;
        _physics = new CarPhysics(Track, _runStartX);
    }
}
