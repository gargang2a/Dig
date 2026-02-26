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
            if (_playerController == null)
                _playerController = GetComponent<Player.PlayerController>();

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
            _activePlayers.Add(this);
            _nextKillRequestAllowedAt = 0f;
            _syncedScale = ResolveScaleFromScore(Score);
            _lastServerPosition = transform.position;
            _lastServerPositionSampleAt = Time.time;
            _serverSpeedEstimate = 0f;
            _killRejectSummaryNextLogAt = Time.time + KILL_REJECT_SUMMARY_INTERVAL_SECONDS;
            _killRejectAssaultCount = 0;
            _killRejectCooldownCount = 0;
            _killRejectDistanceCount = 0;
            _lastKillRejectDetail = string.Empty;

            if (!_killDistanceConfigLogged)
            {
                _killDistanceConfigLogged = true;
                Debug.Log(
                    "[PvP Config] " +
                    $"botFailSafeBonus={BOT_KILL_FAILSAFE_BONUS:F2}, " +
                    $"playerFailSafeBonus={PLAYER_KILL_FAILSAFE_BONUS:F2}, " +
                    $"botFailSafeMax={MAX_BOT_KILL_FAILSAFE_DISTANCE:F2}, " +
                    $"playerFailSafeMax={MAX_PLAYER_KILL_FAILSAFE_DISTANCE:F2}, " +
                    $"driftBase={CLIENT_REPORTED_DRIFT_BASE:F2}, " +
                    $"driftRttFactor={CLIENT_REPORTED_DRIFT_RTT_FACTOR:F2}, " +
                    $"driftSpeedFactor={CLIENT_REPORTED_DRIFT_SPEED_FACTOR:F2}, " +
                    $"driftSpeedCapBot={CLIENT_REPORTED_DRIFT_SPEED_CAP_BOT:F2}, " +
                    $"driftSpeedCapPlayer={CLIENT_REPORTED_DRIFT_SPEED_CAP_PLAYER:F2}, " +
                    $"driftMaxBot={CLIENT_REPORTED_DRIFT_MAX_BOT:F2}, " +
                    $"driftMaxPlayer={CLIENT_REPORTED_DRIFT_MAX_PLAYER:F2}, " +
                    $"driftGraceBot={CLIENT_REPORTED_DRIFT_GRACE_BOT:F2}, " +
                    $"driftGracePlayer={CLIENT_REPORTED_DRIFT_GRACE_PLAYER:F2}, " +
                    $"reportedCompBot={CLIENT_REPORTED_DISTANCE_COMPENSATION_BOT:F2}, " +
                    $"reportedCompPlayer={CLIENT_REPORTED_DISTANCE_COMPENSATION_PLAYER:F2}, " +
                    $"reportedMaxBot={MAX_CLIENT_REPORTED_BOT_KILL_DISTANCE:F2}, " +
                    $"reportedMaxPlayer={MAX_CLIENT_REPORTED_PLAYER_KILL_DISTANCE:F2}");
            }
        }

        public override void OnStopServer()
        {
            _activePlayers.Remove(this);
            base.OnStopServer();
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
            if (!isLocalPlayer)
            {
                TickRemoteRespawnFallback();
                return;
            }

            if (_playerController == null)
                _playerController = GetComponent<Player.PlayerController>();

            TryHandleAutoRespawn();
            if (!CanSendCommands)
            {
                ReconcileStalePredictedScore();
                return;
            }

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

            ReconcileStalePredictedScore();
        }

        private void TickRemoteRespawnFallback()
        {
            if (_remoteRespawnFallbackAt < 0f)
                return;

            if (Time.unscaledTime < _remoteRespawnFallbackAt)
                return;

            _remoteRespawnFallbackAt = -1f;

            if (_playerController == null)
                _playerController = GetComponent<Player.PlayerController>();
            if (_playerController == null)
                return;

            // FIX-042: RpcApplyRespawnState 지연/누락 대비용 지연 fallback.
            // SyncVar(IsDead=false) 직후 즉시 리스폰하지 않고 짧게 대기해
            // 서버 좌표 동기화가 먼저 반영될 여유를 준다.
            _playerController.RemoteRespawn(transform.position);
            ApplyRemoteScale(_syncedScale > 0f ? _syncedScale : ResolveScaleFromScore(Score));
        }

        [ServerCallback]
        private void LateUpdate()
        {
            float now = Time.time;
            if (_lastServerPositionSampleAt <= 0f)
            {
                _lastServerPosition = transform.position;
                _lastServerPositionSampleAt = now;
                _serverSpeedEstimate = 0f;
                return;
            }

            float deltaTime = now - _lastServerPositionSampleAt;
            if (deltaTime <= 0.0001f) return;

            Vector2 currentPosition = transform.position;
            float instantSpeed = Vector2.Distance(currentPosition, _lastServerPosition) / deltaTime;
            float clampedInstantSpeed = Mathf.Clamp(instantSpeed, 0f, MAX_SERVER_SPEED_ESTIMATE);
            _serverSpeedEstimate = Mathf.Lerp(_serverSpeedEstimate, clampedInstantSpeed, 0.35f);
            _lastServerPosition = currentPosition;
            _lastServerPositionSampleAt = now;
            FlushKillRejectSummaryIfDue(now);
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
        private float _autoRespawnReadyAt = -1f;
        private float _remoteRespawnFallbackAt = -1f;
        private float _ignoreClientScoreSyncUntil;
        private Vector2 _lastServerPosition;
        private float _lastServerPositionSampleAt;
        private float _serverSpeedEstimate;
        private float _pendingPredictedScore;
        private float _pendingPredictedScoreExpireAt = -1f;
        private float _hazardRespawnProtectionUntil;
        private int _killRejectAssaultCount;
        private int _killRejectCooldownCount;
        private int _killRejectDistanceCount;
        private float _killRejectSummaryNextLogAt;
        private string _lastKillRejectDetail;
        private const float SCORE_SYNC_INTERVAL = 0.12f;
        private const float ASSAULT_SYNC_INTERVAL = 0.1f;
        private const float SCORE_SYNC_GUARD_SECONDS = 0.4f;
        private const float DEFAULT_KILL_REWARD_SCORE = 35f;
        private const float KILL_REQUEST_COOLDOWN_SECONDS = 0.03f;
        private const float ASSAULT_STATE_GRACE_SECONDS = 0.35f;
        private const float BASE_KILL_RANGE_BUFFER = 0.45f;
        private const float RTT_KILL_RANGE_BUFFER_FACTOR = 3.8f;
        private const float MAX_RTT_KILL_RANGE_BUFFER = 0.9f;
        private const float BOT_KILL_RANGE_BONUS = 0.25f;
        private const float PLAYER_KILL_RANGE_BONUS = 0.45f;
        private const float MAX_KILL_RANGE_BUFFER = 1.8f;
        private const float BOT_KILL_FAILSAFE_BONUS = 4.5f;
        private const float PLAYER_KILL_FAILSAFE_BONUS = 5.5f;
        private const float MAX_BOT_KILL_FAILSAFE_DISTANCE = 6.8f;
        private const float MAX_PLAYER_KILL_FAILSAFE_DISTANCE = 9.0f;
        private const float CLIENT_REPORTED_DISTANCE_COMPENSATION_BOT = 1.35f;
        private const float CLIENT_REPORTED_DISTANCE_COMPENSATION_PLAYER = 1.2f;
        private const float MAX_CLIENT_REPORTED_BOT_KILL_DISTANCE = 7.8f;
        private const float MAX_CLIENT_REPORTED_PLAYER_KILL_DISTANCE = 9.2f;
        private const float CLIENT_REPORTED_DRIFT_BASE = 1.5f;
        private const float CLIENT_REPORTED_DRIFT_RTT_FACTOR = 18f;
        private const float CLIENT_REPORTED_DRIFT_SPEED_FACTOR = 0.45f;
        private const float CLIENT_REPORTED_DRIFT_SPEED_CAP_BOT = 6.5f;
        private const float CLIENT_REPORTED_DRIFT_SPEED_CAP_PLAYER = 5.0f;
        private const float CLIENT_REPORTED_DRIFT_MAX_BOT = 9.5f;
        private const float CLIENT_REPORTED_DRIFT_MAX_PLAYER = 8.2f;
        private const float CLIENT_REPORTED_DRIFT_GRACE_BOT = 0.9f;
        private const float CLIENT_REPORTED_DRIFT_GRACE_PLAYER = 0.8f;
        private const float KILL_REJECT_SUMMARY_INTERVAL_SECONDS = 4.0f;
        private const float MAX_SERVER_SPEED_ESTIMATE = 40f;
        private const float FALLBACK_COLLIDER_RADIUS = 0.3f;
        private const float BASE_COLLIDER_CONTACT_TOLERANCE = 0.1f;
        private const float PLAYER_CONTACT_TOLERANCE_BONUS = 0.1f;
        private const float AUTO_RESPAWN_DELAY_SECONDS = 1.0f;
        private const float REMOTE_RESPAWN_FALLBACK_DELAY_SECONDS = 0.2f;
        private const float PREDICTED_SCORE_RECONCILE_TIMEOUT_SECONDS = 0.65f;
        private const float CLIENT_SCORE_UPSYNC_EPSILON = 0.1f;
        private const float RESPAWN_SANDWORM_SAFE_DISTANCE = 6.5f;
        private const int RESPAWN_POSITION_MAX_ATTEMPTS = 20;
        private const float HAZARD_RESPAWN_PROTECTION_SECONDS = 1.0f;
        private static bool _killDistanceConfigLogged;

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

            float sanitizedScore = Mathf.Max(0f, score);

            // 서버 권한 원칙: 클라이언트는 점수 감소(부스트 소모)만 반영하고
            // 점수 증가(젬/킬)는 ServerAddScore 경로로만 확정한다.
            if (sanitizedScore > Score + CLIENT_SCORE_UPSYNC_EPSILON)
                return;

            Score = sanitizedScore;
            UpdateSyncedScaleFromScore();
        }

        [Command]
        private void CmdSetAssaultState(bool isAssaultActive)
        {
            _isAssaultActive = isAssaultActive && !IsDead;
            if (_isAssaultActive)
                _lastAssaultActivatedAt = Time.time;
        }

        [Command]
        public void CmdRequestCollectGem(NetworkIdentity gemIdentity, Vector2 collectorReportedPos, Vector2 gemReportedPos)
        {
            ServerProcessGemCollectRequest(gemIdentity, collectorReportedPos, gemReportedPos);
        }

        [Server]
        private void ServerProcessGemCollectRequest(NetworkIdentity gemIdentity, Vector2 collectorReportedPos, Vector2 gemReportedPos)
        {
            if (IsDead) return;
            if (gemIdentity == null) return;

            World.Gem gem = gemIdentity.GetComponent<World.Gem>();
            if (gem == null) return;

            gem.ServerTryCollectFromRequest(this, collectorReportedPos, gemReportedPos);
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
            ServerApplyRespawnState((Vector2)transform.position);
        }

        [Command]
        public void CmdRespawnWithReportedPosition(Vector2 reportedRespawnPosition)
        {
            Vector2 respawnPosition = ResolveServerValidatedRespawnPosition(reportedRespawnPosition);
            ServerApplyRespawnState(respawnPosition);
        }

        [Server]
        private void ServerApplyRespawnState(Vector2 respawnPosition)
        {
            // 서버 권한 기준으로 최종 리스폰 좌표를 확정한다.
            Vector3 currentPosition = transform.position;
            transform.position = new Vector3(respawnPosition.x, respawnPosition.y, currentPosition.z);

            IsDead = false;
            Score = 0f;
            _isAssaultActive = false;
            _nextKillRequestAllowedAt = 0f;
            _lastAssaultActivatedAt = 0f;
            _hazardRespawnProtectionUntil = Time.time + HAZARD_RESPAWN_PROTECTION_SECONDS;
            UpdateSyncedScaleFromScore();
            _ignoreClientScoreSyncUntil = Time.time + SCORE_SYNC_GUARD_SECONDS;
            RpcApplyRespawnState(respawnPosition);
            Debug.Log($"[Network] {PlayerName} 리스폰 (서버 SyncVar 리셋)");
        }

        [Server]
        private Vector2 ResolveServerValidatedRespawnPosition(Vector2 reportedRespawnPosition)
        {
            float mapRadius = 32.5f;
            if (Core.GameManager.Instance != null && Core.GameManager.Instance.Settings != null)
            {
                var settings = Core.GameManager.Instance.Settings;
                mapRadius = settings.MapRadius * settings.RespawnRadiusRatio;
            }
            mapRadius = Mathf.Max(1f, mapRadius);

            Vector2 candidate;
            if (!IsFiniteVector2(reportedRespawnPosition))
            {
                candidate = UnityEngine.Random.insideUnitCircle * mapRadius;
            }
            else
            {
                candidate = reportedRespawnPosition;
            }

            float maxSqrRadius = mapRadius * mapRadius;
            if (candidate.sqrMagnitude > maxSqrRadius)
                candidate = candidate.normalized * mapRadius;

            // 샌드웜 근접 즉사 구간을 피하도록 리스폰 후보를 재탐색한다.
            if (IsRespawnPositionSafeFromSandworms(candidate))
                return candidate;

            Vector2 bestCandidate = candidate;
            float bestSafetyDistance = EvaluateClosestSandwormDistance(candidate);

            for (int i = 0; i < RESPAWN_POSITION_MAX_ATTEMPTS; i++)
            {
                Vector2 sample = UnityEngine.Random.insideUnitCircle * mapRadius;
                float safetyDistance = EvaluateClosestSandwormDistance(sample);
                if (safetyDistance > bestSafetyDistance)
                {
                    bestSafetyDistance = safetyDistance;
                    bestCandidate = sample;
                }

                if (safetyDistance >= RESPAWN_SANDWORM_SAFE_DISTANCE)
                    return sample;
            }

            Debug.LogWarning(
                $"[Respawn] Unsafe requested position adjusted for {ResolveDisplayName()} " +
                $"(closestSandwormDist={bestSafetyDistance:F2})");
            return bestCandidate;
        }

        [Server]
        private bool IsRespawnPositionSafeFromSandworms(Vector2 position)
        {
            return EvaluateClosestSandwormDistance(position) >= RESPAWN_SANDWORM_SAFE_DISTANCE;
        }

        [Server]
        private static float EvaluateClosestSandwormDistance(Vector2 position)
        {
            float closestDistance = float.PositiveInfinity;

            foreach (World.Sandworm worm in World.Sandworm.ActiveWorms)
            {
                if (worm == null) continue;

                float headDistance = Vector2.Distance(position, worm.transform.position);
                if (headDistance < closestDistance)
                    closestDistance = headDistance;

                var segments = worm.Segments;
                if (segments == null) continue;

                for (int i = 0; i < segments.Count; i++)
                {
                    Transform segment = segments[i];
                    if (segment == null) continue;

                    float segmentDistance = Vector2.Distance(position, segment.position);
                    if (segmentDistance < closestDistance)
                        closestDistance = segmentDistance;
                }
            }

            return closestDistance;
        }

        private static bool IsFiniteVector2(Vector2 value)
        {
            return
                !float.IsNaN(value.x) &&
                !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) &&
                !float.IsInfinity(value.y);
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
            Vector2 attackerReportedPos = transform.position;
            Vector2 targetReportedPos = target != null
                ? (Vector2)target.transform.position
                : attackerReportedPos;

            ServerProcessKillRequest(target, attackerReportedPos, targetReportedPos);
        }

        [Command]
        public void CmdRequestKillWithReported(NetworkIdentity target, Vector2 attackerReportedPos, Vector2 targetReportedPos)
        {
            ServerProcessKillRequest(target, attackerReportedPos, targetReportedPos);
        }

        [Server]
        private void ServerProcessKillRequest(NetworkIdentity target, Vector2 attackerReportedPos, Vector2 targetReportedPos)
        {
            if (target == null) return;
            if (target == netIdentity) return;
            if (IsDead) return;
            MarkAssaultIntentFromKillRequest();

            bool assaultReady =
                _isAssaultActive ||
                (Time.time - _lastAssaultActivatedAt) <= ASSAULT_STATE_GRACE_SECONDS;
            if (!assaultReady && HasColliderContact(target))
                assaultReady = true;
            if (!assaultReady)
            {
                RecordKillReject(
                    "assault",
                    $"player={ResolveDisplayName()}, target={ResolveDisplayName(target)}");
                return;
            }

            if (Time.time < _nextKillRequestAllowedAt)
            {
                float remaining = Mathf.Max(0f, _nextKillRequestAllowedAt - Time.time);
                int connectionId = connectionToClient != null ? connectionToClient.connectionId : -1;
                RecordKillReject(
                    "cooldown",
                    $"player={ResolveDisplayName()}, remaining={remaining:F3}s, " +
                    $"target={ResolveDisplayName(target)}, connId={connectionId}");
                return;
            }

            var targetPlayer = target.GetComponent<NetworkPlayer>();
            if (targetPlayer != null)
            {
                if (targetPlayer.IsDead) return;

                bool inStrictKillRange = IsValidKillDistance(targetPlayer);
                bool hasColliderContact = HasColliderContact(target);
                bool inFailSafeKillRange = !inStrictKillRange &&
                                           !hasColliderContact &&
                                           IsWithinKillFailSafeRange(target);
                bool inReportedCompensatedRange = !inStrictKillRange &&
                                                  !hasColliderContact &&
                                                  !inFailSafeKillRange &&
                                                  IsWithinClientReportedKillRange(target, attackerReportedPos, targetReportedPos);
                if (!inStrictKillRange &&
                    !hasColliderContact &&
                    !inFailSafeKillRange &&
                    !inReportedCompensatedRange)
                {
                    RecordKillReject(
                        "distance",
                        $"{ResolveDisplayName()} -> {ResolveDisplayName(target)} | " +
                        $"{BuildKillDistanceDebugInfo(target, attackerReportedPos, targetReportedPos)}");
                    return;
                }

                if (!targetPlayer.ServerDieFromServerEvent("PvP", PlayerName))
                    return;

                _nextKillRequestAllowedAt = Time.time + KILL_REQUEST_COOLDOWN_SECONDS;
                ServerAddScore(ResolveKillRewardScore(), playCollectSound: false);
                if (connectionToClient != null)
                    TargetPlayKillConfirm(connectionToClient);
                Debug.Log($"[PvP] {ResolveDisplayName()} -> {ResolveDisplayName(target)} 처치");
                return;
            }

            var targetBot = target.GetComponent<NetworkBot>();
            if (targetBot != null)
            {
                bool inStrictKillRange = IsValidKillDistance(target);
                bool hasColliderContact = HasColliderContact(target);
                bool inFailSafeKillRange = !inStrictKillRange &&
                                           !hasColliderContact &&
                                           IsWithinKillFailSafeRange(target);
                bool inReportedCompensatedRange = !inStrictKillRange &&
                                                  !hasColliderContact &&
                                                  !inFailSafeKillRange &&
                                                  IsWithinClientReportedKillRange(target, attackerReportedPos, targetReportedPos);
                if (!inStrictKillRange &&
                    !hasColliderContact &&
                    !inFailSafeKillRange &&
                    !inReportedCompensatedRange)
                {
                    RecordKillReject(
                        "distance",
                        $"{ResolveDisplayName()} -> {ResolveDisplayName(target)} | " +
                        $"{BuildKillDistanceDebugInfo(target, attackerReportedPos, targetReportedPos)}");
                    return;
                }

                var botDigger = target.GetComponent<Player.IDigger>();
                if (botDigger == null) return;
                var botController = target.GetComponent<Player.AIController>();
                if (botController != null && botController.IsDead) return;

                botDigger.Die();
                if (botController != null && !botController.IsDead) return;
                _nextKillRequestAllowedAt = Time.time + KILL_REQUEST_COOLDOWN_SECONDS;
                ServerAddScore(ResolveKillRewardScore(), playCollectSound: false);
                if (connectionToClient != null)
                    TargetPlayKillConfirm(connectionToClient);
                Debug.Log($"[PvP] {ResolveDisplayName()} -> {ResolveDisplayName(target)} 처치");
            }
        }

        [Server]
        public bool ServerDieFromHazard(string hazardName)
        {
            string source = string.IsNullOrWhiteSpace(hazardName) ? "Hazard" : hazardName.Trim();

            if (string.Equals(source, "Sandworm", System.StringComparison.OrdinalIgnoreCase)
                && Time.time < _hazardRespawnProtectionUntil)
            {
                float remaining = Mathf.Max(0f, _hazardRespawnProtectionUntil - Time.time);
                Debug.Log(
                    $"[{source}] {ResolveDisplayName()} protected after respawn " +
                    $"({remaining:F2}s remaining)");
                return false;
            }

            return ServerDieFromServerEvent(source, "System");
        }

        [Server]
        private bool ServerDieFromServerEvent(string source, string killer)
        {
            if (IsDead) return false;

            IsDead = true;
            _isAssaultActive = false;
            RpcDie();
            string resolvedKiller = string.IsNullOrWhiteSpace(killer) ? "Unknown" : killer.Trim();
            Debug.Log($"[{source}] {resolvedKiller} -> {ResolveDisplayName()} 처치");
            return true;
        }

        /// <summary>
        /// 서버 점수 확정 이벤트를 로컬 클라이언트 상태에 반영한다.
        /// </summary>
        [ClientRpc]
        private void RpcApplyRespawnState(Vector2 respawnPosition)
        {
            _remoteRespawnFallbackAt = -1f;

            var pc = GetComponent<Player.PlayerController>();
            if (pc != null)
                pc.RemoteRespawn(respawnPosition);

            if (!isLocalPlayer)
                ApplyRemoteScale(_syncedScale > 0f ? _syncedScale : ResolveScaleFromScore(Score));
        }

        [TargetRpc]
        private void TargetApplyServerScore(NetworkConnectionToClient target, float amount, bool playCollectSound)
        {
            float applyAmount = Mathf.Max(0f, amount);
            if (_pendingPredictedScore > 0f && applyAmount > 0f)
            {
                float consumed = Mathf.Min(_pendingPredictedScore, applyAmount);
                _pendingPredictedScore -= consumed;
                applyAmount -= consumed;

                if (_pendingPredictedScore <= 0.0001f)
                {
                    _pendingPredictedScore = 0f;
                    _pendingPredictedScoreExpireAt = -1f;
                }
            }

            var pc = GetComponent<Player.PlayerController>();
            if (pc != null && applyAmount > 0f)
                pc.AddScore(applyAmount);

            if (playCollectSound && Systems.SoundManager.Instance != null)
                Systems.SoundManager.Instance.PlayGemCollect();
        }

        [Client]
        public void ClientPredictGemCollect(float amount)
        {
            if (!isLocalPlayer) return;

            float safeAmount = Mathf.Max(0f, amount);
            if (safeAmount <= 0f) return;

            _pendingPredictedScore += safeAmount;
            _pendingPredictedScoreExpireAt = Time.unscaledTime + PREDICTED_SCORE_RECONCILE_TIMEOUT_SECONDS;

            var pc = GetComponent<Player.PlayerController>();
            if (pc != null)
                pc.AddScore(safeAmount);
        }

        [Client]
        private void ReconcileStalePredictedScore()
        {
            if (!isLocalPlayer) return;
            if (_pendingPredictedScore <= 0f) return;
            if (_pendingPredictedScoreExpireAt < 0f) return;
            if (Time.unscaledTime < _pendingPredictedScoreExpireAt) return;

            float rollbackAmount = _pendingPredictedScore;
            _pendingPredictedScore = 0f;
            _pendingPredictedScoreExpireAt = -1f;

            var pc = GetComponent<Player.PlayerController>();
            if (pc != null && rollbackAmount > 0f)
                pc.AddScore(-rollbackAmount);

            Debug.Log($"[Gem][Predict] stale rollback applied: {rollbackAmount:F2}");
        }

        [TargetRpc]
        private void TargetPlayKillConfirm(NetworkConnectionToClient target)
        {
            if (Systems.SoundManager.Instance != null)
                Systems.SoundManager.Instance.PlayKillConfirm();
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
                _autoRespawnReadyAt = -1f;

            if (newVal)
            {
                _pendingPredictedScore = 0f;
                _pendingPredictedScoreExpireAt = -1f;
            }

            if (isLocalPlayer)
                return;

            if (_playerController == null)
                _playerController = GetComponent<Player.PlayerController>();

            if (_playerController == null)
                return;

            if (newVal)
            {
                // FIX-042: SyncVar 훅 fallback.
                // RpcDie 타이밍이 늦어도 원격 객체는 즉시 죽음 상태(터널 중지)로 맞춘다.
                _remoteRespawnFallbackAt = -1f;
                _playerController.Die();
                return;
            }

            if (oldVal == true && newVal == false)
            {
                // FIX-042: SyncVar 훅 fallback.
                // IsDead=false 도착 직후 즉시 리스폰하면 죽은 좌표 기반 직선 터널이 생길 수 있어
                // 짧은 지연 후 fallback을 실행한다(RPC가 먼저 오면 자동 취소).
                _remoteRespawnFallbackAt = Time.unscaledTime + REMOTE_RESPAWN_FALLBACK_DELAY_SECONDS;
            }
        }

        private void TryHandleAutoRespawn()
        {
            if (!Player.PlayerController.IsGlobalAutoModeEnabled)
            {
                _autoRespawnReadyAt = -1f;
                return;
            }

            if (!IsDead)
            {
                _autoRespawnReadyAt = -1f;
                return;
            }

            if (_playerController == null || !CanSendCommands)
                return;

            if (_autoRespawnReadyAt < 0f)
                _autoRespawnReadyAt = Time.unscaledTime + AUTO_RESPAWN_DELAY_SECONDS;

            if (Time.unscaledTime < _autoRespawnReadyAt)
                return;

            // 서버 반영까지 지연될 수 있으므로 재시도 간격을 유지한다.
            _autoRespawnReadyAt = Time.unscaledTime + AUTO_RESPAWN_DELAY_SECONDS;
            _playerController.Respawn();
            CmdRespawnWithReportedPosition(_playerController.transform.position);
        }

        private void OnSyncedScaleChanged(float oldScale, float newScale)
        {
            if (isLocalPlayer) return;
            ApplyRemoteScale(newScale > 0f ? newScale : ResolveScaleFromScore(Score));
        }

        [Server]
        private bool IsValidKillDistance(NetworkPlayer targetPlayer)
        {
            float allowedDistance = ResolveAllowedKillDistance(targetPlayer);
            float sqrDistance = (targetPlayer.transform.position - transform.position).sqrMagnitude;
            return sqrDistance <= allowedDistance * allowedDistance;
        }

        [Server]
        private bool IsValidKillDistance(NetworkIdentity target)
        {
            if (target == null) return false;

            float allowedDistance = ResolveAllowedKillDistance(target);
            float sqrDistance = (target.transform.position - transform.position).sqrMagnitude;
            return sqrDistance <= allowedDistance * allowedDistance;
        }

        [Server]
        private bool IsWithinKillFailSafeRange(NetworkIdentity target)
        {
            if (target == null) return false;

            float strictAllowedDistance = ResolveAllowedKillDistance(target);
            float bonus = ResolveFailSafeBonus(target);
            float maxDistance = ResolveFailSafeMaxDistance(target);
            float failSafeAllowedDistance = Mathf.Min(
                maxDistance,
                strictAllowedDistance + bonus);

            float sqrDistance = (target.transform.position - transform.position).sqrMagnitude;
            return sqrDistance <= failSafeAllowedDistance * failSafeAllowedDistance;
        }

        [Server]
        private bool IsWithinClientReportedKillRange(NetworkIdentity target, Vector2 attackerReportedPos, Vector2 targetReportedPos)
        {
            if (target == null) return false;

            float driftAllowance = ResolveReportedPositionDriftAllowance(target);
            float driftGrace = ResolveReportedPositionDriftGrace(target);
            float effectiveDriftAllowance = driftAllowance + driftGrace;
            float attackerDrift = Vector2.Distance(attackerReportedPos, (Vector2)transform.position);
            if (attackerDrift > effectiveDriftAllowance) return false;

            float targetDrift = Vector2.Distance(targetReportedPos, (Vector2)target.transform.position);
            if (targetDrift > effectiveDriftAllowance) return false;

            float strictAllowedDistance = ResolveAllowedKillDistance(target);
            float bonus = ResolveFailSafeBonus(target);
            float maxDistance = ResolveClientReportedMaxDistance(target);
            float compensation = ResolveClientReportedDistanceCompensation(target);
            float failSafeAllowedDistance = Mathf.Min(
                maxDistance,
                strictAllowedDistance + bonus);
            float compensatedAllowedDistance = Mathf.Min(
                maxDistance,
                failSafeAllowedDistance + compensation);

            float reportedDistance = Vector2.Distance(attackerReportedPos, targetReportedPos);
            return reportedDistance <= compensatedAllowedDistance;
        }

        [Server]
        private float ResolveReportedPositionDriftAllowance(NetworkIdentity target)
        {
            float rttSeconds = connectionToClient != null ? Mathf.Max(0f, (float)connectionToClient.rtt) : 0f;
            float rttAllowance = rttSeconds * CLIENT_REPORTED_DRIFT_RTT_FACTOR;

            bool playerTarget = target != null && target.GetComponent<NetworkPlayer>() != null;
            float speedCap = playerTarget
                ? CLIENT_REPORTED_DRIFT_SPEED_CAP_PLAYER
                : CLIENT_REPORTED_DRIFT_SPEED_CAP_BOT;
            float driftMax = playerTarget
                ? CLIENT_REPORTED_DRIFT_MAX_PLAYER
                : CLIENT_REPORTED_DRIFT_MAX_BOT;
            float speedAllowance = Mathf.Min(speedCap, _serverSpeedEstimate * CLIENT_REPORTED_DRIFT_SPEED_FACTOR);

            return Mathf.Clamp(
                CLIENT_REPORTED_DRIFT_BASE + rttAllowance + speedAllowance,
                CLIENT_REPORTED_DRIFT_BASE,
                driftMax);
        }

        [Server]
        private static float ResolveReportedPositionDriftGrace(NetworkIdentity target)
        {
            if (target == null) return CLIENT_REPORTED_DRIFT_GRACE_BOT;
            return target.GetComponent<NetworkPlayer>() != null
                ? CLIENT_REPORTED_DRIFT_GRACE_PLAYER
                : CLIENT_REPORTED_DRIFT_GRACE_BOT;
        }

        [Server]
        private static float ResolveClientReportedDistanceCompensation(NetworkIdentity target)
        {
            if (target == null) return CLIENT_REPORTED_DISTANCE_COMPENSATION_BOT;
            return target.GetComponent<NetworkPlayer>() != null
                ? CLIENT_REPORTED_DISTANCE_COMPENSATION_PLAYER
                : CLIENT_REPORTED_DISTANCE_COMPENSATION_BOT;
        }

        [Server]
        private static float ResolveClientReportedMaxDistance(NetworkIdentity target)
        {
            if (target == null) return MAX_CLIENT_REPORTED_BOT_KILL_DISTANCE;
            return target.GetComponent<NetworkPlayer>() != null
                ? MAX_CLIENT_REPORTED_PLAYER_KILL_DISTANCE
                : MAX_CLIENT_REPORTED_BOT_KILL_DISTANCE;
        }

        [Server]
        private float ResolveAllowedKillDistance(NetworkPlayer targetPlayer)
        {
            float killRangeBuffer = ResolveKillRangeBuffer(targetPlayer != null ? targetPlayer.netIdentity : null);
            return GetHitRadius(this) + GetHitRadius(targetPlayer) + killRangeBuffer;
        }

        [Server]
        private float ResolveAllowedKillDistance(NetworkIdentity target)
        {
            if (target == null) return FALLBACK_COLLIDER_RADIUS;

            float killRangeBuffer = ResolveKillRangeBuffer(target);
            return GetHitRadius(this) + GetHitRadius(target) + killRangeBuffer;
        }

        [Server]
        private string BuildKillDistanceDebugInfo(NetworkIdentity target, Vector2 attackerReportedPos, Vector2 targetReportedPos)
        {
            if (target == null) return "target=null";

            float strictAllowedDistance = ResolveAllowedKillDistance(target);
            float bonus = ResolveFailSafeBonus(target);
            float maxDistance = ResolveFailSafeMaxDistance(target);
            float reportedMaxDistance = ResolveClientReportedMaxDistance(target);
            float reportedCompensation = ResolveClientReportedDistanceCompensation(target);
            float failSafeAllowedDistance = Mathf.Min(
                maxDistance,
                strictAllowedDistance + bonus);
            float compensatedAllowedDistance = Mathf.Min(
                reportedMaxDistance,
                failSafeAllowedDistance + reportedCompensation);

            float sqrDistance = (target.transform.position - transform.position).sqrMagnitude;
            float distance = Mathf.Sqrt(Mathf.Max(0f, sqrDistance));
            float rttMs = connectionToClient != null ? (float)connectionToClient.rtt * 1000f : 0f;
            float reportedDistance = Vector2.Distance(attackerReportedPos, targetReportedPos);
            float attackerDrift = Vector2.Distance(attackerReportedPos, (Vector2)transform.position);
            float targetDrift = Vector2.Distance(targetReportedPos, (Vector2)target.transform.position);
            float driftAllowance = ResolveReportedPositionDriftAllowance(target);
            float driftGrace = ResolveReportedPositionDriftGrace(target);
            float effectiveDriftAllowance = driftAllowance + driftGrace;

            return
                $"dist={distance:F3}, strict={strictAllowedDistance:F3}, failSafe={failSafeAllowedDistance:F3}, " +
                $"reportedDist={reportedDistance:F3}, compensated={compensatedAllowedDistance:F3}, " +
                $"attackerDrift={attackerDrift:F3}, targetDrift={targetDrift:F3}, driftAllow={driftAllowance:F3}, " +
                $"driftGrace={driftGrace:F3}, effectiveDriftAllow={effectiveDriftAllowance:F3}, " +
                $"bonus={bonus:F3}, failSafeMax={maxDistance:F3}, reportedComp={reportedCompensation:F3}, reportedMax={reportedMaxDistance:F3}, " +
                $"rttMs={rttMs:F1}, attackerPos={transform.position}, targetPos={target.transform.position}";
        }

        [Server]
        private static float ResolveFailSafeBonus(NetworkIdentity target)
        {
            if (target == null) return BOT_KILL_FAILSAFE_BONUS;
            return target.GetComponent<NetworkPlayer>() != null
                ? PLAYER_KILL_FAILSAFE_BONUS
                : BOT_KILL_FAILSAFE_BONUS;
        }

        [Server]
        private static float ResolveFailSafeMaxDistance(NetworkIdentity target)
        {
            if (target == null) return MAX_BOT_KILL_FAILSAFE_DISTANCE;
            return target.GetComponent<NetworkPlayer>() != null
                ? MAX_PLAYER_KILL_FAILSAFE_DISTANCE
                : MAX_BOT_KILL_FAILSAFE_DISTANCE;
        }

        [Server]
        private string ResolveDisplayName()
        {
            if (!string.IsNullOrWhiteSpace(PlayerName))
                return PlayerName;

            return name;
        }

        [Server]
        private static string ResolveDisplayName(NetworkIdentity target)
        {
            if (target == null) return "null";

            var targetPlayer = target.GetComponent<NetworkPlayer>();
            if (targetPlayer != null && !string.IsNullOrWhiteSpace(targetPlayer.PlayerName))
                return targetPlayer.PlayerName;

            return target.name;
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
            else if (target != null && target.GetComponent<NetworkPlayer>() != null)
                rangeBuffer += PLAYER_KILL_RANGE_BONUS;

            return Mathf.Min(MAX_KILL_RANGE_BUFFER, rangeBuffer);
        }

        [Server]
        private void MarkAssaultIntentFromKillRequest()
        {
            _lastAssaultActivatedAt = Time.time;
        }

        [Server]
        private void RecordKillReject(string reason, string detail)
        {
            switch (reason)
            {
                case "assault":
                    _killRejectAssaultCount++;
                    break;
                case "cooldown":
                    _killRejectCooldownCount++;
                    break;
                default:
                    _killRejectDistanceCount++;
                    break;
            }

            _lastKillRejectDetail = detail;
            FlushKillRejectSummaryIfDue(Time.time);
        }

        [Server]
        private void FlushKillRejectSummaryIfDue(float now)
        {
            if (now < _killRejectSummaryNextLogAt) return;

            int total = _killRejectAssaultCount + _killRejectCooldownCount + _killRejectDistanceCount;
            if (total > 0)
            {
                string detail = string.IsNullOrWhiteSpace(_lastKillRejectDetail) ? "-" : _lastKillRejectDetail;
                Debug.Log(
                    $"[PvP][RejectSummary] player={ResolveDisplayName()}, total={total}, " +
                    $"assault={_killRejectAssaultCount}, cooldown={_killRejectCooldownCount}, distance={_killRejectDistanceCount}, " +
                    $"last={detail}");

                _killRejectAssaultCount = 0;
                _killRejectCooldownCount = 0;
                _killRejectDistanceCount = 0;
                _lastKillRejectDetail = string.Empty;
            }

            _killRejectSummaryNextLogAt = now + KILL_REJECT_SUMMARY_INTERVAL_SECONDS;
        }

        [Server]
        private bool HasColliderContact(NetworkIdentity target)
        {
            if (target == null) return false;

            Collider2D myCollider = GetComponent<Collider2D>();
            Collider2D targetCollider = target.GetComponent<Collider2D>();
            if (myCollider == null || targetCollider == null) return false;

            float tolerance = BASE_COLLIDER_CONTACT_TOLERANCE;
            if (target.GetComponent<NetworkPlayer>() != null)
                tolerance += PLAYER_CONTACT_TOLERANCE_BONUS;

            ColliderDistance2D distance = myCollider.Distance(targetCollider);
            return distance.isOverlapped || distance.distance <= tolerance;
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

        [Server]
        private float ResolveKillRewardScore()
        {
            GameSettings settings = Core.GameManager.Instance?.Settings;
            if (settings == null)
                return DEFAULT_KILL_REWARD_SCORE;

            return Mathf.Max(0f, settings.KillRewardScore);
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

