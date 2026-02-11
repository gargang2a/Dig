using UnityEngine;
using Core;
using Core.Data;

namespace Systems
{
    /// <summary>
    /// 점수에 따라 플레이어 크기와 카메라 줌을 조정한다.
    /// 로그 스케일 곡선으로 초반 빠른 성장, 후반 완만한 성장을 표현한다.
    /// </summary>
    public class ProgressionManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform _playerTransform;
        [SerializeField] private Camera _mainCamera;

        [Header("Smoothing")]
        [SerializeField, Range(0.01f, 2f)] private float _scaleSmoothTime = 0.5f;
        [SerializeField, Range(0.01f, 2f)] private float _zoomSmoothTime = 0.5f;

        private GameSettings _settings;
        private Tunnel.TunnelGenerator _tunnelGenerator;
        private float _scaleVelocity;
        private float _zoomVelocity;
        private float _targetScale;
        private float _targetZoom;

        private void Start()
        {
            if (GameManager.Instance == null || GameManager.Instance.Settings == null)
            {
                Debug.LogError("[ProgressionManager] GameManager 또는 Settings 누락");
                enabled = false;
                return;
            }

            _settings = GameManager.Instance.Settings;
            _tunnelGenerator = FindObjectOfType<Tunnel.TunnelGenerator>();
            GameManager.Instance.OnScoreChanged += OnScoreChanged;

            OnScoreChanged(GameManager.Instance.CurrentScore);

            if (_playerTransform != null)
                _playerTransform.localScale = new Vector3(_targetScale, _targetScale, 1f);

            if (_mainCamera != null)
                _mainCamera.orthographicSize = _targetZoom;
        }

        private void OnDestroy()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.OnScoreChanged -= OnScoreChanged;
        }

        private void LateUpdate()
        {
            if (_playerTransform == null) return;

            float scale = Mathf.SmoothDamp(
                _playerTransform.localScale.x, _targetScale,
                ref _scaleVelocity, _scaleSmoothTime
            );
            _playerTransform.localScale = new Vector3(scale, scale, 1f);

            // 터널 너비도 플레이어 스케일에 동기화
            if (_tunnelGenerator != null)
                _tunnelGenerator.UpdateWidth(scale);

            if (_mainCamera != null)
            {
                _mainCamera.orthographicSize = Mathf.SmoothDamp(
                    _mainCamera.orthographicSize, _targetZoom,
                    ref _zoomVelocity, _zoomSmoothTime
                );
            }
        }

        private void OnScoreChanged(float score)
        {
            float growth = Mathf.Log(1f + score / _settings.ScorePerSizeUnit);
            _targetScale = Mathf.Clamp(
                _settings.MinScale + growth,
                _settings.MinScale,
                _settings.MaxScale
            );
            _targetZoom = _settings.BaseCameraZoom + _targetScale * _settings.CameraZoomPerScale;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_playerTransform == null)
            {
                var player = FindObjectOfType<Player.PlayerController>();
                if (player != null)
                    _playerTransform = player.transform;
            }
            if (_mainCamera == null)
                _mainCamera = Camera.main;
        }
#endif
    }
}
