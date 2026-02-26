using UnityEngine;
using Mirror;
using Core;

namespace Core
{
    /// <summary>
    /// 게임 시작 시 모래벌레(Sandworm)를 생성하고 관리한다.
    /// [멀티플레이] 서버에서만 스폰하고 SpawnHandler로 클라이언트에 동기화.
    /// </summary>
    public class SandwormManager : MonoBehaviour
    {
        public static SandwormManager Instance { get; private set; }

        [Header("Sandworm Settings")]
        [Tooltip("모래벌레 프리팹 (Sandworm 컴포넌트 포함)")]
        [SerializeField] private GameObject _sandwormPrefab;

        [Tooltip("Active sandworm count")]
        [SerializeField] private int _sandwormCount = 15;

        [Tooltip("스폰 후 활동까지 대기 시간 (초)")]
        [SerializeField] private float _spawnDelay = 5f;

        // 샌드웜 전용 assetId
        private const uint SANDWORM_ASSET_ID = 10002;
        private const int FREE_MVP_SANDWORM_HARD_CAP = 15;
        private bool _spawnScheduled;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            // 서버/클라이언트 공통: 스폰 핸들러 등록
            NetworkClient.RegisterSpawnHandler(
                SANDWORM_ASSET_ID,
                OnClientSpawnWorm,
                OnClientUnSpawnWorm
            );
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            NetworkClient.UnregisterSpawnHandler(SANDWORM_ASSET_ID);
        }

        private void Start()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.OnGameStarted += OnGameStarted;

            // 전용 서버 자동 시작 경로에서 GameStarted 이벤트보다 늦게 Start가 호출될 수 있다.
            // 이미 게임이 활성화된 상태면 즉시 스폰 예약을 보장한다.
            if (NetworkServer.active && GameManager.Instance != null && GameManager.Instance.IsGameActive)
                ScheduleSpawnIfNeeded();
        }

        private void OnDisable()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.OnGameStarted -= OnGameStarted;

            if (_spawnScheduled)
            {
                CancelInvoke(nameof(SpawnSandworms));
                _spawnScheduled = false;
            }
        }

        private void OnGameStarted()
        {
            // 서버에서만 샌드웜 스폰
            if (!NetworkServer.active) return;
            ScheduleSpawnIfNeeded();
        }

        private void ScheduleSpawnIfNeeded()
        {
            if (_spawnScheduled) return;
            _spawnScheduled = true;
            Invoke(nameof(SpawnSandworms), _spawnDelay);
        }

        private void SpawnSandworms()
        {
            _spawnScheduled = false;

            if (_sandwormPrefab == null)
            {
                Debug.LogWarning("[SandwormManager] Sandworm prefab is not assigned.");
                return;
            }

            float mapRadius = 65f;
            float spawnRadiusRatio = 0.8f;
            if (GameManager.Instance != null && GameManager.Instance.Settings != null)
            {
                mapRadius = GameManager.Instance.Settings.MapRadius;
                spawnRadiusRatio = GameManager.Instance.Settings.SandwormSpawnRadiusRatio;
            }

            int spawnCount = ResolveSpawnCount();
            for (int i = 0; i < spawnCount; i++)
            {
                float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                Vector2 spawnPos = new Vector2(
                    Mathf.Cos(angle) * (mapRadius * spawnRadiusRatio),
                    Mathf.Sin(angle) * (mapRadius * spawnRadiusRatio)
                );

                var worm = Instantiate(_sandwormPrefab, spawnPos, Quaternion.identity);
                worm.SetActive(false); // NullRef 방지
                worm.name = $"Sandworm_{i}";

                // NetworkIdentity 추가 (프리팹에 없으면 런타임 추가)
                if (worm.GetComponent<NetworkIdentity>() == null)
                    worm.AddComponent<NetworkIdentity>();

                // NetworkTransform 추가 (머리 위치 동기화)
                if (worm.GetComponent<NetworkTransformReliable>() == null)
                {
                    var nt = worm.AddComponent<NetworkTransformReliable>();
                    nt.syncDirection = SyncDirection.ServerToClient;
                    nt.useFixedUpdate = true;
                    nt.onlySyncOnChange = false;
                    nt.onlySyncOnChangeCorrectionMultiplier = 3f;
                    nt.interpolatePosition = true;
                    nt.interpolateRotation = true;
                    nt.positionPrecision = 0.003f;
                    nt.rotationSensitivity = 0.003f;
                }

                NetworkServer.Spawn(worm, SANDWORM_ASSET_ID);
                worm.SetActive(true);

                Debug.Log($"[SandwormManager] Spawned Sandworm_{i} at {spawnPos}");
            }
        }

        private int ResolveSpawnCount()
        {
            int safeCount = Mathf.Max(0, _sandwormCount);
            if (!NetworkServer.active)
                return safeCount;

            int clamped = Mathf.Min(safeCount, FREE_MVP_SANDWORM_HARD_CAP);
            if (safeCount != clamped)
            {
                Debug.LogWarning(
                    $"[SandwormManager] Enforcing Free-MVP sandworm cap: {safeCount} -> {clamped}");
            }

            return clamped;
        }

        // === 클라이언트 스폰 핸들러 ===

        private GameObject OnClientSpawnWorm(SpawnMessage msg)
        {
            if (_sandwormPrefab == null)
            {
                Debug.LogError("[SandwormManager] Client spawn failed: prefab missing.");
                // 빈 오브젝트라도 반환해야 Mirror가 크래시하지 않음
                var empty = new GameObject("Sandworm_Empty");
                empty.SetActive(false);
                empty.AddComponent<NetworkIdentity>();
                return empty;
            }

            var worm = Instantiate(_sandwormPrefab, msg.position, msg.rotation);
            worm.SetActive(false); // Mirror가 초기화 후 활성화
            worm.name = "Sandworm_Client";

            // NetworkIdentity 추가
            if (worm.GetComponent<NetworkIdentity>() == null)
                worm.AddComponent<NetworkIdentity>();

            if (worm.GetComponent<NetworkTransformReliable>() == null)
            {
                var nt = worm.AddComponent<NetworkTransformReliable>();
                nt.syncDirection = SyncDirection.ServerToClient;
                nt.useFixedUpdate = true;
                nt.onlySyncOnChange = false;
                nt.onlySyncOnChangeCorrectionMultiplier = 3f;
                nt.interpolatePosition = true;
                nt.interpolateRotation = true;
                nt.positionPrecision = 0.003f;
                nt.rotationSensitivity = 0.003f;
            }

            return worm;
        }

        private void OnClientUnSpawnWorm(GameObject obj)
        {
            Destroy(obj);
        }
    }
}
