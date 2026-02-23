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
        private float _nextPlayerLookupAt;

        private void Start()
        {
            if (GameManager.Instance == null || GameManager.Instance.Settings == null)
            {
                Debug.LogError("[ProgressionManager] GameManager 또는 Settings 누락");
                enabled = false;
                return;
            }

            _settings = GameManager.Instance.Settings;
            if (_mainCamera == null)
                _mainCamera = Camera.main;

            ResolvePlayerTransform(force: true);

            if (_mainCamera != null)
            {
                // 초기 줌 설정
                float initialScale = ResolvePlayerScaleOrDefault();
                _targetZoom = _settings.BaseCameraZoom + initialScale * _settings.CameraZoomPerScale;
                _mainCamera.orthographicSize = _targetZoom;
            }
        }



        private void LateUpdate()
        {
            if (_mainCamera == null || _settings == null) return;

            if (!IsValidTrackedPlayer(_playerTransform))
                ResolvePlayerTransform();

            if (_playerTransform == null) return;

            // 플레이어의 현재 스케일(MoleGrowth가 제어)을 기준으로 목표 줌 계산
            float currentScale = _playerTransform.localScale.x;
            _targetZoom = _settings.BaseCameraZoom + currentScale * _settings.CameraZoomPerScale;

            _mainCamera.orthographicSize = Mathf.SmoothDamp(
                _mainCamera.orthographicSize, _targetZoom,
                ref _zoomVelocity, _zoomSmoothTime
            );
        }

        private float ResolvePlayerScaleOrDefault()
        {
            if (_playerTransform == null)
                return _settings.MinScale;

            return Mathf.Max(_settings.MinScale, _playerTransform.localScale.x);
        }

        private bool IsValidTrackedPlayer(Transform target)
        {
            if (target == null) return false;

            var trackedNetPlayer = target.GetComponent<Network.NetworkPlayer>();
            if (trackedNetPlayer == null) return true;
            return trackedNetPlayer.isLocalPlayer;
        }

        private void ResolvePlayerTransform(bool force = false)
        {
            if (!force && Time.unscaledTime < _nextPlayerLookupAt) return;
            _nextPlayerLookupAt = Time.unscaledTime + 0.5f;

            Network.NetworkPlayer localNetworkPlayer = Network.NetworkPlayer.LocalPlayer;
            if (localNetworkPlayer != null)
            {
                _playerTransform = localNetworkPlayer.transform;
                return;
            }

            Player.PlayerController localController = Player.PlayerController.LocalController;
            if (localController != null)
            {
                _playerTransform = localController.transform;
                return;
            }

            Player.PlayerController[] controllers = FindObjectsOfType<Player.PlayerController>();
            for (int i = 0; i < controllers.Length; i++)
            {
                Player.PlayerController controller = controllers[i];
                if (controller == null) continue;

                var networkPlayer = controller.GetComponent<Network.NetworkPlayer>();
                if (networkPlayer == null || networkPlayer.isLocalPlayer)
                {
                    _playerTransform = controller.transform;
                    return;
                }
            }

            if (controllers.Length > 0 && controllers[0] != null)
                _playerTransform = controllers[0].transform;
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
