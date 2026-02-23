using UnityEngine;
using Mirror;
using System.Collections.Generic;
using Core.Data;

namespace Network
{
    /// <summary>
    /// 네트워크 플레이어 컴포넌트.
    /// Mirror NetworkBehaviour를 기반으로 로컬/원격 플레이어를 구분하고,
    /// 입력은 서버로 전달하며 서버 확정 결과를 동기화한다.
    ///
    /// [Phase 2 목표]
    /// - 로컬 플레이어만 입력을 처리하고 카메라가 따라간다.
    /// - 서버가 이동/상태를 계산하고 모든 클라이언트에 동기화한다.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkPlayer : NetworkBehaviour
    {
        private static readonly HashSet<NetworkPlayer> _activePlayers = new HashSet<NetworkPlayer>();
        public static IReadOnlyCollection<NetworkPlayer> ActivePlayers => _activePlayers;
        public static NetworkPlayer LocalPlayer { get; private set; }
        public static bool CanSendCommands =>
            NetworkClient.active &&
            NetworkClient.connection != null &&
            NetworkClient.isConnected;

        [Header("References")]
        [SerializeField] private Player.PlayerController _playerController;

        // ===== 동기화 변수 =====

        /// <summary>
        /// 서버에서 관리하는 플레이어 이름.
        /// 값이 바뀌면 OnNameChanged 훅이 호출된다.
        /// </summary>
        [SyncVar(hook = nameof(OnNameChanged))]
        public string PlayerName;

        /// <summary>
        /// 서버에서 관리하는 점수.
        /// </summary>
        [SyncVar]
        public float Score;

        /// <summary>
        /// 서버에서 관리하는 사망 상태.
        /// </summary>
        [SyncVar(hook = nameof(OnIsDeadChanged))]
        public bool IsDead;

        [SyncVar]
        private bool _isAssaultActive;

        [SyncVar(hook = nameof(OnSyncedScaleChanged))]
        private float _syncedScale;

        // ===== 생명주기 =====

        public override void OnStartLocalPlayer()
        {
            base.OnStartLocalPlayer();
            LocalPlayer = this;
            Player.PlayerController.RegisterLocal(_playerController != null ? _playerController : GetComponent<Player.PlayerController>());

            // 로컬 플레이어만 카메라가 따라간다.
            if (Systems.CameraFollow.Instance != null)
                Systems.CameraFollow.Instance.SetTarget(transform);

            // 로컬 플레이어만 입력 활성화.
            if (_playerController != null)
                _playerController.enabled = true;

            // 서버로 플레이어 이름 전송.
            string name = Core.GameManager.Instance?.PlayerName ?? "Player";
            if (CanSendCommands)
            {
                CmdSetName(name);
            }

            Debug.Log($"[NetworkPlayer] 로컬 플레이어 활성화: {name}");
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _syncedScale = ResolveScaleFromScore(Score);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            _activePlayers.Add(this);

            // 원격 플레이어 입력은 비활성화한다(서버 동기화 전용).
            if (!isLocalPlayer)
            {
                if (_playerController != null)
                    _playerController.enabled = false;
                var growth = GetComponent<Core.MoleGrowth>();
                if (growth != null)
                    growth.enabled = false;

                ApplyRemoteScale(_syncedScale > 0f ? _syncedScale : ResolveScaleFromScore(Score));

                // 원격 객체도 터널 생성은 유지한다.
                var tg = GetComponent<Tunnel.TunnelGenerator>();
                if (tg != null) tg.SetDigging(true);
            }
        }

        public override void OnStopClient()
        {
            if (LocalPlayer == this)
                LocalPlayer = null;

            _activePlayers.Remove(this);
            base.OnStopClient();
        }

        private void Update()
        {
            if (!isLocalPlayer) return;
            if (!CanSendCommands) return;

            SyncAssaultState();

            _scoreUpdateTimer -= Time.deltaTime;
            if (_scoreUpdateTimer <= 0f)
            {
                _scoreUpdateTimer = SCORE_SYNC_INTERVAL;

                // 점수 동기화.
                float currentScore = Core.GameManager.Instance != null
                    ? Core.GameManager.Instance.CurrentScore : 0f;
                if (Mathf.Abs(currentScore - _lastSentScore) > 0.5f)
                {
                    _lastSentScore = currentScore;
                    CmdUpdateScore(currentScore);
                }

                // 이름 동기화(최초 1회).
                if (!_nameSynced && Core.GameManager.Instance != null)
                {
                    string currentName = Core.GameManager.Instance.PlayerName;
                    if (!string.IsNullOrEmpty(currentName) && currentName != "Player")
                    {
                        CmdSetName(currentName);
                        _nameSynced = true;
                    }
                }
            }
        }

        private void SyncAssaultState()
        {
            if (!CanSendCommands) return;

            bool isAssaultActive = _playerController != null
                && _playerController.IsAttacking
                && !IsDead;

            _assaultSyncTimer -= Time.deltaTime;
            if (isAssaultActive != _lastSentAssaultState || _assaultSyncTimer <= 0f)
            {
                _lastSentAssaultState = isAssaultActive;
                _assaultSyncTimer = ASSAULT_SYNC_INTERVAL;
                CmdSetAssaultState(isAssaultActive);
            }
        }

        private bool _nameSynced;

        private float _scoreUpdateTimer;
        private float _lastSentScore;
        private float _assaultSyncTimer;
        private bool _lastSentAssaultState;
        private float _nextKillRequestAllowedAt;
        private float _lastAssaultActivatedAt;
        private float _ignoreClientScoreSyncUntil;
        private const float SCORE_SYNC_INTERVAL = 0.3f;
        private const float ASSAULT_SYNC_INTERVAL = 0.1f;
        private const float SCORE_SYNC_GUARD_SECONDS = 0.4f;
        private const float KILL_REWARD_SCORE = 50f;
        private const float KILL_REQUEST_COOLDOWN_SECONDS = 0.25f;
        private const float ASSAULT_STATE_GRACE_SECONDS = 0.35f;
        private const float BASE_KILL_RANGE_BUFFER = 0.35f;
        private const float RTT_KILL_RANGE_BUFFER_FACTOR = 3f;
        private const float MAX_RTT_KILL_RANGE_BUFFER = 0.7f;
        private const float BOT_KILL_RANGE_BONUS = 0.25f;
        private const float MAX_KILL_RANGE_BUFFER = 1.25f;
        private const float FALLBACK_COLLIDER_RADIUS = 0.3f;

        // ===== Commands (클라이언트 -> 서버) =====

        /// <summary>
        /// 이름 설정 요청. 클라이언트가 호출하고 서버에서 실행된다.
        /// </summary>
        [Command]
        private void CmdSetName(string name)
        {
            PlayerName = name;
        }

        /// <summary>
        /// 점수 업데이트 요청. 클라이언트가 주기적으로 호출해 SyncVar를 갱신한다.
        /// </summary>
        [Command]
        private void CmdUpdateScore(float score)
        {
            // 서버 권한 점수 갱신 직후에는 짧은 시간 동안 클라이언트 값을 무시한다.
            if (Time.time < _ignoreClientScoreSyncUntil) return;
            Score = Mathf.Max(0f, score);
            UpdateSyncedScaleFromScore();
        }

        [Command]
        private void CmdSetAssaultState(bool isAssaultActive)
        {
            _isAssaultActive = isAssaultActive && !IsDead;
            if (_isAssaultActive)
                _lastAssaultActivatedAt = Time.time;
            }

        /// <summary>
        /// 서버에서 플레이어 점수를 증가시킨다.
        /// (예: 젬 수집 등 서버 확정 이벤트)
        /// </summary>
        [Server]
        public void ServerAddScore(float amount)
        {
            ServerAddScore(amount, playCollectSound: true);
        }

        [Server]
        private void ServerAddScore(float amount, bool playCollectSound)
        {
            if (amount <= 0f) return;

            Score = Mathf.Max(0f, Score + amount);
            UpdateSyncedScaleFromScore();
            _ignoreClientScoreSyncUntil = Time.time + SCORE_SYNC_GUARD_SECONDS;

            if (connectionToClient != null)
                TargetApplyServerScore(connectionToClient, amount, playCollectSound);
        }

        /// <summary>
        /// 리스폰 요청. 서버 SyncVar 상태를 초기화하고 클라이언트에 반영한다.
        /// </summary>
        [Command]
        public void CmdRespawn()
        {
            IsDead = false;
            Score = 0f;
            _isAssaultActive = false;
            _nextKillRequestAllowedAt = 0f;
            _lastAssaultActivatedAt = 0f;
            UpdateSyncedScaleFromScore();
            _ignoreClientScoreSyncUntil = Time.time + SCORE_SYNC_GUARD_SECONDS;
            RpcApplyRespawnState();
            Debug.Log($"[Network] {PlayerName} 리스폰 (서버 SyncVar 리셋)");
        }

        // ===== ClientRpc (서버 -> 모든 클라이언트) =====

        /// <summary>
        /// 서버가 모든 클라이언트에 사망 연출을 알린다.
        /// </summary>
        [ClientRpc]
        private void RpcDie()
        {
            var pc = GetComponent<Player.PlayerController>();
            if (pc != null)
                pc.Die();
        }

        [Command]
        public void CmdRequestKill(NetworkIdentity target)
        {
            if (target == null) return;
            if (target == netIdentity) return;
            if (IsDead) return;

            bool assaultReady =
                _isAssaultActive ||
                (Time.time - _lastAssaultActivatedAt) <= ASSAULT_STATE_GRACE_SECONDS;
            if (!assaultReady && HasColliderContact(target))
                assaultReady = true;
            if (!assaultReady)
            {
                Debug.LogWarning($"[PvP] Kill rejected: assault inactive ({PlayerName})");
                return;
            }

            if (Time.time < _nextKillRequestAllowedAt)
            {
                Debug.LogWarning($"[PvP] Kill rejected: cooldown ({PlayerName})");
                return;
            }

            var targetPlayer = target.GetComponent<NetworkPlayer>();
            if (targetPlayer != null)
            {
                if (targetPlayer.IsDead) return;
                if (!IsValidKillDistance(targetPlayer) && !HasColliderContact(target))
                {
                    Debug.LogWarning($"[PvP] Kill rejected: distance ({PlayerName} -> {targetPlayer.PlayerName})");
                    return;
                }

                if (!targetPlayer.ServerDieFromServerEvent("PvP", PlayerName))
                    return;

                _nextKillRequestAllowedAt = Time.time + KILL_REQUEST_COOLDOWN_SECONDS;
                ServerAddScore(KILL_REWARD_SCORE, playCollectSound: false);
                Debug.Log($"[PvP] {PlayerName} -> {targetPlayer.PlayerName} 처치");
                return;
            }

            var targetBot = target.GetComponent<NetworkBot>();
            if (targetBot != null)
            {
                if (!IsValidKillDistance(target) && !HasColliderContact(target))
                {
                    Debug.LogWarning($"[PvP] Kill rejected: distance ({PlayerName} -> {target.name})");
                    return;
                }

                _nextKillRequestAllowedAt = Time.time + KILL_REQUEST_COOLDOWN_SECONDS;
                var botDigger = target.GetComponent<Player.IDigger>();
                if (botDigger == null) return;

                botDigger.Die();
                ServerAddScore(KILL_REWARD_SCORE, playCollectSound: false);
                Debug.Log($"[PvP] {PlayerName} -> {target.name} 처치");
            }
        }

        [Server]
        public bool ServerDieFromHazard(string hazardName)
        {
            string source = string.IsNullOrWhiteSpace(hazardName) ? "Hazard" : hazardName.Trim();
            return ServerDieFromServerEvent(source, "System");
        }

        [Server]
        private bool ServerDieFromServerEvent(string source, string killer)
        {
            if (IsDead) return false;

            IsDead = true;
            _isAssaultActive = false;
            RpcDie();
            Debug.Log($"[{source}] {killer} -> {PlayerName} 처치");
            return true;
        }

        /// <summary>
        /// 서버 점수 확정 이벤트를 로컬 클라이언트 상태에 반영한다.
        /// </summary>
        [ClientRpc]
        private void RpcApplyRespawnState()
        {
            var pc = GetComponent<Player.PlayerController>();
            if (pc != null)
                pc.RemoteRespawn();

            if (!isLocalPlayer)
                ApplyRemoteScale(_syncedScale > 0f ? _syncedScale : ResolveScaleFromScore(Score));
        }

        [TargetRpc]
        private void TargetApplyServerScore(NetworkConnectionToClient target, float amount, bool playCollectSound)
        {
            var pc = GetComponent<Player.PlayerController>();
            if (pc != null)
                pc.AddScore(amount);

            if (playCollectSound && Systems.SoundManager.Instance != null)
                Systems.SoundManager.Instance.PlayGemCollect();
        }

        // ===== SyncVar Hooks =====

        /// <summary>
        /// 플레이어 이름이 바뀌면 모든 클라이언트에서 호출된다.
        /// </summary>
        private void OnNameChanged(string oldName, string newName)
        {
            gameObject.name = $"Player_{newName}";
        }

        private void OnIsDeadChanged(bool oldVal, bool newVal)
        {
            if (oldVal == true && newVal == false)
            {
                // 사망 -> 리스폰 전이 시 원격 플레이어 비주얼만 복구한다.
                if (!isLocalPlayer)
                {
                    var pc = GetComponent<Player.PlayerController>();
                    if (pc != null) pc.RemoteRespawn();
                    ApplyRemoteScale(_syncedScale > 0f ? _syncedScale : ResolveScaleFromScore(Score));
                }
            }
        }

        private void OnSyncedScaleChanged(float oldScale, float newScale)
        {
            if (isLocalPlayer) return;
            ApplyRemoteScale(newScale > 0f ? newScale : ResolveScaleFromScore(Score));
        }

        [Server]
        private bool IsValidKillDistance(NetworkPlayer targetPlayer)
        {
            float killRangeBuffer = ResolveKillRangeBuffer(targetPlayer != null ? targetPlayer.netIdentity : null);
            float allowedDistance =
                GetHitRadius(this) +
                GetHitRadius(targetPlayer) +
                killRangeBuffer;

            float sqrDistance = (targetPlayer.transform.position - transform.position).sqrMagnitude;
            return sqrDistance <= allowedDistance * allowedDistance;
        }

        [Server]
        private bool IsValidKillDistance(NetworkIdentity target)
        {
            if (target == null) return false;

            float killRangeBuffer = ResolveKillRangeBuffer(target);
            float allowedDistance =
                GetHitRadius(this) +
                GetHitRadius(target) +
                killRangeBuffer;

            float sqrDistance = (target.transform.position - transform.position).sqrMagnitude;
            return sqrDistance <= allowedDistance * allowedDistance;
        }

        [Server]
        private float ResolveKillRangeBuffer(NetworkIdentity target)
        {
            float rangeBuffer = BASE_KILL_RANGE_BUFFER;

            if (connectionToClient != null)
            {
                float rttSeconds = Mathf.Max(0f, (float)connectionToClient.rtt);
                rangeBuffer += Mathf.Min(MAX_RTT_KILL_RANGE_BUFFER, rttSeconds * RTT_KILL_RANGE_BUFFER_FACTOR);
            }

            if (target != null && target.GetComponent<NetworkBot>() != null)
                rangeBuffer += BOT_KILL_RANGE_BONUS;

            return Mathf.Min(MAX_KILL_RANGE_BUFFER, rangeBuffer);
        }
        [Server]
        private bool HasColliderContact(NetworkIdentity target)
        {
            if (target == null) return false;

            Collider2D myCollider = GetComponent<Collider2D>();
            Collider2D targetCollider = target.GetComponent<Collider2D>();
            if (myCollider == null || targetCollider == null) return false;

            ColliderDistance2D distance = myCollider.Distance(targetCollider);
            return distance.isOverlapped || distance.distance <= 0.05f;
        }

        [Server]
        private static float GetHitRadius(NetworkPlayer player)
        {
            if (player == null) return FALLBACK_COLLIDER_RADIUS;

            var circle = player.GetComponent<CircleCollider2D>();
            if (circle == null)
            {
                float fallbackScale = player.ResolveHitScale();
                return FALLBACK_COLLIDER_RADIUS * fallbackScale;
            }

            float scale = player.ResolveHitScale();
            return circle.radius * scale;
        }

        [Server]
        private static float GetHitRadius(NetworkIdentity targetIdentity)
        {
            if (targetIdentity == null) return FALLBACK_COLLIDER_RADIUS;

            CircleCollider2D circle = targetIdentity.GetComponent<CircleCollider2D>();
            float scale = Mathf.Max(0.1f, Mathf.Abs(targetIdentity.transform.lossyScale.x));
            if (circle == null)
                return FALLBACK_COLLIDER_RADIUS * scale;

            return circle.radius * scale;
        }

        [Server]
        private void UpdateSyncedScaleFromScore()
        {
            _syncedScale = ResolveScaleFromScore(Score);
        }

        private float ResolveScaleFromScore(float score)
        {
            GameSettings settings = Core.GameManager.Instance?.Settings;
            if (settings == null)
            {
                float fallback = Mathf.Abs(transform.localScale.x);
                return Mathf.Max(0.1f, fallback);
            }

            float safeScorePerUnit = Mathf.Max(0.0001f, settings.ScorePerSizeUnit);
            float growth = Mathf.Log(1f + Mathf.Max(0f, score) / safeScorePerUnit);
            float rawScale = settings.MinScale + growth;
            return Mathf.Clamp(rawScale, settings.MinScale, settings.MaxScale);
        }

        private float ResolveHitScale()
        {
            if (_syncedScale > 0f)
                return Mathf.Max(0.1f, _syncedScale);

            float fallbackScale = Mathf.Abs(transform.lossyScale.x);
            return Mathf.Max(0.1f, fallbackScale);
        }

        private void ApplyRemoteScale(float scale)
        {
            if (isLocalPlayer) return;
            if (scale <= 0f) return;

            transform.localScale = Vector3.one * scale;
        }
    }
}

