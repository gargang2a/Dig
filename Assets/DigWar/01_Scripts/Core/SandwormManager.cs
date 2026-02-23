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
        [SerializeField] private int _sandwormCount = 1;

        [Tooltip("스폰 후 활동까지 대기 시간 (초)")]
        [SerializeField] private float _spawnDelay = 5f;

        // 샌드웜 전용 assetId
        private const uint SANDWORM_ASSET_ID = 10002;

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
        }

        private void OnDisable()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.OnGameStarted -= OnGameStarted;
        }

        private void OnGameStarted()
        {
            // 서버에서만 샌드웜 스폰
            if (!NetworkServer.active) return;
            Invoke(nameof(SpawnSandworms), _spawnDelay);
        }

        private void SpawnSandworms()
        {
            if (_sandwormPrefab == null)
            {
                Debug.LogWarning("[SandwormManager] 프리팹이 할당되지 않았습니다.");
                return;
            }

            float mapRadius = 50f;
            if (GameManager.Instance != null && GameManager.Instance.Settings != null)
                mapRadius = GameManager.Instance.Settings.MapRadius;

            for (int i = 0; i < _sandwormCount; i++)
            {
                float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                Vector2 spawnPos = new Vector2(
                    Mathf.Cos(angle) * (mapRadius * 0.8f),
                    Mathf.Sin(angle) * (mapRadius * 0.8f)
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
                }

                NetworkServer.Spawn(worm, SANDWORM_ASSET_ID);
                worm.SetActive(true);

                Debug.Log($"🐛 [SandwormManager] Sandworm_{i} 네트워크 스폰 at {spawnPos}");
            }
        }

        // === 클라이언트 스폰 핸들러 ===

        private GameObject OnClientSpawnWorm(SpawnMessage msg)
        {
            if (_sandwormPrefab == null)
            {
                Debug.LogError("[SandwormManager] 클라이언트: 프리팹 누락");
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
            }

            return worm;
        }

        private void OnClientUnSpawnWorm(GameObject obj)
        {
            Destroy(obj);
        }
    }
}
