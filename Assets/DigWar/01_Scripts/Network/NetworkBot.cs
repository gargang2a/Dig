using System.Collections.Generic;
using Core.Data;
using Mirror;
using UnityEngine;

namespace Network
{
    /// <summary>
    /// Network wrapper for AI bots.
    /// Server owns bot gameplay state; clients render synced visuals only.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkBot : NetworkBehaviour
    {
        private static readonly HashSet<NetworkBot> _activeBots = new HashSet<NetworkBot>();
        public static IReadOnlyCollection<NetworkBot> ActiveBots => _activeBots;

        [SyncVar(hook = nameof(OnBotIndexChanged))]
        public int BotIndex;

        [SyncVar]
        public float Score;

        [SyncVar(hook = nameof(OnSyncedScaleChanged))]
        private float _syncedScale;

        public static readonly Color[] BOT_COLORS =
        {
            new Color(0.2f, 0.8f, 0.4f),
            new Color(0.8f, 0.3f, 0.3f),
            new Color(0.3f, 0.5f, 0.9f),
            new Color(0.9f, 0.7f, 0.1f),
            new Color(0.7f, 0.3f, 0.9f),
            new Color(0.9f, 0.5f, 0.2f)
        };

        public static readonly string[] BOT_NAMES =
        {
            "TUNNELKING", "GRUBWORM", "DIRTDASH", "DRILLMA",
            "DIGIDIG", "MOLETRAP", "BURROWBOSS", "SANDCLAW",
            "GEMHUNTER", "MUDSLIDE"
        };

        private Player.AIController _ai;
        private GameSettings _settings;
        private float _scoreSyncTimer;
        private const float SCORE_SYNC_INTERVAL = 0.5f;
        private const float DEFAULT_MIN_SCALE = 0.5f;

        // Client-side smoothing to avoid visible scale stepping.
        private const float REMOTE_SCALE_SMOOTH_TIME = 0.12f;
        private float _remoteScaleVelocity;
        private float _targetRemoteScale;

        public override void OnStartServer()
        {
            base.OnStartServer();
            _activeBots.Add(this);
            _ai = GetComponent<Player.AIController>();
            _settings = Core.GameManager.Instance?.Settings;

            if (_ai != null)
                _ai.enabled = true;

            Score = SanitizeScore(_ai != null ? _ai.Score : Score);
            _syncedScale = ResolveScaleFromScore(Score);
        }

        public override void OnStopServer()
        {
            _activeBots.Remove(this);
            base.OnStopServer();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            _activeBots.Add(this);
            _settings = Core.GameManager.Instance?.Settings;

            if (!isServer)
            {
                var ai = GetComponent<Player.AIController>();
                if (ai != null)
                    ai.enabled = false;

                var growth = GetComponent<Core.MoleGrowth>();
                if (growth != null)
                    growth.enabled = false;

                var tunnelGenerator = GetComponent<Tunnel.TunnelGenerator>();
                if (tunnelGenerator != null)
                    tunnelGenerator.SetDigging(true);
            }

            ApplyVisuals(BotIndex);
            float initialScale = ResolveSafeScale(_syncedScale > 0f ? _syncedScale : ResolveScaleFromScore(Score), ResolveScaleFallback());
            _targetRemoteScale = initialScale;
            ApplyRemoteScaleImmediate(initialScale);
        }

        public override void OnStopClient()
        {
            _activeBots.Remove(this);
            base.OnStopClient();
        }

        private void OnDestroy()
        {
            _activeBots.Remove(this);
        }

        private void Update()
        {
            if (isServer && _ai != null)
            {
                _scoreSyncTimer -= Time.deltaTime;
                if (_scoreSyncTimer <= 0f)
                {
                    _scoreSyncTimer = SCORE_SYNC_INTERVAL;
                    Score = SanitizeScore(_ai.Score);
                    _syncedScale = ResolveScaleFromScore(Score);
                }
            }

            if (!isServer)
            {
                UpdateRemoteScaleSmoothing();
            }
        }

        private void OnBotIndexChanged(int oldVal, int newVal)
        {
            ApplyVisuals(newVal);
        }

        private void OnSyncedScaleChanged(float oldScale, float newScale)
        {
            if (isServer)
                return;

            float fallbackScale = ResolveScaleFromScore(Score);
            _targetRemoteScale = ResolveSafeScale(newScale > 0f ? newScale : fallbackScale, ResolveScaleFallback());
        }

        private void ApplyVisuals(int index)
        {
            string botName = BOT_NAMES[index % BOT_NAMES.Length];
            gameObject.name = $"Bot_{botName}";

            var spriteRenderer = GetComponentInChildren<SpriteRenderer>();
            if (spriteRenderer != null)
                spriteRenderer.color = BOT_COLORS[index % BOT_COLORS.Length];
        }

        private void ApplyRemoteScaleImmediate(float scale)
        {
            if (isServer)
                return;

            float safeScale = ResolveSafeScale(scale, ResolveScaleFallback());
            transform.localScale = Vector3.one * safeScale;
        }

        private void UpdateRemoteScaleSmoothing()
        {
            float fallbackScale = ResolveScaleFallback();

            if (!IsFinitePositive(_targetRemoteScale))
            {
                _targetRemoteScale = fallbackScale;
                _remoteScaleVelocity = 0f;
            }

            float currentScale = transform.localScale.x;
            if (!IsFinitePositive(currentScale))
            {
                currentScale = fallbackScale;
                transform.localScale = Vector3.one * currentScale;
            }

            float nextScale = Mathf.SmoothDamp(
                currentScale,
                _targetRemoteScale,
                ref _remoteScaleVelocity,
                REMOTE_SCALE_SMOOTH_TIME);

            if (!IsFinitePositive(nextScale))
            {
                nextScale = _targetRemoteScale;
                _remoteScaleVelocity = 0f;
            }

            if (Mathf.Abs(nextScale - currentScale) <= 0.0001f)
                return;

            transform.localScale = Vector3.one * nextScale;
        }

        private float ResolveScaleFromScore(float score)
        {
            if (_settings == null)
                _settings = Core.GameManager.Instance?.Settings;

            if (_settings == null)
            {
                float fallbackScale = Mathf.Abs(transform.localScale.x);
                return ResolveSafeScale(fallbackScale, DEFAULT_MIN_SCALE);
            }

            float minScale = ResolveSafeScale(_settings.MinScale, DEFAULT_MIN_SCALE);
            float maxScale = ResolveSafeScale(_settings.MaxScale, minScale);
            maxScale = Mathf.Max(minScale, maxScale);

            float safeScorePerUnit = ResolveSafeScale(_settings.ScorePerSizeUnit, 100f);
            safeScorePerUnit = Mathf.Max(0.0001f, safeScorePerUnit);

            float safeScore = SanitizeScore(score);
            float growth = Mathf.Log(1f + safeScore / safeScorePerUnit);
            if (!IsFinite(growth))
                growth = 0f;

            float rawScale = minScale + growth;
            return ResolveSafeScale(Mathf.Clamp(rawScale, minScale, maxScale), minScale);
        }

        private float ResolveScaleFallback()
        {
            float currentScale = Mathf.Abs(transform.localScale.x);
            return ResolveSafeScale(currentScale, DEFAULT_MIN_SCALE);
        }

        private static float SanitizeScore(float score)
        {
            if (!IsFinite(score))
                return 0f;

            return Mathf.Max(0f, score);
        }

        private static float ResolveSafeScale(float value, float fallback)
        {
            float safeFallback = (!IsFinitePositive(fallback) ? DEFAULT_MIN_SCALE : Mathf.Max(0.1f, fallback));
            if (!IsFinitePositive(value))
                return safeFallback;

            return Mathf.Max(0.1f, value);
        }

        private static bool IsFinitePositive(float value)
        {
            return IsFinite(value) && value > 0f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
