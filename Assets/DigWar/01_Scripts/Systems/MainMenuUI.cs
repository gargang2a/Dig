using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Core;
using Network;
using Mirror;
using UnityEngine.Rendering;

namespace Systems
{
    /// <summary>
    /// 메인 메뉴 UI. 게임 시작 전 닉네임 입력 + Play 버튼.
    /// 네트워크 미연결 상태에서 Play를 눌러도 패널을 닫지 않고 연결 상태를 안내한다.
    /// </summary>
    public class MainMenuUI : MonoBehaviour
    {
        [Header("UI 참조 (패널 필수)")]
        [SerializeField] private GameObject _panel;
        [SerializeField] private TMP_InputField _nameInput;
        [SerializeField] private Button _playButton;
        [SerializeField] private TMP_Text _statusText;

        private bool _retryConnectPending;
        private bool _startRequestedWhileConnecting;
        private bool _isHeadlessRuntime;

        private void OnEnable()
        {
            DigWarNetworkManager.OnClientConnectionStatusChanged += OnClientConnectionStatusChanged;
        }

        private void OnDisable()
        {
            DigWarNetworkManager.OnClientConnectionStatusChanged -= OnClientConnectionStatusChanged;
        }

        private void Start()
        {
            _isHeadlessRuntime = Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            if (_isHeadlessRuntime)
            {
                // Dedicated Server / Headless runtime:
                // UI flow does not apply and Time.timeScale must remain 1 for server simulation.
                if (_panel != null)
                    _panel.SetActive(false);

                Time.timeScale = 1f;
                enabled = false;
                return;
            }

            if (_panel == null)
            {
                Debug.LogWarning("[MainMenuUI] UI References are missing! Please assign them in the Inspector.");
                return;
            }

            EnsureStatusText();

            if (_nameInput != null)
            {
                _nameInput.characterLimit = 12;

                if (GameManager.Instance != null && !string.IsNullOrEmpty(GameManager.Instance.PlayerName))
                    _nameInput.text = GameManager.Instance.PlayerName;
            }

            if (_playButton != null)
                _playButton.onClick.AddListener(OnPlayClicked);

            if (_playButton != null)
                _playButton.interactable = true;

            _panel.SetActive(true);
            ApplyStatusText(string.Empty);

            DigWarNetworkManager.ClientConnectionStatus latestStatus = DigWarNetworkManager.LatestClientConnectionStatus;
            _retryConnectPending = latestStatus.IsError;
            if (!string.IsNullOrWhiteSpace(latestStatus.Message))
                OnClientConnectionStatusChanged(latestStatus);

            Time.timeScale = 0f;
        }

        private void Update()
        {
            if (_panel != null && _panel.activeSelf && Input.GetKeyDown(KeyCode.Return))
                OnPlayClicked();
        }

        private void OnPlayClicked()
        {
            if (_retryConnectPending && TryReconnectClientIfNeeded())
            {
                _startRequestedWhileConnecting = true;
                return;
            }

            if (!EnsureNetworkReadyForPlay())
                return;

            _startRequestedWhileConnecting = false;
            ApplyStatusText(string.Empty);

            if (_playButton != null)
                _playButton.interactable = false;

            string playerName = _nameInput != null ? _nameInput.text.Trim() : "Player";
            if (string.IsNullOrEmpty(playerName))
                playerName = "Player";

            if (GameManager.Instance != null)
            {
                GameManager.Instance.PlayerName = playerName;
                if (!GameManager.Instance.IsGameActive)
                    GameManager.Instance.StartGame();
            }

            Time.timeScale = 1f;
            if (_panel != null)
                _panel.SetActive(false);
        }

        private void OnClientConnectionStatusChanged(DigWarNetworkManager.ClientConnectionStatus status)
        {
            ApplyStatusText(status.Message);
            _retryConnectPending = status.IsError;

            if (!status.IsError)
            {
                if (_playButton != null)
                    _playButton.interactable = true;

                if (_startRequestedWhileConnecting)
                    OnPlayClicked();

                return;
            }

            if (_panel != null)
                _panel.SetActive(true);

            if (_playButton != null)
                _playButton.interactable = true;

            Time.timeScale = 0f;
        }

        private void ApplyStatusText(string message)
        {
            if (_statusText == null)
                EnsureStatusText();

            if (_statusText == null)
            {
                if (!string.IsNullOrWhiteSpace(message))
                    Debug.LogWarning($"[MainMenuUI] {message}");
                return;
            }

            bool hasMessage = !string.IsNullOrWhiteSpace(message);
            _statusText.gameObject.SetActive(hasMessage);
            _statusText.text = hasMessage ? message : string.Empty;
        }

        private bool TryReconnectClientIfNeeded()
        {
            DigWarNetworkManager networkManager = DigWarNetworkManager.Instance;
            if (networkManager == null) return false;

            if (NetworkServer.active) return false;
            if (NetworkClient.isConnected) return false;

            if (NetworkClient.active)
                networkManager.StopClient();

            networkManager.StartClient();
            ApplyStatusText("Retrying connection to server...");
            _retryConnectPending = false;

            if (_playButton != null)
                _playButton.interactable = false;

            return true;
        }

        private bool EnsureNetworkReadyForPlay()
        {
            DigWarNetworkManager networkManager = DigWarNetworkManager.Instance;
            if (networkManager == null) return true;
            if (NetworkServer.active) return true;
            if (NetworkClient.isConnected) return true;

            _startRequestedWhileConnecting = true;

            if (!NetworkClient.active)
            {
                networkManager.StartClient();
                ApplyStatusText("Connecting to server...");
            }
            else
            {
                ApplyStatusText("Waiting for server connection...");
            }

            if (_panel != null)
                _panel.SetActive(true);

            if (_playButton != null)
                _playButton.interactable = false;

            Time.timeScale = 0f;
            return false;
        }

        private void EnsureStatusText()
        {
            if (_statusText != null || _panel == null)
                return;

            GameObject statusObject = new GameObject("StatusText_Auto", typeof(RectTransform));
            statusObject.transform.SetParent(_panel.transform, false);

            RectTransform statusRect = statusObject.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0.5f, 0f);
            statusRect.anchorMax = new Vector2(0.5f, 0f);
            statusRect.pivot = new Vector2(0.5f, 0f);
            statusRect.anchoredPosition = new Vector2(0f, 24f);
            statusRect.sizeDelta = new Vector2(680f, 80f);

            TextMeshProUGUI autoStatusText = statusObject.AddComponent<TextMeshProUGUI>();
            autoStatusText.text = string.Empty;
            autoStatusText.alignment = TextAlignmentOptions.Center;
            autoStatusText.enableWordWrapping = true;
            autoStatusText.fontSize = 24f;
            autoStatusText.color = new Color(1f, 0.86f, 0.24f, 1f);

            statusObject.SetActive(false);
            _statusText = autoStatusText;

            Debug.LogWarning("[MainMenuUI] _statusText is missing. Runtime fallback StatusText_Auto created.");
        }
    }
}
