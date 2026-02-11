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
        [SerializeField] private Image _playerDot;
        [SerializeField] private Image[] _botDots = new Image[8];
        [SerializeField] private float _minimapUsableRadius = 65f;

        // 색상
        private static readonly Color COLOR_FIRST = new Color(1f, 0.65f, 0.2f);   // 주황 (1위)
        private static readonly Color COLOR_PLAYER = Color.white;                   // 본인
        private static readonly Color COLOR_NORMAL = new Color(0.75f, 0.75f, 0.75f); // 일반

        // 내부 데이터
        private readonly List<LeaderboardEntry> _entries = new List<LeaderboardEntry>(16);
        private float _updateTimer;
        private const float UPDATE_INTERVAL = 0.5f;

        private static readonly string[] BOT_NAMES =
            { "Drillma", "MoleTrap", "DigiDig", "GrubWorm",
              "TunnelKing", "DirtDash", "BurrBot", "SubStar" };

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

        // ===== LEADERBOARD =====
        private void UpdateLeaderboard()
        {
            _entries.Clear();

            float playerScore = GameManager.Instance != null
                ? GameManager.Instance.CurrentScore : 0f;
            _entries.Add(new LeaderboardEntry
            {
                Name = "You", Score = playerScore,
                DotColor = Color.white, IsPlayer = true
            });

            var bots = FindObjectsOfType<Player.AIController>();
            for (int i = 0; i < bots.Length; i++)
            {
                var sr = bots[i].GetComponentInChildren<SpriteRenderer>();
                _entries.Add(new LeaderboardEntry
                {
                    Name = BOT_NAMES[i % BOT_NAMES.Length],
                    Score = bots[i].Score,
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

            var player = FindObjectOfType<Player.PlayerController>();
            if (player != null && _playerDot != null)
                SetDotPos(_playerDot, player.transform.position, mapRadius);

            var bots = FindObjectsOfType<Player.AIController>();
            for (int i = 0; i < _botDots.Length; i++)
            {
                if (_botDots[i] == null) continue;

                if (i < bots.Length)
                {
                    _botDots[i].gameObject.SetActive(true);
                    var sr = bots[i].GetComponentInChildren<SpriteRenderer>();
                    _botDots[i].color = sr != null ? sr.color : Color.red;
                    SetDotPos(_botDots[i], bots[i].transform.position, mapRadius);
                }
                else
                {
                    _botDots[i].gameObject.SetActive(false);
                }
            }
        }

        private void SetDotPos(Image dot, Vector3 worldPos, float mapRadius)
        {
            float nx = worldPos.x / mapRadius;
            float ny = worldPos.y / mapRadius;
            dot.rectTransform.anchoredPosition =
                new Vector2(nx * _minimapUsableRadius, ny * _minimapUsableRadius);
        }
    }
}
