using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using Core;

namespace Systems
{
    /// <summary>
    /// 인게임 HUD.
    /// - 우측 상단 점수
    /// - 좌측 상단 리더보드
    /// - 하단 미니맵 루트 참조(실제 렌더링은 MinimapRenderer 담당)
    /// </summary>
    public class GameHUD : MonoBehaviour
    {
        [Header("점수 (우측 상단)")]
        [SerializeField] private TMP_Text _scoreText;

        [Header("리더보드 (좌측 상단)")]
        [SerializeField] private TMP_Text[] _rankNames = new TMP_Text[8];
        [SerializeField] private TMP_Text[] _rankScores = new TMP_Text[8];
        [SerializeField] private Image[] _rankDots = new Image[8];

        [Header("미니맵 (하단)")]
        [SerializeField] private RectTransform _minimapRoot;
        public RectTransform MinimapRoot => _minimapRoot;
        [SerializeField] private Image _playerDot;
        [SerializeField] private Image[] _botDots = new Image[8];
        [SerializeField] private float _minimapUsableRadius = 65f;
        public float MinimapUsableRadius => _minimapUsableRadius;

        // 리더보드 색상 규칙
        private static readonly Color COLOR_FIRST = new Color(1f, 0.65f, 0.2f); // 1위
        private static readonly Color COLOR_PLAYER = Color.white;                 // 로컬 플레이어
        private static readonly Color COLOR_NORMAL = new Color(0.75f, 0.75f, 0.75f);
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

        private readonly List<LeaderboardEntry> _entries = new List<LeaderboardEntry>(16);
        private float _updateTimer;
        private const float UPDATE_INTERVAL = 0.5f;

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
            // 점수 갱신
            if (_scoreText != null && GameManager.Instance != null)
                _scoreText.text = $"{GameManager.Instance.CurrentScore:N0}";

            // 리더보드 주기 갱신
            _updateTimer -= Time.deltaTime;
            if (_updateTimer <= 0f)
            {
                _updateTimer = UPDATE_INTERVAL;
                UpdateLeaderboard();
                RefreshLeaderboardUI();
            }
        }

        private void Start()
        {
            // 미니맵은 MinimapRenderer 단일 경로로 고정한다.
            _minimapRenderer = GetComponent<MinimapRenderer>();
            if (_minimapRenderer == null)
                _minimapRenderer = gameObject.AddComponent<MinimapRenderer>();
            _minimapRenderer.enabled = true;

            if (FindObjectOfType<MainMenuUI>() == null)
                Debug.LogWarning("[GameHUD] MainMenuUI is missing in this scene. Start menu flow will be unavailable.");

            HideLegacyMinimapDots();
        }

        // ===== LEADERBOARD =====
        private void UpdateLeaderboard()
        {
            _entries.Clear();

            // 1) 네트워크 플레이어(로컬 포함)
            bool hasNetworkPlayers = Network.NetworkPlayer.ActivePlayers.Count > 0;
            if (hasNetworkPlayers)
            {
                foreach (Network.NetworkPlayer np in Network.NetworkPlayer.ActivePlayers)
                {
                    if (np == null) continue;
                    bool isLocal = np.isLocalPlayer;

                    // 로컬은 GameManager 점수, 원격은 SyncVar 점수 사용
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
                // Mirror 미사용(싱글플레이) 폴백
                float playerScore = GameManager.Instance != null
                    ? GameManager.Instance.CurrentScore : 0f;
                _entries.Add(new LeaderboardEntry
                {
                    Name = GameManager.Instance?.PlayerName ?? "Player",
                    Score = playerScore,
                    DotColor = Color.white,
                    IsPlayer = true
                });
            }

            // 2) 네트워크 봇
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

                // 이름
                if (_rankNames[i] != null)
                {
                    _rankNames[i].gameObject.SetActive(active);
                    if (active)
                    {
                        var e = _entries[i];
                        _rankNames[i].text = $"{i + 1}. {e.Name}";

                        // 색상: 1위 강조, 로컬 흰색, 나머지 회색
                        Color textColor = i == 0 ? COLOR_FIRST
                            : e.IsPlayer ? COLOR_PLAYER
                            : COLOR_NORMAL;
                        _rankNames[i].color = textColor;
                        _rankNames[i].fontStyle = (i == 0 || e.IsPlayer)
                            ? FontStyles.Bold : FontStyles.Normal;
                    }
                }

                // 점수
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

                // 도트 색상
                if (i < _rankDots.Length && _rankDots[i] != null)
                {
                    _rankDots[i].gameObject.SetActive(active);
                    if (active)
                        _rankDots[i].color = _entries[i].DotColor;
                }
            }
        }

        private void HideLegacyMinimapDots()
        {
            if (_playerDot != null)
                _playerDot.gameObject.SetActive(false);

            for (int i = 0; i < _botDots.Length; i++)
            {
                if (_botDots[i] != null)
                    _botDots[i].gameObject.SetActive(false);
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
    }
}
