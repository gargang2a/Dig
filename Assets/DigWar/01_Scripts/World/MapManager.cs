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
        [Tooltip("맵 바깥쪽으로 타일을 얼마나 더 깔 것인가 (비율)")]
        [SerializeField] private float _paddingRatio = 1.2f;

        [Header("경계선")]
        [SerializeField] private Color _boundaryColor = new Color(1f, 0.2f, 0.2f, 0.8f);
        [SerializeField] private float _boundaryWidth = 1.0f; // 경계선 두께 증가
        [SerializeField] private Color _outsideZoneColor = new Color(0.2f, 0.05f, 0.05f, 1f); // 어두운 붉은색 배경

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

            // 카메라 배경색 변경 (PUBG 자기장 느낌)
            if (Camera.main != null)
            {
                Camera.main.backgroundColor = _outsideZoneColor;
            }

            CreateGround();
            CreateBoundary();
            CreateZoneOverlay();
        }

        /// <summary>
        /// 개별 SpriteRenderer 타일을 랜덤 배치하여 자연스러운 지형 생성.
        /// 원형 맵 범위 + 패딩 영역까지 배치한다.
        /// </summary>
        private void CreateGround()
        {
            if (_groundSprites == null || _groundSprites.Length == 0)
            {
                Debug.LogWarning("[MapManager] Ground Sprites 배열이 비어있습니다.");
                return;
            }

            float radius = _settings.MapRadius;
            float maxRadius = radius * _paddingRatio; // 패딩 포함 최대 반지름
            float halfSize = _tileSize * 0.5f;

            // 타일 부모 오브젝트
            var parent = new GameObject("GroundTiles");
            parent.transform.position = new Vector3(0f, 0f, 1f);

            // 시드 고정 (매번 동일한 맵 레이아웃)
            Random.State prevState = Random.state;
            Random.InitState(42);

            // 그리드 순회, 확장된 범위까지 체크
            for (float x = -maxRadius; x <= maxRadius; x += _tileSize)
            {
                for (float y = -maxRadius; y <= maxRadius; y += _tileSize)
                {
                    // 원형 경계 체크 (타일 중심 기준)
                    // maxRadius 밖으로 나가는 것만 제외
                    float distSqr = x * x + y * y;
                    if (distSqr > (maxRadius + halfSize) * (maxRadius + halfSize))
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

                    // 타일 색상은 이제 Overlay가 담당하므로 항상 흰색
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

        /// <summary>
        /// 맵 바깥쪽 영역을 덮는 도넛 모양의 반투명 메쉬 생성 (스무스한 자기장 효과)
        /// </summary>
        private void CreateZoneOverlay()
        {
            float innerRadius = _settings.MapRadius;
            float outerRadius = innerRadius * 2.5f; // 화면 밖까지 충분히 덮을 크기
            int segments = _settings.BoundarySegments;

            Mesh mesh = new Mesh();
            mesh.name = "ZoneOverlayMesh";

            // 정점: (segments + 1) * 2개 (안쪽 링 + 바깥쪽 링, 닫힌 루프 위해 +1)
            Vector3[] vertices = new Vector3[(segments + 1) * 2];
            int[] triangles = new int[segments * 6];

            for (int i = 0; i <= segments; i++)
            {
                float angle = (2f * Mathf.PI * i) / segments;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                // 안쪽 원 (경계선)
                vertices[i * 2] = new Vector3(cos * innerRadius, sin * innerRadius, 0f);
                // 바깥쪽 원 (먼 배경)
                vertices[i * 2 + 1] = new Vector3(cos * outerRadius, sin * outerRadius, 0f);

                if (i < segments)
                {
                    int vertIndex = i * 2;
                    int triIndex = i * 6;

                    // Quad (2 Triangles)
                    // 0-1-2, 2-1-3 pattern
                    triangles[triIndex] = vertIndex;
                    triangles[triIndex + 1] = vertIndex + 1;
                    triangles[triIndex + 2] = vertIndex + 2;

                    triangles[triIndex + 3] = vertIndex + 2;
                    triangles[triIndex + 4] = vertIndex + 1;
                    triangles[triIndex + 5] = vertIndex + 3;
                }
            }

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();

            // Overlay 오브젝트 생성
            var overlayObj = new GameObject("ZoneOverlay");
            overlayObj.transform.position = new Vector3(0f, 0f, -0.1f); // 타일보다 위, 플레이어보다 아래
            
            var mf = overlayObj.AddComponent<MeshFilter>();
            mf.mesh = mesh;

            var mr = overlayObj.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default"));
            mr.material.color = _outsideZoneColor; // 반투명 색상 적용
            mr.sortingOrder = 1; // 타일(-10)보다 위
        }
    }
}
