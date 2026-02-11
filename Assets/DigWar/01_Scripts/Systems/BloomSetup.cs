using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Systems
{
    /// <summary>
    /// Global Volume + Bloom을 런타임에 자동 생성한다.
    /// 씬에 Volume이 없으면 자동으로 추가되므로 수동 설정 불필요.
    /// </summary>
    public class BloomSetup : MonoBehaviour
    {
        [Header("Bloom 설정")]
        [Tooltip("블룸 강도 (높을수록 밝게 번짐)")]
        [Range(0f, 5f)]
        [SerializeField] private float _bloomIntensity = 1.5f;

        [Tooltip("블룸 임계값 (이 밝기 이상만 번짐)")]
        [Range(0f, 2f)]
        [SerializeField] private float _bloomThreshold = 0.8f;

        [Tooltip("블룸 확산 정도 (0~1)")]
        [Range(0f, 1f)]
        [SerializeField] private float _bloomScatter = 0.7f;

        private void Start()
        {
            // 이미 Volume이 있으면 건너뜀
            var existing = FindObjectOfType<Volume>();
            if (existing != null)
            {
                Debug.Log("[BloomSetup] 기존 Volume 사용");
                ConfigureBloom(existing);
                return;
            }

            // Global Volume 생성
            var volumeObj = new GameObject("GlobalVolume");
            var volume = volumeObj.AddComponent<Volume>();
            volume.isGlobal = true;

            // Volume Profile 생성
            volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();

            ConfigureBloom(volume);
            EnableCameraPostProcessing();
            Debug.Log("[BloomSetup] Global Volume + Bloom 자동 생성 완료");
        }

        /// <summary>
        /// Main Camera의 Post Processing을 자동 활성화한다.
        /// URP에서 카메라에 Post Processing이 꺼져 있으면 Bloom이 보이지 않는다.
        /// </summary>
        private void EnableCameraPostProcessing()
        {
            var cam = Camera.main;
            if (cam == null) return;

            var cameraData = cam.GetUniversalAdditionalCameraData();
            if (cameraData != null)
            {
                cameraData.renderPostProcessing = true;
            }
        }

        private void ConfigureBloom(Volume volume)
        {
            if (volume.profile == null)
                volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();

            // 기존 Bloom 오버라이드 확인
            if (!volume.profile.TryGet<Bloom>(out var bloom))
            {
                bloom = volume.profile.Add<Bloom>(true);
            }

            bloom.active = true;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = _bloomIntensity;
            bloom.threshold.overrideState = true;
            bloom.threshold.value = _bloomThreshold;
            bloom.scatter.overrideState = true;
            bloom.scatter.value = _bloomScatter;
        }
    }
}
