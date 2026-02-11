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
        private SpriteRenderer _sr;
        private GameObject _originPrefab;
        private GemSpawner _spawner;
        private Transform _playerTransform;

        // Wobble (유휴 흔들림)
        private Vector3 _spawnPosition;
        private float _wobbleOffset;
        private const float WOBBLE_AMPLITUDE = 0.08f;
        private const float WOBBLE_SPEED = 3f;

        // Glow (발광 펴싱)
        private Color _baseColor;
        private float _glowOffset;
        private const float GLOW_SPEED = 5f;
        private const float GLOW_INTENSITY = 2.5f; // HDR 배율 (원래 색상 대비)

        // Magnet (자석 흡인)
        private bool _isMagnetized;
        private float _magnetSpeed;
        private float _targetScale;

        // 랜덤 젼 색상 팔레트 (Slither.io 스타일)
        private static readonly Color[] GEM_COLORS = new Color[]
        {
            new Color(1f, 0.3f, 0.3f),   // 빨강
            new Color(0.3f, 1f, 0.3f),   // 초록
            new Color(0.3f, 0.6f, 1f),   // 파랑
            new Color(1f, 1f, 0.2f),     // 노랑
            new Color(1f, 0.5f, 0f),     // 주황
            new Color(0.8f, 0.3f, 1f),   // 보라
            new Color(0f, 1f, 0.9f),     // 청록
            new Color(1f, 0.4f, 0.7f),   // 핑크
        };

        private void Awake()
        {
            _collider = GetComponent<CircleCollider2D>();
            _sr = GetComponent<SpriteRenderer>();
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
            _glowOffset = Random.Range(0f, Mathf.PI * 2f);
            _isMagnetized = false;
            _magnetSpeed = 0f;

            // 랜덤 색상 적용
            _baseColor = GEM_COLORS[Random.Range(0, GEM_COLORS.Length)];
            if (_sr != null)
                _sr.color = _baseColor;

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

            // 발광 펴싱 (Slither.io 스타일 반짝임)
            UpdateGlow();

            // 스폰 시 스케일 복귀 (팝 애니메이션)
            if (transform.localScale.x < _targetScale - 0.01f)
            {
                float s = Mathf.MoveTowards(transform.localScale.x, _targetScale, Time.deltaTime * _targetScale * 4f);
                transform.localScale = new Vector3(s, s, 1f);
            }
        }

        /// <summary>
        /// 색상 밝기를 주기적으로 변화시켜 발광하는 느낌.
        /// </summary>
        private void UpdateGlow()
        {
            if (_sr == null) return;

            float pulse = Mathf.Sin((Time.time + _glowOffset) * GLOW_SPEED);
            // 0.0 ~ 1.0 로 정규화
            float t = (pulse + 1f) * 0.5f;
            // 기본 색상 ~ HDR 밝은 색상 사이 보간
            _sr.color = Color.Lerp(_baseColor, _baseColor * GLOW_INTENSITY, t);
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
