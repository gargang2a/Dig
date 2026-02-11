using UnityEngine;
using Core;
using Core.Data;

namespace World
{
    /// <summary>
    /// 맵 지형과 원형 경계선을 생성한다.
    /// 흙 배경 SpriteRenderer + 경계 LineRenderer.
    /// </summary>
    public class MapManager : MonoBehaviour
    {
        [Header("지형")]
        [Tooltip("흙 지형 스프라이트 (02_Sprites에서 할당)")]
        [SerializeField] private Sprite _groundSprite;
        [Tooltip("흙 배경 색상 보정")]
        [SerializeField] private Color _groundColor = new Color(0.55f, 0.35f, 0.17f, 1f);
        [Tooltip("타일 1칸의 월드 크기 (작을수록 패턴이 촘촘)")]
        [SerializeField] private float _tileSize = 5f;

        [Header("경계선")]
        [Tooltip("경계선 색상")]
        [SerializeField] private Color _boundaryColor = new Color(1f, 0.2f, 0.2f, 0.8f);
        [Tooltip("경계선 두께")]
        [SerializeField] private float _boundaryWidth = 0.3f;

        private GameSettings _settings;
        private LineRenderer _boundaryLine;

        private void Start()
        {
            if (GameManager.Instance == null || GameManager.Instance.Settings == null)
            {
                Debug.LogError("[MapManager] GameManager 누락");
                enabled = false;
                return;
            }

            _settings = GameManager.Instance.Settings;

            CreateGround();
            CreateBoundary();
        }

        /// <summary>
        /// 흙 지형 배경 생성. Quad 메시에 타일링 머테리얼을 적용하여
        /// 텍스처가 타일맵처럼 반복된다.
        /// </summary>
        private void CreateGround()
        {
            var groundObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            groundObj.name = "Ground";
            groundObj.transform.position = new Vector3(0f, 0f, 1f);

            // Quad 콜라이더 제거 (불필요)
            var collider = groundObj.GetComponent<MeshCollider>();
            if (collider != null) Destroy(collider);

            float diameter = _settings.MapRadius * 2f;
            groundObj.transform.localScale = new Vector3(diameter, diameter, 1f);

            // Unlit/Texture 셰이더: 타일링 정상 지원
            var renderer = groundObj.GetComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Unlit/Texture"));

            if (_groundSprite != null && _groundSprite.texture != null)
            {
                var tex = _groundSprite.texture;
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.filterMode = FilterMode.Point; // 픽셀 아트면 Point, 부드러운 텍스처면 Bilinear
                mat.mainTexture = tex;

                // 타일링: 맵 크기 / 타일 크기 = 반복 횟수
                float tiling = diameter / Mathf.Max(_tileSize, 0.1f);
                mat.mainTextureScale = new Vector2(tiling, tiling);
            }

            renderer.material = mat;
            renderer.sortingOrder = -10;
        }

        /// <summary>
        /// 원형 경계선 생성. LineRenderer loop.
        /// </summary>
        private void CreateBoundary()
        {
            var boundaryObj = new GameObject("MapBoundary");
            boundaryObj.transform.position = Vector3.zero;

            _boundaryLine = boundaryObj.AddComponent<LineRenderer>();
            _boundaryLine.useWorldSpace = true;
            _boundaryLine.loop = true;
            _boundaryLine.sortingOrder = 5;
            _boundaryLine.startWidth = _boundaryWidth;
            _boundaryLine.endWidth = _boundaryWidth;

            // 단색 머테리얼
            _boundaryLine.material = new Material(Shader.Find("Sprites/Default"));
            _boundaryLine.startColor = _boundaryColor;
            _boundaryLine.endColor = _boundaryColor;

            int segments = _settings.BoundarySegments;
            float radius = _settings.MapRadius;

            _boundaryLine.positionCount = segments;
            for (int i = 0; i < segments; i++)
            {
                float angle = (2f * Mathf.PI * i) / segments;
                float x = Mathf.Cos(angle) * radius;
                float y = Mathf.Sin(angle) * radius;
                _boundaryLine.SetPosition(i, new Vector3(x, y, 0f));
            }
        }
    }
}
