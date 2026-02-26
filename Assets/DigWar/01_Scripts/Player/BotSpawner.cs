using UnityEngine;
using Mirror;
using Core;
using Core.Data;
using System.Collections.Generic;

namespace Player
{
    /// <summary>
    /// AI 봇을 서버에서 스폰하고 Mirror Spawn Handler를 통해
    /// 모든 클라이언트에 동기화한다.
    ///
    /// [동작 방식]
    /// 1. Awake: 스폰 핸들러 등록 (서버/클라이언트 공통)
    /// 2. Start (서버만): 봇 생성 → NetworkServer.Spawn(bot, assetId)
    /// 3. 클라이언트: 스폰 핸들러가 같은 구조의 봇을 재조립
    /// 4. NetworkBot.OnStartClient: SyncVar로 색상/이름 적용, AI 비활성화
    /// 5. NetworkTransform: 서버 AI의 이동을 클라이언트에 동기화
    /// </summary>
    public class BotSpawner : MonoBehaviour
    {
        private static BotSpawner _serverOwner;
        private const int FREE_MVP_BOT_HARD_CAP = 12;

        [Header("봇 설정")]
        [Tooltip("Number of bots to spawn")]
        [SerializeField] private int _botCount = 12;

        [Tooltip("서버에서 죽은 봇을 자동으로 보충한다.")]
        [SerializeField] private bool _autoRespawnBots = true;

        [Tooltip("봇 개수 보정 주기(초)")]
        [SerializeField] private float _respawnCheckInterval = 0.5f;
        [Tooltip("보정 주기당 초과 봇 정리 최대 개수")]
        [SerializeField] private int _maxTrimPerCheck = 8;

        [Tooltip("Bot sprite")]
        [SerializeField] private Sprite _botSprite;

        private GameSettings _settings;
        private int _nextBotIndexCursor;
        private int _runtimeTargetBotCount;
        private float _nextRespawnCheckAt;
        private readonly List<Network.NetworkBot> _activeServerBotSnapshot = new List<Network.NetworkBot>(32);
        private readonly bool[] _botIndexSlotUsed = new bool[FREE_MVP_BOT_HARD_CAP];

        // 봇 전용 assetId (Mirror가 스폰 핸들러를 식별하는 키)
        private const uint BOT_ASSET_ID = 10001;

        private void Awake()
        {
            // 서버와 클라이언트 모두에서 스폰 핸들러 등록
            NetworkClient.RegisterSpawnHandler(
                BOT_ASSET_ID,
                OnClientSpawnBot,
                OnClientUnSpawnBot
            );
        }

        private void OnDestroy()
        {
            if (_serverOwner == this)
                _serverOwner = null;

            NetworkClient.UnregisterSpawnHandler(BOT_ASSET_ID);
        }

        private void Start()
        {
            // 서버가 아니면 봇 생성하지 않음
            if (!NetworkServer.active)
            {
                Debug.Log("[BotSpawner] Client mode: waiting for server-spawned bots.");
                return;
            }

            if (_serverOwner != null && _serverOwner != this)
            {
                Debug.LogWarning("[BotSpawner] Duplicate server spawner detected. Disabling this instance.");
                enabled = false;
                return;
            }

            _serverOwner = this;

            if (GameManager.Instance == null) return;
            _settings = GameManager.Instance.Settings;
            _runtimeTargetBotCount = ResolveRuntimeTargetBotCount();

            // 전용 서버 재시작/씬 재초기화 경로에서 잔여 봇이 남아 있으면 먼저 정리해
            // 초기 과증식 상태로 진입하지 않도록 방어한다.
            TrimAllServerBots();

            _nextBotIndexCursor = 0;

            for (int i = 0; i < _runtimeTargetBotCount; i++)
                SpawnBot(i);

            Debug.Log($"[BotSpawner] 서버에서 봇 {_runtimeTargetBotCount}마리 네트워크 스폰 완료");
        }

        private void Update()
        {
            if (!NetworkServer.active || !_autoRespawnBots) return;
            if (Time.time < _nextRespawnCheckAt) return;

            _nextRespawnCheckAt = Time.time + Mathf.Max(0.1f, _respawnCheckInterval);

            CaptureServerBots();

            int overflow = _activeServerBotSnapshot.Count - _runtimeTargetBotCount;
            if (overflow > 0)
            {
                int trimLimit = Mathf.Max(1, _maxTrimPerCheck);
                int trimCount = Mathf.Min(overflow, trimLimit);

                for (int i = 0; i < trimCount; i++)
                {
                    int index = _activeServerBotSnapshot.Count - 1 - i;
                    if (index < 0) break;

                    Network.NetworkBot bot = _activeServerBotSnapshot[index];
                    if (bot == null) continue;
                    if (bot.gameObject == null) continue;
                    if (!bot.gameObject.activeInHierarchy) continue;

                    NetworkServer.Destroy(bot.gameObject);
                }

                return;
            }

            int deficit = _runtimeTargetBotCount - _activeServerBotSnapshot.Count;
            for (int i = 0; i < deficit; i++)
            {
                int botIndex = ResolveNextBotIndex();
                SpawnBot(botIndex);
                CaptureServerBots();
            }
        }

        private int ResolveRuntimeTargetBotCount()
        {
            int safeCount = Mathf.Max(0, _botCount);
            if (!NetworkServer.active)
                return safeCount;

            int clamped = Mathf.Min(safeCount, FREE_MVP_BOT_HARD_CAP);
            if (safeCount != clamped)
            {
                Debug.LogWarning(
                    $"[BotSpawner] Enforcing Free-MVP bot cap: {safeCount} -> {clamped}");
            }

            return clamped;
        }

        private void CaptureServerBots()
        {
            _activeServerBotSnapshot.Clear();

            if (NetworkServer.spawned == null || NetworkServer.spawned.Count == 0)
                return;

            foreach (NetworkIdentity identity in NetworkServer.spawned.Values)
            {
                if (identity == null) continue;

                Network.NetworkBot bot = identity.GetComponent<Network.NetworkBot>();
                if (bot == null) continue;

                _activeServerBotSnapshot.Add(bot);
            }
        }

        private void TrimAllServerBots()
        {
            CaptureServerBots();
            for (int i = 0; i < _activeServerBotSnapshot.Count; i++)
            {
                Network.NetworkBot bot = _activeServerBotSnapshot[i];
                if (bot == null) continue;
                if (bot.gameObject == null) continue;

                NetworkServer.Destroy(bot.gameObject);
            }
        }

        private int ResolveNextBotIndex()
        {
            int slotCount = Mathf.Clamp(_runtimeTargetBotCount, 1, FREE_MVP_BOT_HARD_CAP);

            for (int i = 0; i < slotCount; i++)
                _botIndexSlotUsed[i] = false;

            for (int i = 0; i < _activeServerBotSnapshot.Count; i++)
            {
                Network.NetworkBot bot = _activeServerBotSnapshot[i];
                if (bot == null) continue;

                int index = bot.BotIndex;
                if (index < 0 || index >= slotCount) continue;

                _botIndexSlotUsed[index] = true;
            }

            for (int offset = 0; offset < slotCount; offset++)
            {
                int candidate = (_nextBotIndexCursor + offset) % slotCount;
                if (_botIndexSlotUsed[candidate]) continue;

                _nextBotIndexCursor = (candidate + 1) % slotCount;
                return candidate;
            }

            // 모두 사용 중인 경우(예: 타이밍 이슈)에도 인덱스가 급증하지 않도록 슬롯 범위 내에서 순환.
            int fallback = _nextBotIndexCursor;
            _nextBotIndexCursor = (_nextBotIndexCursor + 1) % slotCount;
            return fallback;
        }

        /// <summary>서버에서 봇을 조립하고 네트워크 스폰한다.</summary>
        private void SpawnBot(int index)
        {
            var botObj = AssembleBot(index, enableAI: true);

            // NetworkBot의 SyncVar 설정 (스폰 전에!)
            var networkBot = botObj.GetComponent<Network.NetworkBot>();
            networkBot.BotIndex = index;

            // 비활성 오브젝트 스폰 경로에서 서버 생명주기 콜백 누락이 발생하면
            // ActiveBots 집계/보충 루프가 어긋날 수 있어, 스폰 직전 활성화한다.
            botObj.SetActive(true);

            // 네트워크 스폰 → 클라이언트에 전파
            NetworkServer.Spawn(botObj, BOT_ASSET_ID);
        }

        /// <summary>봇 GameObject를 조립한다.</summary>
        private GameObject AssembleBot(int index, bool enableAI)
        {
            float mapRadius = _settings != null ? _settings.MapRadius : 65f;
            float spawnRadiusRatio = _settings != null ? _settings.BotSpawnRadiusRatio : 0.7f;
            float radius = Mathf.Max(1f, mapRadius * spawnRadiusRatio);
            Vector2 randomPos = Random.insideUnitCircle * radius;

            var botObj = new GameObject($"Bot_{index}");
            botObj.SetActive(false); // 조립 중 비활성 → NetworkBehaviour.Update() 방지
            botObj.transform.position = new Vector3(randomPos.x, randomPos.y, 0f);
            botObj.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            botObj.transform.localScale = Vector3.one * (_settings != null ? _settings.MinScale : 0.5f);
            botObj.layer = gameObject.layer;

            // Network 컴포넌트
            botObj.AddComponent<NetworkIdentity>();
            var nt = botObj.AddComponent<NetworkTransformReliable>();
            nt.syncDirection = SyncDirection.ServerToClient;
            nt.useFixedUpdate = true;
            nt.onlySyncOnChange = false;
            nt.onlySyncOnChangeCorrectionMultiplier = 3f;
            nt.interpolatePosition = true;
            nt.interpolateRotation = true;
            nt.positionPrecision = 0.003f;
            nt.rotationSensitivity = 0.003f;
            botObj.AddComponent<Network.NetworkBot>();

            // Physics
            var rb = botObj.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            var col = botObj.AddComponent<CircleCollider2D>();
            col.radius = 0.3f;
            col.isTrigger = true;

            // Visuals
            var visualObj = new GameObject("Visuals");
            visualObj.transform.SetParent(botObj.transform, false);
            var sr = visualObj.AddComponent<SpriteRenderer>();
            if (_botSprite != null) sr.sprite = _botSprite;
            sr.color = Network.NetworkBot.BOT_COLORS[index % Network.NetworkBot.BOT_COLORS.Length];
            sr.sortingOrder = 1;

            // Gameplay
            var ai = botObj.AddComponent<AIController>();
            ai.enabled = enableAI;
            botObj.AddComponent<MoleGrowth>();
            botObj.AddComponent<DiggingParticle>();
            botObj.AddComponent<Tunnel.TunnelGenerator>();

            return botObj;
        }

        // ===== 스폰 핸들러 (클라이언트에서 호출) =====

        /// <summary>
        /// 클라이언트가 서버의 봇 스폰 메시지를 받으면 호출.
        /// 서버와 동일한 컴포넌트 구조를 가진 GameObject를 생성한다.
        /// </summary>
        private GameObject OnClientSpawnBot(SpawnMessage msg)
        {
            var botObj = new GameObject("Bot_Network");
            botObj.SetActive(false); // Mirror가 초기화 후 자동 활성화함
            botObj.transform.position = msg.position;
            botObj.transform.rotation = msg.rotation;
            botObj.transform.localScale = msg.scale;
            botObj.layer = gameObject.layer;

            // Network 컴포넌트
            botObj.AddComponent<NetworkIdentity>();
            var nt = botObj.AddComponent<NetworkTransformReliable>();
            nt.syncDirection = SyncDirection.ServerToClient;
            nt.useFixedUpdate = true;
            nt.onlySyncOnChange = false;
            nt.onlySyncOnChangeCorrectionMultiplier = 3f;
            nt.interpolatePosition = true;
            nt.interpolateRotation = true;
            nt.positionPrecision = 0.003f;
            nt.rotationSensitivity = 0.003f;
            botObj.AddComponent<Network.NetworkBot>(); // SyncVar가 적용되면 비주얼 설정됨

            // Physics
            var rb = botObj.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            var col = botObj.AddComponent<CircleCollider2D>();
            col.radius = 0.3f;
            col.isTrigger = true;

            // Visuals (색상은 NetworkBot SyncVar 훅에서 설정됨)
            var visualObj = new GameObject("Visuals");
            visualObj.transform.SetParent(botObj.transform, false);
            var sr = visualObj.AddComponent<SpriteRenderer>();
            if (_botSprite != null) sr.sprite = _botSprite;
            sr.sortingOrder = 1;

            // Gameplay — AI는 클라이언트에서 비활성화 (NetworkBot.OnStartClient가 처리)
            var ai = botObj.AddComponent<AIController>();
            ai.enabled = false; // 서버가 NetworkTransform으로 위치 동기화
            botObj.AddComponent<MoleGrowth>();
            botObj.AddComponent<DiggingParticle>();
            botObj.AddComponent<Tunnel.TunnelGenerator>();

            return botObj;
        }

        private void OnClientUnSpawnBot(GameObject obj)
        {
            Destroy(obj);
        }
    }
}

