using UnityEngine;

namespace Player
{
    /// <summary>
    /// 두더지가 이동 중일 때 머리 앞쪽에 흙 파편 파티클을 방출한다.
    /// PlayerController와 같은 오브젝트에 추가한다.
    /// </summary>
    public class DiggingParticle : MonoBehaviour
    {
        [Header("파티클 설정")]
        [SerializeField] private Color _dirtColor = new Color(0.55f, 0.35f, 0.17f, 1f);
        [SerializeField] private Color _dirtColorAlt = new Color(0.70f, 0.50f, 0.25f, 1f);
        [SerializeField] private float _emissionRate = 30f;
        [SerializeField] private float _boostEmissionMultiplier = 3f;

        private ParticleSystem _ps;
        private ParticleSystem.EmissionModule _emission;
        private IDigger _digger;

        private void Start()
        {
            _digger = GetComponent<IDigger>();
            CreateParticleSystem();
        }

        private void Update()
        {
            if (_digger == null || _ps == null) return;

            float scale = transform.localScale.x;
            float speed = _digger.CurrentSpeed;
            bool moving = speed > 0.1f;

            // 이동 중일 때만 파티클 방출
            float rate = moving ? _emissionRate * scale : 0f;
            if (_digger.IsBoosting)
                rate *= _boostEmissionMultiplier;
            _emission.rateOverTime = rate;

            // 크기에 비례하여 파티클 스케일 조정
            var main = _ps.main;
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f * scale, 0.15f * scale);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1f * scale, 3f * scale);

            // 발사 위치도 스케일에 맞게
            _ps.transform.localPosition = new Vector3(0f, 0.5f * scale, 0f);
        }

        private void CreateParticleSystem()
        {
            var psObj = new GameObject("DiggingDust");
            psObj.transform.SetParent(transform, false);
            // 머리 방향(Y+) 앞쪽에 배치
            psObj.transform.localPosition = new Vector3(0f, 0.5f, 0f);

            _ps = psObj.AddComponent<ParticleSystem>();

            // Main
            var main = _ps.main;
            main.startLifetime = 0.6f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.15f);
            main.startColor = new ParticleSystem.MinMaxGradient(_dirtColor, _dirtColorAlt);
            main.gravityModifier = 0.5f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);

            // Emission
            _emission = _ps.emission;
            _emission.rateOverTime = 0f;

            // Shape: 콘 형태로 전방 확산
            var shape = _ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 35f;
            shape.radius = 0.15f;
            // 로컬 Y+ (전방)에서 방출
            shape.rotation = new Vector3(-90f, 0f, 0f);

            // Size over Lifetime: 점점 작아짐
            var sol = _ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(
                    new Keyframe(0f, 1f),
                    new Keyframe(1f, 0f)
                ));

            // Color over Lifetime: 페이드 아웃
            var col = _ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) }
            );
            col.color = grad;

            // Renderer 설정
            var renderer = psObj.GetComponent<ParticleSystemRenderer>();
            renderer.material = new Material(Shader.Find("Sprites/Default"));
            renderer.sortingOrder = 15;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }
    }
}
