using System.Diagnostics;
using AoiDemo.Web.Models;

namespace AoiDemo.Web.Aoi;

/// <summary>
/// 모든 엔티티를 전수 검사해 AOI 결과를 계산하는 기준선 전략입니다.
/// </summary>
public sealed class BruteForceAoiStrategy : IAoiStrategy
{
    public AoiAlgorithm Algorithm => AoiAlgorithm.BruteForce;

    /// <summary>
    /// 플레이어마다 전체 엔티티를 순회해 AOI 반경 안에 들어오는 엔티티를 찾습니다.
    /// </summary>
    /// <param name="snapshot">가시성 계산에 사용할 현재 월드 스냅샷입니다.</param>
    /// <returns>플레이어별 visible set과 전수 검사 비용을 담은 결과입니다.</returns>
    public AoiStrategyResult Compute(WorldSnapshot snapshot)
    {
        var visibility = new Dictionary<string, VisibilitySet>(StringComparer.Ordinal);
        var distanceChecks = 0;
        var queryTimer = Stopwatch.StartNew();

        foreach (var player in snapshot.Players)
        {
            var visibleIds = new List<string>();
            foreach (var entity in snapshot.Entities)
            {
                if (entity.Id == player.Id)
                {
                    continue;
                }

                distanceChecks++;
                if (IsVisible(player.X, player.Y, entity.X, entity.Y, snapshot.World.AoiRadius))
                {
                    visibleIds.Add(entity.Id);
                }
            }

            visibleIds.Sort(StringComparer.Ordinal);
            visibility[player.Id] = new VisibilitySet(player.Id, visibleIds);
        }

        queryTimer.Stop();
        return new AoiStrategyResult(
            visibility,
            distanceChecks,
            snapshot.Players.Count,
            0d,
            queryTimer.Elapsed.TotalMilliseconds,
            DebugOverlayDto.None);
    }

    /// <summary>
    /// 두 좌표 사이의 거리가 지정한 AOI 반경 안에 들어오는지 검사합니다.
    /// </summary>
    /// <param name="originX">기준점의 X 좌표입니다.</param>
    /// <param name="originY">기준점의 Y 좌표입니다.</param>
    /// <param name="targetX">비교할 대상의 X 좌표입니다.</param>
    /// <param name="targetY">비교할 대상의 Y 좌표입니다.</param>
    /// <param name="radius">가시성을 판단할 AOI 반경입니다.</param>
    /// <returns>대상이 반경 안에 있으면 <see langword="true" />, 아니면 <see langword="false" />입니다.</returns>
    internal static bool IsVisible(float originX, float originY, float targetX, float targetY, float radius)
    {
        var dx = targetX - originX;
        var dy = targetY - originY;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }
}

/// <summary>
/// 균등 격자 인덱스를 사용해 주변 셀만 검사하는 AOI 전략입니다.
/// </summary>
public sealed class UniformGridAoiStrategy : IAoiStrategy
{
    public AoiAlgorithm Algorithm => AoiAlgorithm.UniformGrid;

    /// <summary>
    /// 엔티티를 셀 단위로 묶은 뒤 플레이어 주변 셀만 조회해 AOI 결과를 계산합니다.
    /// </summary>
    /// <param name="snapshot">격자 인덱스를 만들고 조회할 현재 월드 스냅샷입니다.</param>
    /// <returns>플레이어별 visible set과 격자 인덱스 비용을 담은 결과입니다.</returns>
    public AoiStrategyResult Compute(WorldSnapshot snapshot)
    {
        var cellSize = snapshot.World.GridCellSize;
        var cellMap = new Dictionary<(int X, int Y), List<EntityState>>();
        var buildTimer = Stopwatch.StartNew();

        foreach (var entity in snapshot.Entities)
        {
            var key = GetCellKey(entity.X, entity.Y, cellSize);
            if (!cellMap.TryGetValue(key, out var entities))
            {
                entities = new List<EntityState>();
                cellMap[key] = entities;
            }

            entities.Add(entity);
        }

        buildTimer.Stop();

        var visibility = new Dictionary<string, VisibilitySet>(StringComparer.Ordinal);
        var distanceChecks = 0;
        var queryTimer = Stopwatch.StartNew();

        foreach (var player in snapshot.Players)
        {
            var visibleIds = new List<string>();
            var minX = (int)MathF.Floor((player.X - snapshot.World.AoiRadius) / cellSize);
            var maxX = (int)MathF.Floor((player.X + snapshot.World.AoiRadius) / cellSize);
            var minY = (int)MathF.Floor((player.Y - snapshot.World.AoiRadius) / cellSize);
            var maxY = (int)MathF.Floor((player.Y + snapshot.World.AoiRadius) / cellSize);

            for (var cellX = minX; cellX <= maxX; cellX++)
            {
                for (var cellY = minY; cellY <= maxY; cellY++)
                {
                    if (!cellMap.TryGetValue((cellX, cellY), out var bucket))
                    {
                        continue;
                    }

                    foreach (var entity in bucket)
                    {
                        if (entity.Id == player.Id)
                        {
                            continue;
                        }

                        distanceChecks++;
                        if (BruteForceAoiStrategy.IsVisible(player.X, player.Y, entity.X, entity.Y, snapshot.World.AoiRadius))
                        {
                            visibleIds.Add(entity.Id);
                        }
                    }
                }
            }

            visibleIds.Sort(StringComparer.Ordinal);
            visibility[player.Id] = new VisibilitySet(player.Id, visibleIds);
        }

        queryTimer.Stop();

        return new AoiStrategyResult(
            visibility,
            distanceChecks,
            snapshot.Players.Count,
            buildTimer.Elapsed.TotalMilliseconds,
            queryTimer.Elapsed.TotalMilliseconds,
            new DebugOverlayDto("grid", cellSize, Array.Empty<DebugRectDto>()));
    }

    /// <summary>
    /// 좌표를 격자 셀 키로 변환합니다.
    /// </summary>
    /// <param name="x">셀을 찾을 월드 X 좌표입니다.</param>
    /// <param name="y">셀을 찾을 월드 Y 좌표입니다.</param>
    /// <param name="cellSize">한 셀의 변 길이입니다.</param>
    /// <returns>해당 좌표가 속한 격자 셀의 X/Y 인덱스 쌍입니다.</returns>
    private static (int X, int Y) GetCellKey(float x, float y, float cellSize) =>
        ((int)MathF.Floor(x / cellSize), (int)MathF.Floor(y / cellSize));
}

/// <summary>
/// 쿼드트리 인덱스를 구성해 관심 영역 주변 후보만 조회하는 AOI 전략입니다.
/// </summary>
public sealed class QuadtreeAoiStrategy : IAoiStrategy
{
    public AoiAlgorithm Algorithm => AoiAlgorithm.Quadtree;

    /// <summary>
    /// 월드 엔티티로 쿼드트리를 만든 뒤 플레이어별 후보 영역을 조회해 AOI 결과를 계산합니다.
    /// </summary>
    /// <param name="snapshot">쿼드트리 구성과 가시성 조회에 사용할 현재 월드 스냅샷입니다.</param>
    /// <returns>플레이어별 visible set과 쿼드트리 인덱스 비용을 담은 결과입니다.</returns>
    public AoiStrategyResult Compute(WorldSnapshot snapshot)
    {
        var worldBounds = new Rect(0f, 0f, snapshot.World.Width, snapshot.World.Height);
        var tree = new QuadtreeNode(worldBounds, 0, snapshot.World.QuadtreeMaxDepth, snapshot.World.QuadtreeCapacity);

        var buildTimer = Stopwatch.StartNew();
        foreach (var entity in snapshot.Entities)
        {
            tree.Insert(entity);
        }

        buildTimer.Stop();

        var visibility = new Dictionary<string, VisibilitySet>(StringComparer.Ordinal);
        var distanceChecks = 0;
        var queryTimer = Stopwatch.StartNew();

        foreach (var player in snapshot.Players)
        {
            var candidates = new List<EntityState>();
            var queryRect = new Rect(
                player.X - snapshot.World.AoiRadius,
                player.Y - snapshot.World.AoiRadius,
                snapshot.World.AoiRadius * 2f,
                snapshot.World.AoiRadius * 2f);

            tree.Query(queryRect, candidates);

            var visibleIds = new List<string>();
            foreach (var entity in candidates)
            {
                if (entity.Id == player.Id)
                {
                    continue;
                }

                distanceChecks++;
                if (BruteForceAoiStrategy.IsVisible(player.X, player.Y, entity.X, entity.Y, snapshot.World.AoiRadius))
                {
                    visibleIds.Add(entity.Id);
                }
            }

            visibleIds.Sort(StringComparer.Ordinal);
            visibility[player.Id] = new VisibilitySet(player.Id, visibleIds);
        }

        queryTimer.Stop();

        var rectangles = new List<DebugRectDto>();
        tree.CollectRectangles(rectangles);

        return new AoiStrategyResult(
            visibility,
            distanceChecks,
            snapshot.Players.Count,
            buildTimer.Elapsed.TotalMilliseconds,
            queryTimer.Elapsed.TotalMilliseconds,
            new DebugOverlayDto("quadtree", 0f, rectangles));
    }

    private readonly struct Rect(float x, float y, float width, float height)
    {
        public float X { get; } = x;
        public float Y { get; } = y;
        public float Width { get; } = width;
        public float Height { get; } = height;
        public float MidX => X + (Width / 2f);
        public float MidY => Y + (Height / 2f);

        /// <summary>
        /// 지정한 점이 현재 사각형 경계 안에 포함되는지 확인합니다.
        /// </summary>
        /// <param name="px">검사할 점의 X 좌표입니다.</param>
        /// <param name="py">검사할 점의 Y 좌표입니다.</param>
        /// <returns>점이 사각형 내부 또는 경계 위에 있으면 <see langword="true" />입니다.</returns>
        public bool ContainsPoint(float px, float py) =>
            px >= X && px <= X + Width && py >= Y && py <= Y + Height;

        /// <summary>
        /// 다른 사각형과 현재 사각형이 겹치는지 검사합니다.
        /// </summary>
        /// <param name="other">교차 여부를 비교할 다른 사각형입니다.</param>
        /// <returns>두 사각형의 영역이 겹치면 <see langword="true" />입니다.</returns>
        public bool Intersects(Rect other) =>
            X <= other.X + other.Width &&
            X + Width >= other.X &&
            Y <= other.Y + other.Height &&
            Y + Height >= other.Y;
    }

    private sealed class QuadtreeNode(Rect bounds, int depth, int maxDepth, int capacity)
    {
        private readonly List<EntityState> _items = new();
        private QuadtreeNode[]? _children;

        public Rect Bounds { get; } = bounds;
        public int Depth { get; } = depth;
        public int MaxDepth { get; } = maxDepth;
        public int Capacity { get; } = capacity;

        /// <summary>
        /// 엔티티를 현재 노드 또는 하위 노드에 삽입합니다.
        /// </summary>
        /// <param name="entity">쿼드트리에 추가할 월드 엔티티입니다.</param>
        public void Insert(EntityState entity)
        {
            if (!Bounds.ContainsPoint(entity.X, entity.Y))
            {
                return;
            }

            if (_children is not null)
            {
                GetChild(entity.X, entity.Y).Insert(entity);
                return;
            }

            _items.Add(entity);
            if (_items.Count <= Capacity || Depth >= MaxDepth)
            {
                return;
            }

            Subdivide();
            foreach (var item in _items.ToArray())
            {
                GetChild(item.X, item.Y).Insert(item);
            }

            _items.Clear();
        }

        /// <summary>
        /// 지정한 영역과 겹치는 후보 엔티티를 결과 목록에 수집합니다.
        /// </summary>
        /// <param name="area">조회할 사각형 영역입니다.</param>
        /// <param name="results">조건에 맞는 엔티티를 누적할 결과 목록입니다.</param>
        public void Query(Rect area, List<EntityState> results)
        {
            if (!Bounds.Intersects(area))
            {
                return;
            }

            foreach (var item in _items)
            {
                if (area.ContainsPoint(item.X, item.Y))
                {
                    results.Add(item);
                }
            }

            if (_children is null)
            {
                return;
            }

            foreach (var child in _children)
            {
                child.Query(area, results);
            }
        }

        /// <summary>
        /// 디버그 렌더링에 사용할 현재 노드와 하위 노드의 경계 사각형을 수집합니다.
        /// </summary>
        /// <param name="results">노드 경계를 순서대로 추가할 출력 목록입니다.</param>
        public void CollectRectangles(List<DebugRectDto> results)
        {
            results.Add(new DebugRectDto(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, Depth));
            if (_children is null)
            {
                return;
            }

            foreach (var child in _children)
            {
                child.CollectRectangles(results);
            }
        }

        /// <summary>
        /// 좌표가 들어갈 하위 사분면 노드를 선택합니다.
        /// </summary>
        /// <param name="x">대상의 X 좌표입니다.</param>
        /// <param name="y">대상의 Y 좌표입니다.</param>
        /// <returns>해당 좌표를 담당하는 하위 쿼드트리 노드입니다.</returns>
        private QuadtreeNode GetChild(float x, float y)
        {
            if (_children is null)
            {
                throw new InvalidOperationException("Children are not initialized.");
            }

            var east = x >= Bounds.MidX;
            var south = y >= Bounds.MidY;
            var index = (south ? 2 : 0) + (east ? 1 : 0);
            return _children[index];
        }

        /// <summary>
        /// 현재 노드를 네 개의 하위 사분면으로 분할합니다.
        /// </summary>
        private void Subdivide()
        {
            if (_children is not null)
            {
                return;
            }

            var halfWidth = Bounds.Width / 2f;
            var halfHeight = Bounds.Height / 2f;

            _children =
            [
                new QuadtreeNode(new Rect(Bounds.X, Bounds.Y, halfWidth, halfHeight), Depth + 1, MaxDepth, Capacity),
                new QuadtreeNode(new Rect(Bounds.X + halfWidth, Bounds.Y, halfWidth, halfHeight), Depth + 1, MaxDepth, Capacity),
                new QuadtreeNode(new Rect(Bounds.X, Bounds.Y + halfHeight, halfWidth, halfHeight), Depth + 1, MaxDepth, Capacity),
                new QuadtreeNode(new Rect(Bounds.X + halfWidth, Bounds.Y + halfHeight, halfWidth, halfHeight), Depth + 1, MaxDepth, Capacity)
            ];
        }
    }
}
