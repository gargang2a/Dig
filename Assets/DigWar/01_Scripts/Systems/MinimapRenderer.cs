using UnityEngine;
using UnityEngine.UI;
using Core;
using Tunnel;

namespace Systems
{
    /// <summary>
    /// Texture2D 기반 미니맵 렌더러.
    /// 터널/경계선을 텍스처에 직접 페인팅하여 UI에 표시한다.
    /// Camera/RenderTexture 방식 대비 레이어 충돌 문제가 없고 GC 최소화.
    /// 
    /// UI 계층: Minimap Bg → [MinimapRender] → Ring → PlayerDot → BotDots
    /// </summary>
    public class MinimapRenderer : MonoBehaviour
    {
        private const int TEX_SIZE = 256;
        private const float UPDATE_INTERVAL = 0.5f;
        private const int HALF = TEX_SIZE / 2;

        // 투명 배경 — 기존 Minimap Bg가 실질적 배경 역할
        private static readonly Color32 CLEAR = new Color32(0, 0, 0, 0);

        [Header("미니맵 설정")]
        [SerializeField] private Color _boundaryColor = new Color(0.78f, 0.24f, 0.24f, 0.86f);

        private Texture2D _tex;
        private Color32[] _px;
        private RawImage _rawImage;
        private float _mapRadius;
        private float _timer;

        // GC 방지: 위치 캐시 배열 재사용
        private Vector3[] _posCache = new Vector3[128];

        // 도트 좌표와 동일한 매핑을 위한 픽셀 스케일
        // mapRadius → 이 값만큼의 픽셀 오프셋 (텍스처 중심 기준)
        private float _pixelScale;

        private void Start()
        {
            if (GameManager.Instance?.Settings == null) return;
            _mapRadius = GameManager.Instance.Settings.MapRadius;

            _tex = new Texture2D(TEX_SIZE, TEX_SIZE, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _px = new Color32[TEX_SIZE * TEX_SIZE];

            SetupUI();
            Repaint();
        }

        private void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer <= 0f)
            {
                _timer = UPDATE_INTERVAL;
                Repaint();
            }
        }

        // ===== UI 설정 =====
        private void SetupUI()
        {
            var hud = GetComponent<GameHUD>();
            if (hud == null) hud = FindObjectOfType<GameHUD>();
            if (hud == null || hud.MinimapRoot == null) return;

            // Minimap Bg(index 0) 바로 뒤(index 1)에 삽입 → Ring/Dots보다 아래 레이어
            var obj = new GameObject("MinimapRender");
            obj.transform.SetParent(hud.MinimapRoot, false);
            obj.transform.SetSiblingIndex(1);

            _rawImage = obj.AddComponent<RawImage>();
            _rawImage.texture = _tex;
            _rawImage.raycastTarget = false;

            // 부모(Minimap Panel) 꽉 채우기
            var rt = _rawImage.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            // 도트 좌표와 동일한 매핑 비율 계산
            // 도트: offset = world/mapRadius * usableRadius (UI px)
            // 텍스처: pixel = HALF + world/mapRadius * _pixelScale
            // UI에서 pixel_offset → 실제 UI offset = pixel_offset / HALF * rootHalfWidth
            // 일치 조건: _pixelScale / HALF * rootHalfWidth = usableRadius
            // → _pixelScale = usableRadius / rootHalfWidth * HALF
            float rootHalfWidth = hud.MinimapRoot.rect.width * 0.5f;
            float usableRadius = hud.MinimapUsableRadius;
            _pixelScale = (rootHalfWidth > 0.01f)
                ? usableRadius / rootHalfWidth * HALF
                : HALF - 2f; // fallback
        }

        // ===== 전체 리페인트 =====
        private void Repaint()
        {
            if (_tex == null) return;

            // 1) 클리어 (투명)
            for (int i = 0; i < _px.Length; i++)
                _px[i] = CLEAR;

            // 2) 경계 링 (맵 외곽 — _pixelScale 위치가 mapRadius)
            int boundaryR = Mathf.RoundToInt(_pixelScale);
            DrawRing(HALF, HALF, boundaryR, 2, _boundaryColor);

            // 3) 터널 페인팅
            PaintAllTunnels();

            // 4) GPU 업로드
            _tex.SetPixels32(_px);
            _tex.Apply();
        }

        // ===== 터널 페인팅 =====
        private void PaintAllTunnels()
        {
            // TunnelSegment를 전부 찾아 LineRenderer 점을 읽어 페인팅
            var segments = FindObjectsOfType<TunnelSegment>();

            foreach (var seg in segments)
            {
                var lrs = seg.GetComponentsInChildren<LineRenderer>();
                if (lrs == null || lrs.Length == 0) continue;

                // Fill LineRenderer (마지막 자식)의 색상과 위치 사용
                var lr = lrs[lrs.Length - 1];
                int count = lr.positionCount;
                if (count < 2) continue;

                // 캐시 배열 확장 (GC는 배열 부족 시에만 발생)
                if (_posCache.Length < count)
                    _posCache = new Vector3[count * 2];
                lr.GetPositions(_posCache);

                // 터널 색상: Fill LR의 startColor를 2배 밝게 (미니맵 시인성)
                Color c = lr.startColor;
                Color32 col = new Color32(
                    (byte)Mathf.Min(255, c.r * 510f),
                    (byte)Mathf.Min(255, c.g * 510f),
                    (byte)Mathf.Min(255, c.b * 510f),
                    255
                );

                // 터널 너비 → 픽셀 반경 (굵게)
                float w = lr.startWidth;
                int pr = Mathf.Max(2, Mathf.RoundToInt(w / _mapRadius * HALF));

                for (int i = 0; i < count; i++)
                    PaintDot(_posCache[i], pr, col);
            }
        }

        // ===== 그리기 헬퍼 =====

        /// <summary>맵 외곽 링 그리기.</summary>
        private void DrawRing(int cx, int cy, int r, int thick, Color32 c)
        {
            int outerSq = r * r;
            int innerSq = (r - thick) * (r - thick);

            for (int y = -r; y <= r; y++)
            {
                int yy = y * y;
                for (int x = -r; x <= r; x++)
                {
                    int d = x * x + yy;
                    if (d > outerSq || d < innerSq) continue;
                    SetPixel(cx + x, cy + y, c);
                }
            }
        }

        /// <summary>월드 좌표에 원형 점 찍기.</summary>
        private void PaintDot(Vector3 world, int r, Color32 c)
        {
            // 월드 → 텍스처 픽셀 좌표 (_pixelScale로 도트와 동일 비율)
            int px = HALF + Mathf.RoundToInt(world.x / _mapRadius * _pixelScale);
            int py = HALF + Mathf.RoundToInt(world.y / _mapRadius * _pixelScale);

            int rSq = r * r;
            int limit = Mathf.RoundToInt(_pixelScale);
            int limitSq = limit * limit;

            for (int dy = -r; dy <= r; dy++)
            {
                int dyy = dy * dy;
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dyy > rSq) continue;

                    int fx = px + dx;
                    int fy = py + dy;

                    // 원형 마스크: 맵 원 밖은 무시
                    int dcx = fx - HALF;
                    int dcy = fy - HALF;
                    if (dcx * dcx + dcy * dcy > limitSq) continue;

                    SetPixel(fx, fy, c);
                }
            }
        }

        private void SetPixel(int x, int y, Color32 c)
        {
            if (x < 0 || x >= TEX_SIZE || y < 0 || y >= TEX_SIZE) return;
            _px[y * TEX_SIZE + x] = c;
        }

        private void OnDestroy()
        {
            if (_tex != null) Destroy(_tex);
        }
    }
}
