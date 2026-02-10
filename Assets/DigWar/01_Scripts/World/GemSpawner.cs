using System.Collections;
using UnityEngine;
using Core;
using Core.Data;
using Player;

namespace World
{
    /// <summary>
    /// 플레이어 주변에 주기적으로 보석을 생성한다.
    /// ObjectPoolManager를 통해 재사용하며, MaxGemCount로 활성 보석 수를 제한한다.
    /// </summary>
    public class GemSpawner : MonoBehaviour
    {
        [SerializeField] private GameObject _gemPrefab;

        private GameSettings _settings;
        private Transform _playerTransform;
        private int _activeGemCount;

        private void Start()
        {
            if (GameManager.Instance == null || GameManager.Instance.Settings == null)
            {
                Debug.LogError("[GemSpawner] GameManager 누락");
                enabled = false;
                return;
            }

            _settings = GameManager.Instance.Settings;
            StartCoroutine(SpawnRoutine());
        }

        private IEnumerator SpawnRoutine()
        {
            var wait = new WaitForSeconds(_settings.GemSpawnInterval);

            while (true)
            {
                if (_playerTransform == null)
                {
                    var player = FindObjectOfType<PlayerController>();
                    if (player != null) _playerTransform = player.transform;
                }

                if (_playerTransform != null && GameManager.Instance.IsGameActive
                    && _activeGemCount < _settings.MaxGemCount)
                {
                    SpawnGem();
                }

                yield return wait;
            }
        }

        private void SpawnGem()
        {
            if (_gemPrefab == null || ObjectPoolManager.Instance == null) return;

            Vector2 dir = Random.insideUnitCircle.normalized;
            float dist = Random.Range(5f, _settings.GemSpawnRadius);
            Vector3 pos = _playerTransform.position + (Vector3)(dir * dist);

            GameObject obj = ObjectPoolManager.Instance.Spawn(_gemPrefab, pos, Quaternion.identity);

            var gem = obj.GetComponent<Gem>();
            if (gem != null)
                gem.Initialize(_gemPrefab);

            _activeGemCount++;
        }

        /// <summary>
        /// Gem이 수집/제거될 때 호출. 활성 카운트를 감소시킨다.
        /// </summary>
        public void NotifyGemCollected()
        {
            _activeGemCount = Mathf.Max(0, _activeGemCount - 1);
        }
    }
}
