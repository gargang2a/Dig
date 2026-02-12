using UnityEngine;

namespace Systems
{
    /// <summary>
    /// 게임 내 모든 사운드(BGM, SFX)를 관리하는 중앙 오디오 매니저.
    /// 싱글톤으로 어디서든 접근 가능하며, 오디오 클립과 소스를 관리한다.
    /// </summary>
    public class SoundManager : MonoBehaviour
    {
        public static SoundManager Instance { get; private set; }

        [Header("Audio Sources")]
        [SerializeField] private AudioSource _bgmSource;
        [SerializeField] private AudioSource _sfxSource;
        [SerializeField] private AudioSource _engineSource; // 플레이어 이동/부스트용 (Loop)

        [Header("BGM Clips")]
        [SerializeField] private AudioClip _mainBgm;

        [Header("SFX Clips")]
        [SerializeField] private AudioClip _gemCollectClip;
        [SerializeField] private AudioClip _playerDieClip;
        [SerializeField] private AudioClip _gameStartClip;
        [SerializeField] private AudioClip _boostLoopClip; // Engine Source용

        [Header("Settings")]
        [SerializeField, Range(0f, 1f)] private float _masterVolume = 1f;
        [SerializeField] private float _gemSoundCooldown = 0.05f; // 너무 잦은 재생 방지

        [Header("Engine Sound Settings")]
        [SerializeField, Range(1f, 2f)] private float _boostPitchBase = 1.4f;
        [SerializeField, Range(0f, 1f)] private float _jitterIntensity = 0.1f;
        [SerializeField, Range(0.1f, 10f)] private float _jitterFrequency = 2.0f;

        private float _lastGemSoundTime;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // AudioSource 자동 생성 (Inspector 연결 안 됐을 경우)
            if (_bgmSource == null) _bgmSource = CreateAudioSource("BGMSource", true);
            if (_sfxSource == null) _sfxSource = CreateAudioSource("SFXSource", false);
            if (_engineSource == null) _engineSource = CreateAudioSource("EngineSource", true);
        }

        private void Start()
        {
            PlayBGM(_mainBgm);
        }

        private AudioSource CreateAudioSource(string name, bool loop)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(transform);
            var source = obj.AddComponent<AudioSource>();
            source.loop = loop;
            source.playOnAwake = false;
            return source;
        }

        public void PlayBGM(AudioClip clip)
        {
            if (clip == null || _bgmSource == null) return;
            if (_bgmSource.clip == clip && _bgmSource.isPlaying) return;

            _bgmSource.clip = clip;
            _bgmSource.volume = _masterVolume * 0.6f;
            _bgmSource.Play();
        }

        public void PlaySFX(AudioClip clip, float volumeScale = 1f)
        {
            if (clip == null || _sfxSource == null) return;
            _sfxSource.PlayOneShot(clip, _masterVolume * volumeScale);
        }

        /// <summary>
        /// 젬 획득 사운드 (쿨타임 적용)
        /// </summary>
        public void PlayGemCollect()
        {
            if (Time.time - _lastGemSoundTime < _gemSoundCooldown) return;
            
            _lastGemSoundTime = Time.time;
            PlaySFX(_gemCollectClip, 0.7f);
        }

        public void PlayPlayerDie()
        {
            Debug.Log("[SoundManager] PlayPlayerDie Called");
            PlaySFX(_playerDieClip);
        }

        public void PlayGameStart()
        {
            PlaySFX(_gameStartClip);
        }

        /// <summary>
        /// 플레이어 이동/부스트 엔진음 제어
        /// </summary>
        /// <param name="isMoving">이동 중인지</param>
        /// <param name="isBoosting">부스트 중인지 (점수 부족 시 false)</param>
        public void UpdateEngineSound(bool isMoving, bool isBoosting)
        {
            if (_engineSource == null) return;

            // 엔진 클립 할당 및 재생 확인
            if (_boostLoopClip != null)
            {
                if (_engineSource.clip != _boostLoopClip)
                    _engineSource.clip = _boostLoopClip;
                
                if (!_engineSource.isPlaying)
                    _engineSource.Play();
            }

            if (_boostLoopClip == null) return;

            float targetVolume = 0f;
            float basePitch = 1.0f;
            float jitter = 0f;

            if (isMoving)
            {
                targetVolume = isBoosting ? 1f : 0.3f;
                basePitch = isBoosting ? _boostPitchBase : 1.0f;

                // 피로도 감소를 위한 미세한 피치 변동 (Perlin Noise)
                // 시간 흐름에 따라 -0.5*Intensity ~ +0.5*Intensity 범위에서 흔들림
                jitter = (Mathf.PerlinNoise(Time.time * _jitterFrequency, 0f) - 0.5f) * _jitterIntensity;
            }

            // 부드러운 전환
            _engineSource.volume = Mathf.Lerp(_engineSource.volume, targetVolume * _masterVolume, Time.deltaTime * 5f);
            
            // 피치 = 기본 피치 + 지터
            float finalPitch = basePitch + jitter;
            _engineSource.pitch = Mathf.Lerp(_engineSource.pitch, finalPitch, Time.deltaTime * 5f);
        }

        public void StopEngineSound()
        {
            if (_engineSource != null && _engineSource.isPlaying)
            {
                _engineSource.Stop();
            }
        }
    }
}
