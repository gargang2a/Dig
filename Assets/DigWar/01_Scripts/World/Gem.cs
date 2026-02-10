using UnityEngine;
using Core;

namespace World
{
    /// <summary>
    /// 맵에 생성되는 보석.
    /// 유휴 상태에서 흔들리고, 플레이어 접근 시 빨려들어간다.
    /// 충돌 시 점수를 부여하고 풀로 반환된다.
    /// </summary>
    [RequireComponent(typeof(CircleCollider2D))]
    public class Gem : MonoBehaviour, IPoolable
    {
        private CircleCollider2D _collider;
        private GameObject _originPrefab;
        private GemSpawner _spawner;
        private Transform _playerTransform;

        // Wobble (유휴 흔들림)
        private Vector3 _spawnPosition;
        private float _wobbleOffset;
        private const float WOBBLE_AMPLITUDE = 0.08f;
        private const float WOBBLE_SPEED = 3f;

        // Magnet (자석 흡인)
        private bool _isMagnetized;
        private float _magnetSpeed;
        private float _targetScale;

        private void Awake()
        {
            _collider = GetComponent<CircleCollider2D>();
        }

        public void Initialize(GameObject prefab)
        {
            _originPrefab = prefab;
        }

        public void OnSpawn()
        {
            if (_collider != null)
                _collider.enabled = true;

            _spawnPosition = transform.position;
            _wobbleOffset = Random.Range(0f, Mathf.PI * 2f);
            _isMagnetized = false;
            _magnetSpeed = 0f;

            // 프리팹 스케일 기준으로 팝 애니메이션
            if (_targetScale <= 0f)
                _targetScale = transform.localScale.x;
            float startScale = _targetScale * 0.3f;
            transform.localScale = new Vector3(startScale, startScale, 1f);
        }

        public void OnDespawn()
        {
            if (_collider != null)
                _collider.enabled = false;

            _isMagnetized = false;
        }

        private void Update()
        {
            if (_isMagnetized)
            {
                UpdateMagnet();
            }
            else
            {
                UpdateWobble();
                CheckMagnetRange();
            }

            // 스폰 시 스케일 복귀 (팝 애니메이션)
            if (transform.localScale.x < _targetScale - 0.01f)
            {
                float s = Mathf.MoveTowards(transform.localScale.x, _targetScale, Time.deltaTime * _targetScale * 4f);
                transform.localScale = new Vector3(s, s, 1f);
            }
        }

        /// <summary>
        /// 타원 궤도를 그리며 부유한다.
        /// 보석마다 _wobbleOffset이 달라 동시에 움직이지 않는다.
        /// </summary>
        private void UpdateWobble()
        {
            float t = (Time.time + _wobbleOffset) * WOBBLE_SPEED;
            float xOffset = Mathf.Cos(t) * WOBBLE_AMPLITUDE * 0.6f;
            float yOffset = Mathf.Sin(t) * WOBBLE_AMPLITUDE;
            transform.position = _spawnPosition + new Vector3(xOffset, yOffset, 0f);
        }

        /// <summary>
        /// 플레이어가 자석 반경 안에 들어왔는지 확인한다.
        /// FindObjectOfType 대신 캐싱하여 매 프레임 호출을 방지한다.
        /// </summary>
        private void CheckMagnetRange()
        {
            if (_playerTransform == null)
            {
                var player = FindObjectOfType<Player.PlayerController>();
                if (player != null) _playerTransform = player.transform;
                else return;
            }

            float magnetRadius = GameManager.Instance != null
                ? GameManager.Instance.Settings.GemMagnetRadius
                : 3f;

            float sqrDist = (_playerTransform.position - transform.position).sqrMagnitude;
            if (sqrDist < magnetRadius * magnetRadius)
            {
                _isMagnetized = true;
                _magnetSpeed = 2f;
            }
        }

        /// <summary>
        /// 플레이어를 향해 가속하며 빨려들어간다.
        /// </summary>
        private void UpdateMagnet()
        {
            if (_playerTransform == null) return;

            _magnetSpeed += Time.deltaTime * 20f; // 점점 빨라짐
            Vector3 dir = (_playerTransform.position - transform.position).normalized;
            transform.position += dir * (_magnetSpeed * Time.deltaTime);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (other.GetComponent<Player.PlayerController>() != null)
                Collect();
        }

        private void Collect()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.AddScore(GameManager.Instance.Settings.GemScore);

            if (_spawner == null)
                _spawner = FindObjectOfType<GemSpawner>();
            if (_spawner != null)
                _spawner.NotifyGemCollected();

            if (ObjectPoolManager.Instance != null && _originPrefab != null)
                ObjectPoolManager.Instance.Despawn(_originPrefab, gameObject);
            else
                Destroy(gameObject);
        }
    }
}
