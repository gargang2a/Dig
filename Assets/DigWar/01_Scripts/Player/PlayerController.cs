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
        private Network.NetworkPlayer _networkPlayer;

        private Camera _mainCamera;
        private GameSettings _settings;
        private Rigidbody2D _rb;
        private bool _isAttacking;   // LMB 입력 = 공격 모드
        private bool _isDead;
        private float _nextNetworkKillRequestAt;
        private bool _debugMovementLocked;
        private Vector3 _autoMoveTarget;
        private float _nextAutoRetargetAt;
        private float _autoRespawnAt = -1f;
        private float _boostSpentScoreAccumulator;
        private float _respawnInvincibleUntil = -1f;
        private SpriteRenderer _primarySpriteRenderer;

        private const float NETWORK_KILL_REQUEST_INTERVAL = 0.02f;
        private const float AUTO_MOVE_RETARGET_INTERVAL = 1.2f;
        private const float AUTO_MOVE_REACH_DISTANCE = 1.0f;
        private const float AUTO_RESPAWN_DELAY_SECONDS = 1.0f;
        private const float AUTO_TARGET_MAX_SEARCH_DISTANCE = 40f;
        private const float LOCAL_RESPAWN_TUNNEL_RESUME_DELAY_SECONDS = 0.35f;
        private const float REMOTE_RESPAWN_TUNNEL_RESUME_DELAY_SECONDS = 0.9f;
        private const int BOOST_DROP_MAX_PER_MOVE = 6;
        private const float BOOST_DROP_PATH_JITTER = 0.2f;
        private const float RESPAWN_INVINCIBILITY_SECONDS = 1.0f;
        private const float RESPAWN_BLINK_SPEED = 14.0f;
        private const float RESPAWN_BLINK_MIN_ALPHA = 0.35f;

        private static bool _globalAutoModeEnabled;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            _growth = GetComponent<Core.MoleGrowth>();
            if (_growth == null) _growth = gameObject.AddComponent<Core.MoleGrowth>();
            _tunnelGen = GetComponent<Tunnel.TunnelGenerator>();
            _networkPlayer = GetComponent<Network.NetworkPlayer>();
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
            UpdateRespawnInvincibilityVisual();

            bool autoModeActive = IsAutoModeActive;
            if (_isDead || !GameManager.Instance.IsGameActive)
            {
                HandleAutoRespawnWhenDead(autoModeActive);
                return;
            }

            if (_debugMovementLocked)
            {
                _isAttacking = false;
                CurrentSpeed = 0f;
                return;
            }

            if (autoModeActive)
                RunAutoModeTick();
            else
            {
                HandleInput();
                Rotate();
                Move();
            }

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

        public static bool IsGlobalAutoModeEnabled => _globalAutoModeEnabled;

        public bool IsAutoModeActive => _globalAutoModeEnabled && IsLocalControllable();

        public static void SetGlobalAutoModeEnabled(bool enabled)
        {
            if (_globalAutoModeEnabled == enabled) return;
            _globalAutoModeEnabled = enabled;

            foreach (PlayerController controller in _activeControllers)
            {
                if (controller == null) continue;
                controller.OnAutoModeChanged(enabled);
            }

            Debug.Log($"[AutoTest] Player auto mode {(enabled ? "ON" : "OFF")}");
        }

        private void TryPromoteAsLocal()
        {
            if (_networkPlayer == null || _networkPlayer.isLocalPlayer)
                LocalController = this;
        }

        private bool IsLocalControllable()
        {
            return _networkPlayer == null || _networkPlayer.isLocalPlayer;
        }

        private void OnAutoModeChanged(bool enabled)
        {
            if (!enabled)
            {
                _isAttacking = false;
                _autoRespawnAt = -1f;
            }

            _nextAutoRetargetAt = 0f;
        }

        private void RunAutoModeTick()
        {
            Vector3 targetPosition = ResolveAutoMoveTarget();
            RotateTowards(targetPosition);
            _isAttacking = !IsRespawnInvincible;
            Move();
        }

        private Vector3 ResolveAutoMoveTarget()
        {
            if (TryGetNearestAutoCombatTarget(out Transform target))
            {
                _autoMoveTarget = target.position;
                _nextAutoRetargetAt = Time.time + 0.2f;
                return _autoMoveTarget;
            }

            float reachedSqrDistance = AUTO_MOVE_REACH_DISTANCE * AUTO_MOVE_REACH_DISTANCE;
            if (Time.time >= _nextAutoRetargetAt ||
                (transform.position - _autoMoveTarget).sqrMagnitude <= reachedSqrDistance)
            {
                float mapRadius = _settings != null ? _settings.MapRadius : 65f;
                float patrolRadiusRatio = _settings != null ? _settings.AutoPatrolRadiusRatio : 0.75f;
                float patrolRadius = Mathf.Max(1f, mapRadius * patrolRadiusRatio);
                Vector2 patrolPoint = Random.insideUnitCircle * patrolRadius;
                _autoMoveTarget = new Vector3(patrolPoint.x, patrolPoint.y, 0f);
                _nextAutoRetargetAt = Time.time + AUTO_MOVE_RETARGET_INTERVAL;
            }

            return _autoMoveTarget;
        }

        private bool TryGetNearestAutoCombatTarget(out Transform targetTransform)
        {
            targetTransform = null;

            var localNetworkPlayer = GetComponent<Network.NetworkPlayer>();
            if (localNetworkPlayer == null)
                return false;

            float maxSearchSqrDistance = AUTO_TARGET_MAX_SEARCH_DISTANCE * AUTO_TARGET_MAX_SEARCH_DISTANCE;
            float bestSqrDistance = maxSearchSqrDistance;

            foreach (Network.NetworkPlayer player in Network.NetworkPlayer.ActivePlayers)
            {
                if (player == null || player == localNetworkPlayer) continue;
                if (player.IsDead) continue;

                float sqrDistance = (player.transform.position - transform.position).sqrMagnitude;
                if (sqrDistance >= bestSqrDistance) continue;

                bestSqrDistance = sqrDistance;
                targetTransform = player.transform;
            }

            foreach (Network.NetworkBot bot in Network.NetworkBot.ActiveBots)
            {
                if (bot == null) continue;
                var botController = bot.GetComponent<AIController>();
                if (botController != null && botController.IsDead) continue;

                float sqrDistance = (bot.transform.position - transform.position).sqrMagnitude;
                if (sqrDistance >= bestSqrDistance) continue;

                bestSqrDistance = sqrDistance;
                targetTransform = bot.transform;
            }

            return targetTransform != null;
        }

        private void HandleAutoRespawnWhenDead(bool autoModeActive)
        {
            if (!_isDead)
            {
                _autoRespawnAt = -1f;
                return;
            }

            if (!autoModeActive)
            {
                _autoRespawnAt = -1f;
                return;
            }

            var networkPlayer = GetComponent<Network.NetworkPlayer>();
            if (networkPlayer != null && networkPlayer.isLocalPlayer)
                return; // NetworkPlayer가 CmdRespawn 경로를 담당한다.

            if (_autoRespawnAt < 0f)
                _autoRespawnAt = Time.unscaledTime + AUTO_RESPAWN_DELAY_SECONDS;

            if (Time.unscaledTime < _autoRespawnAt)
                return;

            _autoRespawnAt = -1f;
            Respawn();
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
            if (IsRespawnInvincible)
            {
                _isAttacking = false;
                return;
            }

            _isAttacking = Input.GetMouseButton(0);
            // 터널은 항상 생성하며, LMB는 부스트/공격에만 사용한다.
        }

        /// <summary>
        /// 바라보는 방향으로 전진한다. 공격 모드에서는 부스트를 적용한다.
        /// </summary>
        private void Move()
        {
            Vector3 previousPosition = transform.position;
            float speed = _settings.BaseSpeed * transform.localScale.x;
            bool canBoost = _isAttacking && !IsRespawnInvincible && GameManager.Instance.CurrentScore > 0f;
            float spentScore = 0f;

            if (canBoost)
            {
                speed *= _settings.BoostMultiplier;
                float cost = _settings.BoostScoreCostPerSecond * Time.deltaTime;
                spentScore = Mathf.Max(0f, cost);

                // 전역 점수(UI)와 성장 점수(크기)를 함께 차감
                GameManager.Instance.AddScore(-cost);
                if (_growth != null) _growth.AddScore(-cost);
            }

            CurrentSpeed = speed;
            transform.position += transform.up * (speed * Time.deltaTime);

            if (spentScore > 0f)
                EmitBoostGemDrops(spentScore, previousPosition, transform.position);
        }

        private void EmitBoostGemDrops(float spentScore, Vector3 fromPosition, Vector3 toPosition)
        {
            float gemScoreUnit = ResolveGemScoreUnit();
            if (gemScoreUnit <= 0f)
                return;

            _boostSpentScoreAccumulator += Mathf.Max(0f, spentScore);
            int spawnCount = Mathf.FloorToInt(_boostSpentScoreAccumulator / gemScoreUnit);
            if (spawnCount <= 0)
                return;

            spawnCount = Mathf.Min(spawnCount, BOOST_DROP_MAX_PER_MOVE);
            _boostSpentScoreAccumulator = Mathf.Max(0f, _boostSpentScoreAccumulator - (gemScoreUnit * spawnCount));

            for (int i = 0; i < spawnCount; i++)
            {
                float t = (i + 1f) / (spawnCount + 1f);
                Vector3 dropPosition = Vector3.Lerp(fromPosition, toPosition, t);
                Vector2 jitter = Random.insideUnitCircle * BOOST_DROP_PATH_JITTER;
                dropPosition.x += jitter.x;
                dropPosition.y += jitter.y;
                DropSpentGemAt(dropPosition);
            }
        }

        private float ResolveGemScoreUnit()
        {
            if (_settings == null)
                return 8f;

            return Mathf.Max(0.1f, _settings.GemScore);
        }

        private void DropSpentGemAt(Vector3 worldPosition)
        {
            World.GemSpawner spawner = World.GemSpawner.Instance;
            if (spawner == null)
                return;

            bool isNetworkMode = _networkPlayer != null || NetworkClient.active || NetworkServer.active;
            if (isNetworkMode)
            {
                if (_networkPlayer == null || !_networkPlayer.isLocalPlayer)
                    return;

                float currentScore = GameManager.Instance != null ? GameManager.Instance.CurrentScore : 0f;
                _networkPlayer.RequestBoostGemDrop(worldPosition, currentScore);
                return;
            }

            spawner.DropGemAt(worldPosition, forceDrop: true);
        }

        /// <summary>현재 프레임의 실제 이동 속도(부스트 포함).</summary>
        public float CurrentSpeed { get; private set; }

        /// <summary>현재 부스트 중인지 여부(공격 모드와 동일).</summary>
        public bool IsBoosting => _isAttacking;

        /// <summary>공격 모드(Assault) 여부.</summary>
        public bool IsAttacking => _isAttacking;

        /// <summary>리스폰 직후 무적 상태 여부.</summary>
        public bool IsRespawnInvincible => !_isDead && Time.time < _respawnInvincibleUntil;

        private void BeginRespawnInvincibility(float durationSeconds = RESPAWN_INVINCIBILITY_SECONDS)
        {
            if (durationSeconds <= 0f)
            {
                StopRespawnInvincibility(resetVisual: true);
                return;
            }

            _respawnInvincibleUntil = Time.time + durationSeconds;
            ApplyRespawnBlinkVisual(Time.time);
        }

        private void StopRespawnInvincibility(bool resetVisual)
        {
            _respawnInvincibleUntil = -1f;
            if (resetVisual)
                ResetRespawnBlinkVisual();
        }

        private void UpdateRespawnInvincibilityVisual()
        {
            if (_isDead)
                return;

            if (IsRespawnInvincible)
            {
                ApplyRespawnBlinkVisual(Time.time);
                return;
            }

            if (_respawnInvincibleUntil >= 0f)
                StopRespawnInvincibility(resetVisual: true);
        }

        private void ApplyRespawnBlinkVisual(float currentTime)
        {
            SpriteRenderer sr = ResolvePrimarySpriteRenderer();
            if (sr == null)
                return;

            float baseAlpha = Mathf.Clamp01(Mathf.Max(0.05f, _originalColor.a));
            float pulse = Mathf.PingPong(currentTime * RESPAWN_BLINK_SPEED, 1f);
            float alpha = Mathf.Lerp(RESPAWN_BLINK_MIN_ALPHA * baseAlpha, baseAlpha, pulse);

            Color blinkColor = _originalColor;
            blinkColor.a = alpha;
            sr.color = blinkColor;
        }

        private void ResetRespawnBlinkVisual()
        {
            SpriteRenderer sr = ResolvePrimarySpriteRenderer();
            if (sr == null)
                return;

            sr.color = _originalColor;
        }

        private SpriteRenderer ResolvePrimarySpriteRenderer()
        {
            if (_primarySpriteRenderer != null)
                return _primarySpriteRenderer;

            if (_visualRoot != null)
            {
                _primarySpriteRenderer = _visualRoot.GetComponent<SpriteRenderer>();
                if (_primarySpriteRenderer != null)
                    return _primarySpriteRenderer;

                _primarySpriteRenderer = _visualRoot.GetComponentInChildren<SpriteRenderer>(true);
                if (_primarySpriteRenderer != null)
                    return _primarySpriteRenderer;
            }

            _primarySpriteRenderer = GetComponentInChildren<SpriteRenderer>(true);
            return _primarySpriteRenderer;
        }

        /// <summary>
        /// 마우스 방향으로 회전한다.
        /// </summary>
        private void Rotate()
        {
            Vector3 mouseWorldPos = _mainCamera.ScreenToWorldPoint(Input.mousePosition);
            mouseWorldPos.z = 0f;
            RotateTowards(mouseWorldPos);
        }

        private void RotateTowards(Vector3 worldPosition)
        {
            Vector2 direction = worldPosition - transform.position;
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
            if (IsRespawnInvincible) return;

            Network.NetworkPlayer myNetPlayer = GetComponent<Network.NetworkPlayer>();
            bool isNetworkMode = myNetPlayer != null || NetworkClient.active || NetworkServer.active;

            if (isNetworkMode)
            {
                if (myNetPlayer == null) return;
                if (!myNetPlayer.isLocalPlayer) return;
                if (!Network.NetworkPlayer.CanSendCommands) return;
                if (Time.time < _nextNetworkKillRequestAt) return;

                NetworkIdentity targetIdentity = ResolveNetworkKillTarget(myNetPlayer, other);
                if (targetIdentity == null) return;

                Vector2 attackerReportedPos = _rb != null
                    ? _rb.position
                    : (Vector2)transform.position;
                Rigidbody2D targetRb = targetIdentity.GetComponent<Rigidbody2D>();
                Vector2 targetReportedPos = targetRb != null
                    ? targetRb.position
                    : (Vector2)targetIdentity.transform.position;
                _nextNetworkKillRequestAt = Time.time + NETWORK_KILL_REQUEST_INTERVAL;
                myNetPlayer.CmdRequestKillWithReported(targetIdentity, attackerReportedPos, targetReportedPos);
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

        private NetworkIdentity ResolveNetworkKillTarget(Network.NetworkPlayer myNetPlayer, Collider2D triggerCollider)
        {
            if (myNetPlayer == null || triggerCollider == null) return null;

            NetworkIdentity candidate = triggerCollider.GetComponent<NetworkIdentity>();
            if (candidate == null)
                candidate = triggerCollider.GetComponentInParent<NetworkIdentity>();
            if (candidate == null) return null;
            if (candidate == myNetPlayer.netIdentity) return null;
            if (!candidate.gameObject.activeInHierarchy) return null;

            var targetPlayer = candidate.GetComponent<Network.NetworkPlayer>();
            if (targetPlayer != null && targetPlayer.IsDead) return null;

            var targetBot = candidate.GetComponent<Network.NetworkBot>();
            if (targetBot != null)
            {
                var botController = targetBot.GetComponent<AIController>();
                if (botController != null && botController.IsDead) return null;
            }

            return candidate;
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
            _isAttacking = false;
            _autoRespawnAt = -1f;
            _boostSpentScoreAccumulator = 0f;
            StopRespawnInvincibility(resetVisual: true);

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
            RemoteRespawn(transform.position);
        }

        public void RemoteRespawn(Vector3 respawnPosition)
        {
            Vector3 preRespawnPosition = transform.position;

            _isDead = false;
            _nextNetworkKillRequestAt = 0f;
            _autoRespawnAt = -1f;
            _isAttacking = false;
            _nextAutoRetargetAt = 0f;
            _autoMoveTarget = respawnPosition;
            _boostSpentScoreAccumulator = 0f;

            Vector3 snappedRespawnPosition = new Vector3(respawnPosition.x, respawnPosition.y, transform.position.z);
            transform.position = snappedRespawnPosition;
            if (_rb != null)
                _rb.position = new Vector2(snappedRespawnPosition.x, snappedRespawnPosition.y);

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

            BeginRespawnInvincibility();

            if (_tunnelGen != null)
            {
                float respawnJumpDistance = Vector2.Distance(preRespawnPosition, snappedRespawnPosition);
                float settleRadius = respawnJumpDistance >= 1.5f ? 1.25f : 0.9f;
                float anchorTimeout = respawnJumpDistance >= 4f ? 2.2f : 1.5f;
                _tunnelGen.ArmRespawnAnchor(snappedRespawnPosition, settleRadius, anchorTimeout);
                _tunnelGen.SuppressDiggingFor(REMOTE_RESPAWN_TUNNEL_RESUME_DELAY_SECONDS);
                _tunnelGen.SetDigging(true);
            }
        }

        /// <summary>
        /// Slither.io 스타일 리스폰: 기존 서버/싱글 경로에서 공통 사용.
        /// </summary>
        public void Respawn()
        {
            _isDead = false;
            _nextNetworkKillRequestAt = 0f;
            _autoRespawnAt = -1f;
            _isAttacking = false;
            _boostSpentScoreAccumulator = 0f;

            // 랜덤 위치로 리스폰
            var settings = GameManager.Instance != null ? GameManager.Instance.Settings : _settings;
            float mapRadius = settings != null
                ? settings.MapRadius * settings.RespawnRadiusRatio
                : 32.5f;
            mapRadius = Mathf.Max(1f, mapRadius);
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

            BeginRespawnInvincibility();

            // 터널 생성 재시작
            if (_tunnelGen != null)
            {
                _tunnelGen.ArmRespawnAnchor(transform.position);
                _tunnelGen.SuppressDiggingFor(LOCAL_RESPAWN_TUNNEL_RESUME_DELAY_SECONDS);
                _tunnelGen.SetDigging(true);
            }

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


