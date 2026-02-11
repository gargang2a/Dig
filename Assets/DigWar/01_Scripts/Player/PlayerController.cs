using UnityEngine;
using Core;
using Core.Data;

namespace Player
{
    /// <summary>
    /// 플레이어의 입력, 이동, 회전, 충돌 처리.
    /// Transform 기반 이동이므로 모든 로직이 Update에서 실행되며
    /// 카메라 추적과의 프레임 불일치가 발생하지 않는다.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    public class PlayerController : MonoBehaviour, IDigger
    {
        [Header("References")]
        [SerializeField] private Transform _visualRoot;

        private Camera _mainCamera;
        private GameSettings _settings;
        private Rigidbody2D _rb;
        private bool _isBoosting;
        private bool _isDead;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _rb.bodyType = RigidbodyType2D.Kinematic;
        }

        private void Start()
        {
            _mainCamera = Camera.main;

            if (GameManager.Instance == null || GameManager.Instance.Settings == null)
            {
                Debug.LogError("[PlayerController] GameManager 또는 Settings 누락");
                enabled = false;
                return;
            }

            _settings = GameManager.Instance.Settings;
        }

        private void Update()
        {
            if (_isDead || !GameManager.Instance.IsGameActive) return;

            HandleInput();
            Rotate();
            Move();
        }

        private void HandleInput()
        {
            _isBoosting = Input.GetMouseButton(0);
        }

        /// <summary>
        /// Y+ 방향(스프라이트 머리)으로 전진한다.
        /// 부스트 중에는 점수를 소모하여 가속한다.
        /// </summary>
        private void Move()
        {
            float speed = _settings.BaseSpeed * transform.localScale.x;

            if (_isBoosting && GameManager.Instance.CurrentScore > 0f)
            {
                speed *= _settings.BoostMultiplier;
                GameManager.Instance.AddScore(-_settings.BoostScoreCostPerSecond * Time.deltaTime);
            }

            CurrentSpeed = speed;
            transform.position += transform.up * (speed * Time.deltaTime);
        }

        /// <summary>현재 프레임의 실제 이동 속도. 부스트 포함.</summary>
        public float CurrentSpeed { get; private set; }

        /// <summary>현재 부스트 중인지 여부.</summary>
        public bool IsBoosting => _isBoosting;

        /// <summary>
        /// 마우스 방향으로 회전한다.
        /// Atan2가 X+축 기준이므로 Y+가 전방인 스프라이트에 맞춰 -90도 보정.
        /// 크기가 커질수록 회전이 느려져 대형 플레이어의 관성을 표현한다.
        /// </summary>
        private void Rotate()
        {
            Vector3 mouseWorldPos = _mainCamera.ScreenToWorldPoint(Input.mousePosition);
            mouseWorldPos.z = 0f;

            Vector2 direction = mouseWorldPos - transform.position;
            if (direction.sqrMagnitude < 0.01f) return;

            float targetAngle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - 90f;
            Quaternion targetRotation = Quaternion.Euler(0f, 0f, targetAngle);

            float scale = transform.localScale.x;
            float turnSpeed = _settings.BaseTurnSpeed / Mathf.Max(scale, 0.1f);

            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRotation,
                turnSpeed * Time.deltaTime
            );
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_isDead) return;

            // TunnelSegment 충돌 → 사망
            if (other.GetComponent<Tunnel.TunnelSegment>() != null)
                Die();
        }

        private void Die()
        {
            _isDead = true;
            CurrentSpeed = 0f;

            // GameManager에 사망 알림
            if (GameManager.Instance != null)
                GameManager.Instance.KillPlayer();

            // 터널 파괴
            var tunnel = GetComponent<Tunnel.TunnelGenerator>();
            if (tunnel != null)
                tunnel.DestroyAllSegments();

            // 시각적 피드백: 빨갛게 변하고 작아짐
            var sr = _visualRoot != null
                ? _visualRoot.GetComponent<SpriteRenderer>()
                : GetComponentInChildren<SpriteRenderer>();
            if (sr != null)
                sr.color = new Color(1f, 0.2f, 0.2f, 0.7f);

            transform.localScale *= 0.5f;

            Debug.Log($"[Player] 사망! 최종 점수: {GameManager.Instance?.CurrentScore:F0}");
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_visualRoot == null)
            {
                var found = transform.Find("Visuals");
                if (found != null)
                    _visualRoot = found;
            }
        }
#endif
    }
}
