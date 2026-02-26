using UnityEngine;
using Mirror;
using Core;
using System.Collections.Generic;

namespace World
{
    /// <summary>
    /// 맵에 ?�성?�는 보석.
    /// ?�휴 ?�태?�서 ?�들리고, ?�레?�어 ?�근 ??빨려?�어간다.
    /// 충돌 ???�수�?부?�하�??��?반환?�다.
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
        private bool _predictedCollectVisualApplied;
        private Coroutine _predictedCollectRestoreRoutine;
        private Coroutine _pendingCollectRequestRoutine;
        private Network.NetworkPlayer _localCollector;
        private Collider2D _localCollectorCollider;
        private float _nextLocalCollectorLookupAt;
        private NetworkIdentity _networkIdentity;
        private bool _serverCollected;
        private const float PREDICTED_COLLECT_RESTORE_SECONDS = 0.35f;
        private const float LOCAL_COLLECTOR_LOOKUP_INTERVAL = 0.25f;
        private const float PREDICTED_COLLECT_MARGIN = 0.12f;
        private const float FALLBACK_COLLIDER_RADIUS = 0.35f;

        // Wobble (?�휴 ?�들�?
        private Vector3 _spawnPosition;
        private float _wobbleOffset;
        private const float WOBBLE_AMPLITUDE = 0.08f;
        private const float WOBBLE_SPEED = 3f;

        // Glow (발광 ?�싱)
        private Color _baseColor;
        private float _glowOffset;
        private const float GLOW_SPEED = 5f;
        private const float GLOW_INTENSITY = 4f; // HDR 배율 ??URP Bloom threshold(0.8) 초과?�야 빛남

        // Magnet (?�석 ?�인)
        private bool _isMagnetized;
        private float _magnetSpeed;
        private float _targetScale;

        // ?�덤 ???�상 ?�레??(Slither.io ?��???
        private static readonly Color[] GEM_COLORS = new Color[]
        {
            new Color(1f, 0.3f, 0.3f),   // 빨강
            new Color(0.3f, 1f, 0.3f),   // 초록
            new Color(0.3f, 0.6f, 1f),   // ?�랑
            new Color(1f, 1f, 0.2f),     // ?�랑
            new Color(1f, 0.5f, 0f),     // 주황
            new Color(0.8f, 0.3f, 1f),   // 보라
            new Color(0f, 1f, 0.9f),     // �?��
            new Color(1f, 0.4f, 0.7f),   // ?�크
        };
    private static readonly int ColorId = Shader.PropertyToID("_Color");

        private void Awake()
        {
            _collider = GetComponent<CircleCollider2D>();
            _sr = GetComponent<SpriteRenderer>();
            _mpb = new MaterialPropertyBlock();
            _networkIdentity = GetComponent<NetworkIdentity>();
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
            ResetPredictedCollectVisual();
            _localCollector = null;
            _localCollectorCollider = null;
            _nextLocalCollectorLookupAt = 0f;
            _serverCollected = false;

            if (_collider != null)
                _collider.enabled = true;
            InitializeVisualState(useDeterministicColor: false);

            // ?�리???��???기�??�로 ???�니메이??            if (_targetScale <= 0f)
                _targetScale = transform.localScale.x;
            float startScale = _targetScale * 0.3f;
            transform.localScale = new Vector3(startScale, startScale, 1f);
        }

        public void OnDespawn()
        {
            ResetPredictedCollectVisual();
            _localCollector = null;
            _localCollectorCollider = null;
            _serverCollected = false;

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
                // ?�트?�크 ?��? ?�버 ?�정 기반?�로�??�집 처리?�다.
                // ?�라?�언?�는 ?�치�?변경하지 ?�고 발광�??�시?�다.
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

            // 발광 ?�싱 (Slither.io ?��???반짝??
            UpdateGlow();

            // ?�폰 ???��???복�? (???�니메이??
            if (transform.localScale.x < _targetScale - 0.01f)
            {
                float s = Mathf.MoveTowards(transform.localScale.x, _targetScale, Time.deltaTime * _targetScale * 4f);
                transform.localScale = new Vector3(s, s, 1f);
            }
        }

        /// <summary>
        /// MaterialPropertyBlock?�로 HDR ?�상???�용?�여 Bloom??반응?�게 ?�다.
        /// SpriteRenderer.color??0~1 ?�램?�이??HDR 불�?.
        /// </summary>
        private void UpdateGlow()
        {
            if (_sr == null) return;

            float pulse = Mathf.Sin((Time.time + _glowOffset) * GLOW_SPEED);
            float t = (pulse + 1f) * 0.5f;
            // HDR ?�상: 기본 ?�상 × 발광 강도 (1.0 초과 ??Bloom 반응)
            Color hdrColor = _baseColor * Mathf.Lerp(1f, GLOW_INTENSITY, t);
            hdrColor.a = 1f;
            ApplyHDRColor(hdrColor);
        }

        /// <summary>
        /// MaterialPropertyBlock???�해 HDR ?�상??SpriteRenderer???�용.
        /// </summary>
        private void ApplyHDRColor(Color hdrColor)
        {
            if (_sr == null) return;
            _sr.GetPropertyBlock(_mpb);
            _mpb.SetColor(ColorId, hdrColor);
            _sr.SetPropertyBlock(_mpb);
        }

        /// <summary>
        /// ?�??궤도�?그리�?부?�한??
        /// 보석마다 _wobbleOffset???�라 ?�시???�직이지 ?�는??
        /// </summary>
        private void UpdateWobble()
        {
            float t = (Time.time + _wobbleOffset) * WOBBLE_SPEED;
            float xOffset = Mathf.Cos(t) * WOBBLE_AMPLITUDE * 0.6f;
            float yOffset = Mathf.Sin(t) * WOBBLE_AMPLITUDE;
            transform.position = _spawnPosition + new Vector3(xOffset, yOffset, 0f);
        }

        /// <summary>
        /// ?�레?�어가 ?�석 반경 ?�에 ?�어?�는지 ?�인?�다.
        /// FindObjectOfType ?�??캐싱?�여 �??�레???�출??방�??�다.
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
        /// ?�레?�어�??�해 가?�하�?빨려?�어간다.
        /// </summary>
        private void UpdateMagnet()
        {
            if (_playerTransform == null) return;

            _magnetSpeed += Time.deltaTime * 20f; // ?�점 빨라�?
            Vector3 dir = (_playerTransform.position - transform.position).normalized;
            transform.position += dir * (_magnetSpeed * Time.deltaTime);
        }

        private void TryApplyClientPredictedCollect()
        {
            if (_predictedCollectVisualApplied) return;
            if (!TryResolveLocalCollector(out Network.NetworkPlayer localNetPlayer)) return;
            if (localNetPlayer == null || localNetPlayer.IsDead) return;

            float score = GameManager.Instance != null
                ? GameManager.Instance.Settings.GemScore
                : 10f;
            if (score <= 0f) return;

            float collectDistance = ResolvePredictedCollectDistance();
            float sqrDist = (localNetPlayer.transform.position - transform.position).sqrMagnitude;
            if (sqrDist > collectDistance * collectDistance)
                return;

            ApplyPredictedCollectVisual();
            localNetPlayer.ClientPredictGemCollect(score);
            if (Systems.SoundManager.Instance != null)
                Systems.SoundManager.Instance.PlayGemCollect(isPredicted: true);
        }

        private bool TryResolveLocalCollector(out Network.NetworkPlayer localNetPlayer)
        {
            localNetPlayer = _localCollector;
            if (localNetPlayer != null && localNetPlayer.isLocalPlayer)
            {
                if (_localCollectorCollider == null)
                    _localCollectorCollider = localNetPlayer.GetComponent<Collider2D>();
                return true;
            }

            if (Time.unscaledTime < _nextLocalCollectorLookupAt)
                return false;

            _nextLocalCollectorLookupAt = Time.unscaledTime + LOCAL_COLLECTOR_LOOKUP_INTERVAL;

            localNetPlayer = Network.NetworkPlayer.LocalPlayer;
            if (localNetPlayer == null)
                localNetPlayer = FindObjectOfType<Network.NetworkPlayer>();

            if (localNetPlayer == null || !localNetPlayer.isLocalPlayer)
            {
                _localCollector = null;
                _localCollectorCollider = null;
                return false;
            }

            _localCollector = localNetPlayer;
            _localCollectorCollider = localNetPlayer.GetComponent<Collider2D>();
            return true;
        }

        private float ResolvePredictedCollectDistance()
        {
            float gemRadius = ResolveColliderRadius(_collider);
            float collectorRadius = ResolveColliderRadius(_localCollectorCollider);
            return gemRadius + collectorRadius + PREDICTED_COLLECT_MARGIN;
        }

        private static float ResolveColliderRadius(Collider2D collider)
        {
            if (collider == null) return FALLBACK_COLLIDER_RADIUS;

            if (collider is CircleCollider2D circleCollider)
            {
                float scaleX = Mathf.Abs(circleCollider.transform.lossyScale.x);
                float scaleY = Mathf.Abs(circleCollider.transform.lossyScale.y);
                float scale = Mathf.Max(scaleX, scaleY);
                return circleCollider.radius * Mathf.Max(scale, 0.0001f);
            }

            Vector3 extents = collider.bounds.extents;
            float radius = Mathf.Max(extents.x, extents.y);
            return Mathf.Max(radius, FALLBACK_COLLIDER_RADIUS);
        }
        private void OnTriggerEnter2D(Collider2D other)
        {
            float score = GameManager.Instance != null
                ? GameManager.Instance.Settings.GemScore
                : 10f;

            if (_isNetworkManaged)
            {
                // ?�트?�크 모드: ?�버?�서�??�집 ?�정 �??�수 ?�정
                if (!NetworkServer.active)
                {
                    var localNetPlayer = ResolveCollectorNetworkPlayer(other);
                    if (localNetPlayer != null && localNetPlayer.isLocalPlayer && !localNetPlayer.IsDead)
                    {
                        TryRequestPredictedCollect(localNetPlayer, score);
                    }
                    return;
                }

                var netPlayer = ResolveCollectorNetworkPlayer(other);
                if (netPlayer != null && TryServerCollectFromPlayer(netPlayer, score))
                {
                    return;
                }
            }

            // 로컬 모드(?��?) ?�는 ?�트?�크 ?�버??AI ?�집 처리
            var digger = other.GetComponent<Player.IDigger>();
            if (digger == null) return;

            digger.AddScore(score);

            bool isPlayerCollector = other.GetComponent<Player.PlayerController>() != null;
            Collect(isPlayerCollector);
        }

        private void Collect(bool isPlayer = true)
        {
            // ?�레?�어??경우 ?�운???�생 (?�수??IDigger.AddScore?�서 처리??
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
                {
                    _serverCollected = true;
                    NetworkServer.Destroy(gameObject);
                }
                return;
            }

            if (ObjectPoolManager.Instance != null && _originPrefab != null)
                ObjectPoolManager.Instance.Despawn(_originPrefab, gameObject);
            else
                Destroy(gameObject);
        }

        private static Network.NetworkPlayer ResolveCollectorNetworkPlayer(Collider2D other)
        {
            if (other == null) return null;

            Network.NetworkPlayer netPlayer = other.GetComponent<Network.NetworkPlayer>();
            if (netPlayer != null) return netPlayer;

            if (other.attachedRigidbody != null)
            {
                netPlayer = other.attachedRigidbody.GetComponent<Network.NetworkPlayer>();
                if (netPlayer != null) return netPlayer;
            }

            return other.GetComponentInParent<Network.NetworkPlayer>();
        }

        private void TryRequestPredictedCollect(Network.NetworkPlayer localNetPlayer, float score)
        {
            if (_predictedCollectVisualApplied) return;
            if (localNetPlayer == null || !localNetPlayer.isLocalPlayer || localNetPlayer.IsDead) return;
            if (score <= 0f) return;

            ApplyPredictedCollectVisual();
            localNetPlayer.ClientPredictGemCollect(score);
            if (Systems.SoundManager.Instance != null)
                Systems.SoundManager.Instance.PlayGemCollect(isPredicted: true);

            RequestServerCollect(localNetPlayer);
        }

        [Client]
        private void RequestServerCollect(Network.NetworkPlayer localNetPlayer)
        {
            if (!_isNetworkManaged) return;
            if (localNetPlayer == null || !localNetPlayer.isLocalPlayer) return;
            if (!Network.NetworkPlayer.CanSendCommands) return;

            if (_networkIdentity == null)
                _networkIdentity = GetComponent<NetworkIdentity>();

            if (_networkIdentity == null)
                return;

            if (_networkIdentity.netId == 0u)
            {
                if (_pendingCollectRequestRoutine == null)
                    _pendingCollectRequestRoutine = StartCoroutine(RetryRequestServerCollect());
                return;
            }

            localNetPlayer.CmdRequestCollectGem(_networkIdentity, localNetPlayer.transform.position, transform.position);
        }

        [Client]
        private System.Collections.IEnumerator RetryRequestServerCollect()
        {
            const int maxRetryFrames = 20;
            for (int i = 0; i < maxRetryFrames; i++)
            {
                if (!_predictedCollectVisualApplied)
                    break;

                if (_networkIdentity == null)
                    _networkIdentity = GetComponent<NetworkIdentity>();

                if (_networkIdentity != null &&
                    _networkIdentity.netId != 0u &&
                    Network.NetworkPlayer.CanSendCommands &&
                    TryResolveLocalCollector(out Network.NetworkPlayer localNetPlayer) &&
                    localNetPlayer != null &&
                    localNetPlayer.isLocalPlayer &&
                    !localNetPlayer.IsDead)
                {
                    localNetPlayer.CmdRequestCollectGem(
                        _networkIdentity,
                        localNetPlayer.transform.position,
                        transform.position);
                    break;
                }

                yield return null;
            }

            _pendingCollectRequestRoutine = null;
        }

        [Server]
        public bool ServerTryCollectFromRequest(Network.NetworkPlayer collector, Vector2 collectorReportedPos, Vector2 gemReportedPos)
        {
            if (!_isNetworkManaged || !NetworkServer.active) return false;
            if (_serverCollected) return false;
            if (collector == null || collector.IsDead) return false;

            float score = GameManager.Instance != null
                ? GameManager.Instance.Settings.GemScore
                : 10f;

            // 서버 위치/보고 위치를 모두 허용치 검증해 지연 환경에서도 확정 누락을 줄인다.
            float strictDistance = Vector2.Distance((Vector2)collector.transform.position, (Vector2)transform.position);
            float reportedDistance = Vector2.Distance(collectorReportedPos, gemReportedPos);
            float allowedDistance = ResolveServerCollectRadius() + ResolveCollectorRadius(collector) + PREDICTED_COLLECT_MARGIN;
            float collectorDrift = Vector2.Distance((Vector2)collector.transform.position, collectorReportedPos);
            float gemDrift = Vector2.Distance((Vector2)transform.position, gemReportedPos);
            bool inStrictRange = strictDistance <= allowedDistance;
            bool inReportedRange = reportedDistance <= (allowedDistance + 0.35f) &&
                                   collectorDrift <= 2.5f &&
                                   gemDrift <= 2.5f;

            if (!inStrictRange && !inReportedRange)
                return false;

            return TryServerCollectFromPlayer(collector, score);
        }

        [Server]
        public float ResolveServerCollectRadius()
        {
            return ResolveColliderRadius(_collider);
        }

        [Server]
        private bool TryServerCollectFromPlayer(Network.NetworkPlayer collector, float score)
        {
            if (!_isNetworkManaged || !NetworkServer.active) return false;
            if (_serverCollected) return false;
            if (collector == null || collector.IsDead) return false;

            _serverCollected = true;
            collector.ServerAddScore(score);
            Collect(isPlayer: true);
            return true;
        }

        [Server]
        private static float ResolveCollectorRadius(Network.NetworkPlayer collector)
        {
            if (collector == null) return FALLBACK_COLLIDER_RADIUS;
            return ResolveColliderRadius(collector.GetComponent<Collider2D>());
        }

        private void ApplyPredictedCollectVisual()
        {
            if (_predictedCollectVisualApplied)
                return;

            _predictedCollectVisualApplied = true;

            if (_collider != null)
                _collider.enabled = false;
            if (_sr != null)
                _sr.enabled = false;

            if (_predictedCollectRestoreRoutine != null)
                StopCoroutine(_predictedCollectRestoreRoutine);
            _predictedCollectRestoreRoutine = StartCoroutine(RestorePredictedCollectVisualRoutine());
        }

        private System.Collections.IEnumerator RestorePredictedCollectVisualRoutine()
        {
            yield return new WaitForSecondsRealtime(PREDICTED_COLLECT_RESTORE_SECONDS);

            if (!_predictedCollectVisualApplied)
            {
                _predictedCollectRestoreRoutine = null;
                yield break;
            }

            // ?�버 ?�정 Destroy가 지?�된 케?�스?�서�??�복?�다.
            if (_collider != null)
                _collider.enabled = true;
            if (_sr != null)
                _sr.enabled = true;

            _predictedCollectVisualApplied = false;
            _predictedCollectRestoreRoutine = null;
        }

        private void ResetPredictedCollectVisual()
        {
            if (_pendingCollectRequestRoutine != null)
            {
                StopCoroutine(_pendingCollectRequestRoutine);
                _pendingCollectRequestRoutine = null;
            }

            if (_predictedCollectRestoreRoutine != null)
            {
                StopCoroutine(_predictedCollectRestoreRoutine);
                _predictedCollectRestoreRoutine = null;
            }

            _predictedCollectVisualApplied = false;
            if (_sr != null)
                _sr.enabled = true;
            if (_collider != null)
                _collider.enabled = true;
        }
    }
}
