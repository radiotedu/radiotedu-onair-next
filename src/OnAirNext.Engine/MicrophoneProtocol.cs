namespace OnAirNext.Engine;

public sealed record MicrophoneRequest(string StationId, string SessionId)
{
    public const int BlockBytes = 7680;
    public const int IdleMilliseconds = 750;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StationId) || StationId.Length > 100 || !Guid.TryParseExact(SessionId, "N", out _))
            throw new ArgumentException("Choose a station and a new microphone session.");
    }
}
public sealed record MicrophoneReply(bool Accepted, string? Error = null);
