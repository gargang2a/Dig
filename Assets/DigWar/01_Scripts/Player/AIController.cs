using UnityEngine;
using Core;
using Core.Data;
using System.Collections.Generic;
using Mirror;

namespace Player
{
    /// <summary>
    /// AI 봇 컨트롤러. PlayerController와 동일한 오브젝트 구조를 사용하지만
    /// 입력 대신 자율 이동 로직으로 움직인다.
    /// 행동 패턴: 평소 랜덤 이동, 근처 젬 감지 시 추적, 벽 근처에서 회피.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(Core.MoleGrowth))]
    public class AIController : MonoBehaviour, IDigger
    {
        private static readonly HashSet<AIController> _activeBots = new HashSet<AIController>();
        public static IReadOnlyCollection<AIController> ActiveBots => _activeBots;

        private GameSettings _settings;
        private Rigidbody2D _rb;
        private bool _isDead;
        public bool IsDead => _isDead;

        // AI 행동
        private float _targetAngle;
        private float _angleChangeTimer;
        private Transform _targetGem;
        private float _gemSearchTimer;

        // 속도 프로퍼티 (TunnelGenerator 호환)
        public float CurrentSpeed { get; private set; }
        public bool IsBoosting => false; // 봇은 부스트 안 함 (단순화)
        
        /// <summary>AI 봇은 항상 공격 모드 (항상 터널 생성).</summary>
        public bool IsAttacking => true;



        public float Score => _growth != null ? _growth.CurrentScore : 0f;

        private Core.MoleGrowth _growth;

        private const float ANGLE_CHANGE_INTERVAL = 2.2f;
        private const float ANGLE_CHANGE_JITTER = 0.4f;
        private const float RANDOM_TURN_ANGLE = 35f;
        private const float GEM_SEARCH_INTERVAL = 0.35f;
        private const float GEM_DETECT_RADIUS = 8f;
        private const float GEM_TARGET_STICKY_RADIUS_MULTIPLIER = 1.4f;
        private const float WALL_AVOID_DISTANCE = 10f;

        private void Awake()
        {
            _activeBots.Add(this);
            _rb = GetComponent<Rigidbody2D>();
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _growth = GetComponent<Core.MoleGrowth>();
        }

        private void OnDestroy()
        {
            _activeBots.Remove(this);
        }

        private void Start()
        {
            if (GameManager.Instance == null || GameManager.Instance.Settings == null)
            {
                enabled = false;
                return;
            }

            _settings = GameManager.Instance.Settings;
            _targetAngle = Random.Range(0f, 360f);

            // AI 봇은 항상 터널 생성 (Stealth & Ambush: AI는 항상 Assault 모드)
            var tunnel = GetComponent<Tunnel.TunnelGenerator>();
            if (tunnel != null) tunnel.SetDigging(true);
        }

        private void Update()
        {
            if (_isDead) return;
            if (_settings == null) return;

            UpdateAI();
        }

        private void FixedUpdate()
        {
            if (_isDead) return;
            if (_settings == null) return;

            Rotate(Time.fixedDeltaTime);
            Move(Time.fixedDeltaTime);
        }

        private void UpdateAI()
        {
            // 젬 탐색 (주기적)
            _gemSearchTimer -= Time.deltaTime;
            if (_gemSearchTimer <= 0f)
            {
                _gemSearchTimer = GEM_SEARCH_INTERVAL;
                SearchNearestGem();
            }

            // 벽 회피 (최우선)
            float distFromCenter = transform.position.magnitude;
            float mapRadius = _settings.MapRadius;

            if (distFromCenter > mapRadius - WALL_AVOID_DISTANCE)
            {
                // 중심 방향으로 회전
                Vector2 toCenter = -transform.position.normalized;
                _targetAngle = Mathf.Atan2(toCenter.y, toCenter.x) * Mathf.Rad2Deg - 90f;
                return;
            }

            // 젬 추적 (전방 90도 이내만)
            if (_targetGem != null && _targetGem.gameObject.activeInHierarchy)
            {
                Vector2 toGem = _targetGem.position - transform.position;
                float dot = Vector2.Dot(transform.up, toGem.normalized);

                // 전방 부채꼴 밖이면 젬 포기
                if (dot < 0.2f) // ~78도 이상 벗어나면 포기
                {
                    _targetGem = null;
                }
                else
                {
                    _targetAngle = Mathf.Atan2(toGem.y, toGem.x) * Mathf.Rad2Deg - 90f;
                    return;
                }
            }

            // 랜덤 방향 전환
            _angleChangeTimer -= Time.deltaTime;
            if (_angleChangeTimer <= 0f)
            {
                _angleChangeTimer = ANGLE_CHANGE_INTERVAL + Random.Range(-ANGLE_CHANGE_JITTER, ANGLE_CHANGE_JITTER);
                _targetAngle += Random.Range(-RANDOM_TURN_ANGLE, RANDOM_TURN_ANGLE);
            }
        }

        private void SearchNearestGem()
        {
            if (IsStickyGemTarget(_targetGem))
                return;

            Transform bestTarget = null;
            float closestSqr = GEM_DETECT_RADIUS * GEM_DETECT_RADIUS;

            foreach (World.Gem gem in World.Gem.ActiveGems)
            {
                if (gem == null) continue;

                Vector2 toGem = gem.transform.position - transform.position;
                float sqrDist = toGem.sqrMagnitude;
                if (sqrDist >= closestSqr) continue;

                // 전방 부채꼴 필터 (후방 젬 무시)
                float dot = Vector2.Dot(transform.up, toGem.normalized);
                if (dot < 0.2f) continue;

                closestSqr = sqrDist;
                bestTarget = gem.transform;
            }

            _targetGem = bestTarget;
        }

        private bool IsStickyGemTarget(Transform gemTarget)
        {
            if (gemTarget == null) return false;
            if (!gemTarget.gameObject.activeInHierarchy) return false;

            Vector2 toGem = gemTarget.position - transform.position;
            float stickyRadius = GEM_DETECT_RADIUS * GEM_TARGET_STICKY_RADIUS_MULTIPLIER;
            if (toGem.sqrMagnitude > stickyRadius * stickyRadius)
                return false;

            return true;
        }

        private void Move(float deltaTime)
        {
            float speed = _settings.BaseSpeed * transform.localScale.x;
            CurrentSpeed = speed;
            transform.position += transform.up * (speed * deltaTime);
        }

        private void Rotate(float deltaTime)
        {
            Quaternion targetRotation = Quaternion.Euler(0f, 0f, _targetAngle);
            float scale = transform.localScale.x;
            float turnSpeed = _settings.BaseTurnSpeed / Mathf.Max(scale, 0.1f);

            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRotation,
                turnSpeed * deltaTime
            );
        }

        /// <summary>
        /// 젬 수집 시 호출 (Gem.cs의 OnTriggerEnter2D에서).
        /// </summary>
        public void AddScore(float amount)
        {
            if (_growth != null)
                _growth.AddScore(amount);
        }

        /// <summary>사망 처리 (IDigger 구현).</summary>
        public void Die()
        {
            if (_isDead) return;

            NetworkIdentity networkIdentity = GetComponent<NetworkIdentity>();
            if (networkIdentity != null)
            {
                // 네트워크 엔티티는 서버에서만 파괴한다.
                if (!NetworkServer.active) return;

                _isDead = true;
                CurrentSpeed = 0f;
                NetworkServer.Destroy(gameObject);
                return;
            }

            _isDead = true;
            CurrentSpeed = 0f;

            // 터널 파기 중단
            var tunnel = GetComponent<Tunnel.TunnelGenerator>();
            if (tunnel != null)
                tunnel.SetDigging(false);

            // 시각적 피드백
            var sr = GetComponentInChildren<SpriteRenderer>();
            if (sr != null)
                sr.color = new Color(1f, 0.2f, 0.2f, 0.5f);

            transform.localScale *= 0.5f;

            // 일정 시간 후 제거
            Destroy(gameObject, 2f);
        }
    }
}
