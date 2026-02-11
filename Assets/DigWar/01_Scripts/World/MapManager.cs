using UnityEngine;
using Core;
using Core.Data;

namespace World
{
    /// <summary>
    /// 맵 지형과 원형 경계선을 생성한다.
    /// 여러 흙 스프라이트를 랜덤으로 배치하여 자연스러운 배경을 만든다.
    /// </summary>
    public class MapManager : MonoBehaviour
    {
        [Header("지형")]
        [Tooltip("흙 타일 스프라이트 배열 (랜덤 선택)")]
        [SerializeField] private Sprite[] _groundSprites;
        [Tooltip("타일 1칸의 월드 크기")]
        [SerializeField] private float _tileSize = 2f;
        [Tooltip("타일 회전 랜덤화")]
        [SerializeField] private bool _randomRotation = true;

        [Header("경계선")]
        [SerializeField] private Color _boundaryColor = new Color(1f, 0.2f, 0.2f, 0.8f);
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
        /// 개별 SpriteRenderer 타일을 랜덤 배치하여 자연스러운 지형 생성.
        /// 원형 맵 범위 안에만 배치한다.
        /// </summary>
        private void CreateGround()
        {
            if (_groundSprites == null || _groundSprites.Length == 0)
            {
                Debug.LogWarning("[MapManager] Ground Sprites 배열이 비어있습니다.");
                return;
            }

            float radius = _settings.MapRadius;
            float halfSize = _tileSize * 0.5f;

            // 타일 부모 오브젝트
            var parent = new GameObject("GroundTiles");
            parent.transform.position = new Vector3(0f, 0f, 1f);

            // 시드 고정 (매번 동일한 맵 레이아웃)
            Random.State prevState = Random.state;
            Random.InitState(42);

            // 그리드 순회, 원형 범위 안에만 배치
            for (float x = -radius; x <= radius; x += _tileSize)
            {
                for (float y = -radius; y <= radius; y += _tileSize)
                {
                    // 원형 경계 체크 (타일 중심 기준)
                    float distSqr = x * x + y * y;
                    if (distSqr > (radius + halfSize) * (radius + halfSize))
                        continue;

                    var tileObj = new GameObject("Tile");
                    tileObj.transform.SetParent(parent.transform, false);
                    tileObj.transform.localPosition = new Vector3(x, y, 0f);

                    // 랜덤 회전 (0, 90, 180, 270도)
                    if (_randomRotation)
                    {
                        float rot = Random.Range(0, 4) * 90f;
                        tileObj.transform.localRotation = Quaternion.Euler(0f, 0f, rot);
                    }

                    // 랜덤 스프라이트 선택
                    var sr = tileObj.AddComponent<SpriteRenderer>();
                    sr.sprite = _groundSprites[Random.Range(0, _groundSprites.Length)];
                    sr.sortingOrder = -10;
                    sr.drawMode = SpriteDrawMode.Simple;

                    // 타일 크기 맞추기
                    float spriteWorldSize = sr.sprite.rect.width / sr.sprite.pixelsPerUnit;
                    float scale = _tileSize / spriteWorldSize;
                    tileObj.transform.localScale = new Vector3(scale, scale, 1f);

                    sr.color = Color.white;
                }
            }

            Random.state = prevState;
        }

        /// <summary>
        /// 원형 경계선 생성. LineRenderer loop.
        /// </summary>
        private void CreateBoundary()
        {
            var boundaryObj = new GameObject("MapBoundary_Line");
            boundaryObj.transform.position = Vector3.zero;

            _boundaryLine = boundaryObj.AddComponent<LineRenderer>();
            _boundaryLine.useWorldSpace = true;
            _boundaryLine.loop = true;
            _boundaryLine.sortingOrder = 5;
            _boundaryLine.startWidth = _boundaryWidth;
            _boundaryLine.endWidth = _boundaryWidth;

            _boundaryLine.material = new Material(
                Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default"));
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
