using System.Collections.Generic;
using UnityEngine;
using Core;
using Core.Data;

namespace Tunnel
{
    /// <summary>
    /// 터널 마스크 텍스처(RenderTexture)를 관리하고, 
    /// 땅을 파는(DrawHole) 및 흙을 덮는(EraseHole) 기능을 제공한다.
    /// </summary>
    public class TunnelMaskManager : MonoBehaviour
    {
        // --- Singleton ---
        public static TunnelMaskManager Instance { get; private set; }
        [Header("Settings")]
        [SerializeField] private int _textureResolution = 2048;
        [SerializeField] private Shader _brushShader;
        [SerializeField] private Shader _eraserShader;
        [Tooltip("터널 바닥 텍스처 (어두운 암석/흙)")]
        [SerializeField] private Texture _floorTexture;

        [Header("Visuals (Real-time Tuning)")]
        [Tooltip("터널 바닥 색상 틴트 (기본: 흰색)")]
        [SerializeField] private Color _floorColor = Color.white;
        [Tooltip("터널 테두리 색상")]
        [SerializeField] private Color _edgeColor = new Color(0.3f, 0.2f, 0.1f, 1f);
        
        [Space]
        [Tooltip("터널 바닥 텍스처를 월드 좌표 기준으로 매핑 (타일 경계 끊김 해결)")]
        [SerializeField] private bool _useWorldSpaceFloor = true;
        [Tooltip("터널 텍스처 반복 횟수 (월드 UV 사용 시)")]
        [SerializeField] private float _floorTiling = 10f;

        // 전역 마스크 텍스처
        private RenderTexture _maskTexture;
        private Material _brushMaterial;
        private Material _eraserMaterial;

        // 맵 정보 (좌표 변환용)
        private float _mapRadius;
        private float _mapSize; // 지름
        private float _mapOffset; // 시작점 (Bottom-Left)

        // A2 Fix: Awake에서 RT 초기화 (TunnelGenerator.Start()보다 먼저 실행 보장)
        private void Awake()
        {
            // Singleton
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            InitializeTexture();
        }

        private void Start()
        {
            UpdateShaderGlobals();
        }

        private void OnDestroy()
        {
            if (_maskTexture != null)
            {
                _maskTexture.Release();
                Destroy(_maskTexture);
            }
            // C2 Fix: 머티리얼 메모리 누수 방지
            if (_brushMaterial != null)
                Destroy(_brushMaterial);
            if (_eraserMaterial != null)
                Destroy(_eraserMaterial);

            if (Instance == this) Instance = null;
        }

        private void InitializeTexture()
        {
            if (_brushShader == null)
                _brushShader = Shader.Find("DigWar/TunnelBrush");
            if (_eraserShader == null)
                _eraserShader = Shader.Find("DigWar/TunnelEraser");

            _brushMaterial = new Material(_brushShader);
            _eraserMaterial = new Material(_eraserShader);

            // R8: 단일 채널(Red)만 필요 (메모리 절약)
            _maskTexture = new RenderTexture(_textureResolution, _textureResolution, 0, RenderTextureFormat.R8);
            _maskTexture.name = "TunnelMaskRT";
            _maskTexture.filterMode = FilterMode.Bilinear; // 부드러운 보간
            _maskTexture.wrapMode = TextureWrapMode.Clamp; // 텍스처 밖으로 나가지 않게
            _maskTexture.Create();

            // 초기화: 검정색 (안 파임)
            ClearMask();

            // 맵 크기 정보 가져오기
            if (GameManager.Instance != null && GameManager.Instance.Settings != null)
            {
                _mapRadius = GameManager.Instance.Settings.MapRadius;
            }
            else
            {
                _mapRadius = 50f; // Default
            }

            _mapSize = _mapRadius * 2f;
            _mapOffset = -_mapRadius;
        }

        public void ClearMask()
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _maskTexture;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = prev;
        }

        private void UpdateShaderGlobals()
        {
            // 지형 쉐이더가 참조할 전역 변수 설정
            Shader.SetGlobalTexture("_TunnelMask", _maskTexture);
            // MapSize: (Width, Height, OffsetX, OffsetY)
            Shader.SetGlobalVector("_MapSize", new Vector4(_mapSize, _mapSize, _mapOffset, _mapOffset));
            
            if (_floorTexture != null)
            {
                Shader.SetGlobalTexture("_FloorTex", _floorTexture);
            }

            // [New] 인스펙터 시각 설정 반영
            Shader.SetGlobalColor("_FloorColor", _floorColor);
            Shader.SetGlobalColor("_EdgeColor", _edgeColor);
            Shader.SetGlobalFloat("_UseWorldFloor", _useWorldSpaceFloor ? 1f : 0f);
            Shader.SetGlobalFloat("_FloorTiling", _floorTiling);
        }

        // O2: 배치 렌더링을 위한 큐
        private struct HoleData
        {
            public float u, v, uvRadius;
        }
        private readonly List<HoleData> _pendingHoles = new List<HoleData>(32);
        private readonly List<HoleData> _pendingErases = new List<HoleData>(16);

        /// <summary>
        /// 특정 위치에 구멍을 낸다 (큐에 추가 → LateUpdate에서 일괄 렌더링).
        /// </summary>
        public void DrawHole(Vector2 worldPos, float radius)
        {
            if (_maskTexture == null) return;

            float u = (worldPos.x - _mapOffset) / _mapSize;
            float v = (worldPos.y - _mapOffset) / _mapSize;
            float uvRadius = radius / _mapSize;

            _pendingHoles.Add(new HoleData { u = u, v = v, uvRadius = uvRadius });
        }

        /// <summary>
        /// 특정 위치의 터널을 흙으로 덮는다 (Sandworm 전용).
        /// 큐에 추가 → LateUpdate에서 일괄 렌더링.
        /// </summary>
        public void EraseHole(Vector2 worldPos, float radius)
        {
            if (_maskTexture == null) return;

            float u = (worldPos.x - _mapOffset) / _mapSize;
            float v = (worldPos.y - _mapOffset) / _mapSize;
            float uvRadius = radius / _mapSize;

            _pendingErases.Add(new HoleData { u = u, v = v, uvRadius = uvRadius });
        }

        private void LateUpdate()
        {
            if (_pendingHoles.Count > 0)
            {
                FlushHoles();
                _pendingHoles.Clear();
            }

            if (_pendingErases.Count > 0)
            {
                FlushErases();
                _pendingErases.Clear();
            }
        }

        /// <summary>
        /// 큐에 쌓인 모든 구멍을 한 번의 GL 세션에서 일괄 렌더링.
        /// </summary>
        private void FlushHoles()
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _maskTexture;

            GL.PushMatrix();
            GL.LoadOrtho();
            _brushMaterial.SetPass(0);
            GL.Begin(GL.QUADS);

            for (int i = 0; i < _pendingHoles.Count; i++)
            {
                var h = _pendingHoles[i];
                float left = h.u - h.uvRadius;
                float right = h.u + h.uvRadius;
                float top = h.v + h.uvRadius;
                float bottom = h.v - h.uvRadius;

                GL.TexCoord2(0, 0); GL.Vertex3(left, bottom, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(left, top, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(right, top, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(right, bottom, 0);
            }

            GL.End();
            GL.PopMatrix();

            RenderTexture.active = prev;
        }

        /// <summary>
        /// 큐에 쌓인 모든 '지우기(흙 덮기)'를 한 번의 GL 세션에서 일괄 렌더링.
        /// </summary>
        private void FlushErases()
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _maskTexture;

            GL.PushMatrix();
            GL.LoadOrtho();
            _eraserMaterial.SetPass(0);
            GL.Begin(GL.QUADS);

            for (int i = 0; i < _pendingErases.Count; i++)
            {
                var h = _pendingErases[i];
                float left  = h.u - h.uvRadius;
                float right = h.u + h.uvRadius;
                float top   = h.v + h.uvRadius;
                float bottom = h.v - h.uvRadius;

                GL.TexCoord2(0, 0); GL.Vertex3(left, bottom, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(left, top, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(right, top, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(right, bottom, 0);
            }

            GL.End();
            GL.PopMatrix();

            RenderTexture.active = prev;
        }
    }
}
