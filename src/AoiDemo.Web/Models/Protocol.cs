namespace AoiDemo.Web.Models;

public abstract record ClientMessage(string Type);

public sealed record JoinClientMessage(string Name) : ClientMessage("join");

public sealed record MoveInputClientMessage(float X, float Y) : ClientMessage("moveInput");

public sealed record ChangeAlgorithmClientMessage(AoiAlgorithm Algorithm) : ClientMessage("changeAlgorithm");

public sealed record ResetWorldClientMessage() : ClientMessage("resetWorld");

public sealed record PingClientMessage() : ClientMessage("ping");

public abstract record ServerMessage(string Type);

public sealed record WelcomeServerMessage(
    string PlayerId,
    string DisplayName,
    AoiAlgorithm Algorithm,
    WorldOptions World,
    VisibleEntityDto Self) : ServerMessage("welcome");

public sealed record WorldResetServerMessage(
    int WorldVersion,
    int Seed,
    AoiAlgorithm Algorithm,
    WorldOptions World,
    VisibleEntityDto Self) : ServerMessage("worldReset");

public sealed record VisibilityDeltaServerMessage(
    long Tick,
    VisibleEntityDto Self,
    IReadOnlyList<VisibleEntityDto> Entered,
    IReadOnlyList<VisibleEntityDto> Updated,
    IReadOnlyList<string> Left) : ServerMessage("visibilityDelta");

public sealed record MetricsServerMessage(MetricsSnapshot Snapshot) : ServerMessage("metrics");

public sealed record ErrorServerMessage(string Message) : ServerMessage("error");
