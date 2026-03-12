using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using AoiDemo.Web.Aoi;
using AoiDemo.Web.Infrastructure;
using AoiDemo.Web.Models;
using Microsoft.Extensions.Options;

namespace AoiDemo.Web.Runtime;

/// <summary>
/// 데모 월드의 틱 루프, WebSocket 세션, 플레이어/NPC 상태를 함께 관리하는 호스티드 서비스입니다.
/// </summary>
public sealed class DemoWorldService : BackgroundService
{
    private const float PlayerRadius = 18f;
    private const float NpcRadius = 14f;

    private readonly object _gate = new();
    private readonly ILogger<DemoWorldService> _logger;
    private readonly WorldOptions _worldOptions;
    private readonly IReadOnlyDictionary<AoiAlgorithm, IAoiStrategy> _strategies;
    private readonly Dictionary<string, ClientConnection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerRuntime> _playersByConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NpcRuntime> _npcsById = new(StringComparer.Ordinal);

    private AoiAlgorithm _currentAlgorithm = AoiAlgorithm.BruteForce;
    private long _tick;
    private int _nextPlayerNumber;
    private int _worldVersion = 1;
    private long _lastSentMessageCount;
    private long _lastSentBytes;

    /// <summary>
    /// 월드 설정과 AOI 전략 집합을 받아 데모 월드 서비스를 초기화합니다.
    /// </summary>
    /// <param name="worldOptions">맵 크기, AOI 반경, 속도 같은 월드 설정입니다.</param>
    /// <param name="strategies">알고리즘별 AOI 전략 구현 목록입니다.</param>
    /// <param name="logger">연결 해제나 전송 실패를 기록할 로거입니다.</param>
    public DemoWorldService(
        IOptions<WorldOptions> worldOptions,
        IEnumerable<IAoiStrategy> strategies,
        ILogger<DemoWorldService> logger)
    {
        _worldOptions = worldOptions.Value;
        _logger = logger;
        _strategies = strategies.ToDictionary(strategy => strategy.Algorithm);

        GenerateNpcsLocked(new Random(_worldOptions.Seed));
    }

    /// <summary>
    /// 승인된 WebSocket 연결 하나를 등록하고, 수신 루프가 끝날 때까지 세션을 관리합니다.
    /// </summary>
    /// <param name="socket">HTTP 업그레이드로 생성된 WebSocket 연결입니다.</param>
    /// <param name="cancellationToken">애플리케이션 종료나 요청 취소를 알리는 토큰입니다.</param>
    /// <returns>연결이 닫히거나 취소될 때 완료되는 비동기 작업입니다.</returns>
    public async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var connection = new ClientConnection(Guid.NewGuid().ToString("N"), socket);
        lock (_gate)
        {
            _connections[connection.ConnectionId] = connection;
        }

        try
        {
            await ReceiveLoopAsync(connection, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        finally
        {
            await RemoveConnectionAsync(connection, CancellationToken.None);
        }
    }

    /// <summary>
    /// 지정한 틱 간격으로 월드를 갱신하고 클라이언트에게 전송할 메시지를 생성합니다.
    /// </summary>
    /// <param name="stoppingToken">호스티드 서비스 종료를 알리는 토큰입니다.</param>
    /// <returns>백그라운드 틱 루프가 종료될 때 완료되는 작업입니다.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / _worldOptions.TickRate));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            IReadOnlyList<QueuedSend> queued;
            lock (_gate)
            {
                queued = AdvanceWorldLocked();
            }

            await SendAllAsync(queued, stoppingToken);
        }
    }

    /// <summary>
    /// 연결된 클라이언트에서 들어오는 텍스트 메시지를 읽고 type별 처리기로 분기합니다.
    /// </summary>
    /// <param name="connection">수신 루프를 실행할 클라이언트 연결입니다.</param>
    /// <param name="cancellationToken">수신 중단이나 애플리케이션 종료를 알리는 토큰입니다.</param>
    /// <returns>연결이 닫히거나 수신 루프가 끝날 때 완료되는 작업입니다.</returns>
    private async Task ReceiveLoopAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
        {
            var payload = await ReceiveTextAsync(connection.Socket, cancellationToken);
            if (payload is null)
            {
                return;
            }

            ClientMessage message;
            try
            {
                message = DemoJson.DeserializeClientMessage(payload);
            }
            catch (Exception ex)
            {
                await SafeSendAsync(connection, new ErrorServerMessage(ex.Message), cancellationToken);
                continue;
            }

            switch (message)
            {
                case JoinClientMessage joinMessage:
                    await HandleJoinAsync(connection, joinMessage, cancellationToken);
                    break;
                case MoveInputClientMessage moveInput:
                    await HandleMoveInputAsync(connection, moveInput, cancellationToken);
                    break;
                case ChangeAlgorithmClientMessage changeAlgorithm:
                    await HandleChangeAlgorithmAsync(connection, changeAlgorithm, cancellationToken);
                    break;
                case ResetWorldClientMessage:
                    await HandleResetWorldAsync(cancellationToken);
                    break;
                case PingClientMessage:
                    break;
            }
        }
    }

    /// <summary>
    /// join 메시지를 처리해 플레이어를 월드에 생성하고 환영 메시지를 돌려줍니다.
    /// </summary>
    /// <param name="connection">플레이어를 생성할 대상 연결입니다.</param>
    /// <param name="message">표시 이름을 포함한 join 메시지입니다.</param>
    /// <param name="cancellationToken">응답 전송 취소를 알리는 토큰입니다.</param>
    /// <returns>환영 메시지 또는 오류 응답 전송이 끝나면 완료되는 작업입니다.</returns>
    private async Task HandleJoinAsync(ClientConnection connection, JoinClientMessage message, CancellationToken cancellationToken)
    {
        WelcomeServerMessage? welcome = null;
        string? error = null;

        lock (_gate)
        {
            if (_playersByConnection.ContainsKey(connection.ConnectionId))
            {
                error = "The connection already joined the world.";
            }
            else
            {
                var playerNumber = ++_nextPlayerNumber;
                var name = NormalizeName(message.Name, playerNumber);
                var spawn = GetPlayerSpawn(playerNumber);
                var player = new PlayerRuntime(
                    $"pc-{playerNumber:000}",
                    name,
                    connection.ConnectionId,
                    spawn.X,
                    spawn.Y,
                    PlayerRadius);

                _playersByConnection[connection.ConnectionId] = player;
                connection.PlayerId = player.Id;

                welcome = new WelcomeServerMessage(
                    player.Id,
                    name,
                    _currentAlgorithm,
                    _worldOptions,
                    player.ToVisibleEntity());
            }
        }

        if (error is not null)
        {
            await SafeSendAsync(connection, new ErrorServerMessage(error), cancellationToken);
            return;
        }

        await SafeSendAsync(connection, welcome!, cancellationToken);
    }

    /// <summary>
    /// moveInput 메시지를 처리해 플레이어의 현재 이동 입력 벡터를 갱신합니다.
    /// </summary>
    /// <param name="connection">입력을 보낸 클라이언트 연결입니다.</param>
    /// <param name="message">정규화 전 또는 후의 이동 벡터를 담은 메시지입니다.</param>
    /// <param name="cancellationToken">오류 응답 전송 취소를 알리는 토큰입니다.</param>
    /// <returns>입력 반영 또는 오류 응답 전송이 끝나면 완료되는 작업입니다.</returns>
    private async Task HandleMoveInputAsync(ClientConnection connection, MoveInputClientMessage message, CancellationToken cancellationToken)
    {
        string? error = null;

        lock (_gate)
        {
            if (!_playersByConnection.TryGetValue(connection.ConnectionId, out var player))
            {
                error = "Join the world before sending input.";
            }
            else
            {
                player.SetInput(message.X, message.Y);
            }
        }

        if (error is not null)
        {
            await SafeSendAsync(connection, new ErrorServerMessage(error), cancellationToken);
        }
    }

    /// <summary>
    /// 알고리즘 전환 요청을 처리하고 월드를 리셋한 뒤 전체 클라이언트에 reset 메시지를 보냅니다.
    /// </summary>
    /// <param name="connection">알고리즘 변경을 요청한 클라이언트 연결입니다.</param>
    /// <param name="message">적용할 AOI 알고리즘을 담은 메시지입니다.</param>
    /// <param name="cancellationToken">브로드캐스트 취소를 알리는 토큰입니다.</param>
    /// <returns>월드 리셋 메시지 전송이 끝나면 완료되는 작업입니다.</returns>
    private async Task HandleChangeAlgorithmAsync(ClientConnection connection, ChangeAlgorithmClientMessage message, CancellationToken cancellationToken)
    {
        if (!_strategies.ContainsKey(message.Algorithm))
        {
            await SafeSendAsync(connection, new ErrorServerMessage($"Unsupported algorithm '{message.Algorithm}'."), cancellationToken);
            return;
        }

        IReadOnlyList<QueuedSend> queued;
        lock (_gate)
        {
            _currentAlgorithm = message.Algorithm;
            queued = ResetWorldLocked();
        }

        await SendAllAsync(queued, cancellationToken);
    }

    /// <summary>
    /// 현재 알고리즘을 유지한 채 월드 상태만 초기화하고 reset 메시지를 전송합니다.
    /// </summary>
    /// <param name="cancellationToken">브로드캐스트 취소를 알리는 토큰입니다.</param>
    /// <returns>리셋 브로드캐스트 전송이 끝나면 완료되는 작업입니다.</returns>
    private async Task HandleResetWorldAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QueuedSend> queued;
        lock (_gate)
        {
            queued = ResetWorldLocked();
        }

        await SendAllAsync(queued, cancellationToken);
    }

    /// <summary>
    /// 한 틱 동안 플레이어/NPC를 갱신하고 visibility delta와 metrics 메시지를 큐로 만듭니다.
    /// </summary>
    /// <returns>현재 틱 결과로 각 연결에 전송할 직렬화된 메시지 목록입니다.</returns>
    private IReadOnlyList<QueuedSend> AdvanceWorldLocked()
    {
        var deltaSeconds = 1f / _worldOptions.TickRate;
        _tick++;

        foreach (var player in _playersByConnection.Values)
        {
            player.Update(deltaSeconds, _worldOptions);
        }

        foreach (var npc in _npcsById.Values)
        {
            npc.Update(deltaSeconds, _worldOptions);
        }

        var players = _playersByConnection.Values
            .OrderBy(player => player.Id, StringComparer.Ordinal)
            .Select(player => player.ToPlayerState())
            .ToArray();

        var npcs = _npcsById.Values
            .OrderBy(npc => npc.Id, StringComparer.Ordinal)
            .Select(npc => npc.ToNpcState())
            .ToArray();

        var entities = new List<EntityState>(players.Length + npcs.Length);
        entities.AddRange(players.Select(player => new EntityState(player.Id, EntityKind.Player, player.Name, player.X, player.Y, player.Radius)));
        entities.AddRange(npcs.Select(npc => new EntityState(npc.Id, EntityKind.Npc, npc.Name, npc.X, npc.Y, npc.Radius)));

        if (players.Length == 0)
        {
            return Array.Empty<QueuedSend>();
        }

        var snapshot = new WorldSnapshot(_tick, _currentAlgorithm, entities, players, _worldOptions);
        var strategy = _strategies[_currentAlgorithm];
        var result = strategy.Compute(snapshot);
        var entityLookup = entities.ToDictionary(
            entity => entity.Id,
            VisibleEntityDto.FromEntity,
            StringComparer.Ordinal);

        var totalVisibleCount = 0;
        var queued = new List<QueuedSend>();

        foreach (var player in _playersByConnection.Values.OrderBy(player => player.Id, StringComparer.Ordinal))
        {
            var currentIds = result.VisibilityByPlayer.TryGetValue(player.Id, out var visibleSet)
                ? new HashSet<string>(visibleSet.EntityIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            totalVisibleCount += currentIds.Count;

            var delta = VisibilityDeltaComputer.Compute(player.VisibleEntityIds, currentIds);
            player.ReplaceVisibleIds(currentIds);

            var deltaMessage = new VisibilityDeltaServerMessage(
                _tick,
                player.ToVisibleEntity(),
                delta.Entered.Select(id => entityLookup[id]).ToArray(),
                delta.Updated.Select(id => entityLookup[id]).ToArray(),
                delta.Left);

            if (_connections.TryGetValue(player.ConnectionId, out var connection))
            {
                queued.Add(new QueuedSend(connection, DemoJson.SerializeServerMessage(deltaMessage)));
            }
        }

        var metrics = new MetricsSnapshot(
            _tick,
            _currentAlgorithm,
            entities.Count,
            totalVisibleCount,
            result.DistanceChecks,
            result.QueryCount,
            Math.Round(result.IndexBuildMs, 3),
            Math.Round(result.QueryMs, 3),
            _lastSentMessageCount,
            _lastSentBytes,
            result.DebugOverlay);

        foreach (var player in _playersByConnection.Values)
        {
            if (_connections.TryGetValue(player.ConnectionId, out var connection))
            {
                queued.Add(new QueuedSend(connection, DemoJson.SerializeServerMessage(new MetricsServerMessage(metrics))));
            }
        }

        _lastSentMessageCount = queued.Count;
        _lastSentBytes = queued.Sum(message => Encoding.UTF8.GetByteCount(message.Payload));

        return queued;
    }

    /// <summary>
    /// NPC와 플레이어 위치, 메트릭 카운터를 초기화하고 worldReset 브로드캐스트를 준비합니다.
    /// </summary>
    /// <returns>리셋 직후 각 연결로 보낼 worldReset 메시지 목록입니다.</returns>
    private IReadOnlyList<QueuedSend> ResetWorldLocked()
    {
        _tick = 0;
        _worldVersion++;
        _lastSentMessageCount = 0;
        _lastSentBytes = 0;

        GenerateNpcsLocked(new Random(_worldOptions.Seed));

        var queued = new List<QueuedSend>();
        var random = new Random(_worldOptions.Seed + 2048);
        foreach (var player in _playersByConnection.Values.OrderBy(player => player.Id, StringComparer.Ordinal))
        {
            var spawn = NextPoint(random, 80f);
            player.Reset(spawn.X, spawn.Y);
            if (_connections.TryGetValue(player.ConnectionId, out var connection))
            {
                var resetMessage = new WorldResetServerMessage(
                    _worldVersion,
                    _worldOptions.Seed,
                    _currentAlgorithm,
                    _worldOptions,
                    player.ToVisibleEntity());

                queued.Add(new QueuedSend(connection, DemoJson.SerializeServerMessage(resetMessage)));
            }
        }

        return queued;
    }

    /// <summary>
    /// 현재 시드 설정으로 NPC들을 새로 생성해 월드에 채웁니다.
    /// </summary>
    /// <param name="random">NPC 위치와 이동 방향을 결정할 난수 생성기입니다.</param>
    private void GenerateNpcsLocked(Random random)
    {
        _npcsById.Clear();
        for (var index = 0; index < _worldOptions.NpcCount; index++)
        {
            var point = NextPoint(random, 64f);
            var angle = random.NextSingle() * MathF.Tau;
            var npc = new NpcRuntime(
                $"npc-{index + 1:000}",
                $"NPC {index + 1:000}",
                point.X,
                point.Y,
                NpcRadius,
                MathF.Cos(angle),
                MathF.Sin(angle));

            _npcsById[npc.Id] = npc;
        }
    }

    /// <summary>
    /// 플레이어 번호를 기준으로 재현 가능한 초기 스폰 좌표를 계산합니다.
    /// </summary>
    /// <param name="playerNumber">스폰 위치를 구분하는 플레이어 순번입니다.</param>
    /// <returns>플레이어가 처음 등장할 월드 X/Y 좌표입니다.</returns>
    private (float X, float Y) GetPlayerSpawn(int playerNumber)
    {
        var random = new Random(_worldOptions.Seed + (playerNumber * 977));
        return NextPoint(random, 80f);
    }

    /// <summary>
    /// 월드 경계 안에서 패딩을 보장하는 임의 좌표를 생성합니다.
    /// </summary>
    /// <param name="random">좌표를 생성할 난수 생성기입니다.</param>
    /// <param name="padding">맵 가장자리에서 확보할 최소 여백 거리입니다.</param>
    /// <returns>월드 내부에 위치한 임의의 X/Y 좌표 쌍입니다.</returns>
    private (float X, float Y) NextPoint(Random random, float padding)
    {
        var x = padding + (random.NextSingle() * (_worldOptions.Width - (padding * 2f)));
        var y = padding + (random.NextSingle() * (_worldOptions.Height - (padding * 2f)));
        return (x, y);
    }

    /// <summary>
    /// 직렬화가 끝난 메시지 목록을 순차 전송하고 실패한 연결은 정리합니다.
    /// </summary>
    /// <param name="queued">연결별로 전송할 준비가 끝난 메시지 목록입니다.</param>
    /// <param name="cancellationToken">전송 취소를 알리는 토큰입니다.</param>
    /// <returns>모든 메시지 전송 시도가 끝나면 완료되는 작업입니다.</returns>
    private async Task SendAllAsync(IReadOnlyList<QueuedSend> queued, CancellationToken cancellationToken)
    {
        if (queued.Count == 0)
        {
            return;
        }

        var failedConnections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var send in queued)
        {
            try
            {
                await send.Connection.SendAsync(send.Payload, cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
            {
                _logger.LogDebug(ex, "Closing websocket connection {ConnectionId}.", send.Connection.ConnectionId);
                failedConnections.Add(send.Connection.ConnectionId);
            }
        }

        foreach (var connectionId in failedConnections)
        {
            ClientConnection? connection;
            lock (_gate)
            {
                _connections.TryGetValue(connectionId, out connection);
            }

            if (connection is not null)
            {
                await RemoveConnectionAsync(connection, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// 단일 연결에 서버 메시지를 최선 시도로 전송하고, 실패는 로그만 남깁니다.
    /// </summary>
    /// <param name="connection">메시지를 보낼 대상 연결입니다.</param>
    /// <param name="message">직렬화해서 전송할 서버 메시지입니다.</param>
    /// <param name="cancellationToken">전송 취소를 알리는 토큰입니다.</param>
    /// <returns>전송 시도가 끝나면 완료되는 작업입니다.</returns>
    private async Task SafeSendAsync(ClientConnection connection, ServerMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(DemoJson.SerializeServerMessage(message), cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
            _logger.LogDebug(ex, "Failed to send websocket payload to {ConnectionId}.", connection.ConnectionId);
        }
    }

    /// <summary>
    /// 연결과 플레이어 상태를 월드에서 제거하고 가능한 경우 close handshake를 수행합니다.
    /// </summary>
    /// <param name="connection">정리할 대상 연결입니다.</param>
    /// <param name="cancellationToken">소켓 종료 시도를 취소할 수 있는 토큰입니다.</param>
    /// <returns>연결 정리가 끝나면 완료되는 작업입니다.</returns>
    private async Task RemoveConnectionAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        var shouldClose = false;

        lock (_gate)
        {
            shouldClose = _connections.Remove(connection.ConnectionId);
            _playersByConnection.Remove(connection.ConnectionId);
        }

        if (!shouldClose)
        {
            return;
        }

        if (connection.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await connection.Socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection closed",
                    cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                _logger.LogDebug(ex, "Socket close handshake failed for {ConnectionId}.", connection.ConnectionId);
            }
        }

        connection.Dispose();
    }

    /// <summary>
    /// 원시 플레이어 이름을 화면 표시용 이름으로 정리하고 길이를 제한합니다.
    /// </summary>
    /// <param name="rawName">클라이언트가 보낸 원본 이름입니다.</param>
    /// <param name="playerNumber">이름이 비었을 때 사용할 플레이어 순번입니다.</param>
    /// <returns>공백 제거와 길이 제한이 적용된 표시 이름입니다.</returns>
    private static string NormalizeName(string? rawName, int playerNumber)
    {
        var fallback = $"Player {playerNumber:000}";
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return fallback;
        }

        var normalized = rawName.Trim();
        return normalized.Length <= 24 ? normalized : normalized[..24];
    }

    /// <summary>
    /// WebSocket 프레임을 끝까지 읽어 하나의 텍스트 메시지 문자열로 합칩니다.
    /// </summary>
    /// <param name="socket">수신 대상 WebSocket 연결입니다.</param>
    /// <param name="cancellationToken">수신 중단을 알리는 토큰입니다.</param>
    /// <returns>완전한 텍스트 메시지 문자열 또는 close 프레임이면 <see langword="null" />입니다.</returns>
    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidOperationException("Only text WebSocket messages are supported.");
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed record QueuedSend(ClientConnection Connection, string Payload);

    private sealed class ClientConnection(string connectionId, WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public string ConnectionId { get; } = connectionId;
        public WebSocket Socket { get; } = socket;
        public string? PlayerId { get; set; }

        /// <summary>
        /// 한 연결에 대한 송신 순서를 보장하면서 텍스트 페이로드를 WebSocket으로 보냅니다.
        /// </summary>
        /// <param name="payload">UTF-8 텍스트로 전송할 JSON 페이로드입니다.</param>
        /// <param name="cancellationToken">송신 취소를 알리는 토큰입니다.</param>
        /// <returns>전송이 끝나면 완료되는 작업입니다.</returns>
        public async Task SendAsync(string payload, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 연결이 보유한 송신 잠금 리소스를 해제합니다.
        /// </summary>
        public void Dispose() => _sendLock.Dispose();
    }

    private sealed class PlayerRuntime(
        string id,
        string name,
        string connectionId,
        float x,
        float y,
        float radius)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string ConnectionId { get; } = connectionId;
        public float Radius { get; } = radius;
        public float X { get; private set; } = x;
        public float Y { get; private set; } = y;
        public float InputX { get; private set; }
        public float InputY { get; private set; }
        public HashSet<string> VisibleEntityIds { get; private set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// 클라이언트 입력 벡터를 정규화해 다음 틱 이동 계산에 사용할 상태로 저장합니다.
        /// </summary>
        /// <param name="x">수평 이동 입력 성분입니다.</param>
        /// <param name="y">수직 이동 입력 성분입니다.</param>
        public void SetInput(float x, float y)
        {
            var length = MathF.Sqrt((x * x) + (y * y));
            if (length > 1f)
            {
                x /= length;
                y /= length;
            }

            InputX = x;
            InputY = y;
        }

        /// <summary>
        /// 저장된 입력 벡터를 사용해 플레이어 위치를 한 틱 전진시킵니다.
        /// </summary>
        /// <param name="deltaSeconds">이번 틱이 표현하는 시간 간격(초)입니다.</param>
        /// <param name="world">속도와 맵 경계를 포함한 월드 설정입니다.</param>
        public void Update(float deltaSeconds, WorldOptions world)
        {
            X = Math.Clamp(X + (InputX * world.PlayerSpeed * deltaSeconds), Radius, world.Width - Radius);
            Y = Math.Clamp(Y + (InputY * world.PlayerSpeed * deltaSeconds), Radius, world.Height - Radius);
        }

        /// <summary>
        /// 현재 플레이어가 보고 있는 엔티티 id 집합을 최신 결과로 교체합니다.
        /// </summary>
        /// <param name="currentIds">이번 틱 계산으로 얻은 최신 visible id 집합입니다.</param>
        public void ReplaceVisibleIds(HashSet<string> currentIds) => VisibleEntityIds = currentIds;

        /// <summary>
        /// 플레이어 위치와 입력 상태를 초기화하고 이전 AOI 캐시를 비웁니다.
        /// </summary>
        /// <param name="x">리셋 후 배치할 X 좌표입니다.</param>
        /// <param name="y">리셋 후 배치할 Y 좌표입니다.</param>
        public void Reset(float x, float y)
        {
            X = x;
            Y = y;
            InputX = 0f;
            InputY = 0f;
            VisibleEntityIds.Clear();
        }

        /// <summary>
        /// 현재 런타임 플레이어 상태를 전송/계산용 불변 DTO로 변환합니다.
        /// </summary>
        /// <returns>현재 위치와 입력이 반영된 플레이어 상태 DTO입니다.</returns>
        public PlayerState ToPlayerState() => new(Id, Name, X, Y, Radius, InputX, InputY);

        /// <summary>
        /// 현재 플레이어 상태를 클라이언트 전송용 visible 엔티티 DTO로 변환합니다.
        /// </summary>
        /// <returns>클라이언트가 자기 자신을 렌더링할 때 사용할 엔티티 DTO입니다.</returns>
        public VisibleEntityDto ToVisibleEntity() => VisibleEntityDto.FromPlayer(ToPlayerState());
    }

    private sealed class NpcRuntime(
        string id,
        string name,
        float x,
        float y,
        float radius,
        float velocityX,
        float velocityY)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public float Radius { get; } = radius;
        public float X { get; private set; } = x;
        public float Y { get; private set; } = y;
        public float VelocityX { get; private set; } = velocityX;
        public float VelocityY { get; private set; } = velocityY;

        /// <summary>
        /// NPC의 현재 이동 벡터를 적용하고 맵 경계에 닿으면 반사시킵니다.
        /// </summary>
        /// <param name="deltaSeconds">이번 틱이 표현하는 시간 간격(초)입니다.</param>
        /// <param name="world">속도와 맵 경계를 포함한 월드 설정입니다.</param>
        public void Update(float deltaSeconds, WorldOptions world)
        {
            X += VelocityX * world.NpcSpeed * deltaSeconds;
            Y += VelocityY * world.NpcSpeed * deltaSeconds;

            if (X <= Radius || X >= world.Width - Radius)
            {
                VelocityX *= -1f;
                X = Math.Clamp(X, Radius, world.Width - Radius);
            }

            if (Y <= Radius || Y >= world.Height - Radius)
            {
                VelocityY *= -1f;
                Y = Math.Clamp(Y, Radius, world.Height - Radius);
            }
        }

        /// <summary>
        /// 현재 NPC 런타임 상태를 계산/전송용 DTO로 변환합니다.
        /// </summary>
        /// <returns>현재 위치와 이동 벡터를 담은 NPC 상태 DTO입니다.</returns>
        public NpcState ToNpcState() => new(Id, Name, X, Y, Radius, VelocityX, VelocityY);
    }
}
