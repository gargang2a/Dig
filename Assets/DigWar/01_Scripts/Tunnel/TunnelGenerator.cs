using System.Collections.Generic;
using UnityEngine;
using Core;
using Core.Data;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Tunnel
{
    /// <summary>
    /// 플레이어 이동 경로를 따라 터널을 생성한다.
    /// 점수에 비례하여 최대 길이가 늘어나며,
    /// 초과 시 꼬리가 부드럽게 수축한다.
    /// </summary>
    public class TunnelGenerator : MonoBehaviour
    {
        [Header("Visuals")]
        [SerializeField] private Material _tunnelMaterial;
        [SerializeField] private Color _tunnelColor = new Color(0.77f, 0.64f, 0.40f, 1f);  // 밝은 모래
        [SerializeField] private Color _outlineColor = new Color(0.24f, 0.17f, 0.10f, 1f);  // 어두운 테두리


        private GameSettings _settings;
        private readonly List<TunnelSegment> _segments = new List<TunnelSegment>();
        private TunnelSegment _currentSegment;
        private int _pointsInCurrentSegment;

        private Vector3 _lastPosition;
        private bool _hasFirstPoint;
        private int _totalPointCount;

        // Object Pooling
        private readonly Queue<TunnelSegment> _segmentPool = new Queue<TunnelSegment>();

        public int TotalPointCount => _totalPointCount;
        public int SegmentCount => _segments.Count;

        // 꼬리 슬라이딩
        private float _tailLerp;
        private int _boostDropCounter; // 부스트 젬 드롭 간격 카운터
        private Player.IDigger _digger;
        private World.GemSpawner _gemSpawner;

        private const int SAFE_SEGMENT_COUNT = 2;
        private const int MAX_POINTS_PER_SEGMENT = 200;
        private const int SAFE_HEAD_POINTS = 5;

        private void Start()
        {
            if (GameManager.Instance == null || GameManager.Instance.Settings == null)
            {
                Debug.LogError("[TunnelGenerator] GameManager 또는 Settings 누락");
                enabled = false;
                return;
            }

            _settings = GameManager.Instance.Settings;
            _digger = GetComponent<Player.IDigger>();
            _gemSpawner = FindObjectOfType<World.GemSpawner>();
            _lastPosition = transform.position;
            CreateNewSegment();
        }

        private void Update()
        {
            float sqrDist = (transform.position - _lastPosition).sqrMagnitude;
            float threshold = _settings.SegmentDistance;

            if (sqrDist >= threshold * threshold)
                AddPoint();

            // 터널 머리를 항상 플레이어에 연결 (포인트 추가 사이 갭 제거)
            if (_currentSegment != null)
                _currentSegment.UpdateLiveHead(transform.position);

            UpdateTailSlide();
        }

        private void AddPoint()
        {
            Vector3 pos = transform.position;

            if (!_hasFirstPoint)
            {
                _hasFirstPoint = true;
                _lastPosition = pos;
                return;
            }

            _currentSegment.AddPoint(pos);
            _pointsInCurrentSegment++;
            _totalPointCount++;

            if (_pointsInCurrentSegment >= MAX_POINTS_PER_SEGMENT)
            {
                CreateNewSegment();
                _currentSegment.AddPoint(pos);
            }

            _lastPosition = pos;
        }

        /// <summary>
        /// 꼬리를 부드럽게 슬라이딩한다.
        /// >= maxPoints이면 항상 슬라이딩 유지. 실제 제거는 > maxPoints일 때만.
        /// 이 분리로 "따라오다-멈추다" 패턴을 방지한다.
        /// </summary>
        private void UpdateTailSlide()
        {
            float score = GameManager.Instance.CurrentScore;
            float maxDistance = _settings.BaseTunnelLength
                + score * _settings.TunnelLengthPerScore;
            int maxPoints = Mathf.Max(2, Mathf.FloorToInt(maxDistance / _settings.SegmentDistance));

            // 아직 한계까지 안 찼으면 슬라이딩 불필요
            if (_totalPointCount < maxPoints || _segments.Count == 0)
            {
                _tailLerp = 0f;
                return;
            }

            var oldest = _segments[0];
            if (oldest.PointCount < 2) return;
            if (oldest == _currentSegment && oldest.PointCount <= SAFE_HEAD_POINTS) return;

            // 실제 플레이어 속도(부스트 포함)로 동기화
            float actualSpeed = _digger != null
                ? _digger.CurrentSpeed
                : _settings.BaseSpeed * transform.localScale.x;
            float slideSpeed = actualSpeed / Mathf.Max(_settings.SegmentDistance, 0.01f);
            _tailLerp += slideSpeed * Time.deltaTime;

            bool isBoosting = _digger != null && _digger.IsBoosting;

            // 실제 포인트 제거
            while (_tailLerp >= 1f)
            {
                if (_totalPointCount <= maxPoints)
                {
                    _tailLerp = 1f;
                    break;
                }

                oldest = _segments[0];
                if (oldest == _currentSegment && oldest.PointCount <= SAFE_HEAD_POINTS) break;
                if (oldest.PointCount < 2) break;

                // 부스트 중 꼬리에서 젬 드롭 (10포인트당 1개, 점수 여유시에만)
                if (isBoosting && _gemSpawner != null)
                {
                    _boostDropCounter++;
                    if (_boostDropCounter >= 10
                        && GameManager.Instance.CurrentScore >= _settings.GemScore)
                    {
                        _gemSpawner.DropGemAt(oldest.GetFirstPointPosition());
                        _boostDropCounter = 0;
                    }
                }

                oldest.RemoveFirstPoint();
                _totalPointCount--;
                _tailLerp = 0f;

                if (oldest != _currentSegment && oldest.PointCount < 2)
                {
                    _segments.RemoveAt(0);
                    // Destroy 대신 풀링 반환
                    oldest.gameObject.SetActive(false);
                    _segmentPool.Enqueue(oldest);
                }
            }

            // 시각적 슬라이딩
            if (_segments.Count > 0)
            {
                oldest = _segments[0];
                if (oldest.PointCount >= 2 && _tailLerp > 0f)
                {
                    oldest.SlideTailPoint(Mathf.Min(_tailLerp, 1f));
                }
            }
        }

        private void CreateNewSegment()
        {
            float width = transform.localScale.x * _settings.TunnelWidthMultiplier;
            TunnelSegment segment = null;

            // 풀에서 가져오기
            if (_segmentPool.Count > 0)
            {
                segment = _segmentPool.Dequeue();
                segment.gameObject.SetActive(true);
            }
            else
            {
                var obj = new GameObject($"TunnelSegment_{Random.Range(0, 10000)}"); // 이름 랜덤화는 디버그용
                segment = obj.AddComponent<TunnelSegment>();
            }

            // 위치 초기화는 Transform만
            segment.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            
            // Initialize 내부에서 재사용(UpdateVisuals + Reset) 처리
            segment.Initialize(_tunnelMaterial, _tunnelColor, _outlineColor, width);

            _segments.Add(segment);
            _currentSegment = segment;
            _pointsInCurrentSegment = 0;

            ActivateOldColliders();
        }

        private void ActivateOldColliders()
        {
            int idx = _segments.Count - 1 - SAFE_SEGMENT_COUNT;
            if (idx >= 0 && _segments[idx] != null)
                _segments[idx].EnableCollider();
        }

        public void UpdateWidth(float newScale)
        {
            if (_settings == null)
            {
                if (GameManager.Instance != null)
                    _settings = GameManager.Instance.Settings;
                
                if (_settings == null) return;
            }

            float width = newScale * _settings.TunnelWidthMultiplier;
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i] != null)
                    _segments[i].SetWidth(width);
            }
        }

        public void DestroyAllSegments()
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i] != null)
                {
                    _segments[i].gameObject.SetActive(false);
                    _segmentPool.Enqueue(_segments[i]);
                }
            }
            _segments.Clear();
            _totalPointCount = 0;
            _tailLerp = 0f;
        }

        /// <summary>
        /// 터널 비주얼을 외부에서 설정한다 (봇 스포너 등).
        /// Start() 전에 호출해야 첫 세그먼트부터 적용된다.
        /// </summary>
        public void SetTunnelVisuals(Material material, Color fillColor, Color outlineColor)
        {
            _tunnelMaterial = material;
            _tunnelColor = fillColor;
            _outlineColor = outlineColor;
        }

        // 하위호환
        public void SetTunnelVisuals(Material material, Color color)
        {
            Color outline = color * 0.5f;
            outline.a = 1f;
            SetTunnelVisuals(material, color, outline);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_tunnelMaterial != null) return;

            string[] guids = AssetDatabase.FindAssets("Tunnel t:Material");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _tunnelMaterial = AssetDatabase.LoadAssetAtPath<Material>(path);
            }
        }
#endif
    }
}
