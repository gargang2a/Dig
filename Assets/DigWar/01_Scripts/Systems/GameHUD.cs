using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using Core;

namespace Systems
{
    /// <summary>
    /// 寃뚯엫 HUD: 醫뚯륫 ?곷떒 由щ뜑蹂대뱶, ?곗륫 ?곷떒 ?먯닔, ?곗륫 ?섎떒 誘몃땲留?
    /// Canvas??誘몃━ 諛곗튂??TMP/Image瑜?SerializeField濡?李몄“?쒕떎.
    /// </summary>
    public class GameHUD : MonoBehaviour
    {
        [Header("?먯닔 (?곗륫 ?곷떒)")]
        [SerializeField] private TMP_Text _scoreText;

        [Header("由щ뜑蹂대뱶 (醫뚯륫 ?곷떒)")]
        [SerializeField] private TMP_Text[] _rankNames = new TMP_Text[8];  // "1. ?대쫫"
        [SerializeField] private TMP_Text[] _rankScores = new TMP_Text[8]; // "20950" (?곗륫?뺣젹)
        [SerializeField] private Image[] _rankDots = new Image[8];         // ?됱긽 ???꾩씠肄?

        [Header("誘몃땲留?(?곗륫 ?섎떒)")]
        [SerializeField] private RectTransform _minimapRoot;
        public RectTransform MinimapRoot => _minimapRoot;
        [SerializeField] private Image _playerDot;
        [SerializeField] private Image[] _botDots = new Image[8];
        [SerializeField] private float _minimapUsableRadius = 65f;
        public float MinimapUsableRadius => _minimapUsableRadius;

        // ?됱긽
        private static readonly Color COLOR_FIRST = new Color(1f, 0.65f, 0.2f);   // 二쇳솴 (1??
        private static readonly Color COLOR_PLAYER = Color.white;                   // 蹂몄씤
        private static readonly Color COLOR_NORMAL = new Color(0.75f, 0.75f, 0.75f); // ?쇰컲
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

        // ?대? ?곗씠??
        private readonly List<LeaderboardEntry> _entries = new List<LeaderboardEntry>(16);
        private float _updateTimer;
        private const float UPDATE_INTERVAL = 0.5f;
        private Player.PlayerController _cachedLocalPlayer;
        private float _nextLocalPlayerLookupAt;

        private static readonly string[] BOT_NAMES = Network.NetworkBot.BOT_NAMES;
        private MinimapRenderer _minimapRenderer;

        private struct LeaderboardEntry
        {
            public string Name;
            public float Score;
            public Color DotColor;
            public bool IsPlayer;
        }

        private void Update()
        {
            // ?먯닔 留??꾨젅??
            if (_scoreText != null && GameManager.Instance != null)
                _scoreText.text = $"{GameManager.Instance.CurrentScore:N0}";

            // 由щ뜑蹂대뱶 + 誘몃땲留?二쇨린??
            _updateTimer -= Time.deltaTime;
            if (_updateTimer <= 0f)
            {
                _updateTimer = UPDATE_INTERVAL;
                UpdateLeaderboard();
                RefreshLeaderboardUI();
                if (_minimapRenderer == null || !_minimapRenderer.enabled)
                    RefreshMinimap();
            }
        }

        private void Start()
        {
            // 미니맵 렌더러 자동 부착
            _minimapRenderer = GetComponent<MinimapRenderer>();
            if (_minimapRenderer == null)
                _minimapRenderer = gameObject.AddComponent<MinimapRenderer>();

            // MainMenuUI???꾩슜 UI ?ㅻ툕?앺듃?먯꽌 李몄“媛 ?곌껐???곹깭濡??숈옉?댁빞 ?쒕떎.
            // HUD ?ㅻ툕?앺듃???고??꾩쑝濡?遺李⑺븯硫?李몄“ ?꾨씫 寃쎄퀬? 以묐났 ?대깽??援щ룆??諛쒖깮?????덈떎.
            if (FindObjectOfType<MainMenuUI>() == null)
                Debug.LogWarning("[GameHUD] MainMenuUI is missing in this scene. Start menu flow will be unavailable.");

            if (_minimapRenderer != null && _minimapRenderer.enabled)
            {
                if (_playerDot != null)
                    _playerDot.gameObject.SetActive(false);

                for (int i = 0; i < _botDots.Length; i++)
                {
                    if (_botDots[i] != null)
                        _botDots[i].gameObject.SetActive(false);
                }
            }
        }


        // ===== LEADERBOARD =====
        private void UpdateLeaderboard()
        {
            _entries.Clear();

            // 1) ?ㅽ듃?뚰겕 ?뚮젅?댁뼱??(濡쒖뺄 ?뚮젅?댁뼱 ?ы븿)
            bool hasNetworkPlayers = Network.NetworkPlayer.ActivePlayers.Count > 0;
            if (hasNetworkPlayers)
            {
                foreach (Network.NetworkPlayer np in Network.NetworkPlayer.ActivePlayers)
                {
                    if (np == null) continue;
                    bool isLocal = np.isLocalPlayer;
                    // 濡쒖뺄 ?뚮젅?댁뼱: GameManager???ㅼ떆媛??먯닔 ?ъ슜
                    // 由щえ???뚮젅?댁뼱: SyncVar Score ?ъ슜
                    float score = isLocal && GameManager.Instance != null
                        ? GameManager.Instance.CurrentScore
                        : np.Score;

                    string name = string.IsNullOrEmpty(np.PlayerName)
                        ? (isLocal ? GameManager.Instance?.PlayerName ?? "Player" : "Player")
                        : np.PlayerName;

                    _entries.Add(new LeaderboardEntry
                    {
                        Name = name,
                        Score = score,
                        DotColor = isLocal ? Color.white : GetRemotePlayerDotColor(np),
                        IsPlayer = isLocal
                    });
                }
            }
            else
            {
                // Mirror 誘몄궗??(?깃??뚮젅???명솚)
                float playerScore = GameManager.Instance != null
                    ? GameManager.Instance.CurrentScore : 0f;
                _entries.Add(new LeaderboardEntry
                {
                    Name = GameManager.Instance?.PlayerName ?? "Player",
                    Score = playerScore,
                    DotColor = Color.white, IsPlayer = true
                });
            }

            // 2) AI 遊?            // 2) AI 遊???NetworkBot.Score(SyncVar)濡??숆린?붾맂 ?먯닔 ?ъ슜
            foreach (Network.NetworkBot bot in Network.NetworkBot.ActiveBots)
            {
                if (bot == null) continue;

                var sr = bot.GetComponentInChildren<SpriteRenderer>();
                _entries.Add(new LeaderboardEntry
                {
                    Name = BOT_NAMES[bot.BotIndex % BOT_NAMES.Length],
                    Score = bot.Score,
                    DotColor = sr != null ? sr.color : Color.gray,
                    IsPlayer = false
                });
            }

            _entries.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        private void RefreshLeaderboardUI()
        {
            int count = Mathf.Min(_entries.Count, _rankNames.Length);

            for (int i = 0; i < _rankNames.Length; i++)
            {
                bool active = i < count;

                // ?대쫫 ?쒖떆
                if (_rankNames[i] != null)
                {
                    _rankNames[i].gameObject.SetActive(active);
                    if (active)
                    {
                        var e = _entries[i];
                        _rankNames[i].text = $"{i + 1}. {e.Name}";

                        // ?됱긽: 1??二쇳솴, 蹂몄씤=?? ?섎㉧吏=?뚯깋
                        Color textColor = i == 0 ? COLOR_FIRST
                            : e.IsPlayer ? COLOR_PLAYER
                            : COLOR_NORMAL;
                        _rankNames[i].color = textColor;
                        _rankNames[i].fontStyle = (i == 0 || e.IsPlayer)
                            ? FontStyles.Bold : FontStyles.Normal;
                    }
                }

                // ?먯닔 ?쒖떆 (?곗륫 ?뺣젹)
                if (i < _rankScores.Length && _rankScores[i] != null)
                {
                    _rankScores[i].gameObject.SetActive(active);
                    if (active)
                    {
                        _rankScores[i].text = $"{_entries[i].Score:N0}";
                        Color scoreColor = i == 0 ? COLOR_FIRST
                            : _entries[i].IsPlayer ? COLOR_PLAYER
                            : COLOR_NORMAL;
                        _rankScores[i].color = scoreColor;
                        _rankScores[i].fontStyle = (i == 0 || _entries[i].IsPlayer)
                            ? FontStyles.Bold : FontStyles.Normal;
                    }
                }

                // ?됱긽 ??
                if (i < _rankDots.Length && _rankDots[i] != null)
                {
                    _rankDots[i].gameObject.SetActive(active);
                    if (active)
                        _rankDots[i].color = _entries[i].DotColor;
                }
            }
        }

        // ===== MINIMAP =====
        private void RefreshMinimap()
        {
            if (GameManager.Instance == null || _minimapRoot == null) return;
            float mapRadius = GameManager.Instance.Settings.MapRadius;

            // 濡쒖뺄 ?뚮젅?댁뼱 李얘린
            bool foundLocal = false;
            if (Network.NetworkPlayer.ActivePlayers.Count > 0)
            {
                foreach (Network.NetworkPlayer np in Network.NetworkPlayer.ActivePlayers)
                {
                    if (np == null) continue;
                    if (np.isLocalPlayer && _playerDot != null)
                    {
                        _playerDot.gameObject.SetActive(true);
                        _playerDot.color = Color.green;
                        _playerDot.rectTransform.sizeDelta = new Vector2(12f, 12f);
                        SetDotPos(_playerDot, np.transform.position, mapRadius);
                        foundLocal = true;
                        break;
                    }
                }
            }

            // ?ㅽ듃?뚰겕 ?뚮젅?댁뼱瑜??꾩쭅 紐?李얠쑝硫?PlayerController ?대갚 (?깃??뚮젅???꾩슜)
            if (!foundLocal && _playerDot != null)
            {
                // 멀티플레이 환경에서는 NetworkPlayer 스폰 시점까지 대기
                if (Mirror.NetworkManager.singleton == null)
                {
                    Player.PlayerController localPc = ResolveLocalPlayerController();
                    if (localPc != null)
                    {
                        _playerDot.gameObject.SetActive(true);
                        _playerDot.color = Color.green;
                        _playerDot.rectTransform.sizeDelta = new Vector2(12f, 12f);
                        SetDotPos(_playerDot, localPc.transform.position, mapRadius);
                        foundLocal = true;
                    }
                    else
                    {
                        _playerDot.gameObject.SetActive(false);
                    }
                }
                else
                {
                    _playerDot.gameObject.SetActive(false);
                }
            }

            // botDots瑜?由щえ???뚮젅?댁뼱 + 遊뉗뿉 諛곕텇
            int dotIndex = 0;

            // 由щえ???뚮젅?댁뼱 (?뚮젅?댁뼱蹂?怨좎쑀 ?됱긽)
            foreach (Network.NetworkPlayer np in Network.NetworkPlayer.ActivePlayers)
            {
                if (np == null) continue;
                if (!np.isLocalPlayer && dotIndex < _botDots.Length)
                {
                    if (_botDots[dotIndex] != null)
                    {
                        _botDots[dotIndex].gameObject.SetActive(true);
                        _botDots[dotIndex].color = GetRemotePlayerDotColor(np);
                        _botDots[dotIndex].rectTransform.sizeDelta = new Vector2(10f, 10f);
                        SetDotPos(_botDots[dotIndex], np.transform.position, mapRadius);
                    }
                    dotIndex++;
                }
            }

            // 遊?(媛곸옄 ?됱긽)
            foreach (Network.NetworkBot bot in Network.NetworkBot.ActiveBots)
            {
                if (bot == null) continue;
                if (dotIndex >= _botDots.Length) break;

                if (_botDots[dotIndex] != null)
                {
                    _botDots[dotIndex].gameObject.SetActive(true);
                    var sr = bot.GetComponentInChildren<SpriteRenderer>();
                    _botDots[dotIndex].color = sr != null ? sr.color : Color.red;
                    SetDotPos(_botDots[dotIndex], bot.transform.position, mapRadius);
                }
                dotIndex++;
            }

            // ?섎㉧吏 鍮꾪솢??
            for (int i = dotIndex; i < _botDots.Length; i++)
            {
                if (_botDots[i] != null)
                    _botDots[i].gameObject.SetActive(false);
            }
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

        private void SetDotPos(Image dot, Vector3 worldPos, float mapRadius)
        {
            float safeMapRadius = Mathf.Max(1f, mapRadius);
            Vector2 normalized = new Vector2(worldPos.x / safeMapRadius, worldPos.y / safeMapRadius);
            if (normalized.sqrMagnitude > 1f)
                normalized = normalized.normalized;

            float dynamicRadius = _minimapUsableRadius;
            if (_minimapRoot != null)
            {
                float rootRadius = Mathf.Min(_minimapRoot.rect.width, _minimapRoot.rect.height) * 0.5f * 0.95f;
                if (rootRadius > 1f)
                    dynamicRadius = rootRadius;
            }

            dot.rectTransform.anchoredPosition = normalized * dynamicRadius;
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
    }
}


