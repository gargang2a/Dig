using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Core;

namespace Systems
{
    /// <summary>
    /// Game over UI controller.
    /// It toggles the panel, updates score text, and triggers local respawn.
    /// </summary>
    public class GameOverUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private GameObject _panel;
        [SerializeField] private TMP_Text _scoreText;
        [SerializeField] private Button _restartButton;

        private bool _isGameOver;

        private void OnEnable()
        {
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnPlayerDied += OnPlayerDied;
                Debug.Log("[GameOverUI] Subscribed to OnPlayerDied");
            }
            else
            {
                Debug.LogWarning("[GameOverUI] GameManager Instance is NULL! Subscription failed.");
            }
        }

        private void OnDisable()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.OnPlayerDied -= OnPlayerDied;
        }

        private void Start()
        {
            if (_panel != null)
                _panel.SetActive(false);

            if (_restartButton != null)
                _restartButton.onClick.AddListener(Restart);
        }

        private void Update()
        {
            if (_isGameOver && GameManager.Instance != null && GameManager.Instance.IsGameActive)
            {
                _isGameOver = false;
                if (_panel != null)
                    _panel.SetActive(false);
            }

            if (_isGameOver && Input.GetKeyDown(KeyCode.Space))
                Restart();
        }

        private void OnPlayerDied()
        {
            Debug.Log("[GameOverUI] OnPlayerDied Event Received -> Activating Panel");
            _isGameOver = true;
            float score = GameManager.Instance != null
                ? GameManager.Instance.CurrentScore : 0f;

            if (_scoreText != null)
            {
                string name = GameManager.Instance != null
                    ? GameManager.Instance.PlayerName : "Player";
                // <size=120%><color=#FFD700>Name</color></size>
                // <size=80%>Final Score: 999</size>
                _scoreText.richText = true;
                _scoreText.text = $"<size=120%><color=#FFD700>{name}</color></size>\n<size=75%>Final Score: {score:F0}</size>";
            }

            if (_panel != null)
            {
                _panel.SetActive(true);
                Debug.Log($"[GameOverUI] Panel Active State: {_panel.activeSelf}");
            }
        }

        private void Restart()
        {
            _isGameOver = false;

            // Hide panel
            if (_panel != null)
                _panel.SetActive(false);

            // Respawn local player (no scene reload)
            if (TryRespawnLocalNetworkPlayer()) return;
            if (TryRespawnLocalSinglePlayer()) return;
            Debug.LogWarning("[GameOverUI] Restart requested but no local player was found.");
        }

        private bool TryRespawnLocalNetworkPlayer()
        {
            Network.NetworkPlayer localNetworkPlayer = Network.NetworkPlayer.LocalPlayer;
            if (localNetworkPlayer == null) return false;

            var pc = localNetworkPlayer.GetComponent<Player.PlayerController>();
            if (pc == null) return false;

            pc.Respawn();
            if (Network.NetworkPlayer.CanSendCommands)
                localNetworkPlayer.CmdRespawnWithReportedPosition(pc.transform.position);
            else
                Debug.LogWarning("[GameOverUI] CmdRespawnWithReportedPosition skipped: client not connected.");
            return true;
        }

        private bool TryRespawnLocalSinglePlayer()
        {
            Player.PlayerController player = Player.PlayerController.LocalController;
            if (player == null)
                player = FindObjectOfType<Player.PlayerController>();
            if (player == null) return false;

            var netPlayer = player.GetComponent<Network.NetworkPlayer>();
            if (netPlayer != null && !netPlayer.isLocalPlayer) return false;

            player.Respawn();
            return true;
        }
    }
}
