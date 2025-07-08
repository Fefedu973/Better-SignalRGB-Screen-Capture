using System.Text.Json.Serialization;

namespace Better_SignalRGB_Screen_Capture.Models;
public class GitHubContributor
{
    [JsonPropertyName("login")]
    public string? Login
    {
        get; set;
    }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl
    {
        get; set;
    }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl
    {
        get; set;
    }
} 