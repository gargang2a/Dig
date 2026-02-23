using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using Core;

namespace Systems
{
    /// <summary>
    /// 게임 HUD: 좌측 상단 리더보드, 우측 상단 점수, 우측 하단 미니맵.
    /// Canvas에 미리 배치된 TMP/Image를 SerializeField로 참조한다.
    /// </summary>
    public class GameHUD : MonoBehaviour
    {
        [Header("점수 (우측 상단)")]
        [SerializeField] private TMP_Text _scoreText;

        [Header("리더보드 (좌측 상단)")]
        [SerializeField] private TMP_Text[] _rankNames = new TMP_Text[8];  // "1. 이름"
        [SerializeField] private TMP_Text[] _rankScores = new TMP_Text[8]; // "20950" (우측정렬)
        [SerializeField] private Image[] _rankDots = new Image[8];         // 색상 점/아이콘

        [Header("미니맵 (우측 하단)")]
        [SerializeField] private RectTransform _minimapRoot;
        public RectTransform MinimapRoot => _minimapRoot;
        [SerializeField] private Image _playerDot;
        [SerializeField] private Image[] _botDots = new Image[8];
        [SerializeField] private float _minimapUsableRadius = 65f;
        public float MinimapUsableRadius => _minimapUsableRadius;

        // 색상
        private static readonly Color COLOR_FIRST = new Color(1f, 0.65f, 0.2f);   // 주황 (1위)
        private static readonly Color COLOR_PLAYER = Color.white;                   // 본인
        private static readonly Color COLOR_NORMAL = new Color(0.75f, 0.75f, 0.75f); // 일반
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

        // 내부 데이터
        private readonly List<LeaderboardEntry> _entries = new List<LeaderboardEntry>(16);
        private float _updateTimer;
        private const float UPDATE_INTERVAL = 0.5f;
        private Player.PlayerController _cachedLocalPlayer;
        private float _nextLocalPlayerLookupAt;

        private static readonly string[] BOT_NAMES = Network.NetworkBot.BOT_NAMES;

        private struct LeaderboardEntry
        {
            public string Name;
            public float Score;
            public Color DotColor;
            public bool IsPlayer;
        }

        private void Update()
        {
            // 점수 매 프레임
            if (_scoreText != null && GameManager.Instance != null)
                _scoreText.text = $"{GameManager.Instance.CurrentScore:N0}";

            // 리더보드 + 미니맵 주기적
            _updateTimer -= Time.deltaTime;
            if (_updateTimer <= 0f)
            {
                _updateTimer = UPDATE_INTERVAL;
                UpdateLeaderboard();
                RefreshLeaderboardUI();
                RefreshMinimap();
            }
        }

        private void Start()
        {
            // 미니맵 렌더러 자동 부착
            if (GetComponent<MinimapRenderer>() == null)
                gameObject.AddComponent<MinimapRenderer>();

            // MainMenuUI는 전용 UI 오브젝트에서 참조가 연결된 상태로 동작해야 한다.
            // HUD 오브젝트에 런타임으로 부착하면 참조 누락 경고와 중복 이벤트 구독이 발생할 수 있다.
            if (FindObjectOfType<MainMenuUI>() == null)
                Debug.LogWarning("[GameHUD] MainMenuUI is missing in this scene. Start menu flow will be unavailable.");
        }


        // ===== LEADERBOARD =====
        private void UpdateLeaderboard()
        {
            _entries.Clear();

            // 1) 네트워크 플레이어들 (로컬 플레이어 포함)
            bool hasNetworkPlayers = Network.NetworkPlayer.ActivePlayers.Count > 0;
            if (hasNetworkPlayers)
            {
                foreach (Network.NetworkPlayer np in Network.NetworkPlayer.ActivePlayers)
                {
                    if (np == null) continue;
                    bool isLocal = np.isLocalPlayer;
                    // 로컬 플레이어: GameManager의 실시간 점수 사용
                    // 리모트 플레이어: SyncVar Score 사용
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
                // Mirror 미사용 (싱글플레이 호환)
                float playerScore = GameManager.Instance != null
                    ? GameManager.Instance.CurrentScore : 0f;
                _entries.Add(new LeaderboardEntry
                {
                    Name = GameManager.Instance?.PlayerName ?? "Player",
                    Score = playerScore,
                    DotColor = Color.white, IsPlayer = true
                });
            }

            // 2) AI 봇
            // 2) AI 봇 — NetworkBot.Score(SyncVar)로 동기화된 점수 사용
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

                // 이름 표시
                if (_rankNames[i] != null)
                {
                    _rankNames[i].gameObject.SetActive(active);
                    if (active)
                    {
                        var e = _entries[i];
                        _rankNames[i].text = $"{i + 1}. {e.Name}";

                        // 색상: 1위=주황, 본인=흰, 나머지=회색
                        Color textColor = i == 0 ? COLOR_FIRST
                            : e.IsPlayer ? COLOR_PLAYER
                            : COLOR_NORMAL;
                        _rankNames[i].color = textColor;
                        _rankNames[i].fontStyle = (i == 0 || e.IsPlayer)
                            ? FontStyles.Bold : FontStyles.Normal;
                    }
                }

                // 점수 표시 (우측 정렬)
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

                // 색상 점
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

            // 로컬 플레이어 찾기
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

            // 네트워크 플레이어를 아직 못 찾으면 PlayerController 폴백 (싱글플레이 전용)
            if (!foundLocal && _playerDot != null)
            {
                // 멀티플레이어 환경에서는 NetworkPlayer 스폰 전까지 대기
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

            // botDots를 리모트 플레이어 + 봇에 배분
            int dotIndex = 0;

            // 리모트 플레이어 (플레이어별 고유 색상)
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

            // 봇 (각자 색상)
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

            // 나머지 비활성
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
            float nx = worldPos.x / mapRadius;
            float ny = worldPos.y / mapRadius;
            dot.rectTransform.anchoredPosition =
                new Vector2(nx * _minimapUsableRadius, ny * _minimapUsableRadius);
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
