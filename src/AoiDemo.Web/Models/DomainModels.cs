namespace AoiDemo.Web.Models;

public enum AoiAlgorithm
{
    BruteForce,
    UniformGrid,
    Quadtree
}

public enum EntityKind
{
    Player,
    Npc
}

public sealed record EntityState(
    string Id,
    EntityKind Kind,
    string Name,
    float X,
    float Y,
    float Radius);

public sealed record PlayerState(
    string Id,
    string Name,
    float X,
    float Y,
    float Radius,
    float InputX,
    float InputY);

public sealed record NpcState(
    string Id,
    string Name,
    float X,
    float Y,
    float Radius,
    float VelocityX,
    float VelocityY);

public sealed record VisibilitySet(
    string PlayerId,
    IReadOnlyList<string> EntityIds);

public sealed record WorldSnapshot(
    long Tick,
    AoiAlgorithm Algorithm,
    IReadOnlyList<EntityState> Entities,
    IReadOnlyList<PlayerState> Players,
    WorldOptions World);

public sealed record MetricsSnapshot(
    long Tick,
    AoiAlgorithm Algorithm,
    int EntityCount,
    int TotalVisibleCount,
    int DistanceChecks,
    int QueryCount,
    double IndexBuildMs,
    double QueryMs,
    long MessageCount,
    long BytesSent,
    DebugOverlayDto DebugOverlay);

public sealed record VisibleEntityDto(
    string Id,
    EntityKind Kind,
    string Name,
    float X,
    float Y,
    float Radius)
{
    /// <summary>
    /// 일반 엔티티 상태를 클라이언트 전송용 visible 엔티티 DTO로 변환합니다.
    /// </summary>
    /// <param name="entity">변환할 엔티티 상태입니다.</param>
    /// <returns>클라이언트 렌더링에 바로 사용할 수 있는 visible 엔티티 DTO입니다.</returns>
    public static VisibleEntityDto FromEntity(EntityState entity) =>
        new(entity.Id, entity.Kind, entity.Name, entity.X, entity.Y, entity.Radius);

    /// <summary>
    /// 플레이어 상태를 클라이언트 전송용 visible 엔티티 DTO로 변환합니다.
    /// </summary>
    /// <param name="player">변환할 플레이어 상태입니다.</param>
    /// <returns>플레이어 정보가 반영된 visible 엔티티 DTO입니다.</returns>
    public static VisibleEntityDto FromPlayer(PlayerState player) =>
        new(player.Id, EntityKind.Player, player.Name, player.X, player.Y, player.Radius);

    /// <summary>
    /// NPC 상태를 클라이언트 전송용 visible 엔티티 DTO로 변환합니다.
    /// </summary>
    /// <param name="npc">변환할 NPC 상태입니다.</param>
    /// <returns>NPC 정보가 반영된 visible 엔티티 DTO입니다.</returns>
    public static VisibleEntityDto FromNpc(NpcState npc) =>
        new(npc.Id, EntityKind.Npc, npc.Name, npc.X, npc.Y, npc.Radius);
}

public sealed record DebugOverlayDto(
    string Mode,
    float CellSize,
    IReadOnlyList<DebugRectDto> Rectangles)
{
    public static DebugOverlayDto None { get; } = new("none", 0f, Array.Empty<DebugRectDto>());
}

public sealed record DebugRectDto(
    float X,
    float Y,
    float Width,
    float Height,
    int Depth);

public sealed record VisibilityDeltaIds(
    IReadOnlyList<string> Entered,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Left);

public sealed record AoiStrategyResult(
    IReadOnlyDictionary<string, VisibilitySet> VisibilityByPlayer,
    int DistanceChecks,
    int QueryCount,
    double IndexBuildMs,
    double QueryMs,
    DebugOverlayDto DebugOverlay);
