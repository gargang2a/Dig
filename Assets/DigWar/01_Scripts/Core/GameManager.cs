using UnityEngine;
using Core.Data;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Core
{
    /// <summary>
    /// 게임의 전역 상태를 관리하는 싱글톤.
    /// 점수, 게임 활성 여부 등 런타임 데이터를 보관하고
    /// 점수 변경 시 구독자에게 이벤트를 발행한다.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        [Header("Settings")]
        [SerializeField] private GameSettings _settings;
        public GameSettings Settings => _settings;

        public float CurrentScore { get; private set; }
        public bool IsGameActive { get; private set; }

        public event System.Action<float> OnScoreChanged;
        public event System.Action OnPlayerDied;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            StartGame();
        }

        public void StartGame()
        {
            CurrentScore = 0f;
            IsGameActive = true;
            OnScoreChanged?.Invoke(CurrentScore);
        }

        /// <summary>
        /// 점수를 증감한다. 음수 입력 시에도 0 미만으로 내려가지 않는다.
        /// </summary>
        public void AddScore(float amount)
        {
            CurrentScore = Mathf.Max(0f, CurrentScore + amount);
            OnScoreChanged?.Invoke(CurrentScore);
        }

        /// <summary>
        /// 플레이어 사망 처리. 경계 이탈, 터널 충돌 등에서 호출.
        /// </summary>
        public void KillPlayer()
        {
            if (!IsGameActive) return;

            IsGameActive = false;
            OnPlayerDied?.Invoke();
            Debug.Log($"[GameManager] 사망! 최종 점수: {CurrentScore:F0}");
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_settings != null) return;

            string[] guids = AssetDatabase.FindAssets("t:GameSettings");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _settings = AssetDatabase.LoadAssetAtPath<GameSettings>(path);
                Debug.Log($"[GameManager] Settings 자동 할당: {path}");
            }
        }
#endif
    }
}
