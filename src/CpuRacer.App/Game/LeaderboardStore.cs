using System.IO;
using System.Text.Json;

namespace CpuRacer.App.Game;

public sealed record LeaderboardEntry(DateTime WhenUtc, double DistanceMeters, int Coins, int Score);

// Local, file-based leaderboard: %AppData%\CpuRacer\leaderboard.json, top 10 runs by distance.
public static class LeaderboardStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CpuRacer", "leaderboard.json");

    public static List<LeaderboardEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<LeaderboardEntry>();
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<LeaderboardEntry>>(json) ?? new List<LeaderboardEntry>();
        }
        catch
        {
            return new List<LeaderboardEntry>();
        }
    }

    public static void RecordRun(double distanceMeters, int coins, int score)
    {
        var entries = Load();
        entries.Add(new LeaderboardEntry(DateTime.UtcNow, distanceMeters, coins, score));

        var top = entries.OrderByDescending(e => e.DistanceMeters).Take(10).ToList();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(top, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Leaderboard persistence is a nice-to-have; a failed write shouldn't crash the run.
        }
    }
}
