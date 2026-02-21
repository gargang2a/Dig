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
        [Header("References")]
        [SerializeField] private TunnelMaskManager _maskManager;
        
        [Header("Settings")]
        [Tooltip("최소 이 거리만큼 이동해야 브러쉬를 찍음 (성능 최적화)")]
        [SerializeField] private float _stepDistance = 0.2f;

        private Vector3 _lastPosition;
        private float _currentWidth = 1.0f;
        private bool _isDigging = false;

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

            float dist = Vector3.Distance(transform.position, _lastPosition);
            
            // 일정 거리 이상 움직였거나, 처음일 때
            if (dist >= _stepDistance)
            {
                Dig(transform.position);
                _lastPosition = transform.position;
            }
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
    }
}
