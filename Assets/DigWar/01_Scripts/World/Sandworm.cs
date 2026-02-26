using UnityEngine;
using System.Collections.Generic;
using Core;
using Mirror;

namespace World
{
    /// <summary>
    /// 맵을 순회하는 거대 샌드웜.
    /// 머리+세그먼트 구조로 이동하며 지렁이처럼 이어진 몸통을 만든다.
    /// 이동 경로에서 터널 마스크를 지우고 맵을 순회한다.
    /// 플레이어/AI와 충돌하면 즉시 처치하며, 이동 경로에 젬을 드롭한다.
    /// </summary>
    public class Sandworm : MonoBehaviour
    {
        private static readonly HashSet<Sandworm> _activeWorms = new HashSet<Sandworm>();
        public static IReadOnlyCollection<Sandworm> ActiveWorms => _activeWorms;

        [Header("Movement")]
        [Tooltip("이동 속도 (월드 유닛/초)")]
        [SerializeField] private float _speed = 4f;
        [Tooltip("회전 속도 (도/초)")]
        [SerializeField] private float _turnSpeed = 40f;
        [Tooltip("방향 전환 주기 (초)")]
        [SerializeField] private float _dirChangeInterval = 3f;

        [Header("Erasing (파기)")]
#if UNITY_EDITOR
        [Tooltip("홀을 지우는 브러시 반경 (월드 유닛)")]
        [SerializeField] private float _eraseRadius = 3f;
#endif
        [Tooltip("EraseHole 호출 최소 이동 거리")]
        [SerializeField] private float _eraseStepDistance = 0.3f;

        [Header("Body Segments")]
        [Tooltip("몸통 세그먼트 개수")]
        [SerializeField] private int _segmentCount = 8;
        [Tooltip("세그먼트 간격 (월드 유닛)")]
        [SerializeField] private float _segmentSpacing = 0.8f;
        [Tooltip("머리 크기")]
        [SerializeField] private float _headScale = 2.5f;
        [Tooltip("꼬리 끝 크기 비율 (머리 대비)")]
        [SerializeField] private float _tailScaleRatio = 0.5f;
        [Tooltip("Head sprite")]
        [SerializeField] private Sprite _headSprite;
        [Tooltip("Body segment sprite")]
        [SerializeField] private Sprite _bodySprite;
        [Tooltip("머리 색상")]
        [SerializeField] private Color _headColor = new Color(0.6f, 0.3f, 0.15f, 1f);
        [Tooltip("몸통 색상")]
        [SerializeField] private Color _bodyColor = new Color(0.5f, 0.25f, 0.12f, 1f);

        [Header("Gem Spawning")]
        [Tooltip("젬 배출 간격 (이동 거리 기준)")]
        [SerializeField] private float _gemDropDistance = 2f;
        [Tooltip("Gem prefab to drop")]
        [SerializeField] private GameObject _gemPrefab;

        // 내부 상태
        private float _targetAngle;
        private float _dirChangeTimer;
        private float _gemDropAccum;
        private float _mapRadius;

        // 세그먼트 리스트(위치 히스토리 기반)
        private readonly List<Vector3> _positionHistory = new List<Vector3>(256);
        private readonly List<Transform> _segments = new List<Transform>();
        private readonly List<Network.NetworkPlayer> _hazardPlayerSnapshot = new List<Network.NetworkPlayer>(32);
        private readonly List<Network.NetworkBot> _hazardBotSnapshot = new List<Network.NetworkBot>(64);
        private float _distanceMoved;

        /// <summary>미니맵 렌더러에서 세그먼트 위치를 읽기 위한 프로퍼티.</summary>
        public IReadOnlyList<Transform> Segments => _segments;

        private const float WALL_AVOID_DISTANCE = 8f;
        private const float HISTORY_STEP = 0.15f; // 히스토리 기록 최소 거리
        private const int SORTING_ORDER_HEAD = 20;
        private const float HAZARD_KILL_RADIUS_SCALE = 0.45f;
        private const float HAZARD_CHECK_INTERVAL = 0.05f;
        private const float CLIENT_HISTORY_RESYNC_DELTA_SECONDS = 0.2f;
        private const float CLIENT_HISTORY_RESYNC_COOLDOWN_SECONDS = 0.6f;
        private const float SEGMENT_DRIFT_RESYNC_MULTIPLIER = 2.6f;
        private const float SEGMENT_SNAP_MULTIPLIER = 2.2f;

        private static bool IsNetworkMode => NetworkClient.active || NetworkServer.active;
        private float _hazardCheckTimer;
        private float _clientHistoryResyncCooldownUntil;

        private void Start()
        {

            if (GameManager.Instance != null && GameManager.Instance.Settings != null)
                _mapRadius = GameManager.Instance.Settings.MapRadius;
            else
                _mapRadius = 65f;

            _targetAngle = Random.Range(0f, 360f);
            _dirChangeTimer = _dirChangeInterval;

            CreateSegments();
            InitializeHistory();
        }

        private void OnEnable()
        {
            _activeWorms.Add(this);
        }

        private void OnDisable()
        {
            _activeWorms.Remove(this);
        }

        /// <summary>
        /// 머리 + N개의 몸통 세그먼트 SpriteRenderer를 생성한다.
        /// </summary>
        private void CreateSegments()
        {
            // 머리 생성(자기 자신의 자식)
            var headObj = new GameObject("Head");
            headObj.transform.SetParent(transform);
            headObj.transform.localPosition = Vector3.zero;
            headObj.transform.localScale = Vector3.one * _headScale;
            var headSR = headObj.AddComponent<SpriteRenderer>();
            headSR.sprite = _headSprite;
            headSR.color = _headColor;
            headSR.sortingOrder = SORTING_ORDER_HEAD;

            // Collider는 머리에만 사용
            var col = gameObject.GetComponent<CircleCollider2D>();
            if (col == null) col = gameObject.AddComponent<CircleCollider2D>();
            col.isTrigger = true;
            col.radius = _headScale * 0.4f;

            var rb = gameObject.GetComponent<Rigidbody2D>();
            if (rb == null) rb = gameObject.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;

            // 몸통 세그먼트 생성(초기 배치 전에는 비활성화)
            for (int i = 0; i < _segmentCount; i++)
            {
                var seg = new GameObject($"Segment_{i}");
                seg.SetActive(false); // 위치 배치 전까지 비활성화
                seg.transform.SetParent(transform.parent ?? transform);
                seg.transform.position = transform.position; // 머리 위치로 초기화

                float t = (float)(i + 1) / _segmentCount;
                float scale = Mathf.Lerp(_headScale, _headScale * _tailScaleRatio, t);
                seg.transform.localScale = Vector3.one * scale;

                var sr = seg.AddComponent<SpriteRenderer>();
                sr.sprite = _bodySprite ?? _headSprite;
                sr.color = (i % 2 == 0) ? _bodyColor : _bodyColor * 1.15f;
                sr.sortingOrder = SORTING_ORDER_HEAD - (i + 1);

                _segments.Add(seg.transform);
            }
        }

        /// <summary>
        /// 초기 위치 히스토리를 머리 뒤쪽으로 채우고 세그먼트를 활성화한다.
        /// </summary>
        private void InitializeHistory()
        {
            RebuildHistory(snapSegments: true);
            for (int i = 0; i < _segments.Count; i++)
                _segments[i].gameObject.SetActive(true);
        }

        private void Update()
        {
            if (IsNetworkMode && !NetworkServer.active)
            {
                // 클라이언트에서는 서버 동기화된 월드 위치만 따라간다.
                if (ShouldForceClientHistoryResync() || ShouldForceResyncForSegmentDrift())
                    RebuildHistory(snapSegments: true);

                RecordHistory();
                UpdateSegments();
                TryErase(allowGemDrop: false);
                return;
            }

            UpdateAI();
            Rotate();
            Move();
            RecordHistory();
            UpdateSegments();
            TryErase(allowGemDrop: true);
            ServerTickHazardKills();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus) return;
            if (!IsNetworkMode || NetworkServer.active) return;

            // WebGL/클라이언트가 포커스를 되찾는 시점에 히스토리를 즉시 정렬해
            // 세그먼트 분리 잔상을 줄인다.
            RebuildHistory(snapSegments: true);
            _clientHistoryResyncCooldownUntil = Time.unscaledTime + CLIENT_HISTORY_RESYNC_COOLDOWN_SECONDS;
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus) return;
            if (!IsNetworkMode || NetworkServer.active) return;

            RebuildHistory(snapSegments: true);
            _clientHistoryResyncCooldownUntil = Time.unscaledTime + CLIENT_HISTORY_RESYNC_COOLDOWN_SECONDS;
        }

        // ===== AI (방향 전환 + 벽 회피) =====
        private void UpdateAI()
        {
            float distFromCenter = transform.position.magnitude;
            if (distFromCenter > _mapRadius - WALL_AVOID_DISTANCE)
            {
                Vector2 toCenter = -((Vector2)transform.position).normalized;
                _targetAngle = Mathf.Atan2(toCenter.y, toCenter.x) * Mathf.Rad2Deg - 90f;
                return;
            }

            _dirChangeTimer -= Time.deltaTime;
            if (_dirChangeTimer <= 0f)
            {
                _dirChangeTimer = _dirChangeInterval + Random.Range(-1f, 1f);
                _targetAngle += Random.Range(-45f, 45f);
            }
        }

        private void Rotate()
        {
            Quaternion target = Quaternion.Euler(0f, 0f, _targetAngle);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, target, _turnSpeed * Time.deltaTime);
        }

        private void Move()
        {
            transform.position += transform.up * (_speed * Time.deltaTime);
        }

        // ===== 세그먼트 몸통 시스템 =====

        /// <summary>
        /// 머리가 일정 거리 이동할 때마다 위치를 히스토리에 기록한다.
        /// </summary>
        private void RecordHistory()
        {
            if (_positionHistory.Count == 0)
            {
                RebuildHistory(snapSegments: true);
                return;
            }

            Vector3 delta = transform.position - _positionHistory[0];
            float teleportThreshold = ResolveHistoryTeleportThreshold();
            if (delta.sqrMagnitude >= teleportThreshold * teleportThreshold)
            {
                // WebGL 탭 전환 복귀 직후처럼 머리 위치가 크게 점프하면
                // 기존 히스토리를 버리고 즉시 재정렬해 세그먼트 분리를 방지한다.
                RebuildHistory(snapSegments: true);
                return;
            }

            if (delta.sqrMagnitude >= HISTORY_STEP * HISTORY_STEP)
            {
                _positionHistory.Insert(0, transform.position);

                // 메모리 제한 (최대 500개)
                if (_positionHistory.Count > 500)
                    _positionHistory.RemoveAt(_positionHistory.Count - 1);
            }
        }

        private bool ShouldForceClientHistoryResync()
        {
            if (Time.unscaledTime < _clientHistoryResyncCooldownUntil)
                return false;

            if (Time.unscaledDeltaTime < CLIENT_HISTORY_RESYNC_DELTA_SECONDS)
                return false;

            _clientHistoryResyncCooldownUntil = Time.unscaledTime + CLIENT_HISTORY_RESYNC_COOLDOWN_SECONDS;
            return true;
        }

        /// <summary>
        /// 각 세그먼트를 히스토리 곡선 위치에 부드럽게 배치한다.
        /// </summary>
        private void UpdateSegments()
        {
            const float smoothSpeed = 15f; // 보간 속도
            float blend = Mathf.Clamp01(Time.deltaTime * smoothSpeed);
            if (IsNetworkMode && !NetworkServer.active && Time.unscaledDeltaTime >= CLIENT_HISTORY_RESYNC_DELTA_SECONDS)
                blend = 1f;

            float snapDistance = ResolveSegmentSnapDistance();
            float snapDistanceSqr = snapDistance * snapDistance;

            for (int i = 0; i < _segments.Count; i++)
            {
                float floatIndex = (i + 1) * _segmentSpacing / HISTORY_STEP;
                Vector3 targetPos = ResolveHistoryPoint(floatIndex);

                // ALT+TAB 복귀 직후처럼 오차가 크게 벌어졌으면 즉시 스냅해 분리를 줄인다.
                if ((_segments[i].position - targetPos).sqrMagnitude >= snapDistanceSqr)
                    _segments[i].position = targetPos;
                else
                    _segments[i].position = Vector3.Lerp(_segments[i].position, targetPos, blend);

                // 이전 세그먼트(또는 머리) 방향으로 회전
                Vector3 lookTarget = (i == 0) ? transform.position : _segments[i - 1].position;
                Vector3 lookDir = lookTarget - _segments[i].position;
                if (lookDir.sqrMagnitude > 0.001f)
                {
                    float angle = Mathf.Atan2(lookDir.y, lookDir.x) * Mathf.Rad2Deg - 90f;
                    Quaternion targetRot = Quaternion.Euler(0f, 0f, angle);
                    _segments[i].rotation = Quaternion.Lerp(
                        _segments[i].rotation, targetRot, blend);
                }
            }
        }

        private bool ShouldForceResyncForSegmentDrift()
        {
            if (_segments.Count == 0 || _positionHistory.Count == 0)
                return false;

            float driftDistance = ResolveSegmentDriftResyncDistance();
            float driftDistanceSqr = driftDistance * driftDistance;

            // 머리-첫 세그먼트 링크가 깨지면 즉시 재정렬.
            if ((_segments[0].position - transform.position).sqrMagnitude >= driftDistanceSqr)
                return true;

            for (int i = 1; i < _segments.Count; i++)
            {
                if ((_segments[i].position - _segments[i - 1].position).sqrMagnitude >= driftDistanceSqr)
                    return true;
            }

            // 꼬리도 히스토리 곡선 근처에 있어야 한다.
            float tailFloatIndex = _segments.Count * _segmentSpacing / HISTORY_STEP;
            Vector3 tailExpected = ResolveHistoryPoint(tailFloatIndex);
            float tailDistance = Mathf.Max(driftDistance, _segmentSpacing * 3f);
            return (_segments[_segments.Count - 1].position - tailExpected).sqrMagnitude >= tailDistance * tailDistance;
        }

        private float ResolveSegmentDriftResyncDistance()
        {
            float bySpacing = Mathf.Max(_segmentSpacing, 0.1f) * SEGMENT_DRIFT_RESYNC_MULTIPLIER;
            float byHead = Mathf.Max(_headScale, 0.1f) * 0.9f;
            return Mathf.Max(bySpacing, byHead);
        }

        private float ResolveSegmentSnapDistance()
        {
            float bySpacing = Mathf.Max(_segmentSpacing, 0.1f) * SEGMENT_SNAP_MULTIPLIER;
            float byHead = Mathf.Max(_headScale, 0.1f) * 0.8f;
            return Mathf.Max(bySpacing, byHead);
        }

        private void RebuildHistory(bool snapSegments)
        {
            _positionHistory.Clear();

            Vector3 backDir = -transform.up;
            int totalNeeded = ResolveHistorySampleCount();
            for (int i = 0; i < totalNeeded; i++)
                _positionHistory.Add(transform.position + backDir * (i * HISTORY_STEP));

            if (snapSegments)
                SnapSegmentsToHistory();
        }

        private int ResolveHistorySampleCount()
        {
            int baseCount = _segmentCount * Mathf.CeilToInt(_segmentSpacing / HISTORY_STEP) + 10;
            return Mathf.Max(64, baseCount);
        }

        private float ResolveHistoryTeleportThreshold()
        {
            float bodyLength = Mathf.Max(_segmentSpacing, _segmentCount * _segmentSpacing);
            float byBody = bodyLength * 0.75f;
            float byHead = _headScale * 2f;
            return Mathf.Max(byBody, byHead);
        }

        private Vector3 ResolveHistoryPoint(float floatIndex)
        {
            if (_positionHistory.Count == 0)
                return transform.position;

            int indexA = Mathf.FloorToInt(floatIndex);
            int indexB = indexA + 1;
            indexA = Mathf.Clamp(indexA, 0, _positionHistory.Count - 1);
            indexB = Mathf.Clamp(indexB, 0, _positionHistory.Count - 1);

            float frac = floatIndex - Mathf.Floor(floatIndex);
            return Vector3.Lerp(_positionHistory[indexA], _positionHistory[indexB], frac);
        }

        private void SnapSegmentsToHistory()
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                float floatIndex = (i + 1) * _segmentSpacing / HISTORY_STEP;
                _segments[i].position = ResolveHistoryPoint(floatIndex);

                Vector3 lookTarget = (i == 0) ? transform.position : _segments[i - 1].position;
                Vector3 lookDir = lookTarget - _segments[i].position;
                if (lookDir.sqrMagnitude > 0.001f)
                {
                    float angle = Mathf.Atan2(lookDir.y, lookDir.x) * Mathf.Rad2Deg - 90f;
                    _segments[i].rotation = Quaternion.Euler(0f, 0f, angle);
                }
            }
        }

        // ===== 지형 파기 + 젬 배출 =====

        /// <summary>
        /// 일정 이동 거리마다 홀 지우기 요청.
        /// </summary>
        private void TryErase(bool allowGemDrop)
        {
            float frameDist = _speed * Time.deltaTime;
            _distanceMoved += frameDist;

            if (_distanceMoved < _eraseStepDistance) return;
            _distanceMoved = 0f;

            var maskMgr = Tunnel.TunnelMaskManager.Instance;
            if (maskMgr == null) return;

            // 머리 위치: 머리 크기에 맞는 반경으로 파기
            maskMgr.EraseHole(transform.position, _headScale * 0.5f);

            // 각 세그먼트 위치: 세그먼트 크기에 맞는 반경으로 파기
            for (int i = 0; i < _segments.Count; i++)
            {
                float t = (float)(i + 1) / _segmentCount;
                float segScale = Mathf.Lerp(_headScale, _headScale * _tailScaleRatio, t);
                maskMgr.EraseHole(_segments[i].position, segScale * 0.5f);
            }

            // 젬 배출
            _gemDropAccum += _eraseStepDistance;
            if (allowGemDrop && _gemDropAccum >= _gemDropDistance)
            {
                _gemDropAccum -= _gemDropDistance;
                SpawnGem();
            }
        }

        private void SpawnGem()
        {
            if (_gemPrefab == null) return;

            // 꼬리 끝 위치에서 젬 배출
            Vector3 spawnPos;
            if (_segments.Count > 0)
                spawnPos = _segments[_segments.Count - 1].position;
            else
                spawnPos = transform.position - transform.up * (_headScale);

            spawnPos += (Vector3)(Random.insideUnitCircle * 0.5f);

            if (IsNetworkMode)
            {
                if (!NetworkServer.active) return;

                if (World.GemSpawner.Instance != null)
                    World.GemSpawner.Instance.DropGemAt(spawnPos);
                else
                    Debug.LogWarning("[Sandworm] GemSpawner missing in network mode. Gem drop skipped.");
                return;
            }

            if (Core.ObjectPoolManager.Instance != null)
                Core.ObjectPoolManager.Instance.Spawn(_gemPrefab, spawnPos, Quaternion.identity);
            else
                Instantiate(_gemPrefab, spawnPos, Quaternion.identity);
        }

        private void ServerTickHazardKills()
        {
            if (!IsNetworkMode || !NetworkServer.active)
                return;

            _hazardCheckTimer -= Time.deltaTime;
            if (_hazardCheckTimer > 0f)
                return;

            _hazardCheckTimer = HAZARD_CHECK_INTERVAL;

            EvaluateHazardKillsAt(transform.position, _headScale * HAZARD_KILL_RADIUS_SCALE);

            for (int i = 0; i < _segments.Count; i++)
            {
                float t = (float)(i + 1) / _segmentCount;
                float segScale = Mathf.Lerp(_headScale, _headScale * _tailScaleRatio, t);
                EvaluateHazardKillsAt(_segments[i].position, segScale * HAZARD_KILL_RADIUS_SCALE);
            }
        }

        private void EvaluateHazardKillsAt(Vector3 center, float killRadius)
        {
            float sqrKillRadius = killRadius * killRadius;

            _hazardPlayerSnapshot.Clear();
            foreach (Network.NetworkPlayer player in Network.NetworkPlayer.ActivePlayers)
                _hazardPlayerSnapshot.Add(player);

            for (int i = 0; i < _hazardPlayerSnapshot.Count; i++)
            {
                Network.NetworkPlayer player = _hazardPlayerSnapshot[i];
                if (player == null || player.IsDead) continue;
                if ((player.transform.position - center).sqrMagnitude > sqrKillRadius) continue;

                player.ServerDieFromHazard("Sandworm");
            }

            _hazardBotSnapshot.Clear();
            foreach (Network.NetworkBot bot in Network.NetworkBot.ActiveBots)
                _hazardBotSnapshot.Add(bot);

            for (int i = 0; i < _hazardBotSnapshot.Count; i++)
            {
                Network.NetworkBot bot = _hazardBotSnapshot[i];
                if (bot == null) continue;
                if ((bot.transform.position - center).sqrMagnitude > sqrKillRadius) continue;

                Player.IDigger digger = bot.GetComponent<Player.IDigger>();
                digger?.Die();
            }
        }

        // ===== 충돌 (즉사) =====
        private void OnTriggerEnter2D(Collider2D other)
        {
            if (IsNetworkMode && !NetworkServer.active)
                return;

            var networkPlayer = other.GetComponent<Network.NetworkPlayer>();
            if (networkPlayer == null)
                networkPlayer = other.GetComponentInParent<Network.NetworkPlayer>();

            if (networkPlayer != null)
            {
                if (networkPlayer.ServerDieFromHazard("Sandworm"))
                    Debug.Log($"[Sandworm] {other.gameObject.name} 충돌 즉사");
                return;
            }

            var digger = other.GetComponent<Player.IDigger>();
            if (digger != null)
            {
                Debug.Log($"[Sandworm] {other.gameObject.name} 충돌 즉사");
                digger.Die();
            }
        }

        private void OnDestroy()
        {
            // 세그먼트 정리
            foreach (var seg in _segments)
            {
                if (seg != null) Destroy(seg.gameObject);
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.6f, 0.3f, 0.1f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, _eraseRadius);
        }
#endif
    }
}


