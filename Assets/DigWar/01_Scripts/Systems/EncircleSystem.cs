using System.Collections.Generic;
using UnityEngine;

namespace DigWar
{
    [RequireComponent(typeof(LineRenderer))]
    public class EncircleSystem : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private float _minDistance = 0.5f; // 점을 찍는 최소 거리
        [SerializeField] private int _maxPoints = 200; // 꼬리 최대 길이 (성능 제한)
        [SerializeField] private float _intersectionCheckDelay = 1.0f; // 생성 직후의 점들과는 충돌 검사 안 함 (초)
        [SerializeField] private LayerMask _enemyLayer; // 적 레이어

        [Header("Visuals")]
        [SerializeField] private Color _lineColor = Color.red;
        [SerializeField] private float _lineWidth = 0.2f;

        private LineRenderer _lineRenderer;
        private LinkedList<Vector2> _pathPoints = new LinkedList<Vector2>();
        private PolygonCollider2D _polygonCollider;
        
        // 최근 점들은 본체와 겹치므로 교차 검사에서 제외하기 위한 타임스탬프 리스트
        private LinkedList<float> _pointTimestamps = new LinkedList<float>();

        private void Awake()
        {
            _lineRenderer = GetComponent<LineRenderer>();
            _lineRenderer.positionCount = 0;
            _lineRenderer.startWidth = _lineWidth;
            _lineRenderer.endWidth = _lineWidth;
            _lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            _lineRenderer.startColor = _lineColor;
            _lineRenderer.endColor = _lineColor;
            
            // 동적으로 PolygonCollider2D 생성 (Trigger)
            _polygonCollider = gameObject.AddComponent<PolygonCollider2D>();
            _polygonCollider.isTrigger = true;
            _polygonCollider.enabled = false; // 평소에는 꺼둠
        }

        public void AddPoint(Vector2 position)
        {
            // 1. 최소 거리 검사
            if (_pathPoints.Count > 0)
            {
                float dist = Vector2.Distance(_pathPoints.Last.Value, position);
                if (dist < _minDistance) return;
            }

            // 2. 점 추가
            _pathPoints.AddLast(position);
            _pointTimestamps.AddLast(Time.time);

            // 3. 성능 최적화: 너무 오래된 점 삭제 (꼬리 자르기)
            if (_pathPoints.Count > _maxPoints)
            {
                _pathPoints.RemoveFirst();
                _pointTimestamps.RemoveFirst();
            }

            // 4. 시각화 업데이트
            UpdateLineRenderer();

            // 5. 교차(루프) 감지
            CheckIntersection(position);
        }

        private void UpdateLineRenderer()
        {
            _lineRenderer.positionCount = _pathPoints.Count;
            int i = 0;
            foreach (Vector2 p in _pathPoints)
            {
                _lineRenderer.SetPosition(i++, new Vector3(p.x, p.y, 0f));
            }
        }

        private void CheckIntersection(Vector2 currentHeadPos)
        {
            if (_pathPoints.Count < 10) return; // 점이 너무 적으면 검사 안 함

            // 현재 머리 위치(currentHeadPos)와 바로 직전 점을 잇는 선분
            Vector2 p2 = currentHeadPos;
            Vector2 p1 = _pathPoints.Last.Value;

            int index = 0;
            int totalCount = _pathPoints.Count;
            
            // LinkedList 순회
            var node = _pathPoints.First;
            var timeNode = _pointTimestamps.First;

            while (node != null && node.Next != null)
            {
                // 최신 점들(최근 생성된 꼬리)은 검사 제외
                if (Time.time - timeNode.Value < _intersectionCheckDelay)
                {
                    // 더 이상 과거의 점이 아니므로 루프 종료 (뒤쪽은 다 최신일 테니)
                     // LinkedList 순서가 [Old ... New] 라면, 여기서 break 하면 안되고 continue 해야 함.
                     // 하지만 AddLast로 넣으므로 뒤쪽이 최신임.
                     // 따라서 앞에서부터 검사하다가 '최신 점' 구간에 도달하면 검사 중단해도 됨.
                    break; 
                }

                Vector2 a = node.Value;
                Vector2 b = node.Next.Value;

                // 선분 교차 검사
                if (IsIntersecting(p1, p2, a, b))
                {
                    // 루프 감지!
                    // a, b는 교차된 오래된 꼬리 지점.
                    // 여기서부터 끝까지가 루프임.
                    CreateKillZone(node); 
                    return;
                }

                node = node.Next;
                timeNode = timeNode.Next;
                index++;
            }
        }

        // 선분 교차 판별 (A-B 와 C-D)
        private bool IsIntersecting(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            bool isIntersecting = false;

            float denominator = (p4.y - p3.y) * (p2.x - p1.x) - (p4.x - p3.x) * (p2.y - p1.y);

            // 평행하면 교차 안 함
            if (denominator != 0)
            {
                float u_a = ((p4.x - p3.x) * (p1.y - p3.y) - (p4.y - p3.y) * (p1.x - p3.x)) / denominator;
                float u_b = ((p2.x - p1.x) * (p1.y - p3.y) - (p2.y - p1.y) * (p1.x - p3.x)) / denominator;

                // 교차 조건: 0 <= u_a <= 1 AND 0 <= u_b <= 1
                if (u_a >= 0 && u_a <= 1 && u_b >= 0 && u_b <= 1)
                {
                    isIntersecting = true;
                }
            }

            return isIntersecting;
        }

        private void CreateKillZone(LinkedListNode<Vector2> startNode)
        {
            Debug.Log("⭕ Loop Detected!");

            // 1. 루프를 형성하는 점들 수집
            List<Vector2> loopPoints = new List<Vector2>();
            var currentNode = startNode;
            while(currentNode != null)
            {
                loopPoints.Add(currentNode.Value);
                currentNode = currentNode.Next;
            }

            // 2. Polygon Collider 설정
            _polygonCollider.enabled = true;
            _polygonCollider.SetPath(0, loopPoints.ToArray());

            // 3. 내부 적 감지 및 처치 (Collider가 업데이트될 때 OnTriggerEnter2D가 호출되기를 기다리거나, 수동으로 검사)
            // 즉시 검사를 위해 Overlap 사용
            ContactFilter2D filter = new ContactFilter2D();
            filter.SetLayerMask(_enemyLayer);
            filter.useTriggers = true;
            
            List<Collider2D> results = new List<Collider2D>();
            int count = _polygonCollider.OverlapCollider(filter, results);

            if (count > 0)
            {
                Debug.Log($"💀 Killing {count} enemies inside loop!");
                foreach (var col in results)
                {
                    // 봇 사망 처리
                    // IDigger 인터페이스나 AIController를 찾아 Kill
                    var digger = col.GetComponent<IDigger>();
                    if (digger != null && digger != (IDigger)GetComponentInParent<PlayerController>()) 
                    {
                        digger.Die(); // IDigger 인터페이스에 Die 있다면 사용, 아니면 GetComponent<AIController>().Die()
                    }
                    else
                    {
                        // 혹시 AIController 직접 참조
                        var ai = col.GetComponent<AIController>();
                        if(ai != null) ai.Die();
                    }
                }
            }

            // 4. 경로 초기화 (루프 터트림)
            ResetPath();
        }

        private void ResetPath()
        {
            _pathPoints.Clear();
            _pointTimestamps.Clear();
            _lineRenderer.positionCount = 0;
            _polygonCollider.enabled = false;
        }
    }
}
