using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Core;
using Network;
using Mirror;

namespace Systems
{
    /// <summary>
    /// 메인 메뉴 UI. 게임 시작 전 닉네임 입력 + Play 버튼.
    /// 미리 배치된 UI 참조를 사용하며, 참조 누락 시 시작 메뉴 기능이 제한된다.
    /// </summary>
    public class MainMenuUI : MonoBehaviour
    {
        [Header("UI 참조 (누락 시 시작 메뉴 기능 제한)")]
        [SerializeField] private GameObject _panel;
        [SerializeField] private TMP_InputField _nameInput;
        [SerializeField] private Button _playButton;
        [SerializeField] private TMP_Text _statusText;

        private bool _retryConnectPending;


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
            // UI가 연결되지 않았으면 경고 로그 출력
            if (_panel == null)
            {
                Debug.LogWarning("[MainMenuUI] UI References are missing! Please assign them in the Inspector.");
                return;
            }

            // 기본값 설정
            if (_nameInput != null)
            {
                _nameInput.characterLimit = 12;

                // 이전 이름이 있다면 입력창에 채워넣기
                if (GameManager.Instance != null && !string.IsNullOrEmpty(GameManager.Instance.PlayerName))
                    _nameInput.text = GameManager.Instance.PlayerName;
            }

            if (_playButton != null)
                _playButton.onClick.AddListener(OnPlayClicked);

            if (_playButton != null)
                _playButton.interactable = true;

            // 게임 시작 전이므로 메뉴 표시
            if (_panel != null)
                _panel.SetActive(true);

            ApplyStatusText(string.Empty);

            DigWarNetworkManager.ClientConnectionStatus latestStatus =
                DigWarNetworkManager.LatestClientConnectionStatus;
            _retryConnectPending = latestStatus.IsError;
            if (!string.IsNullOrWhiteSpace(latestStatus.Message))
                OnClientConnectionStatusChanged(latestStatus);

            // 게임 오브젝트 일시 멈춤 (플레이어/봇 이동 차단)
            Time.timeScale = 0f;
        }

        private void Update()
        {
            // Enter로도 시작 가능
            if (_panel != null && _panel.activeSelf
                && Input.GetKeyDown(KeyCode.Return))
            {
                OnPlayClicked();
            }
        }

        private void OnPlayClicked()
        {
            if (_retryConnectPending && TryReconnectClientIfNeeded()) return;

            ApplyStatusText(string.Empty);

            if (_playButton != null)
                _playButton.interactable = false;

            string playerName = _nameInput != null
                ? _nameInput.text.Trim() : "Player";

            if (string.IsNullOrEmpty(playerName))
                playerName = "Player";

            // GameManager에 이름 전달 및 게임 시작
            if (GameManager.Instance != null)
            {
                GameManager.Instance.PlayerName = playerName;
                if (!GameManager.Instance.IsGameActive)
                    GameManager.Instance.StartGame();
            }

            // 시간 복원 & 메뉴 숨기기
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
            if (_statusText == null) return;

            bool hasMessage = !string.IsNullOrWhiteSpace(message);
            _statusText.gameObject.SetActive(hasMessage);
            _statusText.text = hasMessage ? message : string.Empty;
        }

        private bool TryReconnectClientIfNeeded()
        {
            DigWarNetworkManager networkManager = DigWarNetworkManager.Instance;
            if (networkManager == null) return false;

            // Host/Server 인스턴스는 재접속 대신 기존 시작 흐름 사용.
            if (NetworkServer.active) return false;
            if (NetworkClient.isConnected) return false;

            if (NetworkClient.active)
                networkManager.StopClient();

            networkManager.StartClient();
            ApplyStatusText("서버 재접속 시도 중...");
            _retryConnectPending = false;

            if (_playButton != null)
                _playButton.interactable = false;

            return true;
        }
    }
}
