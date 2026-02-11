using System.Collections.Generic;
using UnityEngine;

namespace Tunnel
{
    /// <summary>
    /// 터널 한 구간. 이중 LineRenderer(외곽선 + 채움) + EdgeCollider2D.
    /// 꼬리 끝 포인트를 Lerp로 슬라이딩하여 부드럽게 수축할 수 있다.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class TunnelSegment : MonoBehaviour
    {
        private LineRenderer _lr;         // 채움 (앞)
        private LineRenderer _outlineLr;  // 외곽선 (뒤)
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

        public void Initialize(Material material, Color fillColor, Color outlineColor, float width)
        {
            // --- 외곽선 LineRenderer (뒤쪽, 더 넓음) ---
            var outlineObj = new GameObject("Outline");
            outlineObj.transform.SetParent(transform, false);
            _outlineLr = outlineObj.AddComponent<LineRenderer>();
            SetupLineRenderer(_outlineLr, material, outlineColor,
                width * 1.5f, sortingOrder: -2, corners: 8, caps: 4);

            // --- 채움 LineRenderer (앞쪽) ---
            _lr = GetComponent<LineRenderer>();
            SetupLineRenderer(_lr, material, fillColor,
                width, sortingOrder: -1, corners: 8, caps: 4);

            // --- 콜라이더 ---
            _collider = gameObject.AddComponent<EdgeCollider2D>();
            _collider.edgeRadius = width * 0.5f;
            _collider.isTrigger = true;
            _collider.enabled = false;
        }

        // 하위호환: 이전 시그니처 지원
        public void Initialize(Material material, Color color, float width)
        {
            // 외곽선 = 채움보다 어두운 색
            Color outline = color * 0.5f;
            outline.a = 1f;
            Initialize(material, color, outline, width);
        }

        private void SetupLineRenderer(LineRenderer lr, Material mat,
            Color color, float width, int sortingOrder, int corners, int caps)
        {
            lr.material = mat;
            lr.startColor = color;
            lr.endColor = color;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.numCornerVertices = corners;
            lr.numCapVertices = caps;
            lr.useWorldSpace = true;
            lr.sortingOrder = sortingOrder;
            lr.positionCount = 0;
        }

        public void AddPoint(Vector3 worldPos)
        {
            _points.Add(worldPos);

            int count = _points.Count;
            // 영구 포인트 + 라이브 헤드(1개)
            _lr.positionCount = count + 1;
            _lr.SetPosition(count - 1, worldPos);
            _lr.SetPosition(count, worldPos);

            _outlineLr.positionCount = count + 1;
            _outlineLr.SetPosition(count - 1, worldPos);
            _outlineLr.SetPosition(count, worldPos);

            _colliderPoints.Add(worldPos);
            _dirtyCount++;

            if (_dirtyCount >= COLLIDER_SYNC_INTERVAL)
            {
                SyncCollider();
                _dirtyCount = 0;
            }
        }

        /// <summary>
        /// 매 프레임 호출. LineRenderer 맨 끝을 플레이어 위치에 고정.
        /// </summary>
        public void UpdateLiveHead(Vector3 playerPos)
        {
            if (_lr.positionCount > 0)
                _lr.SetPosition(_lr.positionCount - 1, playerPos);
            if (_outlineLr.positionCount > 0)
                _outlineLr.SetPosition(_outlineLr.positionCount - 1, playerPos);
        }

        /// <summary>
        /// 꼬리 끝 포인트를 두 번째 방향으로 t만큼 슬라이딩.
        /// </summary>
        public void SlideTailPoint(float t)
        {
            if (_points.Count < 2) return;

            Vector3 lerped = Vector3.Lerp(_points[0], _points[1], t);
            _lr.SetPosition(0, lerped);
            _outlineLr.SetPosition(0, lerped);

            if (_colliderPoints.Count >= 2 && _collider.enabled)
            {
                _colliderPoints[0] = lerped;
                SyncCollider();
            }
        }

        /// <summary>
        /// 첫 번째 포인트를 실제로 제거한다.
        /// </summary>
        public void RemoveFirstPoint()
        {
            if (_points.Count < 2) return;

            _points.RemoveAt(0);

            int count = _points.Count;
            _lr.positionCount = count + 1;
            _outlineLr.positionCount = count + 1;

            for (int i = 0; i < count; i++)
            {
                _lr.SetPosition(i, _points[i]);
                _outlineLr.SetPosition(i, _points[i]);
            }

            _lr.SetPosition(count, _points[count - 1]);
            _outlineLr.SetPosition(count, _points[count - 1]);

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
            if (_outlineLr != null)
            {
                _outlineLr.startWidth = width * 1.5f;
                _outlineLr.endWidth = width * 1.5f;
            }
            if (_collider != null)
                _collider.edgeRadius = width * 0.5f;
        }
    }
}
