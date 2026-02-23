using System;
using System.Collections.Generic;
using UnityEngine;
using Core;
using Mirror;
using Network;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Systems
{
    /// <summary>
    /// 우측 상단 디버그 정보 표시.
    /// IMGUI(OnGUI) 기반이므로 Canvas 없이 즉시 동작한다.
    /// 빌드에서 제외하려면 오브젝트 비활성화 또는 #if UNITY_EDITOR로 감싸면 된다.
    /// </summary>
    public class DebugUI : MonoBehaviour
    {
        private const int RTT_SAMPLE_CAPACITY = 600;
        private const float RTT_SAMPLE_INTERVAL = 0.5f;
        private const float QA_KPI_P95_RTT_LIMIT_MS = 150f;
        private const float QA_SERVER_FRAME_LIMIT_MS = 25f;
        private const float KPI_AUTO_LOG_INTERVAL = 60f;
        private const float DEBUG_SLOW_MOTION_SCALE = 0.35f;
        private const KeyCode ALT_SNAPSHOT_KEY = KeyCode.Alpha9;
        private const KeyCode ALT_MOVEMENT_LOCK_KEY = KeyCode.Alpha0;
        private const KeyCode ALT_SLOW_MOTION_KEY = KeyCode.Minus;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureDebugUiExists()
        {
            if (FindObjectOfType<DebugUI>() != null)
                return;

            var debugUiObject = new GameObject("__DebugUI_Auto");
            debugUiObject.AddComponent<DebugUI>();
            Debug.Log("[DebugUI] Auto bootstrap created (__DebugUI_Auto).");
        }
#endif

        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;
        private bool _stylesReady;

        private Player.PlayerController _player;
        private readonly Queue<float> _rttSamplesMs = new Queue<float>(RTT_SAMPLE_CAPACITY);
        private float _rttSampleTimer;
        private bool _rttStatsDirty;
        private float _rttAvgMs;
        private float _rttP95Ms;
        private float _rttMaxMs;
        private float _nextPlayerLookupAt;
        private bool _wasClientConnected;
        private float _sessionStartedAt;
        private float _nextKpiAutoLogAt;
        private bool _kpiResetRequested;
        private bool _kpiSnapshotRequested;
        private bool _movementLockToggleRequested;
        private bool _slowMotionToggleRequested;
        private bool _slowMotionEnabled;

        private float _serverFrameSumMs;
        private int _serverFrameSampleCount;
        private float _serverFrameMaxMs;
        private int _serverConnectionMax;
        private float _serverRttMaxMs;

        private void Start()
        {
            _player = ResolveLocalPlayer();
            ResetKpiSession();
            Debug.Log("[DebugUI] Active. Hotkeys: F8/F9/F10/F11 (alt: 9/0/-).");
        }

        private void OnDisable()
        {
            if (_player != null && _player.IsDebugMovementLocked)
                _player.SetDebugMovementLocked(false);

            if (_slowMotionEnabled)
            {
                _slowMotionEnabled = false;
                ApplySlowMotionTimeScale();
            }
        }

        private void Update()
        {
            if (_player == null || !_player.gameObject.activeInHierarchy)
                _player = ResolveLocalPlayer();

            HandleKpiSessionInput();

            _rttSampleTimer -= Time.unscaledDeltaTime;
            if (_rttSampleTimer > 0f) return;

            _rttSampleTimer = RTT_SAMPLE_INTERVAL;
            if (!NetworkClient.isConnected) return;

            AddRttSample((float)(NetworkTime.rtt * 1000.0));
            if (NetworkServer.active)
                AddServerSample();
        }

        private void InitStyles()
        {
            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 16,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(12, 12, 10, 10)
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                richText = true,
                normal = { textColor = Color.white }
            };

            _stylesReady = true;
        }

        private void OnGUI()
        {
            HandleOnGuiHotkeys();

            if (!_stylesReady) InitStyles();
            RefreshRttStats();

            float w = 360f;
            float h = 500f;
            float margin = 10f;
            Rect panelRect = new Rect(Screen.width - w - margin, margin, w, h);

            GUI.Box(panelRect, "", _boxStyle);
            GUILayout.BeginArea(new Rect(panelRect.x + 10, panelRect.y + 8, w - 20, h - 16));

            Label("<b>— DEBUG —</b>");

            // 점수
            float score = GameManager.Instance != null ? GameManager.Instance.CurrentScore : 0f;
            Label($"점수: <color=yellow>{score:F0}</color>");

            // 플레이어 스케일
            float scale = _player != null ? _player.transform.localScale.x : 0f;
            Label($"크기: <color=cyan>{scale:F2}</color>");

            // 속도
            if (_player != null && GameManager.Instance != null)
            {
                var settings = GameManager.Instance.Settings;
                float baseSpeed = settings.BaseSpeed * scale;
                float boostSpeed = baseSpeed * settings.BoostMultiplier;
                Label($"속도: <color=white>{baseSpeed:F1}</color>");
                Label($"부스트 속도: <color=orange>{boostSpeed:F1}</color>");
            }

            // 카메라 줌
            if (Camera.main != null)
            {
                Label($"카메라 줌: <color=#88CCFF>{Camera.main.orthographicSize:F1}</color>");
            }

            // FPS
            Label($"FPS: <color=green>{1f / Time.unscaledDeltaTime:F0}</color>");

            // 맵 경계 거리
            if (_player != null && GameManager.Instance != null)
            {
                float dist = _player.transform.position.magnitude;
                float radius = GameManager.Instance.Settings.MapRadius;
                float remaining = radius - dist;
                string distColor = remaining < GameManager.Instance.Settings.MapWarningZone ? "red" : "#AAFFAA";
                Label($"경계까지: <color={distColor}>{remaining:F1}</color>");
            }

            // 게임 상태
            bool alive = GameManager.Instance != null && GameManager.Instance.IsGameActive;
            Label($"상태: {(alive ? "<color=lime>활성</color>" : "<color=red>사망</color>")}");

            Label("");
            Label("<b>— NETWORK —</b>");

            string mode = ResolveNetworkMode();
            Label($"모드: <color=#88CCFF>{mode}</color>");

            DigWarNetworkManager networkManager = DigWarNetworkManager.Instance;
            if (networkManager != null)
            {
                int currentPlayers = networkManager.numPlayers;
                int hardCap = networkManager.maxConnections;
                string playerColor = currentPlayers <= hardCap ? "lime" : "red";
                Label($"접속자: <color={playerColor}>{currentPlayers}</color> / {hardCap}");
            }

            if (NetworkServer.active)
            {
                float fullUpdateMs = (float)(NetworkServer.fullUpdateDuration.average * 1000.0);
                string frameColor = fullUpdateMs <= 25f ? "lime" : "red";

                Label($"Server Tick: {NetworkServer.actualTickRate}/{NetworkServer.tickRate} Hz");
                Label($"Server Frame: <color={frameColor}>{fullUpdateMs:F1}ms</color>");
                Label($"Server Connections: {NetworkServer.connections.Count}");
                Label($"Server RTT(avg/max): {GetServerRttAverageMs():F0}/{GetServerRttMaxMs():F0}ms");
            }

            if (NetworkClient.active)
            {
                bool isConnected = NetworkClient.isConnected;
                string stateColor = isConnected ? "lime" : "orange";
                Label($"Client 연결: <color={stateColor}>{(isConnected ? "Connected" : "Connecting")}</color>");

                if (_rttSamplesMs.Count > 0)
                {
                    float currentRtt = (float)(NetworkTime.rtt * 1000.0);
                    string p95Color = _rttP95Ms <= 150f ? "lime" : "red";
                    Label($"RTT(cur/avg/p95/max): {currentRtt:F0}/{_rttAvgMs:F0}/<color={p95Color}>{_rttP95Ms:F0}</color>/{_rttMaxMs:F0}ms");
                }
                else
                {
                    Label("RTT samples: collecting...");
                }

                Label($"품질: {NetworkClient.connectionQuality}");
            }

            Label("");
            Label("<b>— QA KPI —</b>");
            Label($"세션 시간: {FormatDuration(GetSessionDurationSeconds())}");
            Label("핫키: F8(리셋) / F9(스냅샷) / F10(동결) / F11(슬로우)");
            Label("대체키: 9(스냅샷) / 0(동결) / -(슬로우)");
            Label($"로컬 이동 동결: <color={(_player != null && _player.IsDebugMovementLocked ? "orange" : "lime")}>{(_player != null && _player.IsDebugMovementLocked ? "ON" : "OFF")}</color>");
            Label($"슬로우모션: <color={(_slowMotionEnabled ? "orange" : "lime")}>{(_slowMotionEnabled ? "ON" : "OFF")}</color>");

            if (GUILayout.Button(_player != null && _player.IsDebugMovementLocked ? "로컬 이동 동결 해제 (F10)" : "로컬 이동 동결 (F10)"))
                ToggleLocalMovementLock();

            if (GUILayout.Button(_slowMotionEnabled ? "슬로우모션 해제 (F11)" : "슬로우모션 ON (F11, 0.35x)"))
                ToggleSlowMotion();

            if (GUILayout.Button("KPI 스냅샷 로그 출력"))
                EmitKpiSnapshotLog("manual-gui");

            GUILayout.EndArea();
        }

        private void Label(string text)
        {
            GUILayout.Label(text, _labelStyle);
        }

        private static string ResolveNetworkMode()
        {
            if (NetworkServer.active && NetworkClient.active) return "Host";
            if (NetworkServer.active) return "Server";
            if (NetworkClient.active) return "Client";
            return "Offline";
        }

        private void AddRttSample(float rttMs)
        {
            if (rttMs < 0f) return;

            if (_rttSamplesMs.Count >= RTT_SAMPLE_CAPACITY)
                _rttSamplesMs.Dequeue();

            _rttSamplesMs.Enqueue(rttMs);
            _rttStatsDirty = true;
        }

        private void RefreshRttStats()
        {
            if (!_rttStatsDirty) return;
            if (_rttSamplesMs.Count == 0)
            {
                _rttAvgMs = 0f;
                _rttP95Ms = 0f;
                _rttMaxMs = 0f;
                _rttStatsDirty = false;
                return;
            }

            float[] samples = _rttSamplesMs.ToArray();
            Array.Sort(samples);

            float sum = 0f;
            float max = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float value = samples[i];
                sum += value;
                if (value > max) max = value;
            }

            int p95Index = Mathf.Clamp(Mathf.CeilToInt(samples.Length * 0.95f) - 1, 0, samples.Length - 1);
            _rttAvgMs = sum / samples.Length;
            _rttP95Ms = samples[p95Index];
            _rttMaxMs = max;
            _rttStatsDirty = false;
        }

        private static float GetServerRttAverageMs()
        {
            if (NetworkServer.connections == null || NetworkServer.connections.Count == 0) return 0f;

            double sum = 0.0;
            int count = 0;
            foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
            {
                if (conn == null) continue;
                sum += conn.rtt * 1000.0;
                count++;
            }

            if (count == 0) return 0f;
            return (float)(sum / count);
        }

        private static float GetServerRttMaxMs()
        {
            if (NetworkServer.connections == null || NetworkServer.connections.Count == 0) return 0f;

            double max = 0.0;
            foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
            {
                if (conn == null) continue;
                double rttMs = conn.rtt * 1000.0;
                if (rttMs > max) max = rttMs;
            }

            return (float)max;
        }

        private Player.PlayerController ResolveLocalPlayer()
        {
            Network.NetworkPlayer localNetworkPlayer = Network.NetworkPlayer.LocalPlayer;
            if (localNetworkPlayer != null)
            {
                var networkLocalController = localNetworkPlayer.GetComponent<Player.PlayerController>();
                if (networkLocalController != null)
                    return networkLocalController;
            }

            Player.PlayerController localController = Player.PlayerController.LocalController;
            if (localController != null)
                return localController;

            if (Time.unscaledTime < _nextPlayerLookupAt)
                return null;

            _nextPlayerLookupAt = Time.unscaledTime + 1f;
            return FindObjectOfType<Player.PlayerController>();
        }

        private void HandleKpiSessionInput()
        {
            if (WasHotkeyPressed(KeyCode.F8, ref _kpiResetRequested))
            {
                ResetKpiSession();
                Debug.Log("[QA][KPI] 세션 리셋 (F8)");
            }

            bool snapshotRequested =
                WasHotkeyPressed(KeyCode.F9, ref _kpiSnapshotRequested) ||
                IsLegacyHotkeyPressed(ALT_SNAPSHOT_KEY) ||
                IsInputSystemHotkeyPressed(ALT_SNAPSHOT_KEY);
            if (snapshotRequested)
                EmitKpiSnapshotLog("manual-f9");

            bool movementLockRequested =
                WasHotkeyPressed(KeyCode.F10, ref _movementLockToggleRequested) ||
                IsLegacyHotkeyPressed(ALT_MOVEMENT_LOCK_KEY) ||
                IsInputSystemHotkeyPressed(ALT_MOVEMENT_LOCK_KEY);
            if (movementLockRequested)
                ToggleLocalMovementLock();

            bool slowMotionRequested =
                WasHotkeyPressed(KeyCode.F11, ref _slowMotionToggleRequested) ||
                IsLegacyHotkeyPressed(ALT_SLOW_MOTION_KEY) ||
                IsInputSystemHotkeyPressed(ALT_SLOW_MOTION_KEY);
            if (slowMotionRequested)
                ToggleSlowMotion();

            bool isClientConnected = NetworkClient.isConnected;
            if (isClientConnected && !_wasClientConnected)
            {
                ResetKpiSession();
                Debug.Log("[QA][KPI] 세션 시작 (Client Connected)");
            }

            if (!isClientConnected && _wasClientConnected)
                EmitKpiSnapshotLog("disconnect");

            _wasClientConnected = isClientConnected;

            if (isClientConnected && Time.unscaledTime >= _nextKpiAutoLogAt)
            {
                _nextKpiAutoLogAt = Time.unscaledTime + KPI_AUTO_LOG_INTERVAL;
                EmitKpiSnapshotLog("auto-60s");
            }
        }

        private void HandleOnGuiHotkeys()
        {
            Event currentEvent = Event.current;
            if (currentEvent == null || currentEvent.type != EventType.KeyDown)
                return;

            if (currentEvent.keyCode == KeyCode.F8)
            {
                _kpiResetRequested = true;
                currentEvent.Use();
                return;
            }

            if (currentEvent.keyCode == KeyCode.F9)
            {
                _kpiSnapshotRequested = true;
                currentEvent.Use();
                return;
            }

            if (currentEvent.keyCode == ALT_SNAPSHOT_KEY)
            {
                _kpiSnapshotRequested = true;
                currentEvent.Use();
                return;
            }

            if (currentEvent.keyCode == KeyCode.F10)
            {
                _movementLockToggleRequested = true;
                currentEvent.Use();
                return;
            }

            if (currentEvent.keyCode == ALT_MOVEMENT_LOCK_KEY)
            {
                _movementLockToggleRequested = true;
                currentEvent.Use();
                return;
            }

            if (currentEvent.keyCode == KeyCode.F11)
            {
                _slowMotionToggleRequested = true;
                currentEvent.Use();
                return;
            }

            if (currentEvent.keyCode == ALT_SLOW_MOTION_KEY)
            {
                _slowMotionToggleRequested = true;
                currentEvent.Use();
            }
        }

        private static bool IsInputSystemHotkeyPressed(KeyCode keyCode)
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return false;

            switch (keyCode)
            {
                case KeyCode.F8:
                    return keyboard.f8Key.wasPressedThisFrame;
                case KeyCode.F9:
                    return keyboard.f9Key.wasPressedThisFrame;
                case KeyCode.F10:
                    return keyboard.f10Key.wasPressedThisFrame;
                case KeyCode.F11:
                    return keyboard.f11Key.wasPressedThisFrame;
                case KeyCode.Alpha9:
                    return keyboard.digit9Key.wasPressedThisFrame;
                case KeyCode.Alpha0:
                    return keyboard.digit0Key.wasPressedThisFrame;
                case KeyCode.Minus:
                    return keyboard.minusKey.wasPressedThisFrame;
            }
#endif
            return false;
        }

        private void ToggleLocalMovementLock()
        {
            if (_player == null)
                _player = ResolveLocalPlayer();

            if (_player == null)
            {
                Debug.LogWarning("[DebugUI] 로컬 플레이어를 찾지 못해 동결 토글을 수행하지 못했습니다.");
                return;
            }

            bool nextLock = !_player.IsDebugMovementLocked;
            _player.SetDebugMovementLocked(nextLock);
            Debug.Log($"[DebugUI] 로컬 이동 동결 {(nextLock ? "ON" : "OFF")}");
        }

        private void ToggleSlowMotion()
        {
            _slowMotionEnabled = !_slowMotionEnabled;
            ApplySlowMotionTimeScale();
            Debug.Log($"[DebugUI] 슬로우모션 {(_slowMotionEnabled ? "ON(0.35x)" : "OFF(1.0x)")}");
        }

        private void ApplySlowMotionTimeScale()
        {
            if (_slowMotionEnabled)
            {
                Time.timeScale = DEBUG_SLOW_MOTION_SCALE;
                return;
            }

            // 메뉴/재접속 에러 상태에서 0으로 멈춘 시간을 강제로 깨지 않는다.
            if (GameManager.Instance != null && GameManager.Instance.IsGameActive)
                Time.timeScale = 1f;
        }

        private static bool IsLegacyHotkeyPressed(KeyCode keyCode)
        {
            try
            {
                return Input.GetKeyDown(keyCode);
            }
            catch (InvalidOperationException)
            {
                // Active Input Handling이 New Input System 전용일 때 예외를 무시한다.
                return false;
            }
        }

        private static bool WasHotkeyPressed(KeyCode keyCode, ref bool requestedByOnGui)
        {
            bool pressed = requestedByOnGui || IsLegacyHotkeyPressed(keyCode) || IsInputSystemHotkeyPressed(keyCode);
            requestedByOnGui = false;
            return pressed;
        }

        private void ResetKpiSession()
        {
            _sessionStartedAt = Time.unscaledTime;
            _nextKpiAutoLogAt = _sessionStartedAt + KPI_AUTO_LOG_INTERVAL;

            _rttSamplesMs.Clear();
            _rttStatsDirty = true;
            _rttSampleTimer = 0f;

            _serverFrameSumMs = 0f;
            _serverFrameSampleCount = 0;
            _serverFrameMaxMs = 0f;
            _serverConnectionMax = 0;
            _serverRttMaxMs = 0f;
        }

        private void AddServerSample()
        {
            float frameMs = (float)(NetworkServer.fullUpdateDuration.average * 1000.0);
            _serverFrameSumMs += frameMs;
            _serverFrameSampleCount++;
            if (frameMs > _serverFrameMaxMs)
                _serverFrameMaxMs = frameMs;

            int currentConnections = NetworkServer.connections?.Count ?? 0;
            if (currentConnections > _serverConnectionMax)
                _serverConnectionMax = currentConnections;

            float serverRttMax = GetServerRttMaxMs();
            if (serverRttMax > _serverRttMaxMs)
                _serverRttMaxMs = serverRttMax;
        }

        private void EmitKpiSnapshotLog(string reason)
        {
            RefreshRttStats();

            DigWarNetworkManager networkManager = DigWarNetworkManager.Instance;
            int hardCap = networkManager != null ? networkManager.maxConnections : 0;
            int currentPlayers = networkManager != null ? networkManager.numPlayers : 0;

            float currentRttMs = NetworkClient.isConnected ? (float)(NetworkTime.rtt * 1000.0) : 0f;
            float serverFrameNowMs = NetworkServer.active ? (float)(NetworkServer.fullUpdateDuration.average * 1000.0) : 0f;
            float serverFrameAvgMs = _serverFrameSampleCount > 0 ? _serverFrameSumMs / _serverFrameSampleCount : 0f;
            int serverTickActual = NetworkServer.active ? NetworkServer.actualTickRate : 0;
            int serverTickTarget = NetworkServer.active ? NetworkServer.tickRate : 0;
            int serverConnections = NetworkServer.active ? (NetworkServer.connections?.Count ?? 0) : 0;
            float serverRttAvgNowMs = NetworkServer.active ? GetServerRttAverageMs() : 0f;

            bool rttPass = _rttSamplesMs.Count == 0 || _rttP95Ms <= QA_KPI_P95_RTT_LIMIT_MS;
            bool framePass = !NetworkServer.active || serverFrameNowMs <= QA_SERVER_FRAME_LIMIT_MS;
            string qaStatus = rttPass && framePass ? "PASS" : "CHECK";

            string quality = NetworkClient.active ? NetworkClient.connectionQuality.ToString() : "N/A";
            Debug.Log(
                $"[QA][KPI][{reason}] mode={ResolveNetworkMode()} duration={FormatDuration(GetSessionDurationSeconds())} " +
                $"players={currentPlayers}/{hardCap} conn={serverConnections} connMax={_serverConnectionMax} tick={serverTickActual}/{serverTickTarget} " +
                $"rtt(cur/avg/p95/max)={currentRttMs:F0}/{_rttAvgMs:F0}/{_rttP95Ms:F0}/{_rttMaxMs:F0}ms " +
                $"serverFrame(now/avg/max)={serverFrameNowMs:F1}/{serverFrameAvgMs:F1}/{_serverFrameMaxMs:F1}ms " +
                $"serverRtt(avgNow/maxSample)={serverRttAvgNowMs:F0}/{_serverRttMaxMs:F0}ms quality={quality} qa={qaStatus}");
        }

        private float GetSessionDurationSeconds()
        {
            return Mathf.Max(0f, Time.unscaledTime - _sessionStartedAt);
        }

        private static string FormatDuration(float seconds)
        {
            int totalSeconds = Mathf.Max(0, Mathf.FloorToInt(seconds));
            int minutes = totalSeconds / 60;
            int remainSeconds = totalSeconds % 60;
            return $"{minutes:00}:{remainSeconds:00}";
        }
    }
}
