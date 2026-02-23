using UnityEngine;
using Core;
using Core.Data;
using System.Collections.Generic;
using Mirror;

namespace Player
{
    /// <summary>
    /// 플레이어 입력, 이동, 회전, 충돌을 처리한다.
    /// [Stealth & Ambush] 기본 이동 중 LMB 입력 시 공격/부스트 모드로 동작한다.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(Core.MoleGrowth))]
    public class PlayerController : MonoBehaviour, IDigger
    {
        private static readonly HashSet<PlayerController> _activeControllers = new HashSet<PlayerController>();
        public static IReadOnlyCollection<PlayerController> ActiveControllers => _activeControllers;
        public static PlayerController LocalController { get; private set; }

        [Header("References")]
        [SerializeField] private Transform _visualRoot;

        private Core.MoleGrowth _growth;
        private Tunnel.TunnelGenerator _tunnelGen;

        private Camera _mainCamera;
        private GameSettings _settings;
        private Rigidbody2D _rb;
        private bool _isAttacking;   // LMB 입력 = 공격 모드
        private bool _isDead;
        private float _nextNetworkKillRequestAt;
        private bool _debugMovementLocked;

        private const float NETWORK_KILL_REQUEST_INTERVAL = 0.1f;

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

            // 봇과 동일하게 항상 터널 생성
            if (_tunnelGen != null)
                _tunnelGen.SetDigging(true);
        }

        private void Update()
        {
            if (_isDead || !GameManager.Instance.IsGameActive) return;
            if (_debugMovementLocked)
            {
                _isAttacking = false;
                CurrentSpeed = 0f;
                return;
            }

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
            _activeControllers.Add(this);
            TryPromoteAsLocal();

            if (GameManager.Instance != null)
                GameManager.Instance.OnPlayerDied += OnGlobalDeath;
        }

        private void OnDisable()
        {
            _activeControllers.Remove(this);
            if (LocalController == this)
                LocalController = null;

            if (GameManager.Instance != null)
                GameManager.Instance.OnPlayerDied -= OnGlobalDeath;
        }

        public static void RegisterLocal(PlayerController controller)
        {
            if (controller != null)
                LocalController = controller;
        }

        private void TryPromoteAsLocal()
        {
            var networkPlayer = GetComponent<Network.NetworkPlayer>();
            if (networkPlayer == null || networkPlayer.isLocalPlayer)
                LocalController = this;
        }

        /// <summary>
        /// GameManager에서 전파된 사망 이벤트를 처리한다.
        /// </summary>
        private void OnGlobalDeath()
        {
            if (!_isDead) Die();
        }

        private void HandleInput()
        {
            _isAttacking = Input.GetMouseButton(0);
            // 터널은 항상 생성하며, LMB는 부스트/공격에만 사용한다.
        }

        /// <summary>
        /// 바라보는 방향으로 전진한다. 공격 모드에서는 부스트를 적용한다.
        /// </summary>
        private void Move()
        {
            float speed = _settings.BaseSpeed * transform.localScale.x;
            bool canBoost = _isAttacking && GameManager.Instance.CurrentScore > 0f;

            if (canBoost)
            {
                speed *= _settings.BoostMultiplier;
                float cost = _settings.BoostScoreCostPerSecond * Time.deltaTime;

                // 전역 점수(UI)와 성장 점수(크기)를 함께 차감
                GameManager.Instance.AddScore(-cost);
                if (_growth != null) _growth.AddScore(-cost);
            }

            CurrentSpeed = speed;
            transform.position += transform.up * (speed * Time.deltaTime);
        }

        /// <summary>현재 프레임의 실제 이동 속도(부스트 포함).</summary>
        public float CurrentSpeed { get; private set; }

        /// <summary>현재 부스트 중인지 여부(공격 모드와 동일).</summary>
        public bool IsBoosting => _isAttacking;

        /// <summary>공격 모드(Assault) 여부.</summary>
        public bool IsAttacking => _isAttacking;

        /// <summary>
        /// 마우스 방향으로 회전한다.
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
        /// QA/디버그용 로컬 이동 잠금 토글.
        /// </summary>
        public void SetDebugMovementLocked(bool isLocked)
        {
            if (_debugMovementLocked == isLocked) return;

            _debugMovementLocked = isLocked;
            _isAttacking = false;
            CurrentSpeed = 0f;

            if (_tunnelGen != null)
                _tunnelGen.SetDigging(!_debugMovementLocked && !_isDead);
        }

        public bool IsDebugMovementLocked => _debugMovementLocked;

        /// <summary>
        /// [Stealth & Ambush] 충돌 처리.
        /// 공격 모드에서 상대와 충돌하면 처치를 시도한다.
        /// 네트워크 플레이어는 서버 확정 경로, 봇은 로컬 처리한다.
        /// </summary>
        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_isDead) return;
            if (!_isAttacking) return;

            Network.NetworkPlayer myNetPlayer = GetComponent<Network.NetworkPlayer>();
            bool isNetworkMode = myNetPlayer != null || NetworkClient.active || NetworkServer.active;

            // 네트워크 모드에서는 서버 확정 경로만 사용한다.
            NetworkIdentity targetIdentity = other.GetComponent<NetworkIdentity>();
            if (targetIdentity == null)
                targetIdentity = other.GetComponentInParent<NetworkIdentity>();

            if (isNetworkMode)
            {
                if (myNetPlayer == null) return;
                if (!myNetPlayer.isLocalPlayer) return;
                if (!Network.NetworkPlayer.CanSendCommands) return;
                if (targetIdentity == null) return;
                if (Time.time < _nextNetworkKillRequestAt) return;

                _nextNetworkKillRequestAt = Time.time + NETWORK_KILL_REQUEST_INTERVAL;
                myNetPlayer.CmdRequestKill(targetIdentity);
                return;
            }

            // 싱글플레이 모드에서만 로컬 즉시 처치를 허용한다.
            IDigger enemy = other.GetComponent<IDigger>();
            if (enemy == null)
                enemy = other.GetComponentInParent<IDigger>();

            if (enemy != null && enemy != (IDigger)this)
            {
                Debug.Log("[Assault Kill] 공격 모드로 대상 처치");
                enemy.Die();
            }
        }

        /// <summary>
        /// OnTriggerEnter2D와 동일한 처치 로직 유지(IDigger 구현).
        /// </summary>
        private void OnTriggerStay2D(Collider2D other){
            OnTriggerEnter2D(other);
        }

        public void AddScore(float amount)
        {
            if (GameManager.Instance != null)
                GameManager.Instance.AddScore(amount);

            if (_growth != null)
                _growth.AddScore(amount);
        }

        /// <summary>
        /// 사망 처리(IDigger 구현).
        /// 로컬 플레이어는 GameManager 사망 이벤트를 발생시킨다.
        /// 원격 플레이어는 시각 효과만 적용한다.
        /// </summary>
        public void Die()
        {
            if (_isDead) return;
            _isDead = true;
            CurrentSpeed = 0f;

            // 로컬 플레이어만 GameOver UI 트리거
            var netPlayer = GetComponent<Network.NetworkPlayer>();
            bool isLocal = netPlayer == null || netPlayer.isLocalPlayer;
            if (isLocal && GameManager.Instance != null)
                GameManager.Instance.KillPlayer();

            // 사망 사운드(로컬만)
            if (isLocal && Systems.SoundManager.Instance != null)
            {
                Systems.SoundManager.Instance.PlayPlayerDie();
                Systems.SoundManager.Instance.StopEngineSound();
            }

            // 터널 파기 중지
            if (_tunnelGen != null)
                _tunnelGen.SetDigging(false);

            // 사망 시각 피드백
            _originalScale = transform.localScale; // 리스폰 복구용 저장
            var sr = _visualRoot != null
                ? _visualRoot.GetComponent<SpriteRenderer>()
                : GetComponentInChildren<SpriteRenderer>();
            if (sr != null)
            {
                _originalColor = sr.color; // 리스폰 복구용 저장
                sr.color = new Color(1f, 0.2f, 0.2f, 0.7f);
            }
            transform.localScale *= 0.5f;

            Debug.Log($"[Player] 사망! 최종 점수: {GameManager.Instance?.CurrentScore:F0}");
        }

        /// <summary>
        /// 원격 플레이어 사망/리스폰 상태를 복구한다(클라이언트 사이드).
        /// 서버의 IsDead SyncVar 훅에서 호출된다.
        /// </summary>
        public void RemoteRespawn()
        {
            _isDead = false;
            _nextNetworkKillRequestAt = 0f;

            float minScale = GameManager.Instance != null
                ? GameManager.Instance.Settings.MinScale : 0.5f;
            transform.localScale = Vector3.one * minScale;

            var sr = _visualRoot != null
                ? _visualRoot.GetComponent<SpriteRenderer>()
                : GetComponentInChildren<SpriteRenderer>();
            if (sr != null)
                sr.color = _originalColor;

            if (_growth != null)
                _growth.SetScore(0f);

            if (_tunnelGen != null)
                _tunnelGen.SetDigging(true);
        }

        /// <summary>
        /// Slither.io 스타일 리스폰: 기존 서버/싱글 경로에서 공통 사용.
        /// </summary>
        public void Respawn()
        {
            _isDead = false;
            _nextNetworkKillRequestAt = 0f;

            // 랜덤 위치로 리스폰
            float mapRadius = GameManager.Instance != null
                ? GameManager.Instance.Settings.MapRadius * 0.5f : 15f;
            Vector2 randomPos = Random.insideUnitCircle * mapRadius;
            transform.position = new Vector3(randomPos.x, randomPos.y, 0f);
            transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

            // 스케일/색상 복원
            float minScale = GameManager.Instance != null
                ? GameManager.Instance.Settings.MinScale : 0.5f;
            transform.localScale = Vector3.one * minScale;

            var sr = _visualRoot != null
                ? _visualRoot.GetComponent<SpriteRenderer>()
                : GetComponentInChildren<SpriteRenderer>();
            if (sr != null)
                sr.color = _originalColor;

            // 점수/크기 초기화
            if (GameManager.Instance != null)
                GameManager.Instance.ResetForRespawn();

            // MoleGrowth 크기 초기화
            if (_growth != null)
                _growth.SetScore(0f);

            // 터널 생성 재시작
            if (_tunnelGen != null)
                _tunnelGen.SetDigging(true);

            CurrentSpeed = _settings != null ? _settings.BaseSpeed : 3f;

            Debug.Log("[Player] 리스폰");
        }

        private Vector3 _originalScale;
        private Color _originalColor = Color.white;

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


