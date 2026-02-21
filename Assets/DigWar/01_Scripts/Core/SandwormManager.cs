using UnityEngine;
using Core;

namespace Core
{
    /// <summary>
    /// 게임 시작 시 모래벌레(Sandworm)를 생성하고 관리한다.
    /// Global 오브젝트에 부착하여 사용.
    /// </summary>
    public class SandwormManager : MonoBehaviour
    {
        public static SandwormManager Instance { get; private set; }

        [Header("Sandworm Settings")]
        [Tooltip("모래벌레 프리팹 (Sandworm 컴포넌트 포함)")]
        [SerializeField] private GameObject _sandwormPrefab;

        [Tooltip("동시에 존재하는 모래벌레 수")]
        [SerializeField] private int _sandwormCount = 1;

        [Tooltip("스폰 후 활동까지 대기 시간 (초)")]
        [SerializeField] private float _spawnDelay = 5f;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Start()
        {
            // GameManager의 게임 시작 이벤트에 연동
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
                // 맵 외곽 랜덤 위치에서 스폰 (안쪽을 향해 출발)
                float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                Vector2 spawnPos = new Vector2(
                    Mathf.Cos(angle) * (mapRadius * 0.8f),
                    Mathf.Sin(angle) * (mapRadius * 0.8f)
                );

                var worm = Instantiate(_sandwormPrefab, spawnPos, Quaternion.identity);
                worm.name = $"Sandworm_{i}";

                Debug.Log($"🐛 [SandwormManager] Sandworm_{i} 스폰 at {spawnPos}");
            }
        }
    }
}
