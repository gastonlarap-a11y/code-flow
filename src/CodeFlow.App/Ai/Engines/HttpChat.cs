namespace CodeFlow.Ai.Engines;

/// <summary>
/// The message composition both HTTP engines share.
/// </summary>
/// <remarks>
/// System prompt, then the ask, then the data — the same order the CLI engines compose their briefs
/// in, so a prompt template behaves identically whichever provider a task routes to. Shared because
/// the two endpoints genuinely agree on this shape; everything else about them differs.
/// </remarks>
internal static class HttpChat
{
    public static object[] Messages(AiInvocation invocation)
    {
        List<object> messages = [];

        if (!string.IsNullOrWhiteSpace(invocation.SystemPrompt))
        {
            messages.Add(new { role = "system", content = invocation.SystemPrompt });
        }

        var user = invocation.Prompt;
        if (!string.IsNullOrWhiteSpace(invocation.StdinContent))
        {
            user += "\n\n" + invocation.StdinContent;
        }

        messages.Add(new { role = "user", content = user });
        return [.. messages];
    }
}
