using UnityEngine;
using Core;

namespace Systems
{
    public class ProgressionManager : MonoBehaviour
    {
        [Header("Target References")]
        public Transform PlayerTransform;
        public Camera MainCamera;

        [Header("Damping")]
        public float ScaleSmoothTime = 0.5f;
        public float ZoomSmoothTime = 0.5f;

        private float _currentScaleVelocity;
        private float _currentZoomVelocity;
        private float _targetScale;
        private float _targetZoom;

        private void Start()
        {
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnScoreChanged += HandleScoreChanged;
                // Initialize targets
                HandleScoreChanged(GameManager.Instance.CurrentScore);
            }
            
            // Default targets if GameManager not ready
            _targetScale = PlayerTransform.localScale.x;
            if (MainCamera != null) _targetZoom = MainCamera.orthographicSize;
        }

        private void OnDestroy()
        {
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnScoreChanged -= HandleScoreChanged;
            }
        }

        private void Update()
        {
            if (PlayerTransform == null || GameManager.Instance == null) return;

            // Smoothly interpolate scale
            float newScale = Mathf.SmoothDamp(
                PlayerTransform.localScale.x, 
                _targetScale, 
                ref _currentScaleVelocity, 
                ScaleSmoothTime
            );

            // Apply uniform scale
            PlayerTransform.localScale = Vector3.one * newScale;

            // Smoothly interpolate camera zoom
            if (MainCamera != null)
            {
                MainCamera.orthographicSize = Mathf.SmoothDamp(
                    MainCamera.orthographicSize,
                    _targetZoom,
                    ref _currentZoomVelocity,
                    ZoomSmoothTime
                );
            }
        }

        private void HandleScoreChanged(float score)
        {
            var settings = GameManager.Instance.Settings;
            
            // Calculate scale based on score: Base + (Score / Factor)
            float scoreFactor = score / settings.ScorePerSizeUnit;
            float rawScale = settings.MinScale + scoreFactor;
            _targetScale = Mathf.Clamp(rawScale, settings.MinScale, settings.MaxScale);

            // Calculate camera zoom based on scale
            // Example: Zoom = 5 + (Scale * 2)
            _targetZoom = 5f + (_targetScale * 2f);
        }
    }
}
