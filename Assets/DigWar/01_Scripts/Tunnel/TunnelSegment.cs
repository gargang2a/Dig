using System.Collections.Generic;
using UnityEngine;

namespace Tunnel
{
    /// <summary>
    /// 터널 한 구간. 이중 LineRenderer(외곽선 + 채움) + EdgeCollider2D.
    /// 포인트 추가 시 양옆에 흙 덩어리 파티클을 흩뿌려 자연스러운 테두리를 만든다.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class TunnelSegment : MonoBehaviour
    {
        // ── 공유 리소스 (static 캐시로 GC/GPU 부하 감소) ──
        private static Texture2D s_circleTexture;
        private static Shader s_spriteShader;

        private LineRenderer _lr;         // 채움 (앞)
        private LineRenderer _outlineLr;  // 외곽선 (뒤)
        private EdgeCollider2D _collider;
        private readonly List<Vector3> _points = new List<Vector3>(200);
        private readonly List<Vector2> _colliderPoints = new List<Vector2>(200);

        private int _dirtyCount;
        private const int COLLIDER_SYNC_INTERVAL = 5;

        // 데브리 파티클
        private ParticleSystem _debrisPS;
        private float _tunnelWidth;
        private Color _debrisColor;

        // GC 감소: collider points 배열 캐시
        private Vector2[] _colliderArray;

        public int PointCount => _points.Count;

        /// <summary>첫 번째 포인트(꼬리 끝) 위치 반환. 젬 드롭 용.</summary>
        public Vector3 GetFirstPointPosition()
        {
            return _points.Count > 0 ? _points[0] : Vector3.zero;
        }

        public void Initialize(Material material, Color fillColor, Color outlineColor, float width)
        {
            _tunnelWidth = width;
            _debrisColor = outlineColor;

            EnsureSharedResources();

            // --- 외곽선 LineRenderer (뒤쪽, 더 넓음, 단색) ---
            var outlineObj = new GameObject("Outline");
            outlineObj.transform.SetParent(transform, false);
            _outlineLr = outlineObj.AddComponent<LineRenderer>();
            SetupLineRenderer(_outlineLr, outlineColor,
                width * 1.5f, sortingOrder: -2, corners: 8, caps: 4);

            // --- 채움 LineRenderer (앞쪽) ---
            _lr = GetComponent<LineRenderer>();
            SetupLineRenderer(_lr, fillColor,
                width, sortingOrder: -1, corners: 8, caps: 4);

            // --- 콜라이더 ---
            _collider = gameObject.AddComponent<EdgeCollider2D>();
            _collider.edgeRadius = width * 0.5f;
            _collider.isTrigger = true;
            _collider.enabled = false;

            // --- 데브리 파티클 ---
            CreateDebrisParticleSystem(outlineColor);
        }

        // 하위호환: 이전 시그니처 지원
        public void Initialize(Material material, Color color, float width)
        {
            Color outline = color * 0.5f;
            outline.a = 1f;
            Initialize(material, color, outline, width);
        }

        // ── 정적 리소스 초기화 (한 번만 실행) ──
        private static void EnsureSharedResources()
        {
            if (s_spriteShader == null)
            {
                s_spriteShader = Shader.Find("Sprites/Default");
                if (s_spriteShader == null)
                    s_spriteShader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            }

            if (s_circleTexture == null)
                s_circleTexture = CreateCircleTexture(32);
        }

        // ── 데브리 파티클 시스템 ──
        private void CreateDebrisParticleSystem(Color color)
        {
            var psObj = new GameObject("Debris");
            psObj.transform.SetParent(transform, false);

            _debrisPS = psObj.AddComponent<ParticleSystem>();

            // 수동 Emit만 사용 (자동 방출 없음)
            var main = _debrisPS.main;
            main.startLifetime = 999f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(
                _tunnelWidth * 0.15f, _tunnelWidth * 0.4f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                color,
                color * 1.3f
            );
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 800;
            main.playOnAwake = false;

            var emission = _debrisPS.emission;
            emission.rateOverTime = 0f;

            var shape = _debrisPS.shape;
            shape.enabled = false;

            // Renderer: 공유 원형 텍스처 사용
            var renderer = psObj.GetComponent<ParticleSystemRenderer>();
            var circleMat = new Material(s_spriteShader);
            circleMat.mainTexture = s_circleTexture;
            renderer.material = circleMat;
            renderer.sortingOrder = 0;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;

            _debrisPS.Stop();
        }

        /// <summary>터널 가장자리에 흙 덩어리 파티클을 흩뿌린다.</summary>
        private void EmitDebris(Vector3 pos, Vector3 direction)
        {
            if (_debrisPS == null) return;

            Vector3 perpendicular = new Vector3(-direction.y, direction.x, 0f).normalized;
            float outerEdge = _tunnelWidth * 0.75f; // 외곽선 반경 바깥

            int count = Random.Range(2, 4);
            var emitParams = new ParticleSystem.EmitParams();

            for (int i = 0; i < count; i++)
            {
                float side = (i % 2 == 0) ? 1f : -1f;
                float offset = outerEdge + _tunnelWidth * Random.Range(0.05f, 0.3f);

                emitParams.position = pos + perpendicular * (side * offset);
                emitParams.velocity = Vector3.zero;
                emitParams.startSize = _tunnelWidth * Random.Range(0.2f, 0.5f);
                emitParams.startLifetime = 999f;

                // 색상 미세 변동
                Color c = _debrisColor;
                float v = Random.Range(-0.08f, 0.08f);
                c.r = Mathf.Clamp01(c.r + v);
                c.g = Mathf.Clamp01(c.g + v);
                c.b = Mathf.Clamp01(c.b + v);
                emitParams.startColor = c;

                _debrisPS.Emit(emitParams, 1);
            }
        }

        // ── LineRenderer 설정 ──
        private void SetupLineRenderer(LineRenderer lr,
            Color color, float width, int sortingOrder, int corners, int caps)
        {
            var instanceMat = new Material(s_spriteShader);
            lr.material = instanceMat;
            lr.startColor = color;
            lr.endColor = color;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.numCornerVertices = corners;
            lr.numCapVertices = caps;
            lr.useWorldSpace = true;
            lr.sortingOrder = sortingOrder;
            lr.positionCount = 0;

            // 꼬리→머리 자연스러운 너비 변화
            lr.widthCurve = new AnimationCurve(
                new Keyframe(0.0f, 0.3f),
                new Keyframe(0.1f, 0.8f),
                new Keyframe(0.3f, 1.0f),
                new Keyframe(0.9f, 1.0f),
                new Keyframe(1.0f, 0.7f)
            );
        }

        // ── 포인트 관리 ──
        public void AddPoint(Vector3 worldPos)
        {
            _points.Add(worldPos);

            int count = _points.Count;
            _lr.positionCount = count + 1;
            _lr.SetPosition(count - 1, worldPos);
            _lr.SetPosition(count, worldPos);

            _outlineLr.positionCount = count + 1;
            _outlineLr.SetPosition(count - 1, worldPos);
            _outlineLr.SetPosition(count, worldPos);

            // 데브리 흩뿌리기 (방향이 있을 때만)
            if (count >= 2)
            {
                Vector3 dir = (_points[count - 1] - _points[count - 2]).normalized;
                EmitDebris(worldPos, dir);
            }

            _colliderPoints.Add(worldPos);
            _dirtyCount++;

            if (_dirtyCount >= COLLIDER_SYNC_INTERVAL)
            {
                SyncCollider();
                _dirtyCount = 0;
            }
        }

        /// <summary>매 프레임 호출. LineRenderer 맨 끝을 플레이어 위치에 고정.</summary>
        public void UpdateLiveHead(Vector3 playerPos)
        {
            if (_lr.positionCount > 0)
                _lr.SetPosition(_lr.positionCount - 1, playerPos);
            if (_outlineLr.positionCount > 0)
                _outlineLr.SetPosition(_outlineLr.positionCount - 1, playerPos);
        }

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

        // ── Collider 동기화 (GC 감소: 배열 캐시) ──
        private void SyncCollider()
        {
            int count = _colliderPoints.Count;
            if (count < 2) return;

            if (_colliderArray == null || _colliderArray.Length != count)
                _colliderArray = new Vector2[count];

            for (int i = 0; i < count; i++)
                _colliderArray[i] = _colliderPoints[i];

            _collider.points = _colliderArray;
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
            _tunnelWidth = width;
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

        // ── 유틸리티 ──

        /// <summary>런타임에 원형 텍스처를 생성한다 (static 캐시).</summary>
        private static Texture2D CreateCircleTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = size * 0.5f;
            float radius = center - 1f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center + 0.5f;
                    float dy = y - center + 0.5f;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(radius - dist + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }
    }
}
