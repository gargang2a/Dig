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
        [SerializeField, Range(0.01f, 2f)] private float _zoomSmoothTime = 0.5f;

        private GameSettings _settings;
        private float _zoomVelocity;
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

            if (_mainCamera != null)
            {
                // 초기 줌 설정
                float initialScale = _settings.MinScale;
                _targetZoom = _settings.BaseCameraZoom + initialScale * _settings.CameraZoomPerScale;
                _mainCamera.orthographicSize = _targetZoom;
            }
        }



        private void LateUpdate()
        {
            if (_playerTransform == null || _mainCamera == null) return;

            // 플레이어의 현재 스케일(MoleGrowth가 제어)을 기준으로 목표 줌 계산
            float currentScale = _playerTransform.localScale.x;
            _targetZoom = _settings.BaseCameraZoom + currentScale * _settings.CameraZoomPerScale;

            _mainCamera.orthographicSize = Mathf.SmoothDamp(
                _mainCamera.orthographicSize, _targetZoom,
                ref _zoomVelocity, _zoomSmoothTime
            );
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
