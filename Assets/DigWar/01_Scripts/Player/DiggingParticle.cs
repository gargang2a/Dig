using UnityEngine;

namespace Player
{
    /// <summary>
    /// 두더지가 이동 중일 때 파티클을 방출한다.
    /// 1. Head: 흙 파편 (Original Setting 복구)
    /// 2. Tail: 엉덩이 뒤 연기 (New)
    /// </summary>
    public class DiggingParticle : MonoBehaviour
    {
        [Header("Head Particle (Dirt)")]
        [SerializeField] private Color _dirtColor = new Color(0.55f, 0.35f, 0.17f, 1f);
        [SerializeField] private Color _dirtColorAlt = new Color(0.70f, 0.50f, 0.25f, 1f);
        [SerializeField] private float _emissionRate = 30f;
        [SerializeField] private float _boostEmissionMultiplier = 3f;

        [Header("Tail Particle (Dust Trail)")]
        [SerializeField] private Color _tailColor = new Color(0.6f, 0.45f, 0.3f, 0.3f); // 갈색 계열 반투명
        [SerializeField] private float _tailEmissionRate = 20f;

        private ParticleSystem _headPS;
        private ParticleSystem _tailPS;
        private ParticleSystem.EmissionModule _headEmission;
        private ParticleSystem.EmissionModule _tailEmission;
        
        private IDigger _digger;
        private float _lastScale = -1f;

        private void Start()
        {
            _digger = GetComponent<IDigger>();
            
            // 클린업: 기존 파티클 오브젝트들이 있다면 제거
            foreach (Transform child in transform)
            {
                if (child.name.Contains("Digging")) Destroy(child.gameObject);
            }

            CreateSystems();
        }

        private void Update()
        {
            if (_digger == null || _headPS == null || _tailPS == null) return;

            float scale = transform.localScale.x;
            float speed = _digger.CurrentSpeed;
            bool moving = speed > 0.1f;

            // 1. Head Particle Control (Original Logic)
            float headRate = moving ? _emissionRate * scale : 0f;
            if (_digger.IsBoosting) headRate *= _boostEmissionMultiplier;
            _headEmission.rateOverTime = headRate;

            // 2. Tail Particle Control
            float tailRate = moving ? _tailEmissionRate * scale : 0f;
            if (_digger.IsBoosting) tailRate *= 1.5f; // 부스트 시 약간 증가
            _tailEmission.rateOverTime = tailRate;

            // Scale Sync
            if (!Mathf.Approximately(scale, _lastScale))
            {
                _lastScale = scale;
                UpdateScale(scale);
            }
        }

        private void UpdateScale(float scale)
        {
            // Head Scale (Original)
            var headMain = _headPS.main;
            headMain.startSize = new ParticleSystem.MinMaxCurve(0.05f * scale, 0.15f * scale);
            headMain.startSpeed = new ParticleSystem.MinMaxCurve(1f * scale, 3f * scale);
            var headShape = _headPS.shape;
            headShape.radius = 0.15f * scale;
            _headPS.transform.localPosition = new Vector3(0f, 0.4f, 0f); // 머리 쪽

            // Tail Scale (New)
            var tailMain = _tailPS.main;
            tailMain.startSize = new ParticleSystem.MinMaxCurve(0.2f * scale, 0.4f * scale); // 큼직하게
            tailMain.startSpeed = new ParticleSystem.MinMaxCurve(0.2f * scale, 0.8f * scale); // 느리게
            var tailShape = _tailPS.shape;
            tailShape.radius = 0.1f * scale;
            _tailPS.transform.localPosition = new Vector3(0f, -0.4f, 0f); // 엉덩이 쪽
        }

        private void CreateSystems()
        {
            var root = new GameObject("DiggingEffects");
            root.transform.SetParent(transform, false);
            root.transform.localPosition = Vector3.zero;

            _headPS = CreateHeadParticle(root.transform);
            _tailPS = CreateTailParticle(root.transform);

            _headEmission = _headPS.emission;
            _tailEmission = _tailPS.emission;
        }

        private ParticleSystem CreateHeadParticle(Transform parent)
        {
            var go = new GameObject("HeadDust");
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();

            // Main (Original Settings)
            var main = ps.main;
            main.startLifetime = 0.6f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.15f);
            main.startColor = new ParticleSystem.MinMaxGradient(_dirtColor, _dirtColorAlt);
            main.gravityModifier = 0.5f; // 중력 있음
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);

            // Emission
            var em = ps.emission;
            em.rateOverTime = 0f;

            // Shape
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 35f;
            shape.radius = 0.15f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            // Size Over Lifetime
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(1f, 0f)));

            // Color Over Lifetime
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) }
            );
            col.color = grad;

            SetupRenderer(go, 15); // Body(10)보다 위
            return ps;
        }

        private ParticleSystem CreateTailParticle(Transform parent)
        {
            var go = new GameObject("TailDust");
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();

            // Main (Dust Trail Settings)
            var main = ps.main;
            main.startLifetime = 1.5f; // 길게 남음
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.0f, 0.1f); // 거의 정지 상태 (제자리에 남음)
            main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.5f); // 큼직하게
            main.startColor = _tailColor; // 갈색
            main.gravityModifier = 0.0f; // 중력 영향 없음 (공중에 뜸)
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 100;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);

            // Emission
            var em = ps.emission;
            em.rateOverTime = 0f;

            // Shape (Line 형태나 넓은 Cone)
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere; // 둥글게 퍼짐
            shape.radius = 0.2f;
            
            // Size Over Lifetime (커지면서 사라짐)
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.5f), new Keyframe(1f, 1.2f)));

            // Color Over Lifetime (Fade Out)
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(_tailColor, 0f), new GradientColorKey(_tailColor, 1f) },
                new[] { new GradientAlphaKey(_tailColor.a, 0f), new GradientAlphaKey(0f, 1f) }
            );
            col.color = grad;

            SetupRenderer(go, 9); // Body(10)보다 뒤
            return ps;
        }

        private void SetupRenderer(GameObject go, int sortingOrder)
        {
            // URP 셰이더를 우선 탐색 (Sprites/Default는 URP에서 없을 수 있음)
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                ?? Shader.Find("Sprites/Default");

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer == null) renderer = go.AddComponent<ParticleSystemRenderer>();

            if (shader != null)
            {
                renderer.material = new Material(shader);
            }
            else
            {
                // 모든 셰이더를 못 찾으면 기본 파티클 머티리얼 사용
                renderer.material = new Material(Shader.Find("Particles/Standard Unlit"));
            }

            renderer.sortingOrder = sortingOrder;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }
    }
}
