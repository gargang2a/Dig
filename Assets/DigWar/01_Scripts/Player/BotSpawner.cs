using UnityEngine;
using Mirror;
using Core;
using Core.Data;

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
        [Header("봇 설정")]
        [Tooltip("Number of bots to spawn")]
        [SerializeField] private int _botCount = 3;

        [Tooltip("서버에서 죽은 봇을 자동으로 보충한다.")]
        [SerializeField] private bool _autoRespawnBots = true;

        [Tooltip("봇 개수 보정 주기(초)")]
        [SerializeField] private float _respawnCheckInterval = 0.5f;

        [Tooltip("Bot sprite")]
        [SerializeField] private Sprite _botSprite;

        private GameSettings _settings;
        private int _nextBotIndex;
        private float _nextRespawnCheckAt;

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

            if (GameManager.Instance == null) return;
            _settings = GameManager.Instance.Settings;
            _nextBotIndex = 0;

            for (int i = 0; i < _botCount; i++)
                SpawnBot(_nextBotIndex++);

            Debug.Log($"[BotSpawner] 서버에서 봇 {_botCount}마리 네트워크 스폰 완료");
        }

        private void Update()
        {
            if (!NetworkServer.active || !_autoRespawnBots) return;
            if (Time.time < _nextRespawnCheckAt) return;

            _nextRespawnCheckAt = Time.time + Mathf.Max(0.1f, _respawnCheckInterval);

            int activeServerBots = 0;
            foreach (Network.NetworkBot bot in Network.NetworkBot.ActiveBots)
            {
                if (bot == null) continue;
                if (!bot.isServer) continue;
                activeServerBots++;
            }

            int deficit = _botCount - activeServerBots;
            for (int i = 0; i < deficit; i++)
                SpawnBot(_nextBotIndex++);
        }

        /// <summary>서버에서 봇을 조립하고 네트워크 스폰한다.</summary>
        private void SpawnBot(int index)
        {
            var botObj = AssembleBot(index, enableAI: true);

            // NetworkBot의 SyncVar 설정 (스폰 전에!)
            var networkBot = botObj.GetComponent<Network.NetworkBot>();
            networkBot.BotIndex = index;

            // 네트워크 스폰 → 클라이언트에 전파
            NetworkServer.Spawn(botObj, BOT_ASSET_ID);

            // 스폰 후 활성화 (NetworkTransformReliable NullRef 방지)
            botObj.SetActive(true);
        }

        /// <summary>봇 GameObject를 조립한다.</summary>
        private GameObject AssembleBot(int index, bool enableAI)
        {
            float radius = _settings != null ? _settings.MapRadius * 0.7f : 20f;
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
