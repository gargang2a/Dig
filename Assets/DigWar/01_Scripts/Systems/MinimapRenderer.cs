using UnityEngine;
using UnityEngine.UI;
using Core;
using Tunnel;

namespace Systems
{
    /// <summary>
    /// 개선된 미니맵 렌더러.
    /// - TunnelMaskManager의 RenderTexture를 배경으로 직접 표시 (터널 실시간 반영)
    /// - 플레이어(초록), 봇(빨강), 샌드웜(주황) 위치를 도트로 표시
    /// - CPU 텍스처 페인팅 제거 → GPU RT 직접 참조로 성능 대폭 향상
    ///
    /// UI 계층: Minimap Bg → [TunnelMaskImage] → [EntityDots] → Ring
    /// </summary>
    public class MinimapRenderer : MonoBehaviour
    {
        [Header("미니맵 설정")]
        [SerializeField] private Color _playerDotColor = new Color(0.2f, 1f, 0.3f, 1f);
        [SerializeField] private Color _botDotColor = new Color(1f, 0.3f, 0.3f, 0.8f);
        [SerializeField] private Color _sandwormDotColor = new Color(1f, 0.6f, 0.1f, 1f);
        [SerializeField] private float _playerDotSize = 8f;
        [SerializeField] private float _botDotSize = 5f;
        [SerializeField] private float _sandwormDotSize = 10f;

        private RawImage _tunnelMaskImage;
        private RectTransform _minimapRoot;
        private float _mapRadius;
        private float _usableRadius;

        // Entity dot UI elements
        private RectTransform _playerDotRT;
        private RectTransform[] _botDotRTs;
        private RectTransform[] _sandwormDotRTs; // 머리 + 모든 마디

        // 도트 컨테이너
        private RectTransform _dotsContainer;

        private void Start()
        {
            if (GameManager.Instance?.Settings == null) return;
            _mapRadius = GameManager.Instance.Settings.MapRadius;

            SetupUI();
        }

        private void LateUpdate()
        {
            if (_dotsContainer == null) return;

            UpdatePlayerDot();
            UpdateBotDots();
            UpdateSandwormDots();
        }

        // ===== UI 초기화 =====
        private void SetupUI()
        {
            var hud = GetComponent<GameHUD>();
            if (hud == null) hud = FindObjectOfType<GameHUD>();
            if (hud == null || hud.MinimapRoot == null) return;

            _minimapRoot = hud.MinimapRoot;
            _usableRadius = hud.MinimapUsableRadius;

            // 1) 터널 마스크 RenderTexture를 미니맵 배경으로 표시
            SetupTunnelMaskDisplay();

            // 2) 도트 컨테이너 생성
            var dotsObj = new GameObject("MinimapDots");
            dotsObj.transform.SetParent(_minimapRoot, false);
            dotsObj.transform.SetAsLastSibling();
            _dotsContainer = dotsObj.AddComponent<RectTransform>();
            _dotsContainer.anchorMin = new Vector2(0.5f, 0.5f);
            _dotsContainer.anchorMax = new Vector2(0.5f, 0.5f);
            _dotsContainer.sizeDelta = Vector2.zero;

            // 3) 플레이어 도트
            _playerDotRT = CreateDot("PlayerDot", _playerDotColor, _playerDotSize);


            // 4) 봇 도트 (Start 시점에 없으면 LateUpdate에서 지연 초기화)
            var bots = FindObjectsOfType<Player.AIController>();
            _botDotRTs = new RectTransform[bots.Length];
            for (int i = 0; i < bots.Length; i++)
            {
                _botDotRTs[i] = CreateDot($"BotDot_{i}", _botDotColor, _botDotSize);
            }

            // 5) 샌드웜 도트 → UpdateSandwormDots에서 자동 생성
        }

        private void SetupTunnelMaskDisplay()
        {
            if (TunnelMaskManager.Instance == null) return;

            var maskRT = Shader.GetGlobalTexture("_TunnelMask");
            if (maskRT == null) return;

            var obj = new GameObject("TunnelMaskView");
            obj.transform.SetParent(_minimapRoot, false);
            obj.transform.SetSiblingIndex(1);

            _tunnelMaskImage = obj.AddComponent<RawImage>();
            _tunnelMaskImage.texture = maskRT;
            _tunnelMaskImage.color = new Color(0.3f, 0.25f, 0.15f, 0.6f);
            _tunnelMaskImage.raycastTarget = false;

            var rt = _tunnelMaskImage.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var mask = _minimapRoot.GetComponent<Mask>();
            if (mask == null)
            {
                mask = _minimapRoot.gameObject.AddComponent<Mask>();
                mask.showMaskGraphic = true;
            }
        }

        // ===== 도트 생성 =====
        private RectTransform CreateDot(string name, Color color, float size)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(_dotsContainer, false);

            var img = obj.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;

            var rt = img.rectTransform;
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = Vector2.zero;

            return rt;
        }

        // ===== 도트 위치 업데이트 =====
        private void UpdatePlayerDot()
        {
            if (_playerDotRT == null) return;

            var player = FindObjectOfType<Player.PlayerController>();
            if (player == null)
            {
                _playerDotRT.gameObject.SetActive(false);
                return;
            }

            _playerDotRT.gameObject.SetActive(true);
            _playerDotRT.anchoredPosition = WorldToMinimap(player.transform.position);
        }

        private void UpdateBotDots()
        {
            if (_botDotRTs == null) return;

            var bots = FindObjectsOfType<Player.AIController>();
            for (int i = 0; i < _botDotRTs.Length; i++)
            {
                if (i < bots.Length && bots[i] != null)
                {
                    _botDotRTs[i].gameObject.SetActive(true);
                    _botDotRTs[i].anchoredPosition = WorldToMinimap(bots[i].transform.position);
                }
                else
                {
                    _botDotRTs[i].gameObject.SetActive(false);
                }
            }
        }

        private void UpdateSandwormDots()
        {
            var worms = FindObjectsOfType<World.Sandworm>();
            if (worms.Length == 0) return;

            // 필요한 총 도트 수 계산 (각 벌레의 머리 + 마디)
            int needed = 0;
            for (int w = 0; w < worms.Length; w++)
                needed += 1 + worms[w].Segments.Count;

            // 도트 수가 맞지 않으면 (재)생성
            if (_sandwormDotRTs == null || _sandwormDotRTs.Length != needed)
            {
                if (_sandwormDotRTs != null)
                    for (int i = 0; i < _sandwormDotRTs.Length; i++)
                        if (_sandwormDotRTs[i] != null) Destroy(_sandwormDotRTs[i].gameObject);

                _sandwormDotRTs = new RectTransform[needed];
                int idx = 0;
                for (int w = 0; w < worms.Length; w++)
                {
                    int segCount = 1 + worms[w].Segments.Count;
                    for (int i = 0; i < segCount; i++)
                    {
                        float t = (float)i / segCount;
                        float size = Mathf.Lerp(_sandwormDotSize, _sandwormDotSize * 0.4f, t);
                        Color c = Color.Lerp(_sandwormDotColor, _sandwormDotColor * 0.6f, t);
                        c.a = Mathf.Lerp(1f, 0.5f, t);
                        _sandwormDotRTs[idx++] = CreateDot($"WormDot_{w}_{i}", c, size);
                    }
                }
                Debug.Log($"[Minimap] 샌드웜 {worms.Length}마리, 도트 {needed}개 생성");
            }

            // 위치 업데이트
            int dotIdx = 0;
            for (int w = 0; w < worms.Length; w++)
            {
                var worm = worms[w];
                // 머리
                if (dotIdx < _sandwormDotRTs.Length)
                    _sandwormDotRTs[dotIdx++].anchoredPosition = WorldToMinimap(worm.transform.position);

                // 마디들
                for (int i = 0; i < worm.Segments.Count; i++)
                {
                    if (dotIdx >= _sandwormDotRTs.Length) break;
                    if (worm.Segments[i] == null) { dotIdx++; continue; }
                    _sandwormDotRTs[dotIdx++].anchoredPosition = WorldToMinimap(worm.Segments[i].position);
                }
            }
        }

        // ===== 좌표 변환 =====
        private Vector2 WorldToMinimap(Vector3 worldPos)
        {
            float x = worldPos.x / _mapRadius * _usableRadius;
            float y = worldPos.y / _mapRadius * _usableRadius;
            return new Vector2(x, y);
        }

        private void OnDestroy()
        {
        }
    }
}
