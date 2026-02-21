using UnityEngine;
using System.Collections.Generic;
using Core;

namespace World
{
    /// <summary>
    /// 맵에 항상 상주하는 거대한 모래벌레.
    /// 원형 마디(Segment)로 이루어진 몸체가 지렁이처럼 꿈틀대며 이동한다.
    /// 흙을 다지며(터널 마스크를 지우며) 맵을 유유히 배회한다.
    /// 플레이어/AI와 부딪히면 즉사시키며, 지나간 자리에 고가치 보석을 뿌린다.
    /// </summary>
    public class Sandworm : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("이동 속도 (월드 유닛/초)")]
        [SerializeField] private float _speed = 4f;
        [Tooltip("회전 속도 (도/초)")]
        [SerializeField] private float _turnSpeed = 40f;
        [Tooltip("방향 전환 주기 (초)")]
        [SerializeField] private float _dirChangeInterval = 3f;

        [Header("Erasing (흙 덮기)")]
        [Tooltip("흙을 덮는 브러쉬 반경 (월드 유닛)")]
        [SerializeField] private float _eraseRadius = 3f;
        [Tooltip("EraseHole 호출 최소 이동 거리")]
        [SerializeField] private float _eraseStepDistance = 0.3f;

        [Header("Body Segments (마디 몸통)")]
        [Tooltip("몸통 마디 개수")]
        [SerializeField] private int _segmentCount = 8;
        [Tooltip("마디 간 간격 (월드 유닛)")]
        [SerializeField] private float _segmentSpacing = 0.8f;
        [Tooltip("머리 크기")]
        [SerializeField] private float _headScale = 2.5f;
        [Tooltip("꼬리 끝 크기 비율 (머리 대비)")]
        [SerializeField] private float _tailScaleRatio = 0.5f;
        [Tooltip("머리 스프라이트")]
        [SerializeField] private Sprite _headSprite;
        [Tooltip("몸통 마디 스프라이트")]
        [SerializeField] private Sprite _bodySprite;
        [Tooltip("머리 색상")]
        [SerializeField] private Color _headColor = new Color(0.6f, 0.3f, 0.15f, 1f);
        [Tooltip("몸통 색상")]
        [SerializeField] private Color _bodyColor = new Color(0.5f, 0.25f, 0.12f, 1f);

        [Header("Gem Spawning")]
        [Tooltip("보석 배출 간격 (이동 거리 기준)")]
        [SerializeField] private float _gemDropDistance = 2f;
        [Tooltip("배출할 보석 프리팹")]
        [SerializeField] private GameObject _gemPrefab;

        // 내부 상태
        private float _targetAngle;
        private float _dirChangeTimer;
        private float _gemDropAccum;
        private float _mapRadius;

        // 세그먼트 시스템 (위치 히스토리 기반)
        private readonly List<Vector3> _positionHistory = new List<Vector3>(256);
        private readonly List<Transform> _segments = new List<Transform>();
        private float _distanceMoved;

        /// <summary>미니맵 등 외부에서 마디 위치를 읽기 위한 프로퍼티.</summary>
        public IReadOnlyList<Transform> Segments => _segments;

        private const float WALL_AVOID_DISTANCE = 8f;
        private const float HISTORY_STEP = 0.15f; // 히스토리 기록 최소 거리
        private const int SORTING_ORDER_HEAD = 20;

        private void Start()
        {
            if (GameManager.Instance != null && GameManager.Instance.Settings != null)
                _mapRadius = GameManager.Instance.Settings.MapRadius;
            else
                _mapRadius = 50f;

            _targetAngle = Random.Range(0f, 360f);
            _dirChangeTimer = _dirChangeInterval;

            CreateSegments();
            InitializeHistory();
        }

        /// <summary>
        /// 머리 + N개의 마디 SpriteRenderer를 생성한다.
        /// </summary>
        private void CreateSegments()
        {
            // 머리 (자기 자신의 자식)
            var headObj = new GameObject("Head");
            headObj.transform.SetParent(transform);
            headObj.transform.localPosition = Vector3.zero;
            headObj.transform.localScale = Vector3.one * _headScale;
            var headSR = headObj.AddComponent<SpriteRenderer>();
            headSR.sprite = _headSprite;
            headSR.color = _headColor;
            headSR.sortingOrder = SORTING_ORDER_HEAD;

            // Collider는 머리에만
            var col = gameObject.GetComponent<CircleCollider2D>();
            if (col == null) col = gameObject.AddComponent<CircleCollider2D>();
            col.isTrigger = true;
            col.radius = _headScale * 0.4f;

            var rb = gameObject.GetComponent<Rigidbody2D>();
            if (rb == null) rb = gameObject.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;

            // 몸통 마디들 (비활성 상태로 생성 → 첫 프레임 번쩍임 방지)
            for (int i = 0; i < _segmentCount; i++)
            {
                var seg = new GameObject($"Segment_{i}");
                seg.SetActive(false); // 위치 배치 전까지 비활성
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
        /// 초기 위치 히스토리를 머리 뒤쪽으로 채우고, 세그먼트 활성화.
        /// </summary>
        private void InitializeHistory()
        {
            _positionHistory.Clear();
            Vector3 backDir = -transform.up;
            int totalNeeded = _segmentCount * Mathf.CeilToInt(_segmentSpacing / HISTORY_STEP) + 10;

            for (int i = 0; i < totalNeeded; i++)
            {
                _positionHistory.Add(transform.position + backDir * (i * HISTORY_STEP));
            }

            // 초기 위치에 세그먼트 배치 후 활성화 (번쩍임 방지)
            UpdateSegments();
            for (int i = 0; i < _segments.Count; i++)
                _segments[i].gameObject.SetActive(true);
        }

        private void Update()
        {
            UpdateAI();
            Rotate();
            Move();
            RecordHistory();
            UpdateSegments();
            TryErase();
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
        /// 머리가 일정 거리 이동할 때마다 위치를 기록한다.
        /// </summary>
        private void RecordHistory()
        {
            if (_positionHistory.Count == 0 ||
                Vector3.Distance(transform.position, _positionHistory[0]) >= HISTORY_STEP)
            {
                _positionHistory.Insert(0, transform.position);

                // 메모리 제한 (최대 500개)
                if (_positionHistory.Count > 500)
                    _positionHistory.RemoveAt(_positionHistory.Count - 1);
            }
        }

        /// <summary>
        /// 각 마디를 히스토리 상의 적절한 위치에 부드럽게 배치한다.
        /// </summary>
        private void UpdateSegments()
        {
            float smoothSpeed = 15f; // 보간 속도 (높을수록 즉각 반응)

            for (int i = 0; i < _segments.Count; i++)
            {
                // 히스토리 인덱스를 소수점으로 계산하여 두 점 사이를 보간
                float floatIndex = (i + 1) * _segmentSpacing / HISTORY_STEP;
                int indexA = Mathf.FloorToInt(floatIndex);
                int indexB = indexA + 1;
                indexA = Mathf.Clamp(indexA, 0, _positionHistory.Count - 1);
                indexB = Mathf.Clamp(indexB, 0, _positionHistory.Count - 1);

                float frac = floatIndex - Mathf.Floor(floatIndex);
                Vector3 targetPos = Vector3.Lerp(_positionHistory[indexA], _positionHistory[indexB], frac);

                // 부드러운 위치 이동 (Lerp)
                _segments[i].position = Vector3.Lerp(
                    _segments[i].position, targetPos, Time.deltaTime * smoothSpeed);

                // 이전 마디(또는 머리) 방향으로 부드러운 회전
                Vector3 lookTarget = (i == 0) ? transform.position : _segments[i - 1].position;
                Vector3 lookDir = lookTarget - _segments[i].position;
                if (lookDir.sqrMagnitude > 0.001f)
                {
                    float angle = Mathf.Atan2(lookDir.y, lookDir.x) * Mathf.Rad2Deg - 90f;
                    Quaternion targetRot = Quaternion.Euler(0f, 0f, angle);
                    _segments[i].rotation = Quaternion.Lerp(
                        _segments[i].rotation, targetRot, Time.deltaTime * smoothSpeed);
                }
            }
        }

        // ===== 흙 덮기 + 보석 배출 =====

        /// <summary>
        /// 매 프레임 이동 시 흙 덮기 요청.
        /// </summary>
        private void TryErase()
        {
            float frameDist = _speed * Time.deltaTime;
            _distanceMoved += frameDist;

            if (_distanceMoved < _eraseStepDistance) return;
            _distanceMoved = 0f;

            var maskMgr = Tunnel.TunnelMaskManager.Instance;
            if (maskMgr == null) return;

            // 머리 위치: 머리 크기에 맞는 반경으로 지우기
            maskMgr.EraseHole(transform.position, _headScale * 0.5f);

            // 각 마디 위치: 마디 크기에 맞는 반경으로 지우기
            for (int i = 0; i < _segments.Count; i++)
            {
                float t = (float)(i + 1) / _segmentCount;
                float segScale = Mathf.Lerp(_headScale, _headScale * _tailScaleRatio, t);
                maskMgr.EraseHole(_segments[i].position, segScale * 0.5f);
            }

            // 보석 배출
            _gemDropAccum += _eraseStepDistance;
            if (_gemDropAccum >= _gemDropDistance)
            {
                _gemDropAccum -= _gemDropDistance;
                SpawnGem();
            }
        }

        private void SpawnGem()
        {
            if (_gemPrefab == null) return;

            // 꼬리 끝 위치에서 보석 배출
            Vector3 spawnPos;
            if (_segments.Count > 0)
                spawnPos = _segments[_segments.Count - 1].position;
            else
                spawnPos = transform.position - transform.up * (_headScale);

            spawnPos += (Vector3)(Random.insideUnitCircle * 0.5f);

            if (Core.ObjectPoolManager.Instance != null)
                Core.ObjectPoolManager.Instance.Spawn(_gemPrefab, spawnPos, Quaternion.identity);
            else
                Instantiate(_gemPrefab, spawnPos, Quaternion.identity);
        }

        // ===== 충돌 (즉사) =====
        private void OnTriggerEnter2D(Collider2D other)
        {
            var digger = other.GetComponent<Player.IDigger>();
            if (digger != null)
            {
                Debug.Log($"🐛 [Sandworm] {other.gameObject.name} 삼킴!");
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
