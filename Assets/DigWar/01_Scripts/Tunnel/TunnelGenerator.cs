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
    /// 플레이어 이동 경로를 따라 LineRenderer 기반 터널을 생성한다.
    /// 일정 포인트 수마다 세그먼트를 분할하고,
    /// 오래된 세그먼트만 충돌을 활성화하여 자기 터널 자살을 방지한다.
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

        /// <summary>최근 N개 세그먼트는 충돌 비활성. 자기 터널 자살 방지.</summary>
        private const int SAFE_SEGMENT_COUNT = 2;
        private const int MAX_POINTS_PER_SEGMENT = 200;

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

            if (_pointsInCurrentSegment >= MAX_POINTS_PER_SEGMENT)
            {
                CreateNewSegment();
                // 이전 세그먼트와 시각적으로 이어지도록 동일 포인트로 시작
                _currentSegment.AddPoint(pos);
            }

            _lastPosition = pos;
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

        /// <summary>
        /// SAFE_SEGMENT_COUNT 이전의 세그먼트 충돌을 켠다.
        /// 플레이어가 이미 지나간 구간만 위험해진다.
        /// </summary>
        private void ActivateOldColliders()
        {
            int idx = _segments.Count - 1 - SAFE_SEGMENT_COUNT;
            if (idx >= 0 && _segments[idx] != null)
                _segments[idx].EnableCollider();
        }

        public void UpdateWidth(float newScale)
        {
            float width = newScale * _settings.TunnelWidthMultiplier;
            if (_currentSegment != null)
                _currentSegment.SetWidth(width);
        }

        public void DestroyAllSegments()
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i] != null)
                    Destroy(_segments[i].gameObject);
            }
            _segments.Clear();
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
