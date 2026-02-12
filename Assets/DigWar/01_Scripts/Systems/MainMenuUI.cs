using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Core;

namespace Systems
{
    /// <summary>
    /// 메인 메뉴 UI. 게임 시작 전 닉네임 입력 + Play 버튼.
    /// Canvas에 직접 UI를 생성하거나, 미리 배치된 패널을 참조한다.
    /// </summary>
    public class MainMenuUI : MonoBehaviour
    {
        [Header("UI 참조 (null이면 런타임 자동 생성)")]
        [SerializeField] private GameObject _panel;
        [SerializeField] private TMP_InputField _nameInput;
        [SerializeField] private Button _playButton;



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

            // 게임 시작 전이므로 메뉴 표시
            if (_panel != null)
                _panel.SetActive(true);

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
            string playerName = _nameInput != null
                ? _nameInput.text.Trim() : "Player";

            if (string.IsNullOrEmpty(playerName))
                playerName = "Player";

            // GameManager에 이름 전달 및 게임 시작
            if (GameManager.Instance != null)
            {
                GameManager.Instance.PlayerName = playerName;
                GameManager.Instance.StartGame();
            }

            // 시간 복원 & 메뉴 숨기기
            Time.timeScale = 1f;
            if (_panel != null)
                _panel.SetActive(false);
        }
    }
}
