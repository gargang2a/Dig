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
    [RequireComponent(typeof(Core.MoleGrowth))]
    public class PlayerController : MonoBehaviour, IDigger
    {
        [Header("References")]
        [SerializeField] private Transform _visualRoot;

        private Core.MoleGrowth _growth;

        private Camera _mainCamera;
        private GameSettings _settings;
        private Rigidbody2D _rb;
        private bool _isBoosting;
        private bool _isDead;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _growth = GetComponent<Core.MoleGrowth>();
            if (_growth == null) _growth = gameObject.AddComponent<Core.MoleGrowth>();
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

            // 사운드 업데이트
            if (Systems.SoundManager.Instance != null)
            {
                // 실제 이동 중인지 확인 (속도 > 0)
                bool isMoving = CurrentSpeed > 0.1f;
                // 부스트는 입력(_isBoosting)과 점수(CurrentSpeed가 부스트 속도인지 확인하면 좋지만, 간단히 Score 체크)
                // Move()에서 계산된 상태를 가져오는게 좋지만, 여기서는 로직 중복을 최소화하여 재계산
                bool canBoost = _isBoosting && GameManager.Instance.CurrentScore > 0f;
                Systems.SoundManager.Instance.UpdateEngineSound(isMoving, canBoost);
            }
        }

        private void OnEnable()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.OnPlayerDied += OnGlobalDeath;
        }

        private void OnDisable()
        {
            if (GameManager.Instance != null)
                GameManager.Instance.OnPlayerDied -= OnGlobalDeath;
        }

        /// <summary>
        /// GameManager에서 전파된 사망 이벤트 핸들러.
        /// (MapBoundary 등 외부 요인으로 사망했을 때 처리를 위함)
        /// </summary>
        private void OnGlobalDeath()
        {
            if (!_isDead) Die();
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
            bool canBoost = _isBoosting && GameManager.Instance.CurrentScore > 0f;

            if (canBoost)
            {
                speed *= _settings.BoostMultiplier;
                float cost = _settings.BoostScoreCostPerSecond * Time.deltaTime;
                
                // 전역 점수(UI)와 성장 점수(크기) 모두 차감
                GameManager.Instance.AddScore(-cost);
                if (_growth != null) _growth.AddScore(-cost);
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

        /// <summary>
        /// 젬 획득 시 호출 (IDigger 구현).
        /// 전역 점수와 성장 점수를 모두 증가시킨다.
        /// </summary>
        public void AddScore(float amount)
        {
            if (GameManager.Instance != null)
                GameManager.Instance.AddScore(amount);

            if (_growth != null)
                _growth.AddScore(amount);
        }

        private void Die()
        {
            _isDead = true;
            CurrentSpeed = 0f;

            // GameManager에 사망 알림
            if (GameManager.Instance != null)
                GameManager.Instance.KillPlayer();

            // 사망 사운드 재생
            // 사망 사운드 재생 및 엔진 정지
            if (Systems.SoundManager.Instance != null)
            {
                Systems.SoundManager.Instance.PlayPlayerDie();
                Systems.SoundManager.Instance.StopEngineSound();
            }

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
