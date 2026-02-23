using UnityEngine;
using UnityEngine.UI;
using Core;
using Tunnel;

namespace Systems
{
    /// <summary>
    /// 개선된 미니맵 렌더러
    /// - TunnelMaskManager RenderTexture를 배경으로 직접 표시(터널 변경 실시간 반영)
    /// - 본인(초록), 원격 플레이어(고유색), 봇(팀 색상), 샌드웜(주황) 위치를 도트로 표시
    /// - CPU 텍스처 스캔 제거 후 GPU RenderTexture 직접 참조로 성능을 안정화
    ///
    /// UI 계층: Minimap Bg -> [TunnelMaskImage] -> [EntityDots] -> Ring
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
        private const float MIN_MAP_RADIUS = 1f;
        private const float MINIMAP_EDGE_MARGIN = 0.95f;

        // Entity dot UI elements
        private RectTransform _playerDotRT;
        private Image _playerDotImage;
        private RectTransform[] _botDotRTs;
        private Image[] _botDotImages;
        private RectTransform[] _sandwormDotRTs; // 머리 + 모든 세그먼트

        private static readonly Color[] REMOTE_PLAYER_DOT_COLORS =
        {
            new Color(1f, 0.55f, 0.25f),   // orange
            new Color(1f, 0.82f, 0.25f),   // amber
            new Color(0.35f, 0.78f, 1f),   // sky blue
            new Color(0.9f, 0.48f, 1f),    // magenta
            new Color(1f, 0.55f, 0.75f),   // pink
            new Color(0.55f, 0.65f, 1f),   // indigo
            new Color(1f, 0.4f, 0.4f),     // red
            new Color(0.55f, 1f, 0.75f),   // mint
        };

        // 도트 컨테이너
        private RectTransform _dotsContainer;
        private Player.PlayerController _cachedLocalPlayer;
        private float _nextLocalPlayerLookupAt;

        private void Start()
        {
            SetupUI();
            RefreshRuntimeCache();
        }

        private void LateUpdate()
        {
            if (_dotsContainer == null) return;

            RefreshRuntimeCache();
            UpdatePlayerDot();
            UpdateBotDots();
            UpdateSandwormDots();
        }

        // ===== UI 초기화 =====
        private void SetupUI()
        {
            var hud = GetComponent<GameHUD>();
            if (hud == null || hud.MinimapRoot == null) return;

            _minimapRoot = hud.MinimapRoot;
            _usableRadius = Mathf.Max(1f, hud.MinimapUsableRadius);

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
            _playerDotRT = CreateDot("PlayerDot", _playerDotColor, _playerDotSize, out _playerDotImage);


            // 4) 봇 도트 (동적 수량 변경 대응)
            _botDotRTs = new RectTransform[0];
            _botDotImages = new Image[0];

            // 5) 샌드웜 도트는 UpdateSandwormDots에서 자동 생성
        }

        private void RefreshRuntimeCache()
        {
            if (GameManager.Instance?.Settings != null)
                _mapRadius = Mathf.Max(MIN_MAP_RADIUS, GameManager.Instance.Settings.MapRadius);
            else
                _mapRadius = Mathf.Max(MIN_MAP_RADIUS, _mapRadius);

            if (_minimapRoot != null)
            {
                // 웹 해상도/Canvas 스케일 변화 대응
                float dynamicRadius = Mathf.Min(_minimapRoot.rect.width, _minimapRoot.rect.height) * 0.5f * MINIMAP_EDGE_MARGIN;
                if (dynamicRadius > 1f)
                    _usableRadius = dynamicRadius;
            }
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
        private RectTransform CreateDot(string name, Color color, float size, out Image image)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(_dotsContainer, false);

            image = obj.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;

            var rt = image.rectTransform;
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = Vector2.zero;

            return rt;
        }

        // ===== 도트 위치 업데이트 =====
        private void UpdatePlayerDot()
        {
            if (_playerDotRT == null) return;

            Player.PlayerController player = ResolveLocalPlayerController();
            if (player == null)
            {
                _playerDotRT.gameObject.SetActive(false);
                return;
            }

            _playerDotRT.gameObject.SetActive(true);
            if (_playerDotImage != null)
                _playerDotImage.color = _playerDotColor;
            _playerDotRT.anchoredPosition = WorldToMinimap(player.transform.position);
        }

        private void UpdateBotDots()
        {
            if (_botDotRTs == null || _botDotImages == null) return;

            int remotePlayerCount = 0;
            foreach (Network.NetworkPlayer player in Network.NetworkPlayer.ActivePlayers)
            {
                if (player != null && !player.isLocalPlayer)
                    remotePlayerCount++;
            }

            int networkBotCount = 0;
            foreach (Network.NetworkBot bot in Network.NetworkBot.ActiveBots)
            {
                if (bot != null)
                    networkBotCount++;
            }

            EnsureBotDotCapacity(remotePlayerCount + networkBotCount);

            int index = 0;

            // 1) 원격 플레이어 (플레이어별 고유색)
            foreach (Network.NetworkPlayer player in Network.NetworkPlayer.ActivePlayers)
            {
                if (player == null || player.isLocalPlayer) continue;
                if (index >= _botDotRTs.Length) break;

                RectTransform dotRt = _botDotRTs[index];
                Image dotImage = _botDotImages[index];
                dotRt.gameObject.SetActive(true);
                dotRt.sizeDelta = new Vector2(_botDotSize + 1f, _botDotSize + 1f);
                dotRt.anchoredPosition = WorldToMinimap(player.transform.position);
                if (dotImage != null)
                    dotImage.color = GetRemotePlayerDotColor(player);

                index++;
            }

            // 2) 네트워크 봇 (봇 프리셋 색상)
            foreach (Network.NetworkBot bot in Network.NetworkBot.ActiveBots)
            {
                if (bot == null) continue;
                if (index >= _botDotRTs.Length) break;

                RectTransform dotRt = _botDotRTs[index];
                Image dotImage = _botDotImages[index];
                dotRt.gameObject.SetActive(true);
                dotRt.sizeDelta = new Vector2(_botDotSize, _botDotSize);
                dotRt.anchoredPosition = WorldToMinimap(bot.transform.position);
                if (dotImage != null)
                    dotImage.color = GetNetworkBotDotColor(bot);

                index++;
            }

            for (int i = index; i < _botDotRTs.Length; i++)
                _botDotRTs[i].gameObject.SetActive(false);
        }

        private void UpdateSandwormDots()
        {
            if (World.Sandworm.ActiveWorms.Count == 0)
            {
                SetDotArrayActive(_sandwormDotRTs, false);
                return;
            }

            // 필요한 전체 도트 수 계산 (각 웜의 머리 + 세그먼트)
            int needed = 0;
            foreach (World.Sandworm worm in World.Sandworm.ActiveWorms)
            {
                if (worm == null) continue;
                needed += 1 + worm.Segments.Count;
            }

            if (needed <= 0)
            {
                SetDotArrayActive(_sandwormDotRTs, false);
                return;
            }

            // 도트 수가 맞지 않으면 재생성
            if (_sandwormDotRTs == null || _sandwormDotRTs.Length != needed)
            {
                if (_sandwormDotRTs != null)
                    for (int i = 0; i < _sandwormDotRTs.Length; i++)
                        if (_sandwormDotRTs[i] != null) Destroy(_sandwormDotRTs[i].gameObject);

                _sandwormDotRTs = new RectTransform[needed];
                int idx = 0;
                int wormIndex = 0;
                foreach (World.Sandworm worm in World.Sandworm.ActiveWorms)
                {
                    if (worm == null) continue;

                    int segCount = 1 + worm.Segments.Count;
                    for (int i = 0; i < segCount; i++)
                    {
                        float t = (float)i / segCount;
                        float size = Mathf.Lerp(_sandwormDotSize, _sandwormDotSize * 0.4f, t);
                        Color c = Color.Lerp(_sandwormDotColor, _sandwormDotColor * 0.6f, t);
                        c.a = Mathf.Lerp(1f, 0.5f, t);
                        _sandwormDotRTs[idx++] = CreateDot($"WormDot_{wormIndex}_{i}", c, size, out _);
                    }
                    wormIndex++;
                }
                Debug.Log($"[Minimap] 샌드웜 도트 {needed}개 생성");
            }

            // 위치 업데이트
            int dotIdx = 0;
            foreach (World.Sandworm worm in World.Sandworm.ActiveWorms)
            {
                if (worm == null) continue;

                // 머리
                if (dotIdx < _sandwormDotRTs.Length)
                    _sandwormDotRTs[dotIdx++].anchoredPosition = WorldToMinimap(worm.transform.position);

                // 세그먼트
                for (int i = 0; i < worm.Segments.Count; i++)
                {
                    if (dotIdx >= _sandwormDotRTs.Length) break;
                    if (worm.Segments[i] == null) { dotIdx++; continue; }
                    _sandwormDotRTs[dotIdx++].anchoredPosition = WorldToMinimap(worm.Segments[i].position);
                }
            }
        }

        private void EnsureBotDotCapacity(int requiredCount)
        {
            if (requiredCount <= 0)
            {
                if (_botDotRTs == null) _botDotRTs = new RectTransform[0];
                if (_botDotImages == null) _botDotImages = new Image[0];
                return;
            }

            if (_botDotRTs == null)
                _botDotRTs = new RectTransform[0];
            if (_botDotImages == null)
                _botDotImages = new Image[0];

            if (_botDotRTs.Length >= requiredCount) return;

            int oldLength = _botDotRTs.Length;
            System.Array.Resize(ref _botDotRTs, requiredCount);
            System.Array.Resize(ref _botDotImages, requiredCount);
            for (int i = oldLength; i < requiredCount; i++)
                _botDotRTs[i] = CreateDot($"BotDot_{i}", _botDotColor, _botDotSize, out _botDotImages[i]);
        }

        private Player.PlayerController ResolveLocalPlayerController()
        {
            Network.NetworkPlayer localNetworkPlayer = Network.NetworkPlayer.LocalPlayer;
            if (localNetworkPlayer != null)
            {
                var networkLocalController = localNetworkPlayer.GetComponent<Player.PlayerController>();
                if (networkLocalController != null)
                    return networkLocalController;
            }

            Player.PlayerController localController = Player.PlayerController.LocalController;
            if (localController != null)
                return localController;

            if (_cachedLocalPlayer != null)
                return _cachedLocalPlayer;

            if (Time.unscaledTime < _nextLocalPlayerLookupAt)
                return null;

            _nextLocalPlayerLookupAt = Time.unscaledTime + 1f;
            _cachedLocalPlayer = FindObjectOfType<Player.PlayerController>();
            return _cachedLocalPlayer;
        }

        private static void SetDotArrayActive(RectTransform[] dots, bool active)
        {
            if (dots == null) return;
            for (int i = 0; i < dots.Length; i++)
            {
                if (dots[i] != null)
                    dots[i].gameObject.SetActive(active);
            }
        }

        private static Color GetRemotePlayerDotColor(Network.NetworkPlayer player)
        {
            if (player == null) return Color.yellow;

            uint stableId = player.netId;
            if (stableId == 0 && player.netIdentity != null)
                stableId = player.netIdentity.netId;

            int colorIndex = (int)(stableId % (uint)REMOTE_PLAYER_DOT_COLORS.Length);
            Color color = REMOTE_PLAYER_DOT_COLORS[colorIndex];
            color.a = 1f;
            return color;
        }

        private static Color GetNetworkBotDotColor(Network.NetworkBot bot)
        {
            if (bot == null) return Color.red;

            int index = Mathf.Abs(bot.BotIndex) % Network.NetworkBot.BOT_COLORS.Length;
            Color color = Network.NetworkBot.BOT_COLORS[index];
            color.a = 0.9f;
            return color;
        }

        // ===== 좌표 변환 =====
        private Vector2 WorldToMinimap(Vector3 worldPos)
        {
            float radius = Mathf.Max(MIN_MAP_RADIUS, _mapRadius);
            Vector2 normalized = new Vector2(worldPos.x / radius, worldPos.y / radius);

            if (normalized.sqrMagnitude > 1f)
                normalized = normalized.normalized;

            return normalized * _usableRadius;
        }

        private void OnDestroy()
        {
        }
    }
}


