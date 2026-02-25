using UnityEngine;
using Mirror;
using Mirror.SimpleWeb;
using UnityEngine.Serialization;
using System.Collections;
using System;
using System.Net;

namespace Network
{
    /// <summary>
    /// Mirror NetworkManager를 상속한 DigWar 전용 네트워크 매니저.
    /// 접속/연결 해제/스폰 등 네트워크 라이프사이클을 관리한다.
    /// 
    /// [설정 방법]
    /// 1. 씬의 빈 게임오브젝트에 이 컴포넌트를 추가
    /// 2. Transport에 SimpleWebTransport를 할당
    /// 3. Player Prefab은 NetworkIdentity가 있는 프리팹을 할당
    /// </summary>
    public class DigWarNetworkManager : NetworkManager
    {
        private const int FREE_MVP_HARD_CAP = 24;
        private const int FREE_MVP_SEND_RATE = 15;
        private const double FREE_MVP_SNAPSHOT_BUFFER_TIME_MULTIPLIER = 4.0d;
        private const float FREE_MVP_SNAPSHOT_DYNAMIC_TOLERANCE = 2.0f;
        private const int FREE_MVP_SNAPSHOT_BUFFER_LIMIT = 64;
        private const float FREE_MVP_SNAPSHOT_CATCHUP_NEGATIVE_THRESHOLD = -1.5f;
        private const float FREE_MVP_SNAPSHOT_CATCHUP_POSITIVE_THRESHOLD = 1.5f;
        private const double FREE_MVP_SNAPSHOT_CATCHUP_SPEED = 0.015d;
        private const double FREE_MVP_SNAPSHOT_SLOWDOWN_SPEED = 0.03d;
        private const int FREE_MVP_SNAPSHOT_DRIFT_EMA_DURATION = 2;
        private const int FREE_MVP_SNAPSHOT_DELIVERY_EMA_DURATION = 3;
        private const bool FREE_MVP_PLAYER_ONLY_SYNC_ON_CHANGE = false;
        private const float FREE_MVP_PLAYER_ONLY_SYNC_CORRECTION_MULTIPLIER = 3f;
        private const bool FREE_MVP_PLAYER_USE_FIXED_UPDATE = false;
        private const bool FREE_MVP_PLAYER_INTERPOLATE_POSITION = true;
        private const bool FREE_MVP_PLAYER_INTERPOLATE_ROTATION = true;
        private const float FREE_MVP_PLAYER_POSITION_PRECISION = 0.003f;
        private const float FREE_MVP_PLAYER_ROTATION_SENSITIVITY = 0.003f;
        private const int FREE_MVP_SIMPLEWEB_SEND_TIMEOUT_MS = 12000;
        private const int FREE_MVP_SIMPLEWEB_RECEIVE_TIMEOUT_MS = 60000;
        private const int FREE_MVP_SIMPLEWEB_SERVER_MAX_MSGS_PER_TICK = 20000;
        private const int FREE_MVP_SIMPLEWEB_CLIENT_MAX_MSGS_PER_TICK = 4000;
        private const bool FREE_MVP_SIMPLEWEB_NO_DELAY = true;
        private const string WEB_QUERY_SERVER_KEY = "server";
        private const string WEB_QUERY_PORT_KEY = "port";
        private const string WEB_QUERY_WSS_KEY = "wss";
        private const string CMD_MODE_KEY = "dw-mode";
        private const string CMD_ADDRESS_KEY = "dw-address";
        private const string CMD_PORT_KEY = "dw-port";
        private const string CMD_WSS_KEY = "dw-wss";

        [Header("DigWar Settings")]
        [Tooltip("무료 MVP 최대 접속자 수 (고정: 24)")]
        [SerializeField] private int _maxPlayers = FREE_MVP_HARD_CAP;

        #pragma warning disable CS0414
        [Tooltip("봇 수 (서버에서만 스폰)")]
        [SerializeField] private int _botCount = 5;
        #pragma warning restore CS0414

        private enum AutoStartMode
        {
            Disabled = 0,
            Host = 1,
            Client = 2,
            Server = 3,
        }

        [Header("Auto Start Policy")]
        [FormerlySerializedAs("_autoStartHost")]
        [Tooltip("자동 시작 정책 사용 여부")]
        [SerializeField] private bool _enableAutoStart = true;

#if UNITY_EDITOR
        [Tooltip("에디터 원본 인스턴스 시작 모드")]
        [SerializeField] private AutoStartMode _editorOriginalMode = AutoStartMode.Host;

        [Tooltip("ParrelSync Clone 인스턴스 시작 모드")]
        [SerializeField] private AutoStartMode _editorCloneMode = AutoStartMode.Client;
#endif

        #pragma warning disable CS0414
        [Tooltip("빌드 실행 시작 모드")]
        [SerializeField] private AutoStartMode _buildMode = AutoStartMode.Client;
        #pragma warning restore CS0414

        [Tooltip("Client 모드에서 기본 접속 주소")]
        [SerializeField] private string _defaultClientAddress = "localhost";

        [Tooltip("에디터 Clone 감지 토큰 (Application.dataPath 기준)")]
        [SerializeField] private string _clonePathToken = "_clone";

        public static DigWarNetworkManager Instance { get; private set; }
        public static event Action<ClientConnectionStatus> OnClientConnectionStatusChanged;

        public readonly struct ClientConnectionStatus
        {
            public readonly string Message;
            public readonly bool IsError;

            public ClientConnectionStatus(string message, bool isError)
            {
                Message = message;
                IsError = isError;
            }
        }

        public static ClientConnectionStatus LatestClientConnectionStatus { get; private set; }

        private struct ServerRejectMessage : NetworkMessage
        {
            public string Reason;
        }

        private string _pendingDisconnectReason;
        private AutoStartMode _runtimeAutoStartMode = AutoStartMode.Disabled;

        public override void OnValidate()
        {
            base.OnValidate();
            ApplyFreeMvpRuntimeProfile(logWarnings: false);
        }

        public override void Awake()
        {
            base.Awake();
            Instance = this;
            ApplyFreeMvpRuntimeProfile(logWarnings: true);
        }

        public override void Start()
        {
            base.Start();
            ApplyWebClientOverridesFromUrl();
            ApplyCommandLineOverrides();

            if (!_enableAutoStart || NetworkServer.active || NetworkClient.active) return;

            AutoStartMode mode = ResolveAutoStartMode(out string contextLabel, out bool isCloneInstance);
            ApplyAutoStartMode(mode, contextLabel, isCloneInstance);
            return;
        }

        // ===== 서버 콜백 =====

        public override void OnStartServer()
        {
            base.OnStartServer();
            Debug.Log($"[Network] Server started. MaxConnections={maxConnections}");

            if (ShouldAutoStartGameplayLoop())
                TryStartGameplayLoopForDedicatedServer();
        }

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            if (numPlayers >= maxConnections)
            {
                string reason = BuildServerFullReason();
                conn.Send(new ServerRejectMessage { Reason = reason });
                StartCoroutine(DisconnectNextFrame(conn));
                Debug.LogWarning($"[Network] Connection rejected (server full): {conn.address} | Players={numPlayers}/{maxConnections}");
                return;
            }

            base.OnServerConnect(conn);
        }

        /// <summary>
        /// 서버가 시작될 때 호출된다.
        /// 게임 라운드 시작은 MainMenuUI가 담당하고,
        /// 네트워크 매니저는 세션 연결/스폰만 담당한다.
        /// </summary>
        private AutoStartMode ResolveAutoStartMode(out string contextLabel, out bool isCloneInstance)
        {
#if UNITY_EDITOR
            isCloneInstance = IsEditorCloneInstance();
            contextLabel = isCloneInstance ? "EditorClone" : "EditorOriginal";
            return isCloneInstance ? _editorCloneMode : _editorOriginalMode;
#else
            if (TryResolveCommandLineAutoStartMode(out AutoStartMode commandLineMode))
            {
                isCloneInstance = false;
                contextLabel = "Build(CommandLine)";
                return commandLineMode;
            }

#if UNITY_SERVER
            isCloneInstance = false;
            contextLabel = "Build(ServerDefault)";
            return AutoStartMode.Server;
#else
            isCloneInstance = false;
            contextLabel = "Build";
            return _buildMode;
#endif
#endif
        }

        private bool IsEditorCloneInstance()
        {
            string token = (_clonePathToken ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(token)) return false;

            string projectPath = Application.dataPath;
            return projectPath.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ApplyAutoStartMode(AutoStartMode mode, string contextLabel, bool isCloneInstance)
        {
            _runtimeAutoStartMode = mode;
            Debug.Log(
                $"[Network] AutoStart Policy => Context={contextLabel}, Clone={isCloneInstance}, Mode={mode}");

            switch (mode)
            {
                case AutoStartMode.Disabled:
                    return;

                case AutoStartMode.Host:
                    StartHost();
                    return;

                case AutoStartMode.Client:
                    string defaultAddress = string.IsNullOrWhiteSpace(_defaultClientAddress)
                        ? "localhost"
                        : _defaultClientAddress.Trim();

                    if (string.IsNullOrWhiteSpace(networkAddress))
                        networkAddress = defaultAddress;

                    Debug.Log($"[Network] AutoStart Client connect => {ResolveClientEndpoint()}");
                    StartClient();
                    return;

                case AutoStartMode.Server:
                    StartServer();
                    return;

                default:
                    Debug.LogWarning($"[Network] Unknown AutoStartMode={mode}, fallback to Disabled");
                    return;
            }
        }

        public override void OnClientConnect()
        {
            base.OnClientConnect();
            Debug.Log("[Network] Connected to server.");
            _pendingDisconnectReason = string.Empty;
            PublishConnectionStatus("Connected to server.", isError: false);

            // 클라이언트가 서버 역할이 아닌 순수 클라이언트인 경우 스포너 비활성화
            if (!NetworkServer.active)
            {
                DisableClientSpawners();
            }

        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            NetworkClient.RegisterHandler<ServerRejectMessage>(OnServerRejectMessage, false);
        }

        public override void OnStopClient()
        {
            NetworkClient.UnregisterHandler<ServerRejectMessage>();
            base.OnStopClient();
        }

        /// <summary>
        /// 순수 클라이언트에서는 월드 스포너들을 비활성화한다.
        /// 서버가 멀티 오브젝트를 생성/관리하므로 클라이언트 중복 생성은 금지한다.
        /// </summary>
        private void DisableClientSpawners()
        {
            // 모든 스포너가 자체적으로 NetworkServer.active를 체크하므로
            // 별도 비활성화는 불필요하다. 이 메서드는 호환성을 위해 유지한다.
            Debug.Log("[Network] Client-side spawners remain disabled (server-authoritative spawning).");
        }

        /// <summary>
        /// 신규 플레이어가 접속했을 때 호출.
        /// Mirror가 자동으로 playerPrefab을 인스턴스화하지만
        /// 스폰 위치를 커스텀하기 위해 오버라이드한다.
        /// </summary>
        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            Vector3 spawnPos = ResolvePlayerSpawnPosition();

            GameObject player = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
            NetworkServer.AddPlayerForConnection(conn, player);

            Debug.Log($"[Network] Player joined: {conn.address} | spawn {spawnPos}");
        }

        private Vector3 ResolvePlayerSpawnPosition()
        {
            Core.Data.GameSettings settings = Core.GameManager.Instance?.Settings;
            if (settings != null)
            {
                float spawnRadius = settings.MapRadius * settings.PlayerSpawnRadiusRatio;
                Vector2 randomPos = UnityEngine.Random.insideUnitCircle * spawnRadius;
                return new Vector3(randomPos.x, randomPos.y, 0f);
            }

            Transform startPosition = GetStartPosition();
            if (startPosition != null)
            {
                Debug.LogWarning("[Network] GameSettings missing. Falling back to NetworkStartPosition.");
                return startPosition.position;
            }

            Debug.LogWarning("[Network] GameSettings and NetworkStartPosition missing. Falling back to origin.");
            return Vector3.zero;
        }

        /// <summary>
        /// 플레이어가 연결 해제됐을 때 호출.
        /// </summary>
        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            Debug.Log($"[Network] Player left: {conn.address}");
            base.OnServerDisconnect(conn);
        }

        /// <summary>
        /// 클라이언트가 서버에서 끊겼을 때 호출.
        /// </summary>
        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            Debug.Log("[Network] Disconnected from server.");

            string reason = string.IsNullOrWhiteSpace(_pendingDisconnectReason)
                ? "Disconnected from server. Please retry in a moment."
                : _pendingDisconnectReason;

            PublishConnectionStatus(reason, isError: true);
            _pendingDisconnectReason = string.Empty;
        }

        public override void OnClientError(TransportError error, string reason)
        {
            base.OnClientError(error, reason);
            _pendingDisconnectReason = BuildClientTransportErrorReason(error, reason);

            string rawReason = string.IsNullOrWhiteSpace(reason) ? "(empty)" : reason;
            Debug.LogWarning(
                $"[Network] Client transport error: {error} | endpoint={ResolveClientEndpoint()} | reason={rawReason}");
        }

        public override void OnServerError(NetworkConnectionToClient conn, TransportError error, string reason)
        {
            base.OnServerError(conn, error, reason);

            int connId = conn != null ? conn.connectionId : -1;
            string rawReason = string.IsNullOrWhiteSpace(reason) ? "(empty)" : reason;
            Debug.Log(
                $"[Network] Server transport error observed: connId={connId}, error={error}, reason={rawReason}");
        }

        private IEnumerator DisconnectNextFrame(NetworkConnectionToClient conn)
        {
            yield return null;
            conn.Disconnect();
        }

        private string BuildServerFullReason()
        {
            return $"Server is full ({maxConnections} players). Please retry shortly.";
        }

        private string BuildClientTransportErrorReason(TransportError error, string reason)
        {
            string normalizedReason = string.IsNullOrWhiteSpace(reason)
                ? string.Empty
                : reason.ToLowerInvariant();
            string endpoint = ResolveClientEndpoint();
            string endpointLabel = string.IsNullOrWhiteSpace(endpoint) ? string.Empty : $" ({endpoint})";

            if (error == TransportError.DnsResolve || normalizedReason.Contains("dns"))
                return $"Could not resolve server address{endpointLabel}. Check networkAddress.";

            if (error == TransportError.Timeout || normalizedReason.Contains("timeout"))
                return $"Connection timed out{endpointLabel}. Please retry shortly.";

            if (normalizedReason.Contains("refused") || normalizedReason.Contains("거부"))
                return $"Connection refused{endpointLabel}. Start host first and verify address/port.";

            return $"Network error while connecting{endpointLabel}. Please retry shortly.";
        }

        private void ApplyWebClientOverridesFromUrl()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!(transport is SimpleWebTransport simpleWebTransport))
                return;

            string absoluteUrl = Application.absoluteURL ?? string.Empty;
            if (string.IsNullOrWhiteSpace(absoluteUrl))
                return;

            bool changed = false;
            string effectiveAddress = string.IsNullOrWhiteSpace(networkAddress)
                ? _defaultClientAddress
                : networkAddress;

            if (TryGetQueryParameter(absoluteUrl, WEB_QUERY_SERVER_KEY, out string serverRaw))
            {
                if (TryParseServerOverride(serverRaw, out string host, out ushort serverPort))
                {
                    networkAddress = host;
                    effectiveAddress = host;
                    changed = true;

                    if (serverPort > 0)
                    {
                        simpleWebTransport.port = serverPort;
                        simpleWebTransport.clientWebsocketSettings.ClientPortOption = WebsocketPortOption.SpecifyPort;
                        simpleWebTransport.clientWebsocketSettings.CustomClientPort = serverPort;
                    }
                }
                else
                {
                    Debug.LogWarning($"[Network] Ignored invalid '?{WEB_QUERY_SERVER_KEY}=' value: {serverRaw}");
                }
            }

            if (TryGetQueryParameter(absoluteUrl, WEB_QUERY_PORT_KEY, out string portRaw))
            {
                if (ushort.TryParse(portRaw, out ushort port) && port > 0)
                {
                    simpleWebTransport.port = port;
                    simpleWebTransport.clientWebsocketSettings.ClientPortOption = WebsocketPortOption.SpecifyPort;
                    simpleWebTransport.clientWebsocketSettings.CustomClientPort = port;
                    changed = true;
                }
                else
                {
                    Debug.LogWarning($"[Network] Ignored invalid '?{WEB_QUERY_PORT_KEY}=' value: {portRaw}");
                }
            }

            bool targetUseWss = simpleWebTransport.clientUseWss;
            bool hasExplicitWss = false;

            if (TryGetQueryParameter(absoluteUrl, WEB_QUERY_WSS_KEY, out string wssRaw))
            {
                if (TryParseBooleanFlag(wssRaw, out bool parsedWss))
                {
                    targetUseWss = parsedWss;
                    hasExplicitWss = true;
                }
                else
                {
                    Debug.LogWarning($"[Network] Ignored invalid '?{WEB_QUERY_WSS_KEY}=' value: {wssRaw}");
                }
            }

            if (!hasExplicitWss)
            {
                bool pageIsHttps = absoluteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (pageIsHttps && !IsLoopbackAddress(effectiveAddress))
                    targetUseWss = true;
            }

            if (simpleWebTransport.clientUseWss != targetUseWss)
            {
                simpleWebTransport.clientUseWss = targetUseWss;
                changed = true;
            }

            if (changed)
            {
                Debug.Log(
                    $"[Network] WebGL URL override applied: endpoint={ResolveClientEndpoint()}, " +
                    $"clientUseWss={simpleWebTransport.clientUseWss}");
            }
#endif
        }

        private void ApplyCommandLineOverrides()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return;
#else
            bool changed = false;

            if (TryGetCommandLineOptionValue(CMD_ADDRESS_KEY, out string addressRaw))
            {
                string address = addressRaw?.Trim();
                if (!string.IsNullOrWhiteSpace(address))
                {
                    networkAddress = address;
                    changed = true;
                }
            }

            if (TryGetCommandLineOptionValue(CMD_PORT_KEY, out string portRaw))
            {
                if (ushort.TryParse(portRaw, out ushort port) && port > 0)
                {
                    if (transport is PortTransport portTransport)
                    {
                        portTransport.Port = port;
                        changed = true;
                    }

                    if (transport is SimpleWebTransport simpleWebTransport)
                    {
                        simpleWebTransport.port = port;
                        simpleWebTransport.clientWebsocketSettings.ClientPortOption = WebsocketPortOption.SpecifyPort;
                        simpleWebTransport.clientWebsocketSettings.CustomClientPort = port;
                        changed = true;
                    }
                }
                else
                {
                    Debug.LogWarning($"[Network] Ignored invalid '-{CMD_PORT_KEY}' value: {portRaw}");
                }
            }

            if (TryGetCommandLineOptionValue(CMD_WSS_KEY, out string wssRaw))
            {
                if (TryParseBooleanFlag(wssRaw, out bool useWss))
                {
                    if (transport is SimpleWebTransport simpleWebTransport && simpleWebTransport.clientUseWss != useWss)
                    {
                        simpleWebTransport.clientUseWss = useWss;
                        changed = true;
                    }
                }
                else
                {
                    Debug.LogWarning($"[Network] Ignored invalid '-{CMD_WSS_KEY}' value: {wssRaw}");
                }
            }

            if (changed)
            {
                Debug.Log(
                    $"[Network] Command-line override applied: " +
                    $"modeArg={ResolveCommandLineModeValueOrDefault()}, endpoint={ResolveClientEndpoint()}");
            }
#endif
        }

        private bool TryResolveCommandLineAutoStartMode(out AutoStartMode mode)
        {
            mode = AutoStartMode.Disabled;

            if (!TryGetCommandLineOptionValue(CMD_MODE_KEY, out string modeRaw))
                return false;

            if (string.IsNullOrWhiteSpace(modeRaw))
                return false;

            switch (modeRaw.Trim().ToLowerInvariant())
            {
                case "disabled":
                case "off":
                case "none":
                    mode = AutoStartMode.Disabled;
                    return true;

                case "host":
                    mode = AutoStartMode.Host;
                    return true;

                case "client":
                    mode = AutoStartMode.Client;
                    return true;

                case "server":
                    mode = AutoStartMode.Server;
                    return true;

                default:
                    Debug.LogWarning($"[Network] Ignored invalid '-{CMD_MODE_KEY}' value: {modeRaw}");
                    return false;
            }
        }

        private string ResolveCommandLineModeValueOrDefault()
        {
            return TryGetCommandLineOptionValue(CMD_MODE_KEY, out string modeRaw) ? modeRaw : "(none)";
        }

        private static bool TryGetCommandLineOptionValue(string key, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(key))
                return false;

            string normalizedKey = key.Trim().TrimStart('-', '/');
            if (string.IsNullOrWhiteSpace(normalizedKey))
                return false;

            string[] args = Environment.GetCommandLineArgs();
            if (args == null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrWhiteSpace(arg))
                    continue;

                string trimmed = arg.Trim();
                string candidate = trimmed.TrimStart('-', '/');

                int equalsIndex = candidate.IndexOf('=');
                if (equalsIndex > 0)
                {
                    string candidateKey = candidate.Substring(0, equalsIndex);
                    if (candidateKey.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        value = candidate.Substring(equalsIndex + 1);
                        return true;
                    }

                    continue;
                }

                if (!candidate.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                int nextIndex = i + 1;
                if (nextIndex < args.Length)
                {
                    string nextValue = args[nextIndex];
                    if (!string.IsNullOrWhiteSpace(nextValue) &&
                        !nextValue.StartsWith("-", StringComparison.Ordinal) &&
                        !nextValue.StartsWith("/", StringComparison.Ordinal))
                    {
                        value = nextValue.Trim();
                        return true;
                    }
                }

                value = "true";
                return true;
            }

            return false;
        }

        private bool ShouldAutoStartGameplayLoop()
        {
            return _runtimeAutoStartMode == AutoStartMode.Server;
        }

        private void TryStartGameplayLoopForDedicatedServer()
        {
            if (Core.GameManager.Instance == null)
            {
                Debug.LogWarning("[Network] Dedicated server auto-start skipped: GameManager missing.");
                return;
            }

            if (Core.GameManager.Instance.IsGameActive)
                return;

            Core.GameManager.Instance.StartGame();
            Debug.Log("[Network] Dedicated server gameplay loop auto-started.");
        }

        private static bool TryGetQueryParameter(string absoluteUrl, string key, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(absoluteUrl) || string.IsNullOrWhiteSpace(key))
                return false;

            int queryStart = absoluteUrl.IndexOf('?');
            if (queryStart < 0 || queryStart >= absoluteUrl.Length - 1)
                return false;

            string query = absoluteUrl.Substring(queryStart + 1);
            int fragmentStart = query.IndexOf('#');
            if (fragmentStart >= 0)
                query = query.Substring(0, fragmentStart);

            if (string.IsNullOrWhiteSpace(query))
                return false;

            string[] pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                int equalsIndex = pair.IndexOf('=');
                string rawName = equalsIndex >= 0 ? pair.Substring(0, equalsIndex) : pair;
                string rawValue = equalsIndex >= 0 ? pair.Substring(equalsIndex + 1) : string.Empty;

                string decodedName = Uri.UnescapeDataString(rawName.Replace('+', ' '));
                if (!decodedName.Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
                return true;
            }

            return false;
        }

        private static bool TryParseServerOverride(string rawValue, out string host, out ushort port)
        {
            host = string.Empty;
            port = 0;

            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            string candidate = rawValue.Trim();
            if (candidate.IndexOf("://", StringComparison.Ordinal) < 0)
                candidate = $"ws://{candidate}";

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri uri))
                return false;

            if (string.IsNullOrWhiteSpace(uri.Host))
                return false;

            host = uri.Host;
            if (!uri.IsDefaultPort && uri.Port > 0 && uri.Port <= ushort.MaxValue)
                port = (ushort)uri.Port;

            return true;
        }

        private static bool TryParseBooleanFlag(string rawValue, out bool value)
        {
            value = false;
            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            switch (rawValue.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "y":
                case "on":
                    value = true;
                    return true;

                case "0":
                case "false":
                case "no":
                case "n":
                case "off":
                    value = false;
                    return true;

                default:
                    return false;
            }
        }

        private static bool IsLoopbackAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return false;

            string normalized = address.Trim();
            if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            if (IPAddress.TryParse(normalized, out IPAddress ipAddress))
                return IPAddress.IsLoopback(ipAddress);

            return false;
        }

        private string ResolveClientEndpoint()
        {
            string address = string.IsNullOrWhiteSpace(networkAddress)
                ? _defaultClientAddress
                : networkAddress;
            address = string.IsNullOrWhiteSpace(address) ? "localhost" : address.Trim();

            ushort port = 0;
            if (Transport.active is PortTransport activePortTransport)
                port = activePortTransport.Port;
            else if (transport is PortTransport managerPortTransport)
                port = managerPortTransport.Port;

            return port > 0 ? $"{address}:{port}" : address;
        }

        private void ApplyFreeMvpRuntimeProfile(bool logWarnings)
        {
            if (_maxPlayers != FREE_MVP_HARD_CAP)
            {
                if (logWarnings)
                    Debug.LogWarning($"[Network] Enforcing Free-MVP max players: {FREE_MVP_HARD_CAP}");
                _maxPlayers = FREE_MVP_HARD_CAP;
            }

            if (maxConnections != FREE_MVP_HARD_CAP)
            {
                if (logWarnings)
                    Debug.LogWarning($"[Network] Enforcing Free-MVP maxConnections: {FREE_MVP_HARD_CAP}");
                maxConnections = FREE_MVP_HARD_CAP;
            }

            if (sendRate != FREE_MVP_SEND_RATE)
            {
                if (logWarnings)
                    Debug.LogWarning($"[Network] Enforcing Free-MVP sendRate: {FREE_MVP_SEND_RATE}Hz");
                sendRate = FREE_MVP_SEND_RATE;
            }

            bool snapshotProfileChanged = false;
            if (Math.Abs(snapshotSettings.bufferTimeMultiplier - FREE_MVP_SNAPSHOT_BUFFER_TIME_MULTIPLIER) > 0.001d)
            {
                snapshotSettings.bufferTimeMultiplier = FREE_MVP_SNAPSHOT_BUFFER_TIME_MULTIPLIER;
                snapshotProfileChanged = true;
            }

            if (!snapshotSettings.dynamicAdjustment)
            {
                snapshotSettings.dynamicAdjustment = true;
                snapshotProfileChanged = true;
            }

            if (Mathf.Abs(snapshotSettings.dynamicAdjustmentTolerance - FREE_MVP_SNAPSHOT_DYNAMIC_TOLERANCE) > 0.001f)
            {
                snapshotSettings.dynamicAdjustmentTolerance = FREE_MVP_SNAPSHOT_DYNAMIC_TOLERANCE;
                snapshotProfileChanged = true;
            }

            if (snapshotSettings.bufferLimit != FREE_MVP_SNAPSHOT_BUFFER_LIMIT)
            {
                snapshotSettings.bufferLimit = FREE_MVP_SNAPSHOT_BUFFER_LIMIT;
                snapshotProfileChanged = true;
            }

            if (Mathf.Abs(snapshotSettings.catchupNegativeThreshold - FREE_MVP_SNAPSHOT_CATCHUP_NEGATIVE_THRESHOLD) > 0.001f)
            {
                snapshotSettings.catchupNegativeThreshold = FREE_MVP_SNAPSHOT_CATCHUP_NEGATIVE_THRESHOLD;
                snapshotProfileChanged = true;
            }

            if (Mathf.Abs(snapshotSettings.catchupPositiveThreshold - FREE_MVP_SNAPSHOT_CATCHUP_POSITIVE_THRESHOLD) > 0.001f)
            {
                snapshotSettings.catchupPositiveThreshold = FREE_MVP_SNAPSHOT_CATCHUP_POSITIVE_THRESHOLD;
                snapshotProfileChanged = true;
            }

            if (Math.Abs(snapshotSettings.catchupSpeed - FREE_MVP_SNAPSHOT_CATCHUP_SPEED) > 0.0001d)
            {
                snapshotSettings.catchupSpeed = FREE_MVP_SNAPSHOT_CATCHUP_SPEED;
                snapshotProfileChanged = true;
            }

            if (Math.Abs(snapshotSettings.slowdownSpeed - FREE_MVP_SNAPSHOT_SLOWDOWN_SPEED) > 0.0001d)
            {
                snapshotSettings.slowdownSpeed = FREE_MVP_SNAPSHOT_SLOWDOWN_SPEED;
                snapshotProfileChanged = true;
            }

            if (snapshotSettings.driftEmaDuration != FREE_MVP_SNAPSHOT_DRIFT_EMA_DURATION)
            {
                snapshotSettings.driftEmaDuration = FREE_MVP_SNAPSHOT_DRIFT_EMA_DURATION;
                snapshotProfileChanged = true;
            }

            if (snapshotSettings.deliveryTimeEmaDuration != FREE_MVP_SNAPSHOT_DELIVERY_EMA_DURATION)
            {
                snapshotSettings.deliveryTimeEmaDuration = FREE_MVP_SNAPSHOT_DELIVERY_EMA_DURATION;
                snapshotProfileChanged = true;
            }

            if (snapshotProfileChanged && logWarnings)
            {
                Debug.LogWarning(
                    $"[Network] SnapshotInterpolation profile enforced: " +
                    $"bufferTimeMultiplier={snapshotSettings.bufferTimeMultiplier:F2}, " +
                    $"dynamicAdjustment={snapshotSettings.dynamicAdjustment}, " +
                    $"dynamicTolerance={snapshotSettings.dynamicAdjustmentTolerance:F2}, " +
                    $"bufferLimit={snapshotSettings.bufferLimit}, " +
                    $"catchupThresholds={snapshotSettings.catchupNegativeThreshold:F2}/{snapshotSettings.catchupPositiveThreshold:F2}, " +
                    $"catchupSpeed={snapshotSettings.catchupSpeed:F3}, " +
                    $"slowdownSpeed={snapshotSettings.slowdownSpeed:F3}, " +
                    $"driftEma={snapshotSettings.driftEmaDuration}, " +
                    $"deliveryEma={snapshotSettings.deliveryTimeEmaDuration}");
            }

            ApplySimpleWebRuntimeProfile(logWarnings);
            ApplyPlayerTransformRuntimeProfile(logWarnings);
        }

        private void ApplySimpleWebRuntimeProfile(bool logWarnings)
        {
            if (!(transport is SimpleWebTransport simpleWebTransport))
                return;

            bool changed = false;

            if (simpleWebTransport.sendTimeout != FREE_MVP_SIMPLEWEB_SEND_TIMEOUT_MS)
            {
                simpleWebTransport.sendTimeout = FREE_MVP_SIMPLEWEB_SEND_TIMEOUT_MS;
                changed = true;
            }

            if (simpleWebTransport.receiveTimeout != FREE_MVP_SIMPLEWEB_RECEIVE_TIMEOUT_MS)
            {
                simpleWebTransport.receiveTimeout = FREE_MVP_SIMPLEWEB_RECEIVE_TIMEOUT_MS;
                changed = true;
            }

            if (simpleWebTransport.serverMaxMsgsPerTick != FREE_MVP_SIMPLEWEB_SERVER_MAX_MSGS_PER_TICK)
            {
                simpleWebTransport.serverMaxMsgsPerTick = FREE_MVP_SIMPLEWEB_SERVER_MAX_MSGS_PER_TICK;
                changed = true;
            }

            if (simpleWebTransport.clientMaxMsgsPerTick != FREE_MVP_SIMPLEWEB_CLIENT_MAX_MSGS_PER_TICK)
            {
                simpleWebTransport.clientMaxMsgsPerTick = FREE_MVP_SIMPLEWEB_CLIENT_MAX_MSGS_PER_TICK;
                changed = true;
            }

            if (simpleWebTransport.noDelay != FREE_MVP_SIMPLEWEB_NO_DELAY)
            {
                simpleWebTransport.noDelay = FREE_MVP_SIMPLEWEB_NO_DELAY;
                changed = true;
            }

            if (changed && logWarnings)
            {
                Debug.LogWarning(
                    $"[Network] SimpleWeb profile enforced: " +
                    $"sendTimeout={simpleWebTransport.sendTimeout}ms, " +
                    $"receiveTimeout={simpleWebTransport.receiveTimeout}ms, " +
                    $"serverMaxMsgs={simpleWebTransport.serverMaxMsgsPerTick}, " +
                    $"clientMaxMsgs={simpleWebTransport.clientMaxMsgsPerTick}, " +
                    $"noDelay={simpleWebTransport.noDelay}");
            }
        }

        private void ApplyPlayerTransformRuntimeProfile(bool logWarnings)
        {
            if (playerPrefab == null)
                return;

            NetworkTransformReliable playerTransform = playerPrefab.GetComponent<NetworkTransformReliable>();
            if (playerTransform == null)
                return;

            bool changed = false;

            if (playerTransform.onlySyncOnChange != FREE_MVP_PLAYER_ONLY_SYNC_ON_CHANGE)
            {
                playerTransform.onlySyncOnChange = FREE_MVP_PLAYER_ONLY_SYNC_ON_CHANGE;
                changed = true;
            }

            if (Mathf.Abs(playerTransform.onlySyncOnChangeCorrectionMultiplier - FREE_MVP_PLAYER_ONLY_SYNC_CORRECTION_MULTIPLIER) > 0.001f)
            {
                playerTransform.onlySyncOnChangeCorrectionMultiplier = FREE_MVP_PLAYER_ONLY_SYNC_CORRECTION_MULTIPLIER;
                changed = true;
            }

            if (playerTransform.useFixedUpdate != FREE_MVP_PLAYER_USE_FIXED_UPDATE)
            {
                playerTransform.useFixedUpdate = FREE_MVP_PLAYER_USE_FIXED_UPDATE;
                changed = true;
            }

            if (playerTransform.interpolatePosition != FREE_MVP_PLAYER_INTERPOLATE_POSITION)
            {
                playerTransform.interpolatePosition = FREE_MVP_PLAYER_INTERPOLATE_POSITION;
                changed = true;
            }

            if (playerTransform.interpolateRotation != FREE_MVP_PLAYER_INTERPOLATE_ROTATION)
            {
                playerTransform.interpolateRotation = FREE_MVP_PLAYER_INTERPOLATE_ROTATION;
                changed = true;
            }

            if (Mathf.Abs(playerTransform.positionPrecision - FREE_MVP_PLAYER_POSITION_PRECISION) > 0.0001f)
            {
                playerTransform.positionPrecision = FREE_MVP_PLAYER_POSITION_PRECISION;
                changed = true;
            }

            if (Mathf.Abs(playerTransform.rotationSensitivity - FREE_MVP_PLAYER_ROTATION_SENSITIVITY) > 0.0001f)
            {
                playerTransform.rotationSensitivity = FREE_MVP_PLAYER_ROTATION_SENSITIVITY;
                changed = true;
            }

            if (changed && logWarnings)
            {
                Debug.LogWarning(
                    $"[Network] Player NetworkTransform profile enforced: " +
                    $"onlySyncOnChange={playerTransform.onlySyncOnChange}, " +
                    $"useFixedUpdate={playerTransform.useFixedUpdate}, " +
                    $"positionPrecision={playerTransform.positionPrecision:F4}, " +
                    $"rotationSensitivity={playerTransform.rotationSensitivity:F4}");
            }
        }

        private void OnServerRejectMessage(ServerRejectMessage msg)
        {
            _pendingDisconnectReason = msg.Reason;
        }

        private static void PublishConnectionStatus(string message, bool isError)
        {
            LatestClientConnectionStatus = new ClientConnectionStatus(message, isError);
            OnClientConnectionStatusChanged?.Invoke(LatestClientConnectionStatus);
        }
    }
}
