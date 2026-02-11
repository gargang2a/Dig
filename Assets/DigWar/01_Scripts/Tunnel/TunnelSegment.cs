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

        /// <summary>첫 번째 포인트(꼬리 끝) 위치 반환. 젬 드롭 용.</summary>
        public Vector3 GetFirstPointPosition()
        {
            return _points.Count > 0 ? _points[0] : Vector3.zero;
        }

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

            // LineRenderer: 영구 포인트 + 라이브 헤드(1개 추가)
            // 라이브 헤드는 매 프레임 UpdateLiveHead에서 플레이어 위치로 갱신된다.
            _lr.positionCount = _points.Count + 1;
            _lr.SetPosition(_points.Count - 1, worldPos);
            _lr.SetPosition(_points.Count, worldPos); // 라이브 헤드 초기값

            _colliderPoints.Add(worldPos);
            _dirtyCount++;

            if (_dirtyCount >= COLLIDER_SYNC_INTERVAL)
            {
                SyncCollider();
                _dirtyCount = 0;
            }
        }

        /// <summary>
        /// 매 프레임 호출. LineRenderer 맨 끝을 플레이어 위치에 고정하여
        /// 포인트 추가 사이의 시각적 갭을 제거한다.
        /// </summary>
        public void UpdateLiveHead(Vector3 playerPos)
        {
            if (_lr.positionCount > 0)
                _lr.SetPosition(_lr.positionCount - 1, playerPos);
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

            // 영구 포인트 + 라이브 헤드(+1) 유지
            _lr.positionCount = _points.Count + 1;
            for (int i = 0; i < _points.Count; i++)
                _lr.SetPosition(i, _points[i]);
            // 라이브 헤드는 마지막 영구 포인트와 동일 위치로 초기화
            _lr.SetPosition(_points.Count, _points[_points.Count - 1]);

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
