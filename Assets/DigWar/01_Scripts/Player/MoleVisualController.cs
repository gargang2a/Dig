using UnityEngine;

namespace Player
{
    /// <summary>
    /// MoleAnimated 쉐이더 + 절차적 애니메이션(Level 2 + 3)을 결합하여
    /// 캐릭터에 "살아있는 느낌"을 부여한다.
    /// 
    /// [Level 1 - Shader]: Squash/Stretch, Breathing, Hit Flash, Attack Glow
    /// [Level 2 - Procedural]: 드릴 회전 라인(LineRenderer), 몸체 기울기, 부스트 스케일 펀치
    /// [Level 3 - Juice]: 카메라 흔들림(Screen Shake), 부스트 잔상(Afterimage)
    /// 
    /// [성능 노트]
    /// - Animator 미사용 (CPU 비용 0)
    /// - MaterialPropertyBlock 사용 (배칭 유지, GC 0)
    /// </summary>
    public class MoleVisualController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Visuals 자식의 SpriteRenderer. 비워두면 자동 탐색.")]
        [SerializeField] private SpriteRenderer _spriteRenderer;
        [Tooltip("몸체 Transform (기울기 적용 대상). 비워두면 SpriteRenderer의 Transform 사용.")]
        [SerializeField] private Transform _bodyTransform;

        [Header("Speed Mapping")]
        [Tooltip("이 속도 이상이면 Speed 파라미터가 1.0")]
        [SerializeField] private float _maxSpeedReference = 15f;

        [Header("Hit Flash")]
        [SerializeField] private float _hitFlashDuration = 0.15f;

        [Header("Body Lean (회전 시 기울기)")]
        [SerializeField] private float _maxLeanAngle = 15f;
        [SerializeField] private float _leanSmoothing = 8f;

        [Header("Drill Spin (LineRenderer 기반 회전 라인)")]
        [SerializeField] private bool _enableDrillLines = false;
        [SerializeField] private Color _drillLineColor = new Color(0.8f, 0.8f, 0.8f, 0.5f);
        [SerializeField] private float _drillOffsetY = 0.4f;
        [SerializeField] private float _drillRadius = 0.15f;
        [SerializeField] private float _drillSpinSpeed = 720f;

        [Header("Boost Scale Punch")]
        [SerializeField] private float _boostPunchScale = 0.15f;
        [SerializeField] private float _punchDecay = 10f;

        [Header("Level 3: Game Juice")]
        [Tooltip("부스트 잔상 생성 간격 (초)")]
        [SerializeField] private float _afterimageInterval = 0.2f;
        [Tooltip("잔상 수명 (초)")]
        [SerializeField] private float _afterimageLifetime = 0.15f;

        // === Shader Property IDs ===
        private static readonly int PROP_SPEED = Shader.PropertyToID("_Speed");
        private static readonly int PROP_IS_ATTACKING = Shader.PropertyToID("_IsAttacking");
        private static readonly int PROP_HIT_FLASH = Shader.PropertyToID("_HitFlash");

        // === Internal State ===
        private MaterialPropertyBlock _mpb;
        private IDigger _digger;
        private float _hitFlashTimer;

        // Level 2
        private float _prevAngle;
        private float _currentLean;
        private float _punchValue;
        private bool _wasBoosting;

        // Drill Lines
        private LineRenderer _drillLineRenderer;
        private float _drillAngle;
        private Transform _drillPivot;

        // Level 3
        private float _afterimageTimer;
        private bool _isPlayer;
        private Vector3 _originalCameraPos; // 카메라 흔들림 복구용 (Simple implementation)

        private void Awake()
        {
            _mpb = new MaterialPropertyBlock();

            if (_spriteRenderer == null)
                _spriteRenderer = GetComponentInChildren<SpriteRenderer>();

            if (_bodyTransform == null && _spriteRenderer != null)
                _bodyTransform = _spriteRenderer.transform;

            _digger = GetComponentInParent<IDigger>();
            if (_digger == null)
                _digger = GetComponent<IDigger>();

            // 봇(AI)에서는 잔상 비활성화 (IsAttacking이 항상 true라 Ghost가 무한 생성됨)
            _isPlayer = GetComponentInParent<PlayerController>() != null
                     || GetComponent<PlayerController>() != null;

            _prevAngle = transform.eulerAngles.z;
        }

        private void Start()
        {
            // [NOTE] Drill Spin Lines는 URP 환경에서 렌더링 문제(막대기처럼 보임)가 있어 비활성화.
            // 추후 드릴 진화 시스템에서 SpriteRenderer 기반으로 재구현 예정.
            _enableDrillLines = false;
        }

        private void LateUpdate()
        {
            if (_spriteRenderer == null || _digger == null) return;

            float speed = _digger.CurrentSpeed;
            float normalizedSpeed = Mathf.Clamp01(speed / _maxSpeedReference);
            bool isAttacking = _digger.IsAttacking;

            UpdateShaderParams(normalizedSpeed, isAttacking);
            UpdateBodyLean(normalizedSpeed);
            UpdateDrillSpin(normalizedSpeed, isAttacking);
            UpdateBoostPunch(isAttacking);
            
            // Level 3 (플레이어만)
            if (_isPlayer) UpdateAfterimage(isAttacking);
        }

        // ===============================================================
        // Level 1: Shader
        // ===============================================================
        private void UpdateShaderParams(float normalizedSpeed, bool isAttacking)
        {
            if (_hitFlashTimer > 0f)
                _hitFlashTimer -= Time.deltaTime;

            _spriteRenderer.GetPropertyBlock(_mpb);
            _mpb.SetFloat(PROP_SPEED, normalizedSpeed);
            _mpb.SetFloat(PROP_IS_ATTACKING, isAttacking ? 1f : 0f);
            _mpb.SetFloat(PROP_HIT_FLASH, Mathf.Clamp01(_hitFlashTimer / _hitFlashDuration));
            _spriteRenderer.SetPropertyBlock(_mpb);
        }

        // ===============================================================
        // Level 2: Body Lean
        // ===============================================================
        private void UpdateBodyLean(float normalizedSpeed)
        {
            if (_bodyTransform == null) return;
            if (Time.deltaTime < 0.0001f)
            {
                _prevAngle = transform.eulerAngles.z;
                return;
            }

            float currentAngle = transform.eulerAngles.z;
            float angularVelocity = Mathf.DeltaAngle(_prevAngle, currentAngle) / Time.deltaTime;
            _prevAngle = currentAngle;

            float targetLean = Mathf.Clamp(angularVelocity * 0.01f, -1f, 1f) * _maxLeanAngle;
            targetLean *= normalizedSpeed;

            _currentLean = Mathf.Lerp(_currentLean, targetLean, Time.deltaTime * _leanSmoothing);
            _bodyTransform.localRotation = Quaternion.Euler(0f, 0f, _currentLean);
        }

        // ===============================================================
        // Level 2: Drill Spin Lines (LineRenderer)
        // ===============================================================
        private void CreateDrillSpinLines()
        {
            var pivotObj = new GameObject("DrillSpin");
            _drillPivot = pivotObj.transform;
            _drillPivot.SetParent(_bodyTransform ?? transform, false);
            _drillPivot.localPosition = new Vector3(0f, _drillOffsetY, 0f);

            _drillLineRenderer = pivotObj.AddComponent<LineRenderer>();

            var shader = Shader.Find("Sprites/Default")
                ?? Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            var mat = new Material(shader);
            _drillLineRenderer.material = mat;

            _drillLineRenderer.startColor = _drillLineColor;
            _drillLineRenderer.endColor = _drillLineColor;
            _drillLineRenderer.startWidth = 0.03f;
            _drillLineRenderer.endWidth = 0.03f;
            _drillLineRenderer.useWorldSpace = false;
            _drillLineRenderer.sortingOrder = _spriteRenderer.sortingOrder + 1;
            _drillLineRenderer.positionCount = 5;

            _drillLineRenderer.enabled = false;
        }

        private void UpdateDrillSpin(float normalizedSpeed, bool isAttacking)
        {
            if (_drillLineRenderer == null || _drillPivot == null) return;

            float intensity = Mathf.Max(normalizedSpeed, isAttacking ? 1f : 0f);
            _drillLineRenderer.enabled = intensity > 0.1f;

            if (!_drillLineRenderer.enabled) return;

            float spinMultiplier = isAttacking ? 2f : 1f;
            _drillAngle += _drillSpinSpeed * intensity * spinMultiplier * Time.deltaTime;

            float scale = transform.localScale.x;
            float r = _drillRadius * scale;

            float rad = _drillAngle * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad) * r;
            float sin = Mathf.Sin(rad) * r;

            _drillLineRenderer.SetPosition(0, new Vector3(-cos, -sin, 0f));
            _drillLineRenderer.SetPosition(1, new Vector3(cos, sin, 0f));
            _drillLineRenderer.SetPosition(2, Vector3.zero);
            _drillLineRenderer.SetPosition(3, new Vector3(-sin, cos, 0f));
            _drillLineRenderer.SetPosition(4, new Vector3(sin, -cos, 0f));

            Color c = _drillLineColor;
            c.a = _drillLineColor.a * intensity;
            _drillLineRenderer.startColor = c;
            _drillLineRenderer.endColor = c;

            _drillLineRenderer.startWidth = 0.03f * scale;
            _drillLineRenderer.endWidth = 0.03f * scale;
        }

        // ===============================================================
        // Level 2: Boost Scale Punch
        // ===============================================================
        private void UpdateBoostPunch(bool isAttacking)
        {
            if (_bodyTransform == null) return;

            if (isAttacking && !_wasBoosting)
                _punchValue = _boostPunchScale;
            _wasBoosting = isAttacking;

            if (_punchValue > 0.001f)
                _punchValue = Mathf.Lerp(_punchValue, 0f, Time.deltaTime * _punchDecay);
            else
                _punchValue = 0f;

            float s = 1f + _punchValue;
            _bodyTransform.localScale = new Vector3(s, s, 1f);
        }

        // ===============================================================
        // Level 3: Game Juice (Afterimage & Shake)
        // ===============================================================
        private void UpdateAfterimage(bool isAttacking)
        {
            if (!isAttacking) return;

            _afterimageTimer -= Time.deltaTime;
            if (_afterimageTimer <= 0f)
            {
                CreateAfterimage();
                _afterimageTimer = _afterimageInterval;
            }
        }

        private void CreateAfterimage()
        {
            // 잔상 오브젝트 생성 (풀링 없이 간단 구현, 성능 위해 추후 풀링 권장)
            var ghostObj = new GameObject("Ghost");
            ghostObj.transform.position = _bodyTransform.position;
            ghostObj.transform.rotation = _bodyTransform.rotation;
            ghostObj.transform.localScale = _bodyTransform.lossyScale;

            var sr = ghostObj.AddComponent<SpriteRenderer>();
            sr.sprite = _spriteRenderer.sprite;
            sr.color = new Color(1f, 1f, 1f, 0.2f); // 은은한 잔상
            sr.sortingOrder = _spriteRenderer.sortingOrder - 1;

            // 페이드 아웃 코루틴 대신 간단 스크립트 부착
            var fade = ghostObj.AddComponent<GhostFade>();
            fade.Setup(_afterimageLifetime);
        }



        // ===============================================================
        // Public API
        // ===============================================================
        public void TriggerHitFlash()
        {
            _hitFlashTimer = _hitFlashDuration;
        }
    }
}
