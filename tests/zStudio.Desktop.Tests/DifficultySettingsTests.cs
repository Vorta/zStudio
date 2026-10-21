using System.Text.Json;
using zStudio.Core;
using zStudio.Desktop;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class DifficultySettingsTests
{
    [Theory]
    [InlineData("{}", MissionDifficulty.Medium)]
    [InlineData("{\"Difficulty\":99}", MissionDifficulty.Medium)]
    [InlineData("{\"Difficulty\":0}", MissionDifficulty.Easy)]
    [InlineData("{\"Difficulty\":2}", MissionDifficulty.Hard)]
    public void DifficultySurvivesSettingsRoundTripAndDefaultsSafely(string json, MissionDifficulty expected)
    {
        var settings = JsonSerializer.Deserialize<StudioSettings>(json)!;
        Assert.Equal(expected, settings.Difficulty);
        Assert.Equal(expected, JsonSerializer.Deserialize<StudioSettings>(JsonSerializer.Serialize(settings))!.Difficulty);
    }
}
