using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Core;

namespace Systems
{
    /// <summary>
    /// 게임 오버 UI. Canvas에 미리 배치된 TMP/Button을 참조한다.
    /// 기본적으로 패널이 비활성화 상태이며, 사망 시 활성화된다.
    /// </summary>
    public class GameOverUI : MonoBehaviour
    {
        [Header("UI 참조")]
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
                _scoreText.richText = true; // Ensure Rich Text is enabled
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
            UnityEngine.SceneManagement.SceneManager.LoadScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex
            );
        }
    }
}
