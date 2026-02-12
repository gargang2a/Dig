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
            // UI가 SerializeField로 연결되지 않은 경우 런타임 생성
            if (_panel == null)
                CreateUI();

            // 기본값 설정
            if (_nameInput != null)
            {
                _nameInput.text = "Player";
                _nameInput.characterLimit = 12;
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

        // ── 런타임 UI 생성 ──
        private void CreateUI()
        {
            // Canvas 찾기 또는 생성
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                canvas = FindObjectOfType<Canvas>();
            }
            if (canvas == null) return;

            // 반투명 배경 패널
            _panel = new GameObject("MainMenuPanel");
            _panel.transform.SetParent(canvas.transform, false);

            var panelImg = _panel.AddComponent<Image>();
            panelImg.color = new Color(0f, 0f, 0f, 0.85f);
            var panelRT = panelImg.rectTransform;
            panelRT.anchorMin = Vector2.zero;
            panelRT.anchorMax = Vector2.one;
            panelRT.offsetMin = Vector2.zero;
            panelRT.offsetMax = Vector2.zero;

            // 중앙 컨테이너
            var center = CreateRect("Center", _panel.transform, 300f, 200f);

            // 타이틀
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(center.transform, false);
            var titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = "DIG WAR";
            titleText.fontSize = 48;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = new Color(0.95f, 0.75f, 0.35f); // 금색
            var titleRT = titleText.rectTransform;
            titleRT.anchorMin = new Vector2(0f, 0.7f);
            titleRT.anchorMax = new Vector2(1f, 1f);
            titleRT.offsetMin = Vector2.zero;
            titleRT.offsetMax = Vector2.zero;

            // 닉네임 입력
            CreateInputField(center.transform);

            // Play 버튼
            CreatePlayButton(center.transform);
        }

        private void CreateInputField(Transform parent)
        {
            var inputObj = new GameObject("NameInput");
            inputObj.transform.SetParent(parent, false);

            // 배경
            var bg = inputObj.AddComponent<Image>();
            bg.color = new Color(0.15f, 0.12f, 0.08f, 0.9f);

            var inputRT = bg.rectTransform;
            inputRT.anchorMin = new Vector2(0.1f, 0.4f);
            inputRT.anchorMax = new Vector2(0.9f, 0.6f);
            inputRT.offsetMin = Vector2.zero;
            inputRT.offsetMax = Vector2.zero;

            // 텍스트 영역
            var textArea = new GameObject("Text Area");
            textArea.transform.SetParent(inputObj.transform, false);
            var textAreaRT = textArea.AddComponent<RectTransform>();
            textAreaRT.anchorMin = new Vector2(0.05f, 0f);
            textAreaRT.anchorMax = new Vector2(0.95f, 1f);
            textAreaRT.offsetMin = Vector2.zero;
            textAreaRT.offsetMax = Vector2.zero;

            // Placeholder
            var phObj = new GameObject("Placeholder");
            phObj.transform.SetParent(textArea.transform, false);
            var phText = phObj.AddComponent<TextMeshProUGUI>();
            phText.text = "닉네임 입력...";
            phText.fontSize = 22;
            phText.fontStyle = FontStyles.Italic;
            phText.color = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            phText.alignment = TextAlignmentOptions.MidlineLeft;
            var phRT = phText.rectTransform;
            phRT.anchorMin = Vector2.zero;
            phRT.anchorMax = Vector2.one;
            phRT.offsetMin = Vector2.zero;
            phRT.offsetMax = Vector2.zero;

            // Input Text
            var inputTextObj = new GameObject("Text");
            inputTextObj.transform.SetParent(textArea.transform, false);
            var inputText = inputTextObj.AddComponent<TextMeshProUGUI>();
            inputText.fontSize = 22;
            inputText.color = Color.white;
            inputText.alignment = TextAlignmentOptions.MidlineLeft;
            var inputTextRT = inputText.rectTransform;
            inputTextRT.anchorMin = Vector2.zero;
            inputTextRT.anchorMax = Vector2.one;
            inputTextRT.offsetMin = Vector2.zero;
            inputTextRT.offsetMax = Vector2.zero;

            // TMP_InputField
            _nameInput = inputObj.AddComponent<TMP_InputField>();
            _nameInput.textViewport = textAreaRT;
            _nameInput.textComponent = inputText;
            _nameInput.placeholder = phText;
            _nameInput.text = "Player";
            _nameInput.characterLimit = 12;
            _nameInput.caretColor = Color.white;
        }

        private void CreatePlayButton(Transform parent)
        {
            var btnObj = new GameObject("PlayButton");
            btnObj.transform.SetParent(parent, false);

            // 배경
            var btnImg = btnObj.AddComponent<Image>();
            btnImg.color = new Color(0.85f, 0.55f, 0.15f); // 주황

            var btnRT = btnImg.rectTransform;
            btnRT.anchorMin = new Vector2(0.2f, 0.05f);
            btnRT.anchorMax = new Vector2(0.8f, 0.3f);
            btnRT.offsetMin = Vector2.zero;
            btnRT.offsetMax = Vector2.zero;

            // 텍스트
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(btnObj.transform, false);
            var btnText = textObj.AddComponent<TextMeshProUGUI>();
            btnText.text = "PLAY";
            btnText.fontSize = 28;
            btnText.fontStyle = FontStyles.Bold;
            btnText.alignment = TextAlignmentOptions.Center;
            btnText.color = Color.white;
            var textRT = btnText.rectTransform;
            textRT.anchorMin = Vector2.zero;
            textRT.anchorMax = Vector2.one;
            textRT.offsetMin = Vector2.zero;
            textRT.offsetMax = Vector2.zero;

            _playButton = btnObj.AddComponent<Button>();
            _playButton.targetGraphic = btnImg;

            // 색상 전환
            var colors = _playButton.colors;
            colors.normalColor = new Color(0.85f, 0.55f, 0.15f);
            colors.highlightedColor = new Color(1f, 0.7f, 0.3f);
            colors.pressedColor = new Color(0.65f, 0.4f, 0.1f);
            _playButton.colors = colors;
        }

        private RectTransform CreateRect(string name, Transform parent,
            float width, float height)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            var rt = obj.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = Vector2.zero;
            return rt;
        }
    }
}
