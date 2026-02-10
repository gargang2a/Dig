using UnityEngine;
using Core.Data;

namespace Core
{
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        [Header("Settings")]
        public GameSettings Settings;

        [Header("Runtime Data")]
        public float CurrentScore;
        public bool IsGameActive = false;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void Start()
        {
            // Temporary: Start game immediately for testing
            StartGame();
        }

        public event System.Action<float> OnScoreChanged;

        public void StartGame()
        {
            CurrentScore = 0;
            IsGameActive = true;
            Debug.Log("Game Started");
            OnScoreChanged?.Invoke(CurrentScore);
        }

        public void AddScore(float amount)
        {
            CurrentScore += amount;
            OnScoreChanged?.Invoke(CurrentScore);
        }
    }
}
