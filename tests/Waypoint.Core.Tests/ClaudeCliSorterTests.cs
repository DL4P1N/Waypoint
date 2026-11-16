using Waypoint.Core.Capture;

namespace Waypoint.Core.Tests;

public class ClaudeCliSorterTests
{
    /// <summary>
    /// On the server, claude setup-token's CLAUDE_CODE_OAUTH_TOKEN is the plan sign-in, so it's kept. API keys are
    /// still taken out, so nothing is billed to a key. The note writer runs claude the same way.
    /// </summary>
    [Fact]
    public void The_cli_keeps_the_plan_sign_in_token_and_drops_api_keys()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "plan-token");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-key");
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "test-token");
        try
        {
            foreach (var start in new[]
            {
                new ClaudeCliSorter(Path.GetTempPath()).CreateStartInfo("system", "{}"),
                new Waypoint.Core.Notes.ClaudeCliRunner(Path.GetTempPath()).CreateStartInfo("system", "{}"),
            })
            {
                Assert.Equal("plan-token", start.Environment["CLAUDE_CODE_OAUTH_TOKEN"]);
                Assert.False(start.Environment.ContainsKey("ANTHROPIC_API_KEY"));
                Assert.False(start.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
        }
    }

    [Fact]
    public void Reads_items_from_structured_output()
    {
        var result = ClaudeCliSorter.ParseResult("""
            {"is_error": false, "structured_output": {"items": [
                {"kind": "shopping", "title": "shampoo", "due_date": null, "amount_cents": null}
            ]}}
            """);

        Assert.Equal(new SortedItem(ItemKind.Shopping, "shampoo", null, null), result!.Items.Single());
    }

    [Fact]
    public void Missing_structured_output_returns_null()
    {
        Assert.Null(ClaudeCliSorter.ParseResult("""{"is_error": false, "result": "no thanks"}"""));
    }

    [Fact]
    public void Reported_errors_throw_with_the_cli_message()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ClaudeCliSorter.ParseResult("""{"is_error": true, "result": "usage limit reached"}"""));

        Assert.Equal("usage limit reached", exception.Message);
    }
}
