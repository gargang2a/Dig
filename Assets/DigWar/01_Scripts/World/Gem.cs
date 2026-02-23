using UnityEngine;
using Mirror;
using Core;
using System.Collections.Generic;

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
        private static readonly HashSet<Gem> _activeGems = new HashSet<Gem>();
        public static IReadOnlyCollection<Gem> ActiveGems => _activeGems;

        private CircleCollider2D _collider;
        private SpriteRenderer _sr;
        private MaterialPropertyBlock _mpb;
        private GameObject _originPrefab;
        private GemSpawner _spawner;
        private Transform _playerTransform;
        private bool _isNetworkManaged;
        private float _nextPlayerLookupAt;

        // Wobble (유휴 흔들림)
        private Vector3 _spawnPosition;
        private float _wobbleOffset;
        private const float WOBBLE_AMPLITUDE = 0.08f;
        private const float WOBBLE_SPEED = 3f;

        // Glow (발광 펴싱)
        private Color _baseColor;
        private float _glowOffset;
        private const float GLOW_SPEED = 5f;
        private const float GLOW_INTENSITY = 4f; // HDR 배율 — URP Bloom threshold(0.8) 초과해야 빛남

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
    private static readonly int ColorId = Shader.PropertyToID("_Color");

        private void Awake()
        {
            _collider = GetComponent<CircleCollider2D>();
            _sr = GetComponent<SpriteRenderer>();
            _mpb = new MaterialPropertyBlock();
        }

        private void OnEnable()
        {
            _activeGems.Add(this);
        }

        private void OnDisable()
        {
            _activeGems.Remove(this);
        }

        public void Initialize(GameObject prefab)
        {
            _originPrefab = prefab;
        }

        public void ConfigureNetworkMode(bool isNetworkManaged, GemSpawner spawner)
        {
            _isNetworkManaged = isNetworkManaged;
            _spawner = spawner;

            if (_isNetworkManaged)
                InitializeVisualState(useDeterministicColor: true);
        }

        public void OnSpawn()
        {
            if (_collider != null)
                _collider.enabled = true;
            InitializeVisualState(useDeterministicColor: false);

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

        private void InitializeVisualState(bool useDeterministicColor)
        {
            _spawnPosition = transform.position;
            _wobbleOffset = Random.Range(0f, Mathf.PI * 2f);
            _glowOffset = Random.Range(0f, Mathf.PI * 2f);
            _isMagnetized = false;
            _magnetSpeed = 0f;

            if (useDeterministicColor)
            {
                int index = GetDeterministicColorIndex(transform.position);
                _baseColor = GEM_COLORS[index];
            }
            else
            {
                _baseColor = GEM_COLORS[Random.Range(0, GEM_COLORS.Length)];
            }

            ApplyHDRColor(_baseColor);
        }

        private static int GetDeterministicColorIndex(Vector3 position)
        {
            int x = Mathf.RoundToInt(position.x * 100f);
            int y = Mathf.RoundToInt(position.y * 100f);
            int hash = (x * 73856093) ^ (y * 19349663);
            if (hash < 0) hash = -hash;
            return hash % GEM_COLORS.Length;
        }

        private void Update()
        {
            if (_isNetworkManaged)
            {
                // 네트워크 젬은 서버 판정 기반으로만 수집 처리한다.
                // 클라이언트는 위치를 변경하지 않고 발광만 표시한다.
                UpdateGlow();
                return;
            }

            if (_isMagnetized)
            {
                UpdateMagnet();
            }
            else
            {
                UpdateWobble();
                CheckMagnetRange();
            }

            // 발광 펄싱 (Slither.io 스타일 반짝임)
            UpdateGlow();

            // 스폰 시 스케일 복귀 (팝 애니메이션)
            if (transform.localScale.x < _targetScale - 0.01f)
            {
                float s = Mathf.MoveTowards(transform.localScale.x, _targetScale, Time.deltaTime * _targetScale * 4f);
                transform.localScale = new Vector3(s, s, 1f);
            }
        }

        /// <summary>
        /// MaterialPropertyBlock으로 HDR 색상을 적용하여 Bloom이 반응하게 한다.
        /// SpriteRenderer.color는 0~1 클램핑이라 HDR 불가.
        /// </summary>
        private void UpdateGlow()
        {
            if (_sr == null) return;

            float pulse = Mathf.Sin((Time.time + _glowOffset) * GLOW_SPEED);
            float t = (pulse + 1f) * 0.5f;
            // HDR 색상: 기본 색상 × 발광 강도 (1.0 초과 → Bloom 반응)
            Color hdrColor = _baseColor * Mathf.Lerp(1f, GLOW_INTENSITY, t);
            hdrColor.a = 1f;
            ApplyHDRColor(hdrColor);
        }

        /// <summary>
        /// MaterialPropertyBlock을 통해 HDR 색상을 SpriteRenderer에 적용.
        /// </summary>
        private void ApplyHDRColor(Color hdrColor)
        {
            if (_sr == null) return;
            _sr.GetPropertyBlock(_mpb);
            _mpb.SetColor(ColorId, hdrColor);
            _sr.SetPropertyBlock(_mpb);
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
                Player.PlayerController localController = Player.PlayerController.LocalController;
                if (localController != null)
                {
                    _playerTransform = localController.transform;
                }
                else
                {
                    if (Time.unscaledTime < _nextPlayerLookupAt) return;
                    _nextPlayerLookupAt = Time.unscaledTime + 1f;

                    var player = FindObjectOfType<Player.PlayerController>();
                    if (player != null) _playerTransform = player.transform;
                    else return;
                }
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
            float score = GameManager.Instance != null
                ? GameManager.Instance.Settings.GemScore
                : 10f;

            if (_isNetworkManaged)
            {
                // 네트워크 모드: 서버에서만 수집 판정 및 점수 확정
                if (!NetworkServer.active) return;

                var netPlayer = other.GetComponent<Network.NetworkPlayer>();
                if (netPlayer != null)
                {
                    netPlayer.ServerAddScore(score);
                    Collect(isPlayer: true);
                    return;
                }
            }

            // 로컬 모드(싱글) 또는 네트워크 서버의 AI 수집 처리
            var digger = other.GetComponent<Player.IDigger>();
            if (digger == null) return;

            digger.AddScore(score);

            bool isPlayerCollector = other.GetComponent<Player.PlayerController>() != null;
            Collect(isPlayerCollector);
        }

        private void Collect(bool isPlayer = true)
        {
            // 플레이어일 경우 사운드 재생 (점수는 IDigger.AddScore에서 처리됨)
            if (isPlayer && !_isNetworkManaged)
            {
                if (Systems.SoundManager.Instance != null)
                    Systems.SoundManager.Instance.PlayGemCollect();
            }

            if (_spawner == null)
                _spawner = GemSpawner.Instance != null ? GemSpawner.Instance : FindObjectOfType<GemSpawner>();
            if (_spawner != null)
                _spawner.NotifyGemCollected();

            if (_isNetworkManaged)
            {
                if (NetworkServer.active)
                    NetworkServer.Destroy(gameObject);
                return;
            }

            if (ObjectPoolManager.Instance != null && _originPrefab != null)
                ObjectPoolManager.Instance.Despawn(_originPrefab, gameObject);
            else
                Destroy(gameObject);
        }
    }
}
