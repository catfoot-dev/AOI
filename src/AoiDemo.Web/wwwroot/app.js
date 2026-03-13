const state = {
  socket: null,
  connectionStatus: "connecting",
  playerId: null,
  playerLabel: "-",
  algorithm: "bruteForce",
  world: null,
  self: null,
  visible: new Map(),
  metrics: null,
  overlay: { mode: "none", cellSize: 0, rectangles: [] },
  input: { up: false, down: false, left: false, right: false },
  reconnectTimer: null,
};
const CHART_STACK = 140;
const chartData = {
  entities: Array.from({ length: CHART_STACK }),
  visible: Array.from({ length: CHART_STACK }),
  distanceChecks: Array.from({ length: CHART_STACK }),
  queries: Array.from({ length: CHART_STACK }),
  indexBuild: Array.from({ length: CHART_STACK }),
  query: Array.from({ length: CHART_STACK }),
  messages: Array.from({ length: CHART_STACK }),
  bytes: Array.from({ length: CHART_STACK }),
};

const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");
const resetButton = document.getElementById("reset-button");
const overlayToggle = document.getElementById("overlay-toggle");

const chartEntities = document.getElementById("chart-entities");
const ctxEntities = chartEntities.getContext("2d");
const chartVisible = document.getElementById("chart-visible");
const ctxVisible = chartVisible.getContext("2d");
const chartDistanceChecks = document.getElementById("chart-distance-checks");
const ctxDistanceChecks = chartDistanceChecks.getContext("2d");
const chartQueries = document.getElementById("chart-queries");
const ctxQueries = chartQueries.getContext("2d");
const chartIndexBuild = document.getElementById("chart-index-build");
const ctxIndexBuild = chartIndexBuild.getContext("2d");
const chartQuery = document.getElementById("chart-query");
const ctxQuery = chartQuery.getContext("2d");
const chartMessages = document.getElementById("chart-messages");
const ctxMessages = chartMessages.getContext("2d");
const chartBytes = document.getElementById("chart-bytes");
const ctxBytes = chartBytes.getContext("2d");

chartEntities.width = chartVisible.width = chartDistanceChecks.width = chartQueries.width =
    chartIndexBuild.width = chartQuery.width = chartMessages.width = chartBytes.width = '140';
chartEntities.height = chartVisible.height = chartDistanceChecks.height = chartQueries.height =
    chartIndexBuild.height = chartQuery.height = chartMessages.height = chartBytes.height = '40';

const statusNodes = {
  connection: document.getElementById("connection-status"),
  player: document.getElementById("player-label"),
  visible: document.getElementById("visible-count"),
  algorithm: document.getElementById("algorithm"),
  tick: document.getElementById("tick"),
  entityCount: document.getElementById("entity-count"),
  totalVisibleCount: document.getElementById("total-visible-count"),
  distanceChecks: document.getElementById("distance-checks"),
  queryCount: document.getElementById("query-count"),
  indexBuildMs: document.getElementById("index-build-ms"),
  queryMs: document.getElementById("query-ms"),
  messageCount: document.getElementById("message-count"),
  bytesSent: document.getElementById("bytes-sent"),
  metrics: document.getElementById("metrics-items"),
};

/**
 * 캔버스 크기와 입력 핸들러를 준비하고 초기 WebSocket 연결 및 렌더 루프를 시작합니다.
 */
function boot() {
  resizeCanvas();
  window.addEventListener("resize", resizeCanvas);
  window.addEventListener("keydown", handleKeyChange(true));
  window.addEventListener("keyup", handleKeyChange(false));

  algorithm.addEventListener("change", event => {
    send({type: "changeAlgorithm", algorithm: event.target.value});
  });

  resetButton.addEventListener("click", () => {
    send({ type: "resetWorld" });
  });

  connect();
  requestAnimationFrame(render);
}

/**
 * WebSocket 연결을 열고 서버 메시지 처리 및 자동 재연결 핸들러를 등록합니다.
 */
function connect() {
  const protocol = location.protocol === "https:" ? "wss:" : "ws:";
  const socket = new WebSocket(`${protocol}//${location.host}/ws`);
  state.socket = socket;
  setConnectionStatus("connecting");

  socket.addEventListener("open", () => {
    setConnectionStatus("connected");
    const name = `PC ${Math.random().toString(16).slice(2, 6)}`;
    send({ type: "join", name });
  });

  socket.addEventListener("message", (event) => {
    const message = JSON.parse(event.data);
    handleServerMessage(message);
  });

  socket.addEventListener("close", () => {
    setConnectionStatus("disconnected");
    state.visible.clear();
    state.metrics = null;
    if (state.reconnectTimer) {
      clearTimeout(state.reconnectTimer);
    }

    state.reconnectTimer = window.setTimeout(connect, 1500);
  });
}

/**
 * 서버가 보낸 메시지 type에 따라 로컬 상태와 UI를 갱신합니다.
 *
 * @param {object} message 서버에서 수신한 JSON 메시지 객체입니다.
 */
function handleServerMessage(message) {
  switch (message.type) {
    case "welcome":
      state.playerId = message.playerId;
      state.playerLabel = message.displayName;
      state.algorithm = message.algorithm;
      state.world = message.world;
      state.self = message.self;
      state.visible.clear();
      algorithm.value = message.algorithm;
      break;
    case "worldReset":
      state.algorithm = message.algorithm;
      state.world = message.world;
      state.self = message.self;
      state.visible.clear();
      state.metrics = null;
      state.overlay = { mode: "none", cellSize: 0, rectangles: [] };
      algorithm.value = message.algorithm;
      chartData.entities = Array.from({ length: CHART_STACK });
      chartData.visible = Array.from({ length: CHART_STACK });
      chartData.distanceChecks = Array.from({ length: CHART_STACK });
      chartData.queries = Array.from({ length: CHART_STACK });
      chartData.indexBuild = Array.from({ length: CHART_STACK });
      chartData.query = Array.from({ length: CHART_STACK });
      chartData.messages = Array.from({ length: CHART_STACK });
      chartData.bytes = Array.from({ length: CHART_STACK });
      break;
    case "visibilityDelta":
      state.self = message.self;
      for (const entity of message.entered) {
        state.visible.set(entity.id, entity);
      }
      for (const entity of message.updated) {
        state.visible.set(entity.id, entity);
      }
      for (const entityId of message.left) {
        state.visible.delete(entityId);
      }
      break;
    case "metrics":
      state.metrics = message.snapshot;
      state.overlay = message.snapshot.debugOverlay ?? { mode: "none", cellSize: 0, rectangles: [] };
      state.algorithm = message.snapshot.algorithm;
      renderMetrics();
      break;
    case "error":
      setConnectionStatus(`error: ${message.message}`);
      break;
  }

  renderStatus();
}

/**
 * 현재 연결 상태와 가시 엔티티 수를 상태 패널 텍스트에 반영합니다.
 */
function renderStatus() {
  statusNodes.connection.textContent = state.connectionStatus;
  statusNodes.player.textContent = state.playerLabel;
  statusNodes.visible.textContent = String(state.visible.size);
  statusNodes.tick.textContent = state.metrics?.tick ?? "0";
}

/**
 * 최신 서버 메트릭 스냅샷을 카드 목록으로 렌더링합니다.
 */
function renderMetrics() {
  const snapshot = state.metrics;
  statusNodes.entityCount.textContent = snapshot?.entityCount ?? "0";
  statusNodes.totalVisibleCount.textContent = snapshot?.totalVisibleCount ?? "0";
  statusNodes.distanceChecks.textContent = snapshot?.distanceChecks ?? "0";
  statusNodes.queryCount.textContent = snapshot?.queryCount ?? "0";
  statusNodes.indexBuildMs.textContent = snapshot?.indexBuildMs.toFixed(3) ?? "0.0";
  statusNodes.queryMs.textContent = snapshot?.queryMs.toFixed(3) ?? "0.0";
  statusNodes.messageCount.textContent = snapshot?.messageCount ?? "0";
  statusNodes.bytesSent.textContent = snapshot?.bytesSent ?? "0";

  chartData.entities.shift();
  chartData.entities.push(snapshot?.entityCount);
  chartData.visible.shift();
  chartData.visible.push(snapshot?.totalVisibleCount);
  chartData.distanceChecks.shift();
  chartData.distanceChecks.push(snapshot?.distanceChecks);
  chartData.queries.shift();
  chartData.queries.push(snapshot?.queryCount);
  chartData.indexBuild.shift();
  chartData.indexBuild.push(snapshot?.indexBuildMs);
  chartData.query.shift();
  chartData.query.push(snapshot?.queryMs);
  chartData.messages.shift();
  chartData.messages.push(snapshot?.messageCount);
  chartData.bytes.shift();
  chartData.bytes.push(snapshot?.bytesSent);
}

/**
 * 카메라 중심과 줌을 계산해 현재 월드와 엔티티를 한 프레임 그립니다.
 */
function render() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);

  if (!state.world || !state.self) {
    drawPlaceholder();
    requestAnimationFrame(render);
    return;
  }

  const viewRadius = state.world.aoiRadius * 1.9;
  const zoom = Math.min(canvas.width / (viewRadius * 2), canvas.height / (viewRadius * 2));
  const originX = canvas.width / 2 - state.self.x * zoom;
  const originY = canvas.height / 2 - state.self.y * zoom;
  const worldToScreen = (x, y) => ({
    x: x * zoom + originX,
    y: y * zoom + originY,
  });

  drawWorldBounds(worldToScreen, zoom);
  drawDebugOverlay(worldToScreen, zoom);
  drawAoiCircle(worldToScreen, zoom);
  for (const entity of state.visible.values()) {
    drawEntity(entity, entity.kind === "player" ? "#8fc0ff" : "#80f1d3", worldToScreen, zoom);
  }
  drawEntity(state.self, "#bfdfff", worldToScreen, zoom, true);
  drawChart();
  requestAnimationFrame(render);
}

/**
 * 서버 연결 전 또는 self 정보가 없을 때 표시할 안내 문구를 그립니다.
 */
function drawPlaceholder() {
  ctx.fillStyle = "rgba(244,245,235,0.7)";
  ctx.font = '28px "DIN Alternate", "Franklin Gothic Medium", sans-serif';
  ctx.fillText("Connecting to AOI world...", 40, 60);
}

/**
 * 월드 전체 경계를 현재 카메라 좌표계 기준으로 그립니다.
 *
 * @param {Function} worldToScreen 월드 좌표를 화면 좌표로 바꾸는 함수입니다.
 * @param {number} zoom 월드 단위를 화면에 투영할 확대 비율입니다.
 */
function drawWorldBounds(worldToScreen, zoom) {
  const topLeft = worldToScreen(0, 0);
  ctx.save();
  ctx.strokeStyle = "rgba(255,255,255,0.08)";
  ctx.lineWidth = 2;
  ctx.strokeRect(topLeft.x, topLeft.y, state.world.width * zoom, state.world.height * zoom);
  ctx.restore();
}

/**
 * 선택한 AOI 알고리즘에 맞는 grid 또는 quadtree 디버그 오버레이를 그립니다.
 *
 * @param {Function} worldToScreen 월드 좌표를 화면 좌표로 바꾸는 함수입니다.
 * @param {number} zoom 월드 단위를 화면에 투영할 확대 비율입니다.
 */
function drawDebugOverlay(worldToScreen, zoom) {
  if (state.overlay.mode === "grid" && overlayToggle.checked) {
    const step = state.overlay.cellSize || state.world.aoiRadius;
    const left = Math.max(0, Math.floor((state.self.x - state.world.aoiRadius * 2) / step) * step);
    const right = Math.min(state.world.width, Math.ceil((state.self.x + state.world.aoiRadius * 2) / step) * step);
    const top = Math.max(0, Math.floor((state.self.y - state.world.aoiRadius * 2) / step) * step);
    const bottom = Math.min(state.world.height, Math.ceil((state.self.y + state.world.aoiRadius * 2) / step) * step);

    ctx.save();
    ctx.strokeStyle = "rgba(127,197,255,0.12)";
    ctx.lineWidth = 1;
    for (let x = left; x <= right; x += step) {
      const p1 = worldToScreen(x, top);
      const p2 = worldToScreen(x, bottom);
      ctx.beginPath();
      ctx.moveTo(p1.x, p1.y);
      ctx.lineTo(p2.x, p2.y);
      ctx.stroke();
    }
    for (let y = top; y <= bottom; y += step) {
      const p1 = worldToScreen(left, y);
      const p2 = worldToScreen(right, y);
      ctx.beginPath();
      ctx.moveTo(p1.x, p1.y);
      ctx.lineTo(p2.x, p2.y);
      ctx.stroke();
    }
    ctx.restore();
  }

  if (state.overlay.mode === "quadtree" && overlayToggle.checked) {
    ctx.save();
    ctx.strokeStyle = "rgba(112,179,255,0.24)";
    ctx.lineWidth = 1;
    for (const rect of state.overlay.rectangles || []) {
      const p = worldToScreen(rect.x, rect.y);
      ctx.strokeRect(p.x, p.y, rect.width * zoom, rect.height * zoom);
    }
    ctx.restore();
  }
}

/**
 * 현재 플레이어를 중심으로 한 AOI 반경 원을 시각화합니다.
 *
 * @param {Function} worldToScreen 월드 좌표를 화면 좌표로 바꾸는 함수입니다.
 * @param {number} zoom 월드 단위를 화면에 투영할 확대 비율입니다.
 */
function drawAoiCircle(worldToScreen, zoom) {
  const center = worldToScreen(state.self.x, state.self.y);
  ctx.save();
  ctx.strokeStyle = "rgba(127,197,255,0.45)";
  ctx.fillStyle = "rgba(127,187,255,0.07)";
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.arc(center.x, center.y, state.world.aoiRadius * zoom, 0, Math.PI * 2);
  ctx.fill();
  ctx.stroke();
  ctx.restore();
}

/**
 * 플레이어 또는 NPC 하나를 지정한 색상과 레이블로 렌더링합니다.
 *
 * @param {object} entity 위치와 이름, 반경을 담은 엔티티 객체입니다.
 * @param {string} color 엔티티 원형 마커에 사용할 색상 문자열입니다.
 * @param {Function} worldToScreen 월드 좌표를 화면 좌표로 바꾸는 함수입니다.
 * @param {number} zoom 월드 단위를 화면에 투영할 확대 비율입니다.
 * @param {boolean} [isSelf=false] 자기 자신 엔티티인지 여부입니다.
 */
function drawEntity(entity, color, worldToScreen, zoom, isSelf = false) {
  const point = worldToScreen(entity.x, entity.y);
  ctx.save();
  ctx.fillStyle = color;
  ctx.shadowColor = color;
  ctx.shadowBlur = isSelf ? 18 : 10;
  ctx.beginPath();
  ctx.arc(point.x, point.y, entity.radius * zoom, 0, Math.PI * 2);
  ctx.fill();

  ctx.shadowBlur = 0;
  ctx.fillStyle = "rgba(244,245,235,0.9)";
  ctx.font = '14px "Avenir Next", "Segoe UI", sans-serif';
  ctx.fillText(entity.name, point.x + 12, point.y - 12);
  ctx.restore();
}

function drawChart() {
  drawHudChart(ctxEntities, chartData.entities);
  drawHudChart(ctxVisible, chartData.visible);
  drawHudChart(ctxDistanceChecks, chartData.distanceChecks);
  drawHudChart(ctxQueries, chartData.queries);
  drawHudChart(ctxIndexBuild, chartData.indexBuild);
  drawHudChart(ctxQuery, chartData.query);
  drawHudChart(ctxMessages, chartData.messages);
  drawHudChart(ctxBytes, chartData.bytes);
}

function drawHudChart(ctx, data) {
  const max = Math.max(...data.filter(Number)) || 10;
  const ratio = 35 / max;
  ctx.save();
  ctx.clearRect(0, 0, 140, 40);
  for (let i = 0; i < data.length; i++) {
    if (data[i] === undefined || data[i] === null) continue;
    ctx.lineWidth = 0;
    ctx.fillStyle = "rgba(48,192,48,0.15)";
    ctx.fillRect(i, 39 - data[i] * ratio, 1, 40);
    ctx.lineWidth = 1;
    ctx.fillStyle = "rgba(52,211,52,0.8)";
    ctx.fillRect(i, 39 - data[i] * ratio, 1, 1);
  }
  ctx.restore();
}

/**
 * CSS 픽셀 크기에 맞춰 캔버스의 실제 드로잉 버퍼와 변환 행렬을 갱신합니다.
 */
function resizeCanvas() {
  canvas.width = window.innerWidth;
  canvas.height = window.innerHeight;
  ctx.setTransform(1, 0, 0, 1, 0, 0);
}

/**
 * 키다운 또는 키업 이벤트를 받아 이동 상태를 갱신할 핸들러를 생성합니다.
 *
 * @param {boolean} isPressed 해당 이벤트가 눌림 상태인지 여부입니다.
 * @returns {Function} 전달받은 키 이벤트를 처리하는 실제 핸들러 함수입니다.
 */
function handleKeyChange(isPressed) {
  return (event) => {
    const key = event.key.toLowerCase();
    if (!(key in keyMap)) {
      return;
    }

    state.input[keyMap[key]] = isPressed;
    event.preventDefault();
    sendInput();
  };
}

/**
 * 현재 방향키/WASD 입력 상태를 정규화된 이동 벡터로 변환해 서버에 전송합니다.
 */
function sendInput() {
  const x = Number(state.input.right) - Number(state.input.left);
  const y = Number(state.input.down) - Number(state.input.up);
  const length = Math.hypot(x, y) || 1;
  send({
    type: "moveInput",
    x: x / length,
    y: y / length,
  });
}

/**
 * WebSocket 연결이 열려 있을 때만 지정한 페이로드를 JSON으로 직렬화해 보냅니다.
 *
 * @param {object} payload 서버에 보낼 클라이언트 메시지 페이로드입니다.
 */
function send(payload) {
  if (!state.socket || state.socket.readyState !== WebSocket.OPEN) {
    return;
  }

  state.socket.send(JSON.stringify(payload));
}

/**
 * 연결 상태 문자열을 갱신하고 상태 패널을 즉시 다시 그립니다.
 *
 * @param {string} status 화면에 표시할 현재 연결 상태 문자열입니다.
 */
function setConnectionStatus(status) {
  state.connectionStatus = status;
  renderStatus();
}

const keyMap = {
  arrowup: "up",
  w: "up",
  arrowdown: "down",
  s: "down",
  arrowleft: "left",
  a: "left",
  arrowright: "right",
  d: "right",
};

boot();
