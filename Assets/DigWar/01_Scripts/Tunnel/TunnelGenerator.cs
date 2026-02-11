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
        [SerializeField] private Color _tunnelColor = new Color(0.25f, 0.16f, 0.08f, 1f);

        private GameSettings _settings;
        private readonly List<TunnelSegment> _segments = new List<TunnelSegment>();
        private TunnelSegment _currentSegment;
        private int _pointsInCurrentSegment;

        private Vector3 _lastPosition;
        private bool _hasFirstPoint;
        private int _totalPointCount;

        // 꼬리 슬라이딩
        private float _tailLerp;

        private const int SAFE_SEGMENT_COUNT = 2;
        private const int MAX_POINTS_PER_SEGMENT = 200;

        // 플레이어 머리 근처 보호 포인트 수 (충돌 안전 + 시각적 최소 길이)
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
            _lastPosition = transform.position;
            CreateNewSegment();
        }

        private void Update()
        {
            float sqrDist = (transform.position - _lastPosition).sqrMagnitude;
            float threshold = _settings.SegmentDistance;

            if (sqrDist >= threshold * threshold)
                AddPoint();

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
        /// 플레이어 이동 속도에 정확히 맞춰 제거하여 끊김을 방지한다.
        /// </summary>
        private void UpdateTailSlide()
        {
            float score = GameManager.Instance.CurrentScore;
            float maxDistance = _settings.BaseTunnelLength
                + score * _settings.TunnelLengthPerScore;
            int maxPoints = Mathf.Max(2, Mathf.FloorToInt(maxDistance / _settings.SegmentDistance));

            if (_totalPointCount <= maxPoints || _segments.Count == 0) return;

            // 플레이어 실제 이동 속도와 동기화하여 꼬리 제거 속도 일치
            float playerSpeed = _settings.BaseSpeed * transform.localScale.x;
            float slideSpeed = playerSpeed / Mathf.Max(_settings.SegmentDistance, 0.01f);
            _tailLerp += slideSpeed * Time.deltaTime;

            // 한 프레임에 여러 포인트를 넘길 수 있으므로 while로 처리
            while (_tailLerp >= 1f && _totalPointCount > maxPoints)
            {
                var oldest = _segments[0];

                if (oldest == _currentSegment && oldest.PointCount <= SAFE_HEAD_POINTS) break;
                if (oldest.PointCount < 2) break;

                oldest.RemoveFirstPoint();
                _totalPointCount--;
                _tailLerp -= 1f; // 초과분 이월 (0으로 리셋하지 않음)

                if (oldest != _currentSegment && oldest.PointCount < 2)
                {
                    _segments.RemoveAt(0);
                    Destroy(oldest.gameObject);
                }
            }

            // 남은 lerp로 시각적 슬라이딩
            if (_tailLerp > 0f && _tailLerp < 1f && _segments.Count > 0)
            {
                var oldest = _segments[0];
                if (oldest.PointCount >= 2)
                    oldest.SlideTailPoint(_tailLerp);
            }
        }

        private void CreateNewSegment()
        {
            float width = transform.localScale.x * _settings.TunnelWidthMultiplier;

            var obj = new GameObject($"TunnelSegment_{_segments.Count}");
            obj.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var segment = obj.AddComponent<TunnelSegment>();
            segment.Initialize(_tunnelMaterial, _tunnelColor, width);

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
                    Destroy(_segments[i].gameObject);
            }
            _segments.Clear();
            _totalPointCount = 0;
            _tailLerp = 0f;
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
