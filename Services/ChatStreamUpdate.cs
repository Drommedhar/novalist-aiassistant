namespace Novalist.Extensions.AiAssistant.Services;

internal sealed class ChatStreamUpdate
{
    private string _response = string.Empty;
    private string _thinking = string.Empty;

    public ChatStreamDelta Next(string response, string thinking)
    {
        var reset = response.Length < _response.Length || thinking.Length < _thinking.Length
            || !response.StartsWith(_response, StringComparison.Ordinal)
            || !thinking.StartsWith(_thinking, StringComparison.Ordinal);
        var delta = new ChatStreamDelta(reset ? response : response[_response.Length..],
            reset ? thinking : thinking[_thinking.Length..], reset);
        _response = response;
        _thinking = thinking;
        return delta;
    }
}

internal sealed record ChatStreamDelta(string StreamingDelta, string ThinkingDelta, bool Reset);
