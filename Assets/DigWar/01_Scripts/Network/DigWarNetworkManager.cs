using UnityEngine;
using Mirror;
using UnityEngine.Serialization;
using System.Collections;
using System;

namespace Network
{
    /// <summary>
    /// Mirror NetworkManager를 상속한 DigWar 전용 네트워크 매니저.
    /// 접속/연결 해제/스폰 등 네트워크 라이프사이클을 관리한다.
    /// 
    /// [설정 방법]
    /// 1. 씬의 빈 오브젝트에 이 컴포넌트를 추가
    /// 2. Transport에 SimpleWebTransport 할당
    /// 3. Player Prefab에 NetworkIdentity가 있는 프리팹 할당
    /// </summary>
    public class DigWarNetworkManager : NetworkManager
    {
        private const int FREE_MVP_HARD_CAP = 24;
        private const int FREE_MVP_SEND_RATE = 15;

        [Header("DigWar Settings")]
        [Tooltip("무료 MVP 최대 접속자 수(고정: 24)")]
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

        [Tooltip("에디터 원본 인스턴스 시작 모드")]
        [SerializeField] private AutoStartMode _editorOriginalMode = AutoStartMode.Host;

        [Tooltip("ParrelSync Clone 인스턴스 시작 모드")]
        [SerializeField] private AutoStartMode _editorCloneMode = AutoStartMode.Client;

        #pragma warning disable CS0414
        [Tooltip("빌드 실행 시작 모드")]
        [SerializeField] private AutoStartMode _buildMode = AutoStartMode.Client;
        #pragma warning restore CS0414

        [Tooltip("Client 모드일 때 기본 접속 주소")]
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
        }

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            if (numPlayers >= maxConnections)
            {
                string reason = BuildServerFullReason();
                conn.Send(new ServerRejectMessage { Reason = reason });
                StartCoroutine(DisconnectNextFrame(conn));
                Debug.LogWarning($"[Network] 접속 거부(정원 초과): {conn.address} | Players={numPlayers}/{maxConnections}");
                return;
            }

            base.OnServerConnect(conn);
        }

        /// <summary>
        /// 서버가 시작될 때 호출.
        /// 게임 라운드 시작은 MainMenuUI가 담당하며,
        /// 네트워크 매니저는 세션 연결/스폰만 담당한다.
        /// </summary>
        private AutoStartMode ResolveAutoStartMode(out string contextLabel, out bool isCloneInstance)
        {
#if UNITY_EDITOR
            isCloneInstance = IsEditorCloneInstance();
            contextLabel = isCloneInstance ? "EditorClone" : "EditorOriginal";
            return isCloneInstance ? _editorCloneMode : _editorOriginalMode;
#else
            isCloneInstance = false;
            contextLabel = "Build";
            return _buildMode;
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

                    Debug.Log($"[Network] AutoStart Client connect => {networkAddress}");
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
            Debug.Log("[Network] 서버에 접속 완료!");
            _pendingDisconnectReason = string.Empty;
            PublishConnectionStatus("서버 접속 완료", isError: false);

            // 클라이언트(서버 역할이 아닌 순수 클라이언트)에서는 스포너 비활성화
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
        /// 순수 클라이언트에서 월드 스포너들을 비활성화.
        /// 서버가 이미 엔티티를 생성/관리하므로 클라이언트가 중복 생성하면 안 됨.
        /// </summary>
        private void DisableClientSpawners()
        {
            // 모든 스포너가 자체적으로 NetworkServer.active를 체크하므로
            // 외부 비활성화 불필요. 이 메서드는 호환성을 위해 유지.
            Debug.Log("[Network] 클라이언트 스포너 설정 완료 (모두 자체 서버 체크)");
        }

        /// <summary>
        /// 새 플레이어가 접속했을 때 호출.
        /// Mirror가 자동으로 playerPrefab을 인스턴스화하지만,
        /// 스폰 위치를 커스터마이즈한다.
        /// </summary>
        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            Vector3 spawnPos = ResolvePlayerSpawnPosition();

            GameObject player = Instantiate(playerPrefab, spawnPos, Quaternion.identity);
            NetworkServer.AddPlayerForConnection(conn, player);

            Debug.Log($"[Network] 플레이어 접속: {conn.address} → 위치 {spawnPos}");
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
                Debug.LogWarning("[Network] GameSettings 누락. NetworkStartPosition으로 스폰합니다.");
                return startPosition.position;
            }

            Debug.LogWarning("[Network] GameSettings/NetworkStartPosition 모두 누락. 원점으로 스폰합니다.");
            return Vector3.zero;
        }

        /// <summary>
        /// 플레이어가 연결 해제됐을 때 호출.
        /// </summary>
        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            Debug.Log($"[Network] 플레이어 퇴장: {conn.address}");
            base.OnServerDisconnect(conn);
        }

        /// <summary>
        /// 클라이언트가 서버에서 끊겼을 때 호출.
        /// </summary>
        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            Debug.Log("[Network] 서버 연결 끊김");

            string reason = string.IsNullOrWhiteSpace(_pendingDisconnectReason)
                ? "서버 연결이 끊겼습니다. 잠시 후 다시 시도하세요."
                : _pendingDisconnectReason;

            PublishConnectionStatus(reason, isError: true);
            _pendingDisconnectReason = string.Empty;
        }

        private IEnumerator DisconnectNextFrame(NetworkConnectionToClient conn)
        {
            yield return null;
            conn.Disconnect();
        }

        private string BuildServerFullReason()
        {
            return $"서버 인원({maxConnections}명)이 가득 찼습니다. 잠시 후 다시 시도하세요.";
        }

        private void ApplyFreeMvpRuntimeProfile(bool logWarnings)
        {
            if (_maxPlayers != FREE_MVP_HARD_CAP)
            {
                if (logWarnings)
                    Debug.LogWarning($"[Network] Free-MVP 정책으로 최대 접속자 수를 {FREE_MVP_HARD_CAP}명으로 고정합니다.");
                _maxPlayers = FREE_MVP_HARD_CAP;
            }

            if (maxConnections != FREE_MVP_HARD_CAP)
            {
                if (logWarnings)
                    Debug.LogWarning($"[Network] MaxConnections를 Free-MVP 정책({FREE_MVP_HARD_CAP})으로 고정합니다.");
                maxConnections = FREE_MVP_HARD_CAP;
            }

            if (sendRate != FREE_MVP_SEND_RATE)
            {
                if (logWarnings)
                    Debug.LogWarning($"[Network] sendRate를 Free-MVP 정책({FREE_MVP_SEND_RATE}Hz)으로 고정합니다.");
                sendRate = FREE_MVP_SEND_RATE;
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
