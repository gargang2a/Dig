using System.Collections;
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
                if (_playerTransform == null)
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
                int liveCount = 0;
                foreach (Network.NetworkPlayer np in Network.NetworkPlayer.ActivePlayers)
                {
                    if (np != null) liveCount++;
                }

                if (liveCount == 0) return null;

                int target = Random.Range(0, liveCount);
                foreach (Network.NetworkPlayer np in Network.NetworkPlayer.ActivePlayers)
                {
                    if (np == null) continue;
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

        private void SpawnGem()
        {
            if (_gemPrefab == null || _playerTransform == null) return;

            Vector2 dir = Random.insideUnitCircle.normalized;
            float dist = Random.Range(5f, _settings.GemSpawnRadius);
            Vector3 pos = _playerTransform.position + (Vector3)(dir * dist);

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

            GameObject obj = ObjectPoolManager.Instance.Spawn(_gemPrefab, pos, Quaternion.identity);

            var gem = obj.GetComponent<Gem>();
            if (gem != null)
            {
                gem.Initialize(_gemPrefab);
                gem.ConfigureNetworkMode(false, this);
            }

            _activeGemCount++;
        }

        private void SpawnNetworkGem(Vector3 pos)
        {
            if (!NetworkServer.active) return;

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

            var gemObj = Instantiate(_gemPrefab, msg.position, msg.rotation);
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


