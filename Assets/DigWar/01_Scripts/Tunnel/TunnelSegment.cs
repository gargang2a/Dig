using System.Collections.Generic;
using UnityEngine;

namespace Tunnel
{
    /// <summary>
    /// 터널 한 구간을 담당하는 LineRenderer + EdgeCollider2D 조합.
    /// LineRenderer가 부드러운 곡선과 고정 너비를 제공하고,
    /// EdgeCollider2D(Trigger)가 충돌 판정을 처리한다.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class TunnelSegment : MonoBehaviour
    {
        private LineRenderer _lr;
        private EdgeCollider2D _collider;
        private readonly List<Vector3> _points = new List<Vector3>(200);
        private readonly List<Vector2> _colliderPoints = new List<Vector2>(200);

        private int _dirtyCount;
        private const int COLLIDER_SYNC_INTERVAL = 5;

        public void Initialize(Material material, Color color, float width)
        {
            _lr = GetComponent<LineRenderer>();
            _lr.material = material;
            _lr.startColor = color;
            _lr.endColor = color;
            _lr.startWidth = width;
            _lr.endWidth = width;
            _lr.numCornerVertices = 8;   // 곡선부 부드러움
            _lr.numCapVertices = 4;      // 양 끝 마감
            _lr.useWorldSpace = true;
            _lr.sortingOrder = -1;       // 플레이어 스프라이트 아래
            _lr.positionCount = 0;

            _collider = gameObject.AddComponent<EdgeCollider2D>();
            _collider.edgeRadius = width * 0.5f;
            _collider.isTrigger = true;  // Kinematic RB와 충돌하려면 Trigger 필수
            _collider.enabled = false;   // TunnelGenerator가 안전 거리 후 활성화
        }

        public void AddPoint(Vector3 worldPos)
        {
            _points.Add(worldPos);
            _lr.positionCount = _points.Count;
            _lr.SetPosition(_points.Count - 1, worldPos);

            _colliderPoints.Add(worldPos);
            _dirtyCount++;

            // ToArray 할당을 줄이기 위해 일정 간격으로만 동기화
            if (_dirtyCount >= COLLIDER_SYNC_INTERVAL)
            {
                _collider.points = _colliderPoints.ToArray();
                _dirtyCount = 0;
            }
        }

        /// <summary>남은 포인트를 콜라이더에 반영한다. 세그먼트 종료 시 호출.</summary>
        public void FlushCollider()
        {
            if (_colliderPoints.Count >= 2)
                _collider.points = _colliderPoints.ToArray();
        }

        public void EnableCollider()
        {
            FlushCollider();
            if (_collider != null)
                _collider.enabled = true;
        }

        public void SetWidth(float width)
        {
            if (_lr != null)
            {
                _lr.startWidth = width;
                _lr.endWidth = width;
            }
            if (_collider != null)
                _collider.edgeRadius = width * 0.5f;
        }
    }
}
