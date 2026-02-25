using UnityEngine;
using Core;

namespace Tunnel
{
    /// <summary>
    /// 플레이어의 움직임에 따라 TunnelMaskManager를 호출하여 땅을 판다.
    /// (기존 TunnelSegment 생성 방식 대체)
    /// </summary>
    public class TunnelGenerator : MonoBehaviour
    {
        private const int MAX_STAMPS_PER_UPDATE = 48;
        private const float MIN_EFFECTIVE_STEP_DISTANCE = 0.03f;
        private const float MIN_TELEPORT_RESET_DISTANCE = 0.8f;
        private const float DEFAULT_RESPAWN_ANCHOR_RADIUS = 0.9f;
        private const float DEFAULT_RESPAWN_ANCHOR_TIMEOUT = 1.5f;
        private const float APP_RESUME_DIG_SUPPRESS_SECONDS = 0.2f;
        private const float STEP_TO_RADIUS_RATIO = 0.35f;

        [Header("References")]
        [SerializeField] private TunnelMaskManager _maskManager;
        
        [Header("Settings")]
        [Tooltip("최소 이 거리만큼 이동해야 브러쉬를 찍음 (성능 최적화)")]
        [SerializeField] private float _stepDistance = 0.2f;
        [Tooltip("Large position jump is treated as teleport and tunnel interpolation is reset.")]
        [SerializeField] private float _teleportResetDistance = 4.5f;

        private Vector3 _lastPosition;
        private float _currentWidth = 1.0f;
        private bool _isDigging = false;
        private float _suppressDigUntilTime = -1f;
        private bool _respawnAnchorArmed;
        private Vector3 _respawnAnchorPosition;
        private float _respawnAnchorRadiusSqr;
        private float _respawnAnchorExpireAt;

        private void Awake()
        {
            if (_maskManager == null)
            {
                _maskManager = TunnelMaskManager.Instance;
            }
        }

        private void Start()
        {
            if (_maskManager == null)
            {
                _maskManager = FindObjectOfType<TunnelMaskManager>();
                if (_maskManager == null)
                {
                    Debug.LogError("[TunnelGenerator] TunnelMaskManager를 찾을 수 없습니다.");
                    enabled = false;
                    return;
                }
            }

            _lastPosition = transform.position;
            // NOTE: _isDigging은 여기서 초기화하지 않음.
            // AIController.Start()에서 SetDigging(true)를 먼저 호출했을 수 있으므로
            // 덮어쓰면 봇의 터널 생성이 안 됨.
        }

        private void Update()
        {
            if (!_isDigging) return;

            // A1 Fix: 성장에 따라 터널 크기 자동 연동
            _currentWidth = transform.localScale.x;

            if (Time.time < _suppressDigUntilTime)
            {
                _lastPosition = transform.position;
                return;
            }

            Vector3 currentPosition = transform.position;
            if (ShouldHoldDiggingForRespawnAnchor(currentPosition))
                return;

            Vector3 delta = currentPosition - _lastPosition;
            float distance = delta.magnitude;
            if (ShouldResetForTeleport(distance))
            {
                _lastPosition = currentPosition;
                return;
            }

            float stepDistance = ResolveEffectiveStepDistance();

            if (distance < stepDistance)
            {
                return;
            }

            // 프레임 드랍/ALT+TAB 이후에도 경로를 선분 샘플링해서 끊김(원-원-원)을 줄인다.
            Vector3 direction = delta / distance;
            int requiredStamps = Mathf.CeilToInt(distance / stepDistance);
            int stampsThisFrame = Mathf.Min(requiredStamps, MAX_STAMPS_PER_UPDATE);

            for (int i = 1; i <= stampsThisFrame; i++)
            {
                Vector3 samplePosition = _lastPosition + direction * (stepDistance * i);

                // 마지막 샘플이면서 이번 프레임에 경로를 모두 소화한 경우에는 정확히 현재 위치로 보정.
                if (i == stampsThisFrame && stampsThisFrame == requiredStamps)
                {
                    samplePosition = currentPosition;
                }

                Dig(samplePosition);
            }

            if (stampsThisFrame < requiredStamps)
            {
                // 이번 프레임 처리량 상한을 넘는 구간은 다음 프레임에서 이어서 처리.
                _lastPosition += direction * (stepDistance * stampsThisFrame);
                return;
            }

            _lastPosition = currentPosition;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus) return;
            HandleAppResumeDigReset();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus) return;
            HandleAppResumeDigReset();
        }

        private void HandleAppResumeDigReset()
        {
            _lastPosition = transform.position;
            _suppressDigUntilTime = Mathf.Max(_suppressDigUntilTime, Time.time + APP_RESUME_DIG_SUPPRESS_SECONDS);
        }

        private float ResolveEffectiveStepDistance()
        {
            float safeBaseStep = Mathf.Max(_stepDistance, MIN_EFFECTIVE_STEP_DISTANCE);
            float radius = Mathf.Max(_currentWidth * 0.5f, 0.05f);
            // DrawHole 가장자리 알파가 완만해서 반경의 70% 간격은 점선처럼 보일 수 있다.
            // 반경 대비 샘플 간격을 더 촘촘히(35%) 유지해 터널 연속성을 우선한다.
            float continuityStep = radius * STEP_TO_RADIUS_RATIO;
            return Mathf.Max(MIN_EFFECTIVE_STEP_DISTANCE, Mathf.Min(safeBaseStep, continuityStep));
        }

        private bool ShouldResetForTeleport(float distance)
        {
            float threshold = Mathf.Max(MIN_TELEPORT_RESET_DISTANCE, _teleportResetDistance);
            return distance >= threshold;
        }

        private void Dig(Vector3 pos)
        {
            // 터널 너비의 절반 = 반지름
            // 브러쉬 크기는 Shader에서 처리되지만, 여기서 반지름을 넘겨줌
            // _currentWidth는 지름.
            if (_maskManager != null)
            {
                _maskManager.DrawHole(pos, _currentWidth * 0.5f);
            }
        }

        /// <summary>
        /// 외부(PlayerController)에서 터널 너비 설정
        /// </summary>
        public void SetTunnelWidth(float width)
        {
            _currentWidth = width;
        }

        /// <summary>
        /// 땅파기 일시 정지/재개.
        /// [Stealth & Ambush] 즉시 Dig하지 않고, Update 루프에서 이동 후 자연스럽게 찍히도록 함.
        /// </summary>
        public void SetDigging(bool isDigging)
        {
            _isDigging = isDigging;
            if (isDigging)
            {
                _lastPosition = transform.position;
                // 즉시 Dig 호출 제거 — Update에서 이동 후 자연스럽게 찍힘
            }
        }

        /// <summary>
        /// 리스폰 지점 기준으로 위치 보간이 안정될 때까지 터널 생성을 홀드한다.
        /// (원격 플레이어가 죽은 위치 -> 리스폰 위치로 보간 이동하는 직선 터널 방지)
        /// </summary>
        public void ArmRespawnAnchor(Vector3 respawnPosition, float settleRadius = DEFAULT_RESPAWN_ANCHOR_RADIUS, float timeoutSeconds = DEFAULT_RESPAWN_ANCHOR_TIMEOUT)
        {
            float safeRadius = Mathf.Max(0.1f, settleRadius);
            float safeTimeout = Mathf.Max(0.2f, timeoutSeconds);

            _respawnAnchorArmed = true;
            _respawnAnchorPosition = respawnPosition;
            _respawnAnchorRadiusSqr = safeRadius * safeRadius;
            _respawnAnchorExpireAt = Time.time + safeTimeout;
            _lastPosition = transform.position;
        }

        /// <summary>
        /// 리스폰/위치 보정 직후 일정 시간 터널 생성을 억제한다.
        /// </summary>
        public void SuppressDiggingFor(float durationSeconds)
        {
            float duration = Mathf.Max(0f, durationSeconds);
            _suppressDigUntilTime = Mathf.Max(_suppressDigUntilTime, Time.time + duration);
            _lastPosition = transform.position;
        }

        private bool ShouldHoldDiggingForRespawnAnchor(Vector3 currentPosition)
        {
            if (!_respawnAnchorArmed)
                return false;

            if (Time.time >= _respawnAnchorExpireAt)
            {
                _respawnAnchorArmed = false;
                _lastPosition = currentPosition;
                return false;
            }

            Vector3 toAnchor = currentPosition - _respawnAnchorPosition;
            if (toAnchor.sqrMagnitude > _respawnAnchorRadiusSqr)
            {
                // 아직 리스폰 앵커로 수렴 중(네트워크 보간 이동 구간).
                _lastPosition = currentPosition;
                return true;
            }

            // 앵커 근처에 도달한 프레임은 터널을 스킵하고 다음 프레임부터 재개한다.
            _respawnAnchorArmed = false;
            _suppressDigUntilTime = Mathf.Max(_suppressDigUntilTime, Time.time + 0.05f);
            _lastPosition = currentPosition;
            return true;
        }
    }
}

