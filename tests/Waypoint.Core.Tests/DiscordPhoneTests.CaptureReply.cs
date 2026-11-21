using Waypoint.Core.Capture;

namespace Waypoint.Core.Tests;

/// <summary>The answer to something typed in Discord is tried again when it doesn't go, under the same nonce.</summary>
public sealed partial class DiscordPhoneTests
{
    [Fact]
    public async Task The_answer_to_a_capture_is_tried_again_with_the_same_nonce_when_Discord_cant_be_reached()
    {
        var worker = Worker(toDo: null, lists: null);
        _discord.FailWith = "made up outage";
        worker.RetryWait = _ =>
        {
            // Discord comes back once the answer's first try has failed.
            if (_discord.Of(HttpMethod.Post).Any(post => post.Body.Contains("Made up answer")))
            {
                _discord.FailWith = null;
            }

            return Task.CompletedTask;
        };

        worker.ReplyInDiscord(new CaptureOutcome([], [], "Made up answer", Origin: CaptureOrigin.Discord) { CaptureId = 41 }, "Made up answer");

        await WaitFor(() => _discord.MadeMessages.Any(made => made.Body.Contains("Made up answer")));
        var sent = _discord.Of(HttpMethod.Post).Where(post => post.Body.Contains("Made up answer")).ToList();
        Assert.Equal(2, sent.Count);
        var nonce = System.Text.RegularExpressions.Regex.Match(sent[0].Body, "\"nonce\":\"([^\"]+)\"").Groups[1].Value;
        Assert.StartsWith("r41", nonce);
        Assert.Contains($"\"nonce\":\"{nonce}\"", sent[1].Body);
        Assert.Single(_discord.MadeMessages, made => made.Body.Contains("Made up answer"));
    }
}
