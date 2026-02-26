using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using Core;
using Core.Data;
using Player;

namespace World
{
    /// <summary>
    /// 플레이어 주변에 주기적으로 젬을 생성한다.
    /// ObjectPoolManager를 사용할 때 MaxGemCount로 활성 젬 수를 제한한다.
    /// </summary>
    public class GemSpawner : MonoBehaviour
    {
        public static GemSpawner Instance { get; private set; }

        [SerializeField] private GameObject _gemPrefab;

        #pragma warning disable CS0414
        [Tooltip("젬 배치 초기 시드 (재현성 유지)")]
        [SerializeField] private int _spawnSeed = 123;
        #pragma warning restore CS0414

        private GameSettings _settings;
        private Transform _playerTransform;
        private int _activeGemCount;
        private float _nextSinglePlayerLookupAt;
        private float _nextSpawnCenterWarningAt;
        private const float MIN_SPAWN_DISTANCE = 2f;
        private const float MAP_EDGE_MARGIN = 1.5f;
        private const int SPAWN_POSITION_MAX_ATTEMPTS = 10;

        private const uint GEM_ASSET_ID = 10003;
        private static bool IsNetworkMode => NetworkClient.active || NetworkServer.active;
        private static bool IsClientOnlyNetwork => NetworkClient.active && !NetworkServer.active;

        private void Awake()
        {
            if (Instance != null && Instance != this)
                Debug.LogWarning("[GemSpawner] Multiple instances detected. Latest instance will be used.");
            Instance = this;

            NetworkClient.RegisterSpawnHandler(
                GEM_ASSET_ID,
                OnClientSpawnGem,
                OnClientUnSpawnGem
            );
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            NetworkClient.UnregisterSpawnHandler(GEM_ASSET_ID);
        }

        private void Start()
        {
            if (GameManager.Instance == null || GameManager.Instance.Settings == null)
            {
                Debug.LogError("[GemSpawner] GameManager 누락");
                enabled = false;
                return;
            }

            _settings = GameManager.Instance.Settings;

            StartCoroutine(BootstrapSpawnRoutine());
        }

        private IEnumerator BootstrapSpawnRoutine()
        {
            // AutoStart Policy로 네트워크 role이 확정될 때까지 한 프레임 대기.
            yield return null;

            if (IsClientOnlyNetwork)
            {
                Debug.Log("[GemSpawner] client waiting for server gem sync");
                yield break;
            }

            StartCoroutine(SpawnRoutine());
        }

        private IEnumerator SpawnRoutine()
        {
            var wait = new WaitForSeconds(_settings.GemSpawnInterval);

            while (true)
            {
                if (IsClientOnlyNetwork)
                {
                    yield return wait;
                    continue;
                }

                if (!IsSpawnCenterValid(_playerTransform))
                {
                    _playerTransform = ResolveSpawnCenter();
                }

                if (_playerTransform != null && GameManager.Instance.IsGameActive
                    && _activeGemCount < _settings.MaxGemCount)
                {
                    SpawnGem();
                }

                yield return wait;
            }
        }

        private Transform ResolveSpawnCenter()
        {
            if (IsNetworkMode)
            {
                // Dedicated Server에서는 ActivePlayers 레지스트리 타이밍 이슈가
                // 있을 수 있어 spawned 테이블 스캔을 1순위로 사용한다.
                if (NetworkServer.active)
                {
                    Transform spawnedTarget = ResolveSpawnCenterFromServerSpawned();
                    if (spawnedTarget != null)
                        return spawnedTarget;
                }

                int liveCount = 0;
                foreach (Network.NetworkPlayer np in Network.NetworkPlayer.ActivePlayers)
                {
                    if (np == null) continue;
                    if (!IsSpawnCenterValid(np.transform)) continue;
                    liveCount++;
                }

                if (liveCount == 0)
                {
                    LogSpawnCenterMissingIfNeeded();
                    return null;
                }

                int target = Random.Range(0, liveCount);
                foreach (Network.NetworkPlayer np in Network.NetworkPlayer.ActivePlayers)
                {
                    if (np == null) continue;
                    if (!IsSpawnCenterValid(np.transform)) continue;
                    if (target == 0) return np.transform;
                    target--;
                }

                return null;
            }

            PlayerController localController = PlayerController.LocalController;
            if (localController != null)
                return localController.transform;

            if (Time.unscaledTime < _nextSinglePlayerLookupAt) return null;
            _nextSinglePlayerLookupAt = Time.unscaledTime + 1f;

            var player = FindObjectOfType<PlayerController>();
            return player != null ? player.transform : null;
        }

        private Transform ResolveSpawnCenterFromServerSpawned()
        {
            int liveCount = 0;
            foreach (KeyValuePair<uint, NetworkIdentity> pair in NetworkServer.spawned)
            {
                NetworkIdentity identity = pair.Value;
                if (identity == null) continue;

                Network.NetworkPlayer player = identity.GetComponent<Network.NetworkPlayer>();
                if (player == null || player.IsDead) continue;
                if (!IsSpawnCenterValid(player.transform)) continue;
                liveCount++;
            }

            if (liveCount == 0)
            {
                LogSpawnCenterMissingIfNeeded();
                return null;
            }

            int target = Random.Range(0, liveCount);
            foreach (KeyValuePair<uint, NetworkIdentity> pair in NetworkServer.spawned)
            {
                NetworkIdentity identity = pair.Value;
                if (identity == null) continue;

                Network.NetworkPlayer player = identity.GetComponent<Network.NetworkPlayer>();
                if (player == null || player.IsDead) continue;
                if (!IsSpawnCenterValid(player.transform)) continue;

                if (target == 0)
                    return player.transform;

                target--;
            }

            return null;
        }

        private void LogSpawnCenterMissingIfNeeded()
        {
            if (!NetworkServer.active || Time.unscaledTime < _nextSpawnCenterWarningAt)
                return;

            _nextSpawnCenterWarningAt = Time.unscaledTime + 5f;

            int activePlayers = 0;
            foreach (Network.NetworkPlayer np in Network.NetworkPlayer.ActivePlayers)
            {
                if (np != null) activePlayers++;
            }

            int spawnedPlayers = 0;
            foreach (KeyValuePair<uint, NetworkIdentity> pair in NetworkServer.spawned)
            {
                NetworkIdentity identity = pair.Value;
                if (identity == null) continue;
                if (identity.GetComponent<Network.NetworkPlayer>() == null) continue;
                spawnedPlayers++;
            }

            Debug.LogWarning(
                "[GemSpawner] spawn center unavailable: " +
                $"activePlayers={activePlayers}, spawnedPlayers={spawnedPlayers}, " +
                $"isGameActive={GameManager.Instance != null && GameManager.Instance.IsGameActive}");
        }

        private void SpawnGem()
        {
            if (_gemPrefab == null || _playerTransform == null) return;

            Vector3 pos = ResolveSpawnPositionAroundCenter(_playerTransform);

            if (IsNetworkMode)
            {
                SpawnNetworkGem(pos);
                return;
            }

            SpawnLocalGem(pos);
        }

        private void SpawnLocalGem(Vector3 pos)
        {
            if (ObjectPoolManager.Instance == null) return;
            pos = ClampToMapBounds(pos);

            GameObject obj = ObjectPoolManager.Instance.Spawn(_gemPrefab, pos, Quaternion.identity);

            var gem = obj.GetComponent<Gem>();
            if (gem != null)
            {
                gem.Initialize(_gemPrefab);
                gem.ConfigureNetworkMode(false, this);
            }

            _activeGemCount++;
        }

        private bool IsSpawnCenterValid(Transform center)
        {
            if (center == null)
                return false;

            Vector3 position = center.position;
            if (!IsFiniteVector3(position))
                return false;

            float mapRadius = _settings != null ? _settings.MapRadius : 65f;
            float maxAllowedDistance = Mathf.Max(1f, mapRadius - MAP_EDGE_MARGIN + 2f);
            if (position.sqrMagnitude > maxAllowedDistance * maxAllowedDistance)
                return false;

            if (!IsNetworkMode)
                return true;

            Network.NetworkPlayer owner = center.GetComponent<Network.NetworkPlayer>();
            if (owner == null)
                owner = center.GetComponentInParent<Network.NetworkPlayer>();

            if (owner == null)
                return true;

            return !owner.IsDead;
        }

        private Vector3 ResolveSpawnPositionAroundCenter(Transform center)
        {
            Vector3 centerPos = center.position;
            float mapRadius = _settings != null ? _settings.MapRadius : 65f;
            mapRadius = Mathf.Max(10f, mapRadius);

            float requestedSpawnRadius = _settings != null ? _settings.GemSpawnRadius : 15f;
            float effectiveSpawnRadius = Mathf.Min(
                Mathf.Max(MIN_SPAWN_DISTANCE, requestedSpawnRadius),
                Mathf.Max(MIN_SPAWN_DISTANCE, mapRadius - MAP_EDGE_MARGIN));

            float maxRadius = Mathf.Max(1f, mapRadius - MAP_EDGE_MARGIN);
            float maxRadiusSqr = maxRadius * maxRadius;

            // 네트워크 모드는 월드 전역 균등 분포를 우선해 특정 방향 편중을 막는다.
            if (IsNetworkMode)
                return ResolveRandomPointInsideMap(maxRadius, centerPos.z);

            for (int attempt = 0; attempt < SPAWN_POSITION_MAX_ATTEMPTS; attempt++)
            {
                Vector2 dir = Random.insideUnitCircle;
                if (dir.sqrMagnitude < 0.0001f)
                    dir = Vector2.right;

                float dist = Random.Range(MIN_SPAWN_DISTANCE, effectiveSpawnRadius);
                Vector2 candidate2D = (Vector2)centerPos + dir.normalized * dist;
                if (candidate2D.sqrMagnitude <= maxRadiusSqr)
                    return new Vector3(candidate2D.x, candidate2D.y, centerPos.z);
            }

            // 경계 바깥 후보를 경계로 투영하면 특정 방향(예: 동쪽)으로 편중될 수 있어
            // 재시도 실패 시에는 맵 내부 균등 랜덤 위치로 폴백한다.
            return ResolveRandomPointInsideMap(maxRadius, centerPos.z);
        }

        private static Vector3 ResolveRandomPointInsideMap(float radius, float z)
        {
            Vector2 randomPoint = Random.insideUnitCircle * Mathf.Max(1f, radius);
            return new Vector3(randomPoint.x, randomPoint.y, z);
        }

        private static bool IsFiniteVector3(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private void SpawnNetworkGem(Vector3 pos)
        {
            if (!NetworkServer.active) return;
            pos = ClampToMapBounds(pos);

            var gemObj = Instantiate(_gemPrefab, pos, Quaternion.identity);
            gemObj.SetActive(false); // NetworkIdentity 초기화 안정화
            if (gemObj.GetComponent<NetworkIdentity>() == null)
                gemObj.AddComponent<NetworkIdentity>();

            var gem = gemObj.GetComponent<Gem>();
            if (gem != null)
                gem.ConfigureNetworkMode(true, this);

            NetworkServer.Spawn(gemObj, GEM_ASSET_ID);
            gemObj.SetActive(true);

            _activeGemCount++;
        }

        /// <summary>
        /// Gem 수집/제거 시 호출. 활성 카운트를 감소시킨다.
        /// </summary>
        public void NotifyGemCollected()
        {
            _activeGemCount = Mathf.Max(0, _activeGemCount - 1);
        }

        /// <summary>
        /// 특정 위치에 젬을 드롭한다. 부스트 중 소비된 점수를 월드에 환원한다.
        /// </summary>
        public void DropGemAt(Vector3 worldPos)
        {
            if (_gemPrefab == null) return;
            worldPos = ClampToMapBounds(worldPos);

            if (IsNetworkMode)
            {
                if (NetworkServer.active)
                    SpawnNetworkGem(worldPos);
                return;
            }

            if (ObjectPoolManager.Instance == null) return;

            GameObject obj = ObjectPoolManager.Instance.Spawn(_gemPrefab, worldPos, Quaternion.identity);
            var gem = obj.GetComponent<Gem>();
            if (gem != null)
            {
                gem.Initialize(_gemPrefab);
                gem.ConfigureNetworkMode(false, this);
            }

            _activeGemCount++;
        }

        private Vector3 ClampToMapBounds(Vector3 worldPos)
        {
            float mapRadius = _settings != null ? _settings.MapRadius : 65f;
            float clampRadius = Mathf.Max(1f, mapRadius - MAP_EDGE_MARGIN);

            if (!IsFiniteVector3(worldPos))
            {
                Vector2 fallback = Random.insideUnitCircle * clampRadius;
                return new Vector3(fallback.x, fallback.y, 0f);
            }

            Vector2 clamped2D = new Vector2(worldPos.x, worldPos.y);
            float maxSqrRadius = clampRadius * clampRadius;
            if (clamped2D.sqrMagnitude > maxSqrRadius)
                clamped2D = clamped2D.normalized * clampRadius;

            return new Vector3(clamped2D.x, clamped2D.y, worldPos.z);
        }

        // === 클라이언트 스폰 핸들러 ===
        private GameObject OnClientSpawnGem(SpawnMessage msg)
        {
            if (_gemPrefab == null)
            {
                var empty = new GameObject("Gem_Empty");
                empty.SetActive(false);
                empty.AddComponent<NetworkIdentity>();
                return empty;
            }

            Vector3 clampedPos = ClampToMapBounds(msg.position);
            var gemObj = Instantiate(_gemPrefab, clampedPos, msg.rotation);
            gemObj.SetActive(false);
            gemObj.transform.localScale = msg.scale;

            if (gemObj.GetComponent<NetworkIdentity>() == null)
                gemObj.AddComponent<NetworkIdentity>();

            var gem = gemObj.GetComponent<Gem>();
            if (gem != null)
                gem.ConfigureNetworkMode(true, this);

            return gemObj;
        }

        private void OnClientUnSpawnGem(GameObject obj)
        {
            Destroy(obj);
        }
    }
}

