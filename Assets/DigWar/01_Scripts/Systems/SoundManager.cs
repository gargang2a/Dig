using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Systems
{
    /// <summary>
    /// 寃뚯엫 ??紐⑤뱺 ?ъ슫??BGM, SFX)瑜?愿由ы븯??以묒븰 ?ㅻ뵒??留ㅻ땲?.
    /// ?깃??ㅼ쑝濡??대뵒?쒕뱺 ?묎렐 媛?ν븯硫? ?ㅻ뵒???대┰怨??뚯뒪瑜?愿由ы븳??
    /// </summary>
    public class SoundManager : MonoBehaviour
    {
        private const KeyCode MUTE_HOTKEY = KeyCode.M;

        public static SoundManager Instance { get; private set; }

        [Header("Audio Sources")]
        [SerializeField] private AudioSource _bgmSource;
        [SerializeField] private AudioSource _sfxSource;
        [SerializeField] private AudioSource _engineSource; // ?뚮젅?댁뼱 ?대룞/遺?ㅽ듃??(Loop)

        [Header("BGM Clips")]
        [SerializeField] private AudioClip _mainBgm;

        [Header("SFX Clips")]
        [SerializeField] private AudioClip _gemCollectClip;
        [SerializeField] private AudioClip _playerDieClip;
        [SerializeField] private AudioClip _killConfirmClip;
        [SerializeField] private AudioClip _gameStartClip;
        [SerializeField] private AudioClip _boostLoopClip; // Engine Source??

        [Header("Settings")]
        [SerializeField, Range(0f, 1f)] private float _masterVolume = 0.5f;
        [SerializeField] private float _gemSoundCooldown = 0.05f; // ?덈Т ??? ?ъ깮 諛⑹?
        [SerializeField] private bool _startMuted = false;
        [SerializeField] private bool _enableGlobalMuteHotkey = true;

        [Header("Engine Sound Settings")]
        [SerializeField, Range(1f, 2f)] private float _boostPitchBase = 1.4f;
        [SerializeField, Range(0f, 1f)] private float _jitterIntensity = 0.1f;
        [SerializeField, Range(0.1f, 10f)] private float _jitterFrequency = 2.0f;

        private float _lastGemSoundTime;
        private float _predictedGemSoundSuppressUntil;
        private bool _isMuted;
        private int _lastMuteToggleFrame = -1;
        private const float GEM_CONFIRM_SUPPRESS_WINDOW = 0.2f;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // AudioSource ?먮룞 ?앹꽦 (Inspector ?곌껐 ???먯쓣 寃쎌슦)
            if (_bgmSource == null) _bgmSource = CreateAudioSource("BGMSource", true);
            if (_sfxSource == null) _sfxSource = CreateAudioSource("SFXSource", false);
            if (_engineSource == null) _engineSource = CreateAudioSource("EngineSource", true);

            _isMuted = _startMuted;
            ApplyMuteState();

#if UNITY_WEBGL && !UNITY_EDITOR
            _enableGlobalMuteHotkey = true;
#endif
        }

        private void Start()
        {
            PlayBGM(_mainBgm);
        }

        private void Update()
        {
            if (!_enableGlobalMuteHotkey)
                return;

            if (WasMuteHotkeyPressedThisFrame())
                TryToggleMuteFromHotkey();
        }

        private void OnGUI()
        {
            if (!_enableGlobalMuteHotkey)
                return;

            Event currentEvent = Event.current;
            if (currentEvent == null || currentEvent.type != EventType.KeyDown)
                return;

            if (!IsMuteHotkeyEvent(currentEvent))
                return;

            TryToggleMuteFromHotkey();
            currentEvent.Use();
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

        public bool IsMuted => _isMuted;

        public void ToggleMute()
        {
            SetMuted(!_isMuted);
        }

        public void SetMuted(bool muted)
        {
            if (_isMuted == muted) return;
            _isMuted = muted;
            ApplyMuteState();
            Debug.Log($"[SoundManager] Mute {(_isMuted ? "ON" : "OFF")}");
        }

        public void PlaySFX(AudioClip clip, float volumeScale = 1f)
        {
            if (clip == null || _sfxSource == null) return;
            _sfxSource.PlayOneShot(clip, _masterVolume * volumeScale);
        }

        /// <summary>
        /// ???띾뱷 ?ъ슫??(荑⑦????곸슜)
        /// </summary>
        public void PlayGemCollect(bool isPredicted = false)
        {
            float now = Time.unscaledTime;
            if (!isPredicted && now <= _predictedGemSoundSuppressUntil) return;
            if (Time.time - _lastGemSoundTime < _gemSoundCooldown) return;

            _lastGemSoundTime = Time.time;
            if (isPredicted)
                _predictedGemSoundSuppressUntil = now + GEM_CONFIRM_SUPPRESS_WINDOW;

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

        public void PlayKillConfirm()
        {
            if (_killConfirmClip != null)
            {
                PlaySFX(_killConfirmClip, 0.95f);
                return;
            }

            if (_playerDieClip != null)
            {
                PlaySFX(_playerDieClip, 0.85f);
                return;
            }

            // 전용 클립이 없으면 시작 SFX로 대체 피드백을 제공한다.
            if (_gameStartClip != null)
                PlaySFX(_gameStartClip, 0.9f);
        }

        /// <summary>
        /// ?뚮젅?댁뼱 ?대룞/遺?ㅽ듃 ?붿쭊???쒖뼱
        /// </summary>
        /// <param name="isMoving">?대룞 以묒씤吏</param>
        /// <param name="isBoosting">遺?ㅽ듃 以묒씤吏 (?먯닔 遺議???false)</param>
        public void UpdateEngineSound(bool isMoving, bool isBoosting)
        {
            if (_engineSource == null) return;

            // ?붿쭊 ?대┰ ?좊떦 諛??ъ깮 ?뺤씤
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

                // ?쇰줈??媛먯냼瑜??꾪븳 誘몄꽭???쇱튂 蹂??(Perlin Noise)
                // ?쒓컙 ?먮쫫???곕씪 -0.5*Intensity ~ +0.5*Intensity 踰붿쐞?먯꽌 ?붾뱾由?
                jitter = (Mathf.PerlinNoise(Time.time * _jitterFrequency, 0f) - 0.5f) * _jitterIntensity;
            }

            // 遺?쒕윭???꾪솚
            _engineSource.volume = Mathf.Lerp(_engineSource.volume, targetVolume * _masterVolume, Time.deltaTime * 5f);
            
            // ?쇱튂 = 湲곕낯 ?쇱튂 + 吏??
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

        private void ApplyMuteState()
        {
            if (_bgmSource != null) _bgmSource.mute = _isMuted;
            if (_sfxSource != null) _sfxSource.mute = _isMuted;
            if (_engineSource != null) _engineSource.mute = _isMuted;
            AudioListener.pause = _isMuted;
        }

        private void TryToggleMuteFromHotkey()
        {
            if (_lastMuteToggleFrame == Time.frameCount)
                return;

            _lastMuteToggleFrame = Time.frameCount;
            ToggleMute();
        }

        private static bool WasMuteHotkeyPressedThisFrame()
        {
            if (WasLegacyMuteHotkeyPressedThisFrame())
                return true;

#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.mKey.wasPressedThisFrame)
                return true;
#endif

            if (WasMuteCharacterTypedThisFrame())
                return true;

            return false;
        }

        private static bool WasMuteCharacterTypedThisFrame()
        {
            try
            {
                string input = Input.inputString;
                if (string.IsNullOrEmpty(input))
                    return false;

                foreach (char c in input)
                {
                    if (c == 'm' || c == 'M' || c == 'ㅡ')
                        return true;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            return false;
        }

        private static bool WasLegacyMuteHotkeyPressedThisFrame()
        {
            try
            {
                return Input.GetKeyDown(MUTE_HOTKEY);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool IsMuteHotkeyEvent(Event currentEvent)
        {
            if (currentEvent.keyCode == MUTE_HOTKEY)
                return true;

            if (currentEvent.character == 'm' || currentEvent.character == 'M' || currentEvent.character == 'ㅡ')
                return true;

            return false;
        }
    }
}

