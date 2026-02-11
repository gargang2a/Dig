using System.Collections.Generic;
using UnityEngine;

namespace Tunnel
{
    /// <summary>
    /// 터널 한 구간. LineRenderer + EdgeCollider2D.
    /// 꼬리 끝 포인트를 Lerp로 슬라이딩하여 부드럽게 수축할 수 있다.
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

        public int PointCount => _points.Count;

        public void Initialize(Material material, Color color, float width)
        {
            _lr = GetComponent<LineRenderer>();
            _lr.material = material;
            _lr.startColor = color;
            _lr.endColor = color;
            _lr.startWidth = width;
            _lr.endWidth = width;
            _lr.numCornerVertices = 8;
            _lr.numCapVertices = 4;
            _lr.useWorldSpace = true;
            _lr.sortingOrder = -1;
            _lr.positionCount = 0;

            _collider = gameObject.AddComponent<EdgeCollider2D>();
            _collider.edgeRadius = width * 0.5f;
            _collider.isTrigger = true;
            _collider.enabled = false;
        }

        public void AddPoint(Vector3 worldPos)
        {
            _points.Add(worldPos);
            _lr.positionCount = _points.Count;
            _lr.SetPosition(_points.Count - 1, worldPos);

            _colliderPoints.Add(worldPos);
            _dirtyCount++;

            if (_dirtyCount >= COLLIDER_SYNC_INTERVAL)
            {
                SyncCollider();
                _dirtyCount = 0;
            }
        }

        /// <summary>
        /// 꼬리 끝(첫 번째 포인트)을 두 번째 포인트 방향으로 t만큼 이동시킨다.
        /// t=0이면 원래 위치, t=1이면 두 번째 포인트와 동일 (제거 가능).
        /// </summary>
        public void SlideTailPoint(float t)
        {
            if (_points.Count < 2) return;

            Vector3 lerped = Vector3.Lerp(_points[0], _points[1], t);
            _lr.SetPosition(0, lerped);

            // 콜라이더도 동기화
            if (_colliderPoints.Count >= 2 && _collider.enabled)
            {
                _colliderPoints[0] = lerped;
                SyncCollider();
            }
        }

        /// <summary>
        /// 첫 번째 포인트를 실제로 제거한다. SlideTailPoint(1)에 도달한 후 호출.
        /// </summary>
        public void RemoveFirstPoint()
        {
            if (_points.Count < 2) return;

            _points.RemoveAt(0);
            _lr.positionCount = _points.Count;
            _lr.SetPositions(_points.ToArray());

            if (_colliderPoints.Count > 0)
                _colliderPoints.RemoveAt(0);

            if (_collider.enabled)
                SyncCollider();
        }

        private void SyncCollider()
        {
            if (_colliderPoints.Count >= 2)
                _collider.points = _colliderPoints.ToArray();
        }

        public void FlushCollider() => SyncCollider();

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
