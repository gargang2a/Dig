using UnityEngine;
using Core;
using Core.Data;

namespace Player
{
    /// <summary>
    /// 플레이어의 입력, 이동, 회전, 충돌 처리.
    /// [Stealth & Ambush] 평상시 터널 없이 이동, LMB 홀드 시 공격 모드(터널+부스트+처치).
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(Core.MoleGrowth))]
    public class PlayerController : MonoBehaviour, IDigger
    {
        [Header("References")]
        [SerializeField] private Transform _visualRoot;

        private Core.MoleGrowth _growth;
        private Tunnel.TunnelGenerator _tunnelGen;

        private Camera _mainCamera;
        private GameSettings _settings;
        private Rigidbody2D _rb;
        private bool _isAttacking;   // LMB 홀드 = 공격 모드
        private bool _isDead;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _growth = GetComponent<Core.MoleGrowth>();
            if (_growth == null) _growth = gameObject.AddComponent<Core.MoleGrowth>();
            _tunnelGen = GetComponent<Tunnel.TunnelGenerator>();
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
                bool isMoving = CurrentSpeed > 0.1f;
                bool canBoost = _isAttacking && GameManager.Instance.CurrentScore > 0f;
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
        /// </summary>
        private void OnGlobalDeath()
        {
            if (!_isDead) Die();
        }

        private void HandleInput()
        {
            bool wasAttacking = _isAttacking;
            _isAttacking = Input.GetMouseButton(0);

            // [Stealth & Ambush] 상태 전환 시에만 터널 토글 (매 프레임 호출 방지)
            if (_tunnelGen != null && wasAttacking != _isAttacking)
                _tunnelGen.SetDigging(_isAttacking);
        }

        /// <summary>
        /// Y+ 방향으로 전진. 공격 모드 시 부스트.
        /// </summary>
        private void Move()
        {
            float speed = _settings.BaseSpeed * transform.localScale.x;
            bool canBoost = _isAttacking && GameManager.Instance.CurrentScore > 0f;

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

        /// <summary>현재 부스트 중인지 여부. 공격 모드와 동일.</summary>
        public bool IsBoosting => _isAttacking;

        /// <summary>공격 모드(Assault) 여부.</summary>
        public bool IsAttacking => _isAttacking;

        /// <summary>
        /// 마우스 방향으로 회전.
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

        /// <summary>
        /// [Stealth & Ambush] 충돌 처리.
        /// 공격 모드일 때 적과 충돌하면 적을 처치한다.
        /// </summary>
        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_isDead) return;

            // 공격 모드가 아니면 충돌 무시
            if (!_isAttacking) return;

            // AI 봇과의 충돌 처리
            var enemy = other.GetComponent<IDigger>();
            if (enemy != null && enemy != (IDigger)this)
            {
                Debug.Log($"💀 [Assault Kill] 공격 모드로 적 처치!");
                enemy.Die();
            }
        }

        /// <summary>
        /// 젬 획득 시 호출 (IDigger 구현).
        /// </summary>
        public void AddScore(float amount)
        {
            if (GameManager.Instance != null)
                GameManager.Instance.AddScore(amount);

            if (_growth != null)
                _growth.AddScore(amount);
        }

        /// <summary>
        /// 사망 처리 (IDigger 구현).
        /// </summary>
        public void Die()
        {
            _isDead = true;
            CurrentSpeed = 0f;

            // GameManager에 사망 알림
            if (GameManager.Instance != null)
                GameManager.Instance.KillPlayer();

            // 사망 사운드 재생 및 엔진 정지
            if (Systems.SoundManager.Instance != null)
            {
                Systems.SoundManager.Instance.PlayPlayerDie();
                Systems.SoundManager.Instance.StopEngineSound();
            }

            // 터널 파괴 (더 이상 파지 않음)
            if (_tunnelGen != null)
                _tunnelGen.SetDigging(false);

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
