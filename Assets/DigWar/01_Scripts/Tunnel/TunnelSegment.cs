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
        private static Texture2D s_shadowTexture; // 세로 그라데이션 텍스처
        private static Shader s_spriteShader;

        private LineRenderer _lr;         // 채움 (앞)
        private LineRenderer _shadowLr;   // 그림자 (중간)
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

        private bool _isInitialized;

        public void Initialize(Material material, Color fillColor, Color outlineColor, float width)
        {
            _tunnelWidth = width;
            _debrisColor = outlineColor;

            // 이미 초기화된 경우(풀링 재사용), 설정만 업데이트하고 리턴
            if (_isInitialized)
            {
                UpdateVisuals(material, fillColor, outlineColor, width);
                Reset(); // 상태 초기화
                return;
            }

            EnsureSharedResources();

            // --- 외곽선 LineRenderer (뒤쪽, 더 넓음, 단색) ---
            var outlineObj = new GameObject("Outline");
            outlineObj.transform.SetParent(transform, false);
            _outlineLr = outlineObj.AddComponent<LineRenderer>();
            // 1.3배 -> 1.2배로 더 줄임 (테두리 두께 최소화)
            SetupLineRenderer(_outlineLr, outlineColor,
                width * 1.2f, sortingOrder: -2, corners: 8, caps: 4);

            // --- 그림자 LineRenderer (중간, 그라데이션) ---
            var shadowObj = new GameObject("Shadow");
            shadowObj.transform.SetParent(transform, false);
            _shadowLr = shadowObj.AddComponent<LineRenderer>();
            
            // 그림자 재질 (흰색 그라데이션 텍스처 + 검정 틴트)
            var shadowMat = new Material(s_spriteShader);
            shadowMat.mainTexture = s_shadowTexture;
            _shadowLr.material = shadowMat;
            
            // 검정색 (알파값으로 진하기 조절)
            Color shadowColor = new Color(0f, 0f, 0f, 0.6f); 
            SetupLineRenderer(_shadowLr, shadowColor,
                width, sortingOrder: -1, corners: 8, caps: 4, isShadow: true);

            // --- 채움 LineRenderer (앞쪽, 기본 색상) ---
            _lr = GetComponent<LineRenderer>();
            SetupLineRenderer(_lr, fillColor,
                width, sortingOrder: -1, corners: 8, caps: 4); 
            _shadowLr.sortingOrder = 0; 
            _lr.sortingOrder = -1;

            // --- 콜라이더 ---
            _collider = gameObject.AddComponent<EdgeCollider2D>();
            _collider.edgeRadius = width * 0.5f;
            _collider.isTrigger = true;
            _collider.enabled = false;

            // --- 데브리 파티클 ---
            CreateDebrisParticleSystem(outlineColor);

            _isInitialized = true;
        }

        /// <summary>풀링 재사용을 위한 상태 초기화</summary>
        public void Reset()
        {
            _points.Clear();
            _colliderPoints.Clear();
            
            if (_lr != null) _lr.positionCount = 0;
            if (_outlineLr != null) _outlineLr.positionCount = 0;
            if (_shadowLr != null) _shadowLr.positionCount = 0;

            if (_collider != null)
            {
                _collider.enabled = false;
                _collider.points = System.Array.Empty<Vector2>();
            }

            _dirtyCount = 0;
            // 활성화
            gameObject.SetActive(true);
        }

        private void UpdateVisuals(Material material, Color fillColor, Color outlineColor, float width)
        {
            if (_lr != null)
            {
                _lr.startColor = fillColor;
                _lr.endColor = fillColor;
                _lr.startWidth = width;
                _lr.endWidth = width;
                // Material은 공유되므로 인스턴스화 주의 (여기서는 유지)
            }

            if (_outlineLr != null)
            {
                _outlineLr.startColor = outlineColor;
                _outlineLr.endColor = outlineColor;
                // 1.3 -> 1.2
                _outlineLr.startWidth = width * 1.2f;
                _outlineLr.endWidth = width * 1.2f;
            }
            
            if (_shadowLr != null)
            {
                _shadowLr.startWidth = width;
                _shadowLr.endWidth = width;
            }

            if (_debrisPS != null)
            {
                var main = _debrisPS.main;
                main.startColor = new ParticleSystem.MinMaxGradient(
                    outlineColor,
                    outlineColor * 1.3f
                );
            }
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

            if (s_shadowTexture == null)
                s_shadowTexture = CreateShadowTexture(64, 32);
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
            // 외곽선 반경: width * 0.6 (1.2f / 2)
            // 파티클 생성 위치: 외곽선 경계에 걸치도록 함
            // 0.55f ~ 0.65f (평균 0.6f) -> 파티클의 중심이 아웃라인 위에 옴
            float baseOffset = _tunnelWidth * 0.55f; 

            int count = Random.Range(2, 4);
            var emitParams = new ParticleSystem.EmitParams();

            for (int i = 0; i < count; i++)
            {
                float side = (i % 2 == 0) ? 1f : -1f;
                // 약간의 랜덤 (0.55 ~ 0.65)
                float offset = baseOffset + _tunnelWidth * Random.Range(0.0f, 0.1f);

                emitParams.position = pos + perpendicular * (side * offset);
                emitParams.velocity = Vector3.zero;
                // 크기: 0.2 ~ 0.4 (터널 벽과 자연스럽게 겹치게)
                emitParams.startSize = _tunnelWidth * Random.Range(0.2f, 0.4f);
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
            Color color, float width, int sortingOrder, int corners, int caps, bool isShadow = false)
        {
            if (!isShadow)
            {
                var instanceMat = new Material(s_spriteShader);
                lr.material = instanceMat;
            }
            // Shadow는 Initialize에서 별도 Material 할당됨

            lr.startColor = color;
            lr.endColor = color;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.numCornerVertices = corners;
            lr.numCapVertices = caps;
            lr.useWorldSpace = true;
            lr.sortingOrder = sortingOrder;
            lr.positionCount = 0;
            
            // 텍스처 모드: Stretch (그라데이션이 라인 전체 길이가 아니라 너비 방향으로 적용되게 하려면 Tile 모드 등을 고려해야 하지만,
            // LineRenderer 기본 매핑은 U:길이, V:너비 이므로 Stretch가 적절함)
            lr.textureMode = LineTextureMode.Stretch; 

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
            
            _shadowLr.positionCount = count + 1;
            _shadowLr.SetPosition(count - 1, worldPos);
            _shadowLr.SetPosition(count, worldPos);

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
            {
                _lr.SetPosition(_lr.positionCount - 1, playerPos);
                _outlineLr.SetPosition(_outlineLr.positionCount - 1, playerPos);
                _shadowLr.SetPosition(_shadowLr.positionCount - 1, playerPos);
            }
        }

        public void SlideTailPoint(float t)
        {
            if (_points.Count < 2) return;

            Vector3 lerped = Vector3.Lerp(_points[0], _points[1], t);
            _lr.SetPosition(0, lerped);
            _outlineLr.SetPosition(0, lerped);
            _shadowLr.SetPosition(0, lerped);

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
            _shadowLr.positionCount = count + 1;

            for (int i = 0; i < count; i++)
            {
                _lr.SetPosition(i, _points[i]);
                _outlineLr.SetPosition(i, _points[i]);
                _shadowLr.SetPosition(i, _points[i]);
            }

            _lr.SetPosition(count, _points[count - 1]);
            _outlineLr.SetPosition(count, _points[count - 1]);
            _shadowLr.SetPosition(count, _points[count - 1]);

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
            if (_shadowLr != null)
            {
                _shadowLr.startWidth = width;
                _shadowLr.endWidth = width;
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

        /// <summary>
        /// 세로 그라데이션 텍스처 생성 (Top: Opaque, Center: Transparent, Bottom: Opaque)
        /// V좌표가 너비 방향 (0~1). 1이 위쪽(Top Edge), 0이 아래쪽(Bottom Edge).
        /// LineRenderer TextureMode.Stretch 기준.
        /// </summary>
        private static Texture2D CreateShadowTexture(int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            
            for (int y = 0; y < height; y++)
            {
                // V: 0 (Below) -> 1 (Above)
                float v = (float)y / (height - 1);
                
                // 그림자 로직: 양쪽 끝(0.0, 1.0)은 진하고, 중앙(0.5)은 투명
                // Pipe 형태의 입체감 표현
                
                // 중심(0.5)에서 얼마나 떨어져 있는지 (0 ~ 0.5)
                float dist = Mathf.Abs(v - 0.5f);
                
                // 0.3 이상(가장자리 20%)부터 그림자 시작
                // 0.3 -> 0.0, 0.5 -> 1.0
                float alpha = 0f;
                if (dist > 0.3f)
                {
                    alpha = (dist - 0.3f) / 0.2f;
                    // 부드러운 곡선 (Ease-In)
                    alpha = alpha * alpha; 
                }

                Color c = new Color(1f, 1f, 1f, alpha); // RGB는 흰색(Tint로 검정 만듦), A가 그림자 강도
                
                for (int x = 0; x < width; x++)
                {
                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }
    }
}
