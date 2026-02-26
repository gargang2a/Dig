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

        [Header("Drop Spawn Control")]
        [Tooltip("샌드웜/드롭 젬 전역 초당 생성 상한(0 이하: 제한 없음)")]
        [Min(0f)] [SerializeField] private float _maxDropSpawnPerSecond = 4f;
        [Tooltip("활성 젬 점유율이 이 값 이상이면 드롭 스폰을 중지한다.")]
        [Range(0.1f, 1f)] [SerializeField] private float _dropSpawnStopFillRatio = 0.75f;
        [Tooltip("드롭 좌표가 경계 밖일 때 안쪽으로 밀어 넣는 기본 거리.")]
        [Range(0f, 10f)] [SerializeField] private float _dropClampInset = 1f;
        [Tooltip("드롭 경계 보정 시 외곽 라인 고착을 줄이기 위한 반경 지터.")]
        [Range(0f, 6f)] [SerializeField] private float _dropClampJitter = 1f;

        [Header("Initial Spawn Distribution")]
        [Tooltip("초기/보정 스폰을 맵 전역 랜덤 분포로 강제한다.")]
        [SerializeField] private bool _useRandomizedDistribution = true;
        [Tooltip("랜덤 스폰 반경 비율(맵 반경 대비). 1에 가까울수록 외곽까지 생성된다.")]
        [Range(0.5f, 1f)] [SerializeField] private float _randomSpawnRadiusRatio = 1f;
        [Tooltip("랜덤 스폰 반경 가중치(0.5=면적 균등, 1.0 이상일수록 중앙 밀집).")]
        [Range(0.5f, 2.5f)] [SerializeField] private float _randomSpawnRadialExponent = 0.65f;
        [Tooltip("초기 루틴 스폰에서 섹터 밸런싱을 적용해 사분면 편중을 줄인다.")]
        [SerializeField] private bool _enableInitialSectorBalancing = true;
        [Tooltip("초기 루틴 스폰 중 섹터 밸런싱을 적용할 개수")]
        [Min(0)] [SerializeField] private int _initialSectorBalancedSpawnCount = 210;
        [Tooltip("초기 반경 분포 가중치(0.5=면적 균등, 1.0 이상일수록 중앙 밀집)")]
        [Range(0.5f, 2.5f)] [SerializeField] private float _initialSpawnRadialExponent = 1.25f;
        [Tooltip("초기 필드 균등 배치가 끝날 때까지 샌드웜 드롭 스폰을 차단한다.")]
        [SerializeField] private bool _blockDropSpawnDuringInitialFill = true;

        [Header("Drop Distribution Rebalance")]
        [Tooltip("드롭 좌표가 이 반경 비율을 넘으면 안쪽 재배치를 시작한다.")]
        [Range(0.5f, 1f)] [SerializeField] private float _dropRecenterStartRatio = 0.78f;
        [Tooltip("외곽 재배치 시 목표 반경 비율(맵 반경 대비).")]
        [Range(0.3f, 1f)] [SerializeField] private float _dropRecenterTargetRatio = 0.68f;
        [Tooltip("외곽 재배치 강도(0=비활성, 1=강하게 안쪽 이동).")]
        [Range(0f, 1f)] [SerializeField] private float _dropRecenterStrength = 0.75f;

        #pragma warning disable CS0414
        [Tooltip("젬 배치 초기 시드 (재현성 유지)")]
        [SerializeField] private int _spawnSeed = 123;
        #pragma warning restore CS0414

        private GameSettings _settings;
        private Transform _playerTransform;
        private int _activeGemCount;
        private int _totalSpawnCount;
        private bool _initialBurstCompleted;
        private float _nextSinglePlayerLookupAt;
        private float _nextSpawnCenterWarningAt;
        private float _nextDropSpawnAt;
        private float _initialSpawnAngleOffset;
        private const float MIN_SPAWN_DISTANCE = 2f;
        private const float MAP_EDGE_MARGIN = 1.5f;
        private const int SPAWN_POSITION_MAX_ATTEMPTS = 10;
        private const float GOLDEN_ANGLE_RADIANS = 2.39996323f;

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
            ResetInitialSpawnBalancingState();
            _initialBurstCompleted = false;

            StartCoroutine(BootstrapSpawnRoutine());
        }

        private IEnumerator BootstrapSpawnRoutine()
        {
            // AutoStart Policy로 네트워크 role이 확정될 때까지 대기.
            yield return null;
            const float maxWaitSeconds = 10f;
            float waitedSeconds = 0f;
            while (NetworkManager.singleton != null &&
                   !NetworkServer.active &&
                   !NetworkClient.active &&
                   waitedSeconds < maxWaitSeconds)
            {
                waitedSeconds += Time.unscaledDeltaTime;
                yield return null;
            }

            if (IsClientOnlyNetwork)
            {
                Debug.Log("[GemSpawner] client waiting for server gem sync");
                yield break;
            }

            TrySpawnInitialFieldBurstIfNeeded();
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

                if (!_initialBurstCompleted)
                    TrySpawnInitialFieldBurstIfNeeded();

                if (!IsSpawnCenterValid(_playerTransform))
                {
                    _playerTransform = ResolveSpawnCenter();
                }

                if (_playerTransform != null && GameManager.Instance.IsGameActive
                    && CanSpawnMoreGems())
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

        private bool CanSpawnMoreGems()
        {
            int maxGemCount = _settings != null
                ? Mathf.Max(1, _settings.MaxGemCount)
                : int.MaxValue;
            return _activeGemCount < maxGemCount;
        }

        private bool ShouldAllowDropSpawn(bool forceDrop)
        {
            if (!CanSpawnMoreGems())
                return false;

            if (forceDrop)
                return true;

            if (_blockDropSpawnDuringInitialFill && ShouldUseInitialSectorBalancing())
                return false;

            int maxGemCount = _settings != null
                ? Mathf.Max(1, _settings.MaxGemCount)
                : 1;
            float fillRatio = (float)_activeGemCount / maxGemCount;
            if (fillRatio >= _dropSpawnStopFillRatio)
                return false;

            if (_maxDropSpawnPerSecond <= 0f)
                return true;

            float minInterval = 1f / Mathf.Max(0.1f, _maxDropSpawnPerSecond);
            float now = Time.unscaledTime;
            if (now < _nextDropSpawnAt)
                return false;

            _nextDropSpawnAt = now + minInterval;
            return true;
        }

        private bool TrySpawnInitialFieldBurstIfNeeded()
        {
            if (_initialBurstCompleted)
                return true;

            if (_gemPrefab == null || _settings == null)
                return false;

            if (NetworkManager.singleton != null && !NetworkServer.active && !NetworkClient.active)
                return false;

            if (IsNetworkMode && !NetworkServer.active)
                return false;

            int targetGemCount = Mathf.Max(1, _settings.MaxGemCount);
            if (_activeGemCount >= targetGemCount)
            {
                _initialBurstCompleted = true;
                return true;
            }

            float mapRadius = Mathf.Max(10f, _settings.MapRadius);
            float maxRadius = Mathf.Max(1f, mapRadius - MAP_EDGE_MARGIN);
            int spawnedCount = 0;
            Debug.Log(
                $"[GemSpawner] Initial burst start: mode={(IsNetworkMode ? "network" : "local")}, " +
                $"active={_activeGemCount}, target={targetGemCount}");

            for (int i = _activeGemCount; i < targetGemCount; i++)
            {
                float randomSpawnRadius = ResolveRandomSpawnRadius(maxRadius);
                Vector3 pos = _useRandomizedDistribution
                    ? ResolveRandomPointInsideMap(randomSpawnRadius, 0f)
                    : ResolveInitialSectorBalancedPointInsideMap(maxRadius, 0f);
                if (IsNetworkMode)
                    SpawnNetworkGem(pos);
                else
                    SpawnLocalGem(pos);
                spawnedCount++;
            }

            Debug.Log($"[GemSpawner] Initial burst completed: spawned={spawnedCount}, active={_activeGemCount}");
            _initialBurstCompleted = true;
            return true;
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
            _totalSpawnCount++;
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

            // 초기 필드 채우기 구간은 네트워크/로컬 모드와 무관하게
            // 전역 섹터 밸런싱을 우선해 방향 편중을 완화한다.
            if (ShouldUseInitialSectorBalancing())
                return ResolveInitialSectorBalancedPointInsideMap(maxRadius, centerPos.z);

            // 네트워크 모드는 외곽 과집중을 막기 위해
            // 랜덤 반경/중앙 가중치를 적용한 전역 랜덤 분포를 사용한다.
            if (IsNetworkMode)
            {
                float randomSpawnRadius = ResolveRandomSpawnRadius(maxRadius);
                return ResolveRandomPointInsideMap(randomSpawnRadius, centerPos.z);
            }

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
            return ResolveRandomPointInsideMap(ResolveRandomSpawnRadius(maxRadius), centerPos.z);
        }

        private bool ShouldUseInitialSectorBalancing()
        {
            if (_useRandomizedDistribution)
                return false;

            if (!_enableInitialSectorBalancing)
                return false;

            int limit = ResolveInitialSectorBalancingLimit();
            if (limit <= 0)
                return false;

            return _totalSpawnCount < limit;
        }

        private int ResolveInitialSectorBalancingLimit()
        {
            int configuredLimit = Mathf.Max(0, _initialSectorBalancedSpawnCount);
            int maxGemCount = _settings != null ? Mathf.Max(0, _settings.MaxGemCount) : 0;
            return Mathf.Max(configuredLimit, maxGemCount);
        }

        private Vector3 ResolveInitialSectorBalancedPointInsideMap(float radius, float z)
        {
            float safeRadius = Mathf.Max(1f, radius);
            int limit = Mathf.Max(1, ResolveInitialSectorBalancingLimit());
            float normalizedIndex = ((_totalSpawnCount % limit) + 0.5f) / limit;
            float radialExponent = Mathf.Clamp(_initialSpawnRadialExponent, 0.5f, 2.5f);
            float distance = Mathf.Pow(normalizedIndex, radialExponent) * safeRadius;
            float angle = _initialSpawnAngleOffset + (_totalSpawnCount * GOLDEN_ANGLE_RADIANS);

            // 동일 반경대에서의 겹침을 줄이기 위한 소폭 지터.
            float jitterRadius = safeRadius / Mathf.Sqrt(limit) * 0.35f;
            Vector2 jitter = Random.insideUnitCircle * jitterRadius;
            Vector2 point = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance + jitter;
            if (point.sqrMagnitude > safeRadius * safeRadius)
                point = point.normalized * safeRadius;

            return new Vector3(
                point.x,
                point.y,
                z);
        }

        private void ResetInitialSpawnBalancingState()
        {
            _totalSpawnCount = 0;
            _initialSpawnAngleOffset = Random.Range(0f, Mathf.PI * 2f);
        }

        private float ResolveRandomSpawnRadius(float maxRadius)
        {
            float safeMaxRadius = Mathf.Max(1f, maxRadius);
            float ratio = Mathf.Clamp(_randomSpawnRadiusRatio, 0.5f, 1f);
            return Mathf.Max(1f, safeMaxRadius * ratio);
        }

        private Vector3 ResolveRandomPointInsideMap(float radius, float z)
        {
            float safeRadius = Mathf.Max(1f, radius);
            float radialExponent = Mathf.Clamp(_randomSpawnRadialExponent, 0.5f, 2.5f);
            float distance = Mathf.Pow(Random.value, radialExponent) * safeRadius;
            float angle = Random.Range(0f, Mathf.PI * 2f);

            return new Vector3(
                Mathf.Cos(angle) * distance,
                Mathf.Sin(angle) * distance,
                z);
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
            _totalSpawnCount++;
        }

        /// <summary>
        /// Gem 수집/제거 시 호출. 활성 카운트를 감소시킨다.
        /// </summary>
        public void NotifyGemCollected()
        {
            _activeGemCount = Mathf.Max(0, _activeGemCount - 1);
            if (_activeGemCount == 0)
            {
                ResetInitialSpawnBalancingState();
                _initialBurstCompleted = false;
            }
        }

        /// <summary>
        /// 특정 위치에 젬을 드롭한다. 부스트 중 소비된 점수를 월드에 환원한다.
        /// </summary>
        public void DropGemAt(Vector3 worldPos, bool forceDrop = false)
        {
            if (_gemPrefab == null) return;
            if (!ShouldAllowDropSpawn(forceDrop)) return;

            // 초기 필드 채우기 구간에서는 드롭 경로도 섹터 밸런싱에 참여시켜
            // 특정 방향으로 누적되는 편중을 완화한다.
            if (!forceDrop && ShouldUseInitialSectorBalancing())
            {
                float mapRadius = _settings != null ? _settings.MapRadius : 65f;
                float maxRadius = Mathf.Max(1f, mapRadius - MAP_EDGE_MARGIN);
                worldPos = ResolveInitialSectorBalancedPointInsideMap(maxRadius, worldPos.z);
            }

            worldPos = RebalanceDropPositionForDistribution(worldPos);
            worldPos = ClampToMapBounds(worldPos, applyDropInset: true);

            if (IsNetworkMode)
            {
                if (NetworkServer.active)
                    SpawnNetworkGem(worldPos);
                return;
            }

            if (ObjectPoolManager.Instance == null) return;

            SpawnLocalGem(worldPos);
        }

        private Vector3 RebalanceDropPositionForDistribution(Vector3 worldPos)
        {
            if (!IsFiniteVector3(worldPos))
                return worldPos;

            float mapRadius = _settings != null ? _settings.MapRadius : 65f;
            float clampRadius = Mathf.Max(1f, mapRadius - MAP_EDGE_MARGIN);
            Vector2 pos = new Vector2(worldPos.x, worldPos.y);
            float radius = pos.magnitude;
            if (radius <= 0.0001f)
                return worldPos;

            float startRadius = Mathf.Clamp(_dropRecenterStartRatio, 0.5f, 1f) * clampRadius;
            if (radius <= startRadius)
                return worldPos;

            float targetRadius = clampRadius * Mathf.Clamp(_dropRecenterTargetRatio, 0.3f, 1f);
            targetRadius = Mathf.Min(targetRadius, radius);

            float sourceRadius = Mathf.Min(radius, clampRadius);
            float t = Mathf.InverseLerp(startRadius, clampRadius, sourceRadius);
            float strength = Mathf.Clamp01(_dropRecenterStrength);
            float recenteredRadius = Mathf.Lerp(sourceRadius, targetRadius, t * strength);

            Vector2 recentered = pos.normalized * recenteredRadius;
            return new Vector3(recentered.x, recentered.y, worldPos.z);
        }

        private Vector3 ClampToMapBounds(Vector3 worldPos, bool applyDropInset = false)
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
            {
                Vector2 direction = clamped2D.sqrMagnitude > 0.0001f
                    ? clamped2D.normalized
                    : Vector2.right;
                float targetRadius = clampRadius;

                if (applyDropInset)
                {
                    float maxInset = Mathf.Max(0f, clampRadius - 1f);
                    float baseInset = Mathf.Clamp(_dropClampInset, 0f, maxInset);
                    float jitter = Mathf.Clamp(_dropClampJitter, 0f, maxInset);
                    float inwardOffset = baseInset;
                    if (jitter > 0.0001f)
                        inwardOffset += Random.Range(0f, jitter);
                    targetRadius = Mathf.Max(1f, clampRadius - inwardOffset);
                }

                clamped2D = direction * targetRadius;
            }

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

